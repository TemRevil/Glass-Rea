using System.Globalization;
using GlassRea.App.Controls;
using GlassRea.App.Support;
using GlassRea.Core.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Win32;

namespace GlassRea.App.Pages;

public sealed partial class SettingsPage : UserControl, IPage
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "Glass-Rea";
    private const double Gb = 1024d * 1024 * 1024;
    private static readonly string[] PriorityValues = [Priority.Normal, Priority.AboveNormal, Priority.High];
    private static int _section; // the section stays picked between visits
    private readonly Dictionary<string, Button> _swatches = [];
    private readonly Grid[] _sections;
    private bool _loading = true, _loaded;

    public SettingsPage()
    {
        InitializeComponent();
        _sections = [GeneralSection, NotificationsSection, DataSection, AppearanceSection, AboutSection];
        SectionPick.SetItems("General", "Notifications", "Data", "Appearance", "About");
        SectionPick.SelectionChanged += i => ShowSection(i, animate: true);
        ShowSection(_section, animate: false);
        PriorityPick.SetItems("Normal", "Above normal", "High");
        PriorityPick.SelectionChanged += OnPriority;
        ShowLocalSettings();
        ThemePick.SetItems("System", "Light", "Dark");
        ThemePick.SelectionChanged += i => Appearance.Update(s => s.Theme = (ThemeChoice)i);
        Swatches.ItemsSource = Appearance.Palettes.Select(Swatch).ToList();
        ShowAppearance();
        Unloaded += (_, _) => Appearance.Changed -= ShowAppearance;
        Loaded += (_, _) => { ShowAppearance(); Appearance.Changed += ShowAppearance; };
        VersionText.Text = $"Version {typeof(App).Assembly.GetName().Version?.ToString(3)}";
        _loading = false;
    }

    public TimeSpan RefreshEvery => TimeSpan.FromSeconds(5);

    private void ShowSection(int index, bool animate)
    {
        _section = index;
        SectionPick.SelectedIndex = index;
        for (var i = 0; i < _sections.Length; i++) _sections[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        if (animate) Motion.Stagger(_sections[index], startDelayMs: 0);
    }

    // ---------- this PC: startup, window, notifications ----------

    private void ShowLocalSettings()
    {
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey))
        {
            var entry = run?.GetValue(RunName) as string;
            StartupBox.IsOn = entry is not null;
            StartHiddenBox.IsOn = entry is null || entry.Contains("--tray", StringComparison.Ordinal);
        }
        StartHiddenBox.IsEnabled = StartupBox.IsOn;
        var p = Preferences.State;
        MinimizeTrayBox.IsOn = p.MinimizeToTray;
        CloseTrayBox.IsOn = p.CloseToTray;
        ShowNotifyBox.IsOn = p.ShowNotifications;
    }

    private void OnStartup(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        StartHiddenBox.IsEnabled = StartupBox.IsOn;
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (StartupBox.IsOn) run.SetValue(RunName, $"\"{Environment.ProcessPath}\"" + (StartHiddenBox.IsOn ? " --tray" : ""));
        else run.DeleteValue(RunName, false);
    }

    private void OnWindowPrefs(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Preferences.Update(p =>
        {
            p.MinimizeToTray = MinimizeTrayBox.IsOn;
            p.CloseToTray = CloseTrayBox.IsOn;
            p.ShowNotifications = ShowNotifyBox.IsOn;
        });
    }

    private void OnTestNotify(object sender, RoutedEventArgs e) =>
        App.Current.Notify("Notifications are working",
            "This is how Glass-Rea tells you about new apps, new devices and data limits. Click one to see it on the Alerts page.",
            evenWhenOff: true);

    private async void OnPriority(int index)
    {
        if (_loading || !_loaded) return;
        var value = PriorityValues[index];
        var error = await Save(new() { [SettingKeys.Priority] = value });
        var self = Priority.ApplyToSelf(value);
        PriorityNote.Text = error ?? (self
            ? index == 0 ? "Saved. Glass-Rea runs at normal priority." : $"Saved. Windows now gives Glass-Rea {PriorityLabel(index)} priority."
            : "Saved for the service, but Windows refused it for this window.");
    }

    private static string PriorityLabel(int index) => index == 1 ? "above normal" : "high";

    // ---------- appearance (kept on this PC, applied at once) ----------

    private Button Swatch(PaletteDef p)
    {
        var dot = new Ellipse
        {
            Width = 18, Height = 18,
            Fill = new LinearGradientBrush
            {
                StartPoint = new(0, 0), EndPoint = new(1, 1),
                GradientStops =
                {
                    new GradientStop { Offset = 0.5, Color = p.LightAccent },
                    new GradientStop { Offset = 0.5, Color = p.DarkAccent },
                },
            },
            Stroke = new SolidColorBrush(Appearance.WithAlpha(Microsoft.UI.Colors.Gray, 0.35)),
            StrokeThickness = 1,
        };
        var b = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { dot, new TextBlock { Text = p.Name } } },
            CornerRadius = new CornerRadius(17),
            Padding = new Thickness(7, 4, 12, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(b, $"{p.Name} accent");
        b.Click += (_, _) => Appearance.Update(s => { s.Palette = p.Key; s.FollowWindowsAccent = false; });
        _swatches[p.Key] = b;
        return b;
    }

    private void ShowAppearance()
    {
        _loading = true;
        var s = Appearance.State;
        ThemePick.SelectedIndex = (int)s.Theme;
        FollowBox.IsOn = s.FollowWindowsAccent;
        foreach (var (key, b) in _swatches)
        {
            var on = !s.FollowWindowsAccent && key == s.Palette;
            b.IsEnabled = !s.FollowWindowsAccent;
            b.BorderThickness = new Thickness(on ? 2 : 1);
            b.BorderBrush = on ? (Brush)Application.Current.Resources["AccentBrush"] : (Brush)Application.Current.Resources["CardStrokeBrush"];
        }
        Swatches.Opacity = s.FollowWindowsAccent ? 0.45 : 1;
        if ((int)BlurSlider.Value != s.Blur) BlurSlider.Value = s.Blur;
        if ((int)GlassSlider.Value != s.Glass) GlassSlider.Value = s.Glass;
        BlurValue.Text = s.Blur.ToString(CultureInfo.InvariantCulture);
        GlassValue.Text = s.Glass.ToString(CultureInfo.InvariantCulture);
        EffectsNote.Opacity = Appearance.EffectsEnabled ? 0.8 : 1;
        _loading = false;
    }

    private void OnFollow(object sender, RoutedEventArgs e)
    {
        if (!_loading) Appearance.Update(s => s.FollowWindowsAccent = FollowBox.IsOn);
    }

    private void OnBlur(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_loading) Appearance.Update(s => s.Blur = (int)Math.Round(e.NewValue));
    }

    private void OnGlass(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_loading) Appearance.Update(s => s.Glass = (int)Math.Round(e.NewValue));
    }

    // ---------- service settings (saved to the service as you change them) ----------

    public async Task Refresh()
    {
        var status = await Service.Call<ServiceStatus>("status");
        ServiceText.Text =
            (status.Capturing ? $"Recording since {Fmt.DateTime(status.TrackingSince)}." : $"Not recording: {status.CaptureError ?? "starting"}.") + "\n" +
            $"Windows history: {status.HistoryImport}.\n" +
            "Data is kept in C:\\ProgramData\\Glass-Rea.";
        if (_loaded) return; // load once per visit so typing is never overwritten

        var s = await Service.Call<Dictionary<string, string>>("settings");
        _loading = true;
        DailyBox.Text = ToGb(s.GetValueOrDefault(SettingKeys.DailyLimit));
        MonthlyBox.Text = ToGb(s.GetValueOrDefault(SettingKeys.MonthlyLimit));
        NewAppBox.IsOn = s.GetValueOrDefault(SettingKeys.AlertNewApp) == "true";
        NewDeviceBox.IsOn = s.GetValueOrDefault(SettingKeys.AlertNewDevice) == "true";
        TunnelBox.Text = s.GetValueOrDefault(SettingKeys.TunnelProcesses, "");
        PriorityPick.SelectedIndex = Math.Max(0, Array.IndexOf(PriorityValues, s.GetValueOrDefault(SettingKeys.Priority)));
        _loading = false;
        _loaded = true;
    }

    private static string ToGb(string? bytes) => long.TryParse(bytes, out var b) && b > 0 ? Fmt.Number(b / Gb) : "";

    private static string? FromGb(string text, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text)) return "0";
        if (!Fmt.TryParseNumber(text, out var gb) || gb < 0) { error = $"\"{text.Trim()}\" isn't a number of GB. Use digits, like 50 or 2.5."; return null; }
        return ((long)Math.Round(gb * Gb)).ToString(CultureInfo.InvariantCulture);
    }

    private async void OnLimits(object sender, RoutedEventArgs e)
    {
        if (_loading || !_loaded) return;
        var daily = FromGb(DailyBox.Text, out var e1);
        var monthly = FromGb(MonthlyBox.Text, out var e2);
        if ((e1 ?? e2) is { } err) { LimitsNote.Text = err; return; }
        // Show what was understood, in Latin digits, whatever keyboard it was typed on.
        DailyBox.Text = ToGb(daily);
        MonthlyBox.Text = ToGb(monthly);
        LimitsNote.Text = await Save(new() { [SettingKeys.DailyLimit] = daily!, [SettingKeys.MonthlyLimit] = monthly! })
            ?? "Saved. Counts internet traffic only; leave empty for no limit.";
    }

    private async void OnAlerts(object sender, RoutedEventArgs e)
    {
        if (_loading || !_loaded) return;
        await Save(new()
        {
            [SettingKeys.AlertNewApp] = NewAppBox.IsOn ? "true" : "false",
            [SettingKeys.AlertNewDevice] = NewDeviceBox.IsOn ? "true" : "false",
        });
    }

    private async void OnTunnels(object sender, RoutedEventArgs e)
    {
        if (_loading || !_loaded) return;
        var list = string.Join(",", TunnelBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        TunnelBox.Text = list.Replace(",", ", ");
        TunnelNote.Text = await Save(new() { [SettingKeys.TunnelProcesses] = list }) ?? "Saved.";
    }

    /// <summary>Saves to the service. Returns an error message, or null when it worked.</summary>
    private static async Task<string?> Save(Dictionary<string, string> values)
    {
        try { await Service.Do("settings.set", settings: values); return null; }
        catch (Exception ex) { return $"Couldn't save: {ex.Message}"; }
    }
}
