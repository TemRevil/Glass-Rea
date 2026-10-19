using System.IO.Pipes;
using GlassRea.App.Support;
using GlassRea.Core.Paths;
using GlassRea.Core.Protocol;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace GlassRea.App;

/// <summary>
/// Tray app. Closing the window keeps it running in the tray; new alerts from the service appear as Windows
/// notifications; Exit in the tray menu quits.
/// </summary>
public partial class App : Application
{
    private readonly bool _startInTray;
    private readonly CancellationTokenSource _stop = new();
    private DispatcherQueue? _ui;
    private MainWindow? _window;
    private Tray? _tray;
    private long _lastAlertId = -1;

    public App(bool startInTray)
    {
        _startInTray = startInTray;
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            // A refresh that fails because the service went away must not take the window down with it.
            if (Service.IsOffline(e.Exception) || e.Exception is ServiceException) e.Handled = true;
            else Log.Crash("App", $"Glass-Rea closed on an error it didn't expect ({Log.Place}): {e.Message}", e.Exception);
        };
    }

    public static new App Current => (App)Application.Current;

    public static string IconPath => Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _ui = DispatcherQueue.GetForCurrentThread();
        Log.Start(_ui);
        Appearance.Load();
        Appearance.Prime();
        Preferences.Load();
        _tray = new Tray(IconPath, () => _ui.TryEnqueue(() => ShowWindow()), () => _ui.TryEnqueue(() => ShowWindow("alerts")), () => _ui.TryEnqueue(Exit));
        _ = ListenForShowRequests(_stop.Token);
        _ = WatchAlerts(_stop.Token);
        _ = ApplySavedPriority(_stop.Token);
        // The Store updates its copy itself.
        if (!Packaging.IsPackaged) _ = Updater.Watch(release => _ui.TryEnqueue(() => Notify($"Glass-Rea {release.Version.ToString(3)} is available",
            "Click to see what's new and install it from Settings > About.", onClick: () => _ui.TryEnqueue(() =>
            {
                Pages.SettingsPage.Open(Pages.SettingsPage.About);
                ShowWindow("settings");
            }))), _stop.Token);
        if (!_startInTray && !(Program.StartedAtSignIn && Preferences.State.StartHidden)) ShowWindow();
        else TrimMemorySoon();
    }

    public void ShowWindow(string? page = null)
    {
        _window ??= new MainWindow();
        _window.Reveal(page);
    }

    /// <summary>
    /// Hands the memory the hidden window isn't using back to Windows, a moment after it went to the tray. It stays
    /// the app's, and comes back page by page as it's touched again. Hidden, the app held about 200 MB of RAM; after
    /// this, about 40.
    /// </summary>
    public void TrimMemorySoon()
    {
        if (_trim is null)
        {
            _trim = _ui!.CreateTimer(); // held here: a timer nobody references gets collected before it fires
            _trim.Interval = TimeSpan.FromSeconds(2);
            _trim.IsRepeating = false;
            _trim.Tick += (_, _) =>
            {
                if (_window?.AppWindow.IsVisible == true) return; // opened again meanwhile
                using var self = System.Diagnostics.Process.GetCurrentProcess();
                SetProcessWorkingSetSize(self.Handle, -1, -1);
            };
        }
        _trim.Stop();
        _trim.Start();
    }

    private DispatcherQueueTimer? _trim;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(nint process, nint minimum, nint maximum);

    /// <summary>Shows a Windows notification, unless they're turned off in Settings.</summary>
    public void Notify(string title, string body, bool evenWhenOff = false, Action? onClick = null)
    {
        if (evenWhenOff || Preferences.State.ShowNotifications) _tray?.Notify(title, body, onClick);
    }

    /// <summary>Quits the app: the window, the tray icon and notifications. The service keeps recording.</summary>
    public void Quit() => Exit();

    private new void Exit()
    {
        Appearance.Flush();
        _stop.Cancel();
        _tray?.Dispose();
        _tray = null;
        _window?.Shutdown();
        base.Exit();
    }

    /// <summary>Polls the service for alerts and shows each new one as a notification.</summary>
    private async Task WatchAlerts(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_lastAlertId < 0)
                {
                    // Start from the newest alert so old ones aren't replayed after a restart.
                    var latest = await Service.Call<List<AlertInfo>>("alerts", afterId: 0);
                    _lastAlertId = latest.Count > 0 ? latest.Max(a => a.Id) : 0;
                }
                else
                {
                    var fresh = await Service.Call<List<AlertInfo>>("alerts", afterId: _lastAlertId);
                    foreach (var a in fresh.OrderBy(a => a.Id))
                    {
                        Notify(Pages.AlertsPage.Title(a), a.Body);
                        _lastAlertId = Math.Max(_lastAlertId, a.Id);
                    }
                }
            }
            catch { /* the service isn't running yet; try again shortly */ }
            try { await Task.Delay(TimeSpan.FromSeconds(8), ct); } catch (OperationCanceledException) { }
        }
    }

    /// <summary>The service holds the priority setting; once it answers, this process asks Windows for the same.</summary>
    private static async Task ApplySavedPriority(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var settings = await Service.Call<Dictionary<string, string>>("settings");
                Priority.ApplyToSelf(settings.GetValueOrDefault(SettingKeys.Priority));
                return;
            }
            catch { /* the service isn't running yet */ }
            try { await Task.Delay(TimeSpan.FromSeconds(8), ct); } catch (OperationCanceledException) { }
        }
    }

    /// <summary>A second launch connects here and asks for the window.</summary>
    private async Task ListenForShowRequests(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(Program.InstanceName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct);
                _ui?.TryEnqueue(() => ShowWindow());
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { await Task.Delay(500, ct); }
        }
    }
}
