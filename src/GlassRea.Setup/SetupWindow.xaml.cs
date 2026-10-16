using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using GlassRea.App.Support;
using GlassRea.Setup.Installer;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace GlassRea.Setup;

/// <summary>
/// One calm window for install, update and removal: who it is on the left, one page at a time on the right.
/// Pages arrive with the app's motion (content rises in and settles, 45 ms apart) and leave quickly.
/// </summary>
public sealed partial class SetupWindow : Window
{
    private readonly SetupMode _mode;
    private readonly GlassBackdrop _backdrop = new();
    private readonly bool _animate = new UISettings().AnimationsEnabled;
    private readonly string? _installed = Integration.InstalledVersion();
    private CancellationTokenSource? _work;
    private Grid? _page;
    private Func<IReadOnlyList<Step>>? _lastSteps;
    private string _lastTitle = "";
    private Action? _lastDone;
    private readonly string? _previousDir = Integration.InstalledLocation();
    private readonly bool _legacy = Legacy.IsPresent();
    private bool _finished;

    public SetupWindow(SetupMode mode)
    {
        _mode = mode;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        SystemBackdrop = _backdrop;
        if (AppWindow.Presenter is OverlappedPresenter p) { p.IsResizable = false; p.IsMaximizable = false; }
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(840 * scale), (int)(540 * scale)));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(area.X + (area.Width - AppWindow.Size.Width) / 2, area.Y + (area.Height - AppWindow.Size.Height) / 2));

        AppWindow.Closing += (_, e) =>
        {
            if (_work is not null) { e.Cancel = true; return; } // never stop halfway
            if (_page == DonePage) Finish();                     // closing the finished page counts as Finish
        };
        Closed += (_, _) => { if (Environment.GetCommandLineArgs().Contains("--temp-copy")) DeleteSelfLater(); };

        Appearance.Attach(Root, _backdrop, UpdateCaptionButtons);
        Appearance.Changed += UpdateGlow;
        UpdateGlow();

        VersionText.Text = $"Version {Paths.Version}";
        InstallPathText.Text = Paths.InstallDir;
        KeepText.Text = $"It stays in {Paths.DataDir}, so installing again picks up where you left off.";
        PrepareWelcome();

        Root.Loaded += (_, _) =>
        {
            Arrive(Logo, 60, 0.86f);
            Arrive(Names, 180);
            Arrive(Signature, 320);
            Breathe();
            Show(_mode == SetupMode.Install ? WelcomePage : RemovePage);
        };
    }

    private void PrepareWelcome()
    {
        if (_installed is null)
        {
            WelcomeTitle.Text = "Install Glass-Rea";
            InstallButton.Content = "Install";
        }
        else if (_installed == Paths.Version)
        {
            WelcomeTitle.Text = "Reinstall Glass-Rea";
            WelcomeBody.Text = $"Version {_installed} is already installed. Installing it again repairs it; your history and settings stay as they are.";
            InstallButton.Content = "Reinstall";
        }
        else
        {
            WelcomeTitle.Text = "Update Glass-Rea";
            WelcomeBody.Text = $"Updates {(_installed.Length > 0 ? $"version {_installed}" : "the installed copy")} to {Paths.Version}. Your history and settings stay as they are.";
            InstallButton.Content = "Update";
        }
        StartToggle.IsOn = (_installed is null && !_legacy) || Integration.StartsWithWindows() || Legacy.StartedWithWindows();
        if (_installed is null && _legacy)
        {
            WelcomeTitle.Text = "Update to Glass-Rea";
            WelcomeBody.Text = "Glasswire REA is now called Glass-Rea. This replaces the old copy and brings your history, settings and firewall blocks with it.";
            InstallButton.Content = "Update";
        }
        RuntimeText.Text = Runtime.IsInstalled()
            ? "Already on this PC"
            : "Not on this PC yet. Setup downloads it from Microsoft (about 30 MB) and checks it's signed by Microsoft before running it.";
        if (!Engine.HasPayload)
        {
            WelcomeBody.Text = "This copy of setup was built without the program files, so it can't install. Build it with scripts\\build-setup.ps1.";
            InstallButton.IsEnabled = false;
        }
    }

    // ---------- actions ----------

    private void OnInstall(object sender, RoutedEventArgs e)
    {
        var start = StartToggle.IsOn;
        var fresh = _installed is null && !_legacy;
        var verb = fresh ? "Installing" : _installed == Paths.Version ? "Reinstalling" : "Updating";
        ChangeFolderButton.IsEnabled = false;
        Run(verb, () => Engine.Install(start, _previousDir), () =>
        {
            DoneTitle.Text = fresh ? "Glass-Rea is ready" : "Glass-Rea is up to date";
            DoneBody.Text = "It's recording now, even with its window closed. You'll find it in Start and in the tray.";
            OpenAppCheck.Visibility = Visibility.Visible;
        });
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        var keep = KeepToggle.IsOn;
        Run("Removing", () => Engine.Uninstall(keep), () =>
        {
            DoneTitle.Text = "Glass-Rea is removed";
            DoneBody.Text = keep
                ? $"Your history is still in {Paths.DataDir}. Install again any time to pick up where you left off."
                : "Your history was deleted as well.";
            OpenAppCheck.IsChecked = false;
            OpenAppCheck.Visibility = Visibility.Collapsed;
        });
    }

    private void OnRetry(object sender, RoutedEventArgs e)
    {
        if (_lastSteps is not null && _lastDone is not null) Run(_lastTitle, _lastSteps, _lastDone);
    }

    private void OnChangeFolder(object sender, RoutedEventArgs e)
    {
        var picked = FolderDialog.Pick(WinRT.Interop.WindowNative.GetWindowHandle(this), "Choose where to install Glass-Rea",
            System.IO.Path.GetDirectoryName(Paths.InstallDir) ?? Paths.InstallDir);
        if (picked is null) return;
        var dir = Paths.ChooseInstallDir(picked, out var problem);
        InstallPathProblem.Text = problem ?? "";
        InstallPathProblem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        if (dir is null) return;
        Paths.InstallDir = dir;
        InstallPathText.Text = dir;
    }

    private void OnFinish(object sender, RoutedEventArgs e) => Close();

    /// <summary>Does what the finished page's boxes ask for, once, as the signed-in user.</summary>
    private void Finish()
    {
        if (_finished) return;
        _finished = true;
        if (OpenAppCheck.Visibility == Visibility.Visible && OpenAppCheck.IsChecked == true)
        {
            try { AppProcess.OpenAsUser(); }
            catch (Exception ex) { Log.Write($"Could not open the app: {ex}"); }
        }
        if (OpenSiteCheck.IsChecked == true)
        {
            try { AppProcess.OpenWebsiteAsUser(Paths.Website); }
            catch (Exception ex) { Log.Write($"Could not open {Paths.Website}: {ex}"); }
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (_work is null) Close();
    }

    // ---------- running steps ----------

    private async void Run(string title, Func<IReadOnlyList<Step>> make, Action done)
    {
        (_lastTitle, _lastSteps, _lastDone) = (title, make, done);
        var steps = make();
        _work = new CancellationTokenSource();
        Log.Write($"--- {title} {Paths.Version}");
        ProgressTitle.Text = title;
        ProgressStatus.Text = "";
        Bar.Value = 0;
        StepList.Children.Clear();
        var rows = steps.Select(s => new StepRow(s.Title)).ToList();
        foreach (var r in rows) StepList.Children.Add(r.Root);
        Show(ProgressPage);
        await Task.Delay(_animate ? 450 : 0);

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            rows[i].SetRunning();
            ProgressStatus.Text = step.Title;
            MoveBar((i + 0.35) / steps.Count, 900);
            var status = new Progress<string>(s => ProgressStatus.Text = s);
            try
            {
                Log.Write(step.Title);
                await step.Run(status, _work.Token);
            }
            catch (Exception ex)
            {
                Log.Write($"Failed at \"{step.Title}\": {ex}");
                rows[i].SetFailed();
                _work = null;
                ChangeFolderButton.IsEnabled = true;
                ErrorTitle.Text = _mode == SetupMode.Install ? "Setup couldn't finish" : "Glass-Rea couldn't be fully removed";
                var reason = ex;
                while (reason.InnerException is not null) reason = reason.InnerException;
                ErrorText.Text = ex is SetupException ? ex.Message : $"{step.Title} didn't work. Windows said: {reason.Message}";
                ErrorLog.Text = $"Full details are in {Log.FilePath}";
                await Task.Delay(_animate ? 700 : 0);
                Show(ErrorPage);
                return;
            }
            rows[i].SetDone(_animate);
            MoveBar((i + 1.0) / steps.Count, 500);
        }
        Log.Write("Finished");
        await Task.Delay(_animate ? 650 : 0);
        _work = null;
        done();
        Show(DonePage);
        Pop(DoneBadge);
    }

    private void MoveBar(double to, int ms)
    {
        var target = to * Bar.Maximum;
        if (!_animate) { Bar.Value = target; return; }
        var a = new DoubleAnimation
        {
            To = target, Duration = TimeSpan.FromMilliseconds(ms), EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(a, Bar);
        Storyboard.SetTargetProperty(a, "Value");
        var sb = new Storyboard();
        sb.Children.Add(a);
        sb.Begin();
    }

    /// <summary>A row in the step list: a hollow ring waiting, a spinner while running, the accent check when done.</summary>
    private sealed class StepRow
    {
        public readonly Grid Root = new() { ColumnSpacing = 12, MinHeight = 30 };
        private readonly Grid _icon = new() { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _text;

        public StepRow(string title)
        {
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Root.ColumnDefinitions.Add(new ColumnDefinition());
            _text = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.55 };
            Grid.SetColumn(_text, 1);
            Root.Children.Add(_icon);
            Root.Children.Add(_text);
            _icon.Children.Add(new Ellipse
            {
                Width = 14, Height = 14, StrokeThickness = 1.5,
                Stroke = (Brush)Application.Current.Resources["TextSecondaryBrush"], Opacity = 0.6,
            });
        }

        public void SetRunning()
        {
            _icon.Children.Clear();
            _icon.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16 });
            _text.Opacity = 1;
            _text.FontWeight = FontWeights.SemiBold;
        }

        public void SetDone(bool animate)
        {
            _icon.Children.Clear();
            var badge = new Grid { Width = 18, Height = 18 };
            badge.Children.Add(new Ellipse { Fill = (Brush)Application.Current.Resources["AccentBrush"] });
            badge.Children.Add(new FontIcon { Glyph = "", FontSize = 10, Foreground = (Brush)Application.Current.Resources["OnAccentBrush"] });
            _icon.Children.Add(badge);
            _text.FontWeight = FontWeights.Normal;
            if (animate) Pop(badge);
        }

        public void SetFailed()
        {
            _icon.Children.Clear();
            _icon.Children.Add(new FontIcon { Glyph = "", FontSize = 18, Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] });
            _text.FontWeight = FontWeights.Normal;
        }
    }

    // ---------- motion ----------

    private async void Show(Grid page)
    {
        var old = _page;
        _page = page;
        if (old is not null && old != page)
        {
            if (_animate)
            {
                var v = ElementCompositionPreview.GetElementVisual(old);
                var c = v.Compositor;
                var ease = c.CreateCubicBezierEasingFunction(new(0.7f, 0f), new(1f, 0.5f));
                var fade = c.CreateScalarKeyFrameAnimation();
                fade.InsertKeyFrame(1, 0, ease);
                fade.Duration = TimeSpan.FromMilliseconds(160);
                v.StartAnimation("Opacity", fade);
                await Task.Delay(170);
                v.StopAnimation("Opacity");
                v.Opacity = 1;
            }
            old.Visibility = Visibility.Collapsed;
        }
        page.Visibility = Visibility.Visible;
        var i = 0;
        foreach (var child in page.Children) Arrive(child, 40 + i++ * 45);
    }

    /// <summary>Rises 14 px into place while fading in, decelerating, after <paramref name="delayMs"/>.</summary>
    private void Arrive(UIElement e, int delayMs, float fromScale = 1f)
    {
        if (!_animate) return;
        ElementCompositionPreview.SetIsTranslationEnabled(e, true);
        var v = ElementCompositionPreview.GetElementVisual(e);
        var c = v.Compositor;
        var ease = c.CreateCubicBezierEasingFunction(new(0.1f, 0.9f), new(0.2f, 1f));
        var delay = TimeSpan.FromMilliseconds(delayMs);
        var duration = TimeSpan.FromMilliseconds(fromScale < 1 ? 700 : 420);

        v.Opacity = 0;
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, ease);
        fade.Duration = duration;
        fade.DelayTime = delay;
        fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        v.StartAnimation("Opacity", fade);

        var rise = c.CreateVector3KeyFrameAnimation();
        rise.InsertKeyFrame(0, new Vector3(0, fromScale < 1 ? 0 : 14, 0));
        rise.InsertKeyFrame(1, Vector3.Zero, ease);
        rise.Duration = duration;
        rise.DelayTime = delay;
        rise.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        v.StartAnimation("Translation", rise);

        if (fromScale < 1 && e is FrameworkElement f)
        {
            v.CenterPoint = new Vector3((float)f.Width / 2, (float)f.Height / 2, 0);
            var grow = c.CreateVector3KeyFrameAnimation();
            grow.InsertKeyFrame(0, new Vector3(fromScale, fromScale, 1));
            grow.InsertKeyFrame(1, Vector3.One, ease);
            grow.Duration = duration;
            grow.DelayTime = delay;
            grow.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            v.StartAnimation("Scale", grow);
        }
    }

    /// <summary>A small, settled overshoot: the check lands rather than appears.</summary>
    private static void Pop(FrameworkElement e)
    {
        var v = ElementCompositionPreview.GetElementVisual(e);
        var c = v.Compositor;
        v.CenterPoint = new Vector3((float)e.Width / 2, (float)e.Height / 2, 0);
        var grow = c.CreateVector3KeyFrameAnimation();
        grow.InsertKeyFrame(0, new Vector3(0.4f, 0.4f, 1));
        grow.InsertKeyFrame(1, Vector3.One, c.CreateCubicBezierEasingFunction(new(0.34f, 1.4f), new(0.64f, 1f)));
        grow.Duration = TimeSpan.FromMilliseconds(520);
        v.StartAnimation("Scale", grow);
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1);
        fade.Duration = TimeSpan.FromMilliseconds(220);
        v.StartAnimation("Opacity", fade);
    }

    /// <summary>The glow behind the mark swells and settles every few seconds, slower than a breath.</summary>
    private void Breathe()
    {
        var v = ElementCompositionPreview.GetElementVisual(Glow);
        v.CenterPoint = new Vector3((float)Glow.ActualWidth / 2, (float)Glow.ActualHeight / 2, 0);
        if (!_animate) return;
        var c = v.Compositor;
        var ease = c.CreateCubicBezierEasingFunction(new(0.45f, 0f), new(0.55f, 1f));
        var scale = c.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0, new Vector3(0.9f, 0.9f, 1));
        scale.InsertKeyFrame(1, new Vector3(1.08f, 1.08f, 1), ease);
        scale.Duration = TimeSpan.FromMilliseconds(5200);
        scale.IterationBehavior = AnimationIterationBehavior.Forever;
        scale.Direction = AnimationDirection.Alternate;
        v.StartAnimation("Scale", scale);
        var glow = c.CreateScalarKeyFrameAnimation();
        glow.InsertKeyFrame(0, 0.55f);
        glow.InsertKeyFrame(1, 1f, ease);
        glow.Duration = TimeSpan.FromMilliseconds(5200);
        glow.IterationBehavior = AnimationIterationBehavior.Forever;
        glow.Direction = AnimationDirection.Alternate;
        v.StartAnimation("Opacity", glow);
    }

    private void UpdateGlow()
    {
        var accent = ((SolidColorBrush)Application.Current.Resources["AccentBrush"]).Color;
        GlowCenter.Color = Appearance.WithAlpha(accent, Appearance.IsDark ? 0.42 : 0.3);
        ((GradientStop)((RadialGradientBrush)Glow.Fill).GradientStops[1]).Color = Appearance.WithAlpha(accent, 0);
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

    /// <summary>The temp copy that ran the removal deletes itself once it has exited.</summary>
    private static void DeleteSelfLater()
    {
        var self = Environment.ProcessPath!;
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & del /f /q \"{self}\"")
            { UseShellExecute = false, CreateNoWindow = true });
        }
        catch { /* the temp folder is cleaned by Windows eventually */ }
        MoveFileEx(self, null, 4); // MOVEFILE_DELAY_UNTIL_REBOOT, in case the line above couldn't
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string existing, string? replacement, int flags);
}
