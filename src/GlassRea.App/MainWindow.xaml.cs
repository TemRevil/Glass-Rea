using System.Runtime.InteropServices;
using GlassRea.App.Controls;
using GlassRea.App.Pages;
using GlassRea.App.Support;
using GlassRea.Core.Alerts;
using GlassRea.Core.Paths;
using GlassRea.Core.Protocol;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace GlassRea.App;

/// <summary>A page in the window. Refresh is called on a timer while the page is showing.</summary>
public interface IPage
{
    TimeSpan RefreshEvery { get; }
    Task Refresh();
}

public sealed partial class MainWindow : Window
{
    private sealed record Tab(string Key, string Glyph, string Label, Func<UserControl> Create);

    private readonly Tab[] _tabs =
    [
        new("overview", "", "Overview", () => new OverviewPage()),
        new("apps", "", "Apps", () => new AppsPage()),
        new("connections", "", "Connections", () => new ConnectionsPage()),
        new("firewall", "", "Firewall", () => new FirewallPage()),
        new("devices", "", "Devices", () => new DevicesPage()),
        new("alerts", "", "Alerts", () => new AlertsPage()),
        new("settings", "", "Settings", () => new SettingsPage()),
    ];
    private readonly Dictionary<string, UserControl> _pages = [];
    private readonly Dictionary<string, Button> _tabButtons = [];
    private readonly DispatcherQueueTimer _timer;
    private readonly GlassBackdrop _backdrop = new();
    private string _current = "";
    private DateTime _lastRefresh = DateTime.MinValue;
    private bool _refreshing, _closing, _shown, _twoCopiesClosed;
    private readonly AutoScroll _autoScroll = new();
    private readonly TabLens _lens;

    public static MainWindow? Instance { get; private set; }
    /// <summary>The newest live sample, shared with pages that show "right now".</summary>
    public static LiveSnapshot? Live { get; private set; }

