using System.Runtime.InteropServices;
using GlassRea.App.Controls;
using GlassRea.App.Pages;
using GlassRea.App.Support;
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
    private bool _refreshing, _closing, _shown;

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
        TabsGlass.BlurSource = ThemeGlass.BlurSource = Edge.BlurSource = PageHost;
        RatesGlass.BlurSource = Pulse;
        Appearance.Attach(Root, _backdrop, UpdateCaptionButtons);
        Appearance.Changed += UpdateThemeIcon;
        UpdateThemeIcon();

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += async (_, _) => await Tick();
        Root.Loaded += (_, _) => { Navigate("overview"); Breathe(); };
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
        foreach (var (k, b) in _tabButtons)
        {
            var on = k == key;
            b.Foreground = on ? (Brush)Application.Current.Resources["AccentBrush"] : (Brush)Application.Current.Resources["TextSecondaryBrush"];
            ((TextBlock)((StackPanel)b.Content).Children[1]).FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
        MoveLens(animate: !first);

        if (!_pages.TryGetValue(key, out var page))
        {
            _pages[key] = page = _tabs.First(t => t.Key == key).Create();
            // Give a new page a moment to fetch its data, so it arrives filled rather than filling in afterwards.
            if (page is IPage p)
            {
                try { await Task.WhenAny(p.Refresh(), Task.Delay(450)); } catch { /* shown by the next tick */ }
            }
        }
        await Motion.SwapPage(PageHost, Ghost, page, arrived: () => { if (StaggerTarget(page) is { } panel) Motion.Stagger(panel); });
        _lastRefresh = DateTime.MinValue;
        await Tick();
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
        Lens.Height = b.ActualHeight;
        Lens.CornerRadius = new CornerRadius(b.ActualHeight / 2);
        if (!animate || !Motion.Enabled)
        {
            Lens.Width = b.ActualWidth;
            LensMove.TranslateX = x;
            return;
        }
        // The lens slides with a gel-like overshoot and squashes slightly as it travels, then settles.
        var sb = new Storyboard();
        var gel = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.32 };
        var time = TimeSpan.FromMilliseconds(560);
        sb.Children.Add(Anim(LensMove, "TranslateX", x, time, gel));
        var width = Anim(Lens, "Width", b.ActualWidth, time, gel);
        width.EnableDependentAnimation = true;
        sb.Children.Add(width);
        sb.Children.Add(Squash(LensMove, "ScaleY", 0.86, time));
        sb.Children.Add(Squash(LensMove, "ScaleX", 1.04, time));
        sb.Begin();
    }

    private static DoubleAnimation Anim(DependencyObject target, string prop, double to, TimeSpan time, EasingFunctionBase ease)
    {
        var a = new DoubleAnimation { To = to, Duration = time, EasingFunction = ease };
        Storyboard.SetTarget(a, target);
        Storyboard.SetTargetProperty(a, prop);
        return a;
    }

    private static DoubleAnimationUsingKeyFrames Squash(DependencyObject target, string prop, double mid, TimeSpan time)
    {
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(time.TotalMilliseconds * 0.3), Value = mid, EasingFunction = new SineEase() });
        a.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = time, Value = 1, EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 } });
        Storyboard.SetTarget(a, target);
        Storyboard.SetTargetProperty(a, prop);
        return a;
    }

    /// <summary>The bar materializes: it sharpens in from below instead of just appearing.</summary>
    private void ArriveDock() => Motion.Arrive(Dock, 220, rise: 18);

    /// <summary>The live dot breathes slowly, like a recording light.</summary>
    private void Breathe()
    {
        if (!Motion.Enabled) return;
        var v = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(LiveDot);
        var a = v.Compositor.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0, 1); a.InsertKeyFrame(0.5f, 0.3f); a.InsertKeyFrame(1, 1);
        a.Duration = TimeSpan.FromSeconds(2.4);
        a.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
        v.StartAnimation("Opacity", a);
    }

    // ---------- light and dark ----------

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

    private void UpdateCaptionButtons(bool dark)
    {
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

    // ---------- refresh ----------

    private async Task Tick()
    {
        if (_refreshing || !AppWindow.IsVisible) return;
        _refreshing = true;
        try
        {
            var live = await Service.Call<LiveSnapshot>("live");
            Live = live;
            // The newest second is still filling, so the rates use the last complete one.
            var last = Math.Max(0, live.In.Length - 2);
            DownRate.Text = Fmt.Rate(live.In.Length > 0 ? live.In[last] : 0);
            UpRate.Text = Fmt.Rate(live.Out.Length > 0 ? live.Out[last] : 0);
            if (live.In.Length > 1) Pulse.SetData(live.In[..^1], live.Out[..^1]);
            Offline.IsOpen = false;

            if (_pages.TryGetValue(_current, out var view) && view is IPage page && DateTime.UtcNow - _lastRefresh >= page.RefreshEvery)
            {
                _lastRefresh = DateTime.UtcNow;
                var status = await Service.Call<ServiceStatus>("status");
                StatusText.Text = status.Capturing
                    ? $"Recording since {Fmt.Date(status.TrackingSince)}"
                    : $"Not recording: {status.CaptureError ?? "starting"}";
                await page.Refresh();
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