    public MainWindow()
    {
        Instance = this;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(App.IconPath);
        SystemBackdrop = _backdrop;
        if (AppWindow.Presenter is OverlappedPresenter p) { p.PreferredMinimumWidth = 980; p.PreferredMinimumHeight = 680; }
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1240 * scale), (int)(840 * scale)));
        CenterOnScreen();

        AppWindow.Closing += (_, e) =>
        {
            if (_closing) return;
            e.Cancel = true;
            if (Preferences.State.CloseToTray) HideToTray(); // Exit is in the tray menu
            else DispatcherQueue.TryEnqueue(App.Current.Quit); // after this Closing event has finished
        };
        WatchMinimize();

        foreach (var t in _tabs) Tabs.Children.Add(MakeTab(t));
        _lens = new TabLens(LensHost);
        // The tabs change width when one turns bold, and with the text size; the pill follows to wherever the tab is now.
        Tabs.SizeChanged += (_, _) => { if (_current != "") MoveLens(animate: true); };
        TabsGlass.BlurSource = NetworkGlass.BlurSource = ThemeGlass.BlurSource = Stage;
        NetworkSwitch.ShownChanged += shown => NetworkGlass.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        RatesGlass.BlurSource = Pulse;
        Pulse.TipAvoid = RatesGlass;
        Grid.SetRowSpan(_autoScroll, 3);
        Root.Children.Add(_autoScroll);
        _autoScroll.Attach(Root);
        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated) _autoScroll.Stop(); };
        Appearance.Changed += OnAppearance;
        Appearance.Attach(Root, _backdrop, UpdateCaptionButtons);

        if (Packaging.IsPackaged)
            Offline.Message = "Nothing is being recorded right now. It starts with Windows; if it stays stopped, choose Repair for Glass-Rea in Windows' Settings > Apps > Installed apps > Advanced options.";
        TwoCopies.Closed += (_, _) => _twoCopiesClosed = true;
        PermissionState.Changed += ShowPermissions;

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += async (_, _) => await Tick();
        Root.Loaded += (_, _) => Navigate("overview");
    }

    public void Reveal(string? page)
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        Activate();
        _timer.Start();
        _lastRefresh = DateTime.MinValue;
        if (page is not null && Root.IsLoaded) Navigate(page);
        if (!_shown) { _shown = true; ArriveDock(); }
    }

    private void HideToTray()
    {
        AppWindow.Hide();
        _timer?.Stop();
        App.Current.TrimMemorySoon();
    }

    // ---------- minimize to the tray ----------

    private const uint WmSize = 0x0005, WmSysCommand = 0x0112, ScMinimize = 0xF020, SizeMinimized = 1;
    private SubclassProc? _subclass; // kept alive: Windows calls back into it

    /// <summary>With "Minimize to the tray" on, minimizing hides the window instead of parking it on the taskbar.</summary>
    private void WatchMinimize()
    {
        _subclass = (hwnd, msg, wParam, lParam, _, _) =>
        {
            if (Preferences.State.MinimizeToTray)
            {
                if (msg == WmSysCommand && ((uint)wParam & 0xFFF0) == ScMinimize) { HideToTray(); return 0; }
                // Minimized some other way (Win+Down, Show desktop): it still leaves the taskbar.
                if (msg == WmSize && (uint)wParam == SizeMinimized) DispatcherQueue.TryEnqueue(HideToTray);
            }
            return DefSubclassProc(hwnd, msg, wParam, lParam);
        };
        SetWindowSubclass(WinRT.Interop.WindowNative.GetWindowHandle(this), _subclass, 1, 0);
    }

    private delegate nint SubclassProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam);

    public void Shutdown()
    {
        _closing = true;
        _timer.Stop();
        Close();
    }

    // ---------- tabs and the sliding glass lens ----------

    private Button MakeTab(Tab t)
    {
        var b = new Button
        {
            Style = (Style)Application.Current.Resources["TabButton"],
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children = { new FontIcon { Glyph = t.Glyph, FontSize = 14 }, new TextBlock { Text = t.Label } },
            },
        };
        AutomationProperties.SetName(b, t.Label);
        b.Click += (_, _) => Navigate(t.Key);
        _tabButtons[t.Key] = b;
        return b;
    }

    public async void Navigate(string key)
    {
        if (key == _current || _tabs.All(t => t.Key != key)) return;
        var first = _current == "";
        _current = key;
        Log.Where = key;
        ShowPermissions();
        var opening = System.Diagnostics.Stopwatch.GetTimestamp();
        var fresh = false;
        foreach (var (k, b) in _tabButtons)
        {
            var on = k == key;
            b.Foreground = on ? (Brush)Application.Current.Resources["AccentBrush"] : (Brush)Application.Current.Resources["TextSecondaryBrush"];
            ((TextBlock)((StackPanel)b.Content).Children[1]).FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
        MoveLens(animate: !first);
        // Building or re-adding a page holds this thread, and Windows' compositor with it, so the pill would freeze
        // mid-slide and then jump. The page waits until the pill has all but arrived.
        var travelled = first || !Motion.Enabled ? Task.CompletedTask : Task.Delay(TabLens.Arrives);

        if (!_pages.TryGetValue(key, out var page))
        {
            fresh = true;
            // The loader goes up first and reaches the screen before the page is built, which can take a moment.
            ShowLoading(true);
            await Task.WhenAll(Motion.NextFrame(), travelled);
            if (key != _current) { ShowLoading(false); return; }
            _pages[key] = page = _tabs.First(t => t.Key == key).Create();
            // Give a new page a moment to fetch its data, so it arrives filled rather than filling in afterwards.
            if (page is IPage p)
            {
                var filling = p.Refresh();
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                _ = filling.ContinueWith(t =>
                {
                    if (t.Exception is { } ex && !Service.IsOffline(ex.InnerException!)) Log.Error("Window", $"The {key} page couldn't load its data", ex.InnerException);
                    else Log.IfSlow("Window", $"Filling the {key} page the first time", System.Diagnostics.Stopwatch.GetElapsedTime(started));
                }, TaskScheduler.Default);
                try { await Task.WhenAny(filling, Task.Delay(1200)); } catch { /* shown by the next tick */ }
                _ = HideLoadingAfter(filling);
            }
            else ShowLoading(false);
            if (key != _current) return; // another tab was picked meanwhile; this page waits for its turn
        }
        await travelled;
        await Motion.SwapPage(PageHost, Ghost, page, StaggerTarget(page), wanted: () => key == _current);
        if (key != _current) return;
        Log.IfSlow("Window", $"Opening the {key} page{(fresh ? " the first time" : "")}", System.Diagnostics.Stopwatch.GetElapsedTime(opening));
        _lastRefresh = DateTime.MinValue;
        await Tick();
    }

    private int _loads;

    /// <summary>The loader, counted, since a second page can start loading before the first has finished.</summary>
    private void ShowLoading(bool on)
    {
        _loads = Math.Max(0, _loads + (on ? 1 : -1));
        if (_loads > 0)
        {
            Loading.Visibility = Visibility.Visible;
            Loading.Opacity = 1;
            return;
        }
        Loading.Opacity = 0;
        _ = CollapseLoading();
    }

    /// <summary>Collapsed once faded out: a running loader keeps the compositor drawing every frame even when unseen.</summary>
    private async Task CollapseLoading()
    {
        await Task.Delay(300);
        if (_loads == 0) Loading.Visibility = Visibility.Collapsed;
    }

    private async Task HideLoadingAfter(Task filling)
    {
        try { await filling; } catch { /* reported where it was started */ }
        ShowLoading(false);
    }

    /// <summary>The panel whose children arrive one by one: a page's top grid, inside its scroller if it has one.</summary>
    private static Panel? StaggerTarget(UserControl page) => page.Content switch
    {
        ScrollViewer { Content: Panel inner } => inner,
        Panel panel => panel,
        _ => null,
    };

    private void MoveLens(bool animate)
    {
        var b = _tabButtons[_current];
        b.UpdateLayout();
        if (b.ActualWidth == 0) { DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => MoveLens(animate)); return; }
        var x = b.TransformToVisual(Tabs).TransformPoint(default).X;
        _lens.MoveTo((float)x, (float)b.ActualWidth, (float)b.ActualHeight, animate);
    }

    private static DoubleAnimation Anim(DependencyObject target, string prop, double to, TimeSpan time, EasingFunctionBase ease)
    {
        var a = new DoubleAnimation { To = to, Duration = time, EasingFunction = ease };
        Storyboard.SetTarget(a, target);
        Storyboard.SetTargetProperty(a, prop);
        return a;
    }

    /// <summary>The bar materializes: it sharpens in from below instead of just appearing.</summary>
    private void ArriveDock() => Motion.Arrive(Dock, 220, rise: 18);

    // ---------- light and dark ----------

    private readonly SolidColorBrush _plain = new();

    private void OnAppearance()
    {
        UpdateThemeIcon();
        // Ultra performance swaps the window's acrylic, which Windows redraws whenever anything behind the window moves,
        // for its plain colour.
        var plain = Appearance.State.UltraPerformance;
        if (plain != (SystemBackdrop is null)) SystemBackdrop = plain ? null : _backdrop;
        _plain.Color = Appearance.WindowTint;
        Root.Background = plain ? _plain : null;
    }

    private void OnThemeClick(object sender, RoutedEventArgs e) =>
        Appearance.Update(s => s.Theme = Appearance.IsDark ? ThemeChoice.Light : ThemeChoice.Dark);

    private bool? _iconDark;

    /// <summary>The theme button shows the current theme; switching turns one icon away and the other in.</summary>
    private void UpdateThemeIcon()
    {
        var dark = Appearance.IsDark;
        if (_iconDark == dark) return;
        var animate = _iconDark is not null && Motion.Enabled;
        _iconDark = dark;
        Show(MoonIcon, dark, animate);
        Show(SunIcon, !dark, animate);
        _tabButtons.TryGetValue(_current, out var sel);
        if (sel is not null) sel.Foreground = (Brush)Application.Current.Resources["AccentBrush"];
    }

    private static void Show(FontIcon icon, bool on, bool animate)
    {
        var t = (CompositeTransform)icon.RenderTransform;
        if (!animate) { icon.Opacity = on ? 1 : 0; t.Rotation = on ? 0 : -90; t.ScaleX = t.ScaleY = on ? 1 : 0.5; return; }
        var sb = new Storyboard();
        var time = TimeSpan.FromMilliseconds(520);
        var gel = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 };
        sb.Children.Add(Anim(icon, "Opacity", on ? 1 : 0, TimeSpan.FromMilliseconds(260), new SineEase()));
        sb.Children.Add(Anim(t, "Rotation", on ? 0 : -90, time, gel));
        sb.Children.Add(Anim(t, "ScaleX", on ? 1 : 0.5, time, gel));
        sb.Children.Add(Anim(t, "ScaleY", on ? 1 : 0.5, time, gel));
        if (on) { t.Rotation = 90; }
        sb.Begin();
    }

    private bool? _captionDark;

    /// <summary>Each caption colour is a round trip to the window manager (about 30 ms for the set), so only on a theme change.</summary>
    private void UpdateCaptionButtons(bool dark)
    {
        if (_captionDark == dark) return;
        _captionDark = dark;
        var tb = AppWindow.TitleBar;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        tb.ButtonInactiveForegroundColor = dark ? Appearance.WithAlpha(Colors.White, 0.45) : Appearance.WithAlpha(Colors.Black, 0.45);
        tb.ButtonHoverBackgroundColor = dark ? Appearance.WithAlpha(Colors.White, 0.08) : Appearance.WithAlpha(Colors.Black, 0.06);
        tb.ButtonHoverForegroundColor = tb.ButtonForegroundColor;
        tb.ButtonPressedBackgroundColor = dark ? Appearance.WithAlpha(Colors.White, 0.05) : Appearance.WithAlpha(Colors.Black, 0.04);
        tb.ButtonPressedForegroundColor = tb.ButtonForegroundColor;
    }

    private async void OnRemoveOtherCopy(object sender, RoutedEventArgs e)
    {
        try { await OtherCopy.Remove(); }
        catch (InvalidOperationException ex) { TwoCopies.Message = ex.Message; }
    }

    // ---------- permissions ----------

    /// <summary>The page that needs each feature; Past usage from Windows has none.</summary>
    private static Feature? FeatureOf(string page) => page switch
    {
        "overview" or "apps" or "connections" => Feature.Usage,
        "firewall" => Feature.Blocking,
        "devices" => Feature.Devices,
        _ => null,
    };

    /// <summary>The permission screen until it's been answered, and the bar on a page whose feature is off.</summary>
    private void ShowPermissions()
    {
        PermissionScreen.Visibility = PermissionState.Current is { Reviewed: false } ? Visibility.Visible : Visibility.Collapsed;
        if (FeatureOf(_current) is { } f && PermissionState.IsOff(f))
        {
            FeatureOff.Title = PermissionText.OffTitle(f);
            FeatureOff.Message = PermissionText.OffBody(f);
            FeatureOff.IsOpen = true;
        }
        else FeatureOff.IsOpen = false;
    }

    private void OnBarsResized(object sender, SizeChangedEventArgs e) =>
        PageHost.Margin = new Thickness(14, 12 + (e.NewSize.Height > 0 ? e.NewSize.Height + 12 : 0), 14, 0);

    private async void OnTurnOn(object sender, RoutedEventArgs e)
    {
        if (FeatureOf(_current) is not { } f) return;
        await Permit(() => PermissionState.Turn(f, true));
        _lastRefresh = DateTime.MinValue; // the page fills in now rather than at its next refresh
    }

    private async void OnAllowAll(object sender, RoutedEventArgs e) => await Permit(PermissionState.AllowAll);

    private async void OnPermissionsDone(object sender, RoutedEventArgs e) => await Permit(() => PermissionState.Choose(ScreenChoices.Chosen()));

    private static async Task Permit(Func<Task> change)
    {
        try { await change(); }
        catch (Exception ex) when (Service.IsOffline(ex) || ex is ServiceException) { Log.Warn("Permissions", $"Couldn't change permissions: {ex.Message}"); }
    }

    // ---------- refresh ----------

    private async Task Tick()
    {
        // Nothing to show while hidden in the tray or minimized; the first tick after it comes back catches up.
        if (_refreshing || !AppWindow.IsVisible || AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }) return;
        _refreshing = true;
        TwoCopies.IsOpen = !_twoCopiesClosed && OtherCopy.Present;
        try
        {
            var live = await Service.Call<LiveSnapshot>("live");
            Live = live;
            // The newest second is still filling, so the rates use the last complete one.
            var last = Math.Max(0, live.In.Length - 2);
            DownRate.Text = Fmt.Rate(live.In.Length > 0 ? live.In[last] : 0);
            UpRate.Text = Fmt.Rate(live.Out.Length > 0 ? live.Out[last] : 0);
            if (live.In.Length > 1) Pulse.SetData(live.In[..^1], live.Out[..^1], live.Now - 1);
            Offline.IsOpen = false;

            if (_pages.TryGetValue(_current, out var view) && view is IPage page && DateTime.UtcNow - _lastRefresh >= page.RefreshEvery)
            {
                _lastRefresh = DateTime.UtcNow;
                var status = await Service.Call<ServiceStatus>("status");
                PermissionState.Use(status.Permissions);
                ShowPermissions();
                StatusText.Text = status.Capturing
                    ? $"Recording since {Fmt.Date(status.TrackingSince)}"
                    : PermissionState.IsOff(Feature.Usage) ? "Not recording: Usage per app is off"
                    : $"Not recording: {status.CaptureError ?? "starting"}";
                Periods.Use(await Service.Call<Dictionary<string, string>>("settings")); // the reset day shapes "this month"
                Networks.Use(await Service.Call<List<NetworkInfo>>("networks")); // for the network switch
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                await page.Refresh();
                Log.IfSlow("Window", $"Refreshing the {_current} page", System.Diagnostics.Stopwatch.GetElapsedTime(started));
            }
        }
        catch (Exception ex) when (Service.IsOffline(ex))
        {
            Offline.IsOpen = true;
            StatusText.Text = "";
            _lastRefresh = DateTime.MinValue;
        }
        catch (ServiceException ex) { StatusText.Text = ex.Message; }
        finally { _refreshing = false; }
    }

    private void CenterOnScreen()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(area.X + (area.Width - AppWindow.Size.Width) / 2, area.Y + (area.Height - AppWindow.Size.Height) / 2));
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
}
