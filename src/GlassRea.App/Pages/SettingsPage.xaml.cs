using System.Globalization;
using GlassRea.App.Controls;
using GlassRea.App.Support;
using GlassRea.Core.Protocol;
using GlassRea.Core.Updates;
using GlassRea.Core.Usage;
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
    public const int About = 4;
    private static event Action<int>? SectionRequested;
    private bool _checking, _installing;
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
        MonthDayPick.ItemsSource = Enumerable.Range(1, 31).Select(Cycle.Ordinal).ToList();
        WeekPick.SetItems(Cycle.WeekStarts.Select(d => d.ToString()).ToArray());
        WeekPick.SelectionChanged += _ => OnCycle(WeekPick, null!);
        ShowLocalSettings();
        ThemePick.SetItems("System", "Light", "Dark");
        ThemePick.SelectionChanged += i => Appearance.Update(s => s.Theme = (ThemeChoice)i);
        Swatches.ItemsSource = Appearance.Palettes.Select(Swatch).ToList();
        BuildGlassControls();
        ShowAppearance();
        Unloaded += (_, _) => Appearance.Changed -= ShowAppearance;
        Loaded += (_, _) => { ShowAppearance(); Appearance.Changed += ShowAppearance; };
        VersionText.Text = $"Version {Updater.Current.ToString(3)}";
        AutoUpdateBox.IsOn = Preferences.State.CheckForUpdates;
        ShowUpdate();
        // The page is kept between visits: listen only while it's on screen.
        Unloaded += (_, _) => { SectionRequested -= OnSectionRequested; Updater.Checked -= OnUpdateChecked; };
        Loaded += (_, _) =>
        {
            SectionRequested += OnSectionRequested;
            Updater.Checked += OnUpdateChecked;
            if (SectionPick.SelectedIndex != _section) ShowSection(_section, animate: false);
            ShowUpdate();
        };
        _loading = false;
    }

    /// <summary>Makes Settings open on <paramref name="section"/>, now if it's showing, or the next time it is.</summary>
    public static void Open(int section)
    {
        _section = section;
        SectionRequested?.Invoke(section);
    }

    private void OnSectionRequested(int section) => DispatcherQueue.TryEnqueue(() => ShowSection(section, animate: true));

    private void OnUpdateChecked() => DispatcherQueue.TryEnqueue(ShowUpdate);

    // ---------- updates ----------

    private void ShowUpdate()
    {
        if (_installing) return;
        var release = Updater.Available;
        if (release is not null)
        {
            var when = release.Published > DateTimeOffset.MinValue ? $", released {Fmt.Date(release.Published.ToUnixTimeSeconds())}" : "";
            UpdateText.Text = $"Version {release.Version.ToString(3)} is available{when}. You have {Updater.Current.ToString(3)}. Your history and settings are kept.";
            ReleaseNotes.Text = Releases.PlainNotes(release.Notes) is { Length: > 0 } notes ? notes : "No notes were published with this release.";
            ReleaseLink.NavigateUri = new Uri(release.PageUrl);
            ReleasePanel.Visibility = Visibility.Visible;
        }
        else
        {
            UpdateText.Text = Updater.LastChecked is { } at
                ? $"You have the latest version, {Updater.Current.ToString(3)}. Checked {Fmt.Ago(at.ToUnixTimeSeconds())}."
                : "Glass-Rea's releases are published on GitHub. Check to see if there's a newer one.";
            ReleasePanel.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        if (_checking) return;
        _checking = true;
        CheckButton.IsEnabled = false;
        CheckRing.IsActive = true;
        CheckRing.Visibility = Visibility.Visible;
        CheckLabel.Text = "Checking";
        try
        {
            await Updater.Check();
            ShowUpdate();
        }
        catch (UpdateException ex) { UpdateText.Text = ex.Message; }
        finally
        {
            _checking = false;
            CheckButton.IsEnabled = true;
            CheckRing.IsActive = false;
            CheckRing.Visibility = Visibility.Collapsed;
            CheckLabel.Text = "Check now";
        }
    }

    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (Updater.Available is not { } release || _installing) return;
        _installing = true;
        InstallButton.IsEnabled = CheckButton.IsEnabled = false;
        DownloadBar.Value = 0;
        DownloadBar.Visibility = Visibility.Visible;
        UpdateText.Text = $"Downloading version {release.Version.ToString(3)} ({Fmt.Bytes(release.SetupSize)})...";
        try
        {
            var path = await Updater.Download(release, new Progress<double>(p => DownloadBar.Value = p));
            UpdateText.Text = "Downloaded and checked: it's the release's own file, properly signed. Windows will ask to let the setup make changes; it closes this window and opens the new version when it's done.";
            await Updater.Install(path);
        }
        catch (UpdateException ex) { UpdateText.Text = ex.Message; }
        finally
        {
            _installing = false;
            InstallButton.IsEnabled = CheckButton.IsEnabled = true;
            DownloadBar.Visibility = Visibility.Collapsed;
        }
    }

    private void OnAutoUpdate(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Preferences.Update(p => p.CheckForUpdates = AutoUpdateBox.IsOn);
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
        foreach (var (def, slider, value) in _glassRows)
        {
            var v = def.Get(s);
            if ((int)Math.Round(slider.Value) != v) slider.Value = v;
            value.Text = def.Show(v);
        }
        var tint = Appearance.ParseTint(s.TintColor) ?? Microsoft.UI.Colors.White;
        _tintSwatch!.Fill = new SolidColorBrush(tint);
        _tintHex!.Text = s.TintColor;
        if (_tintPicker!.Color != tint) _tintPicker.Color = tint;
        EffectsNote.Opacity = Appearance.EffectsEnabled ? 0.8 : 1;
        _loading = false;
    }

    private void OnFollow(object sender, RoutedEventArgs e)
    {
        if (!_loading) Appearance.Update(s => s.FollowWindowsAccent = FollowBox.IsOn);
    }

    /// <summary>One glass slider: what it's called, what it does, its range, and how it reads and writes the setting.</summary>
    private sealed record GlassSetting(string Label, string Caption, int Min, int Max,
        Func<AppearanceState, int> Get, Action<AppearanceState, int> Set, Func<int, string> Show);

    private readonly List<(GlassSetting Def, Slider Slider, TextBlock Value)> _glassRows = [];
    private Rectangle? _tintSwatch;
    private TextBlock? _tintHex;
    private ColorPicker? _tintPicker;

    private static string Number(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Percent(int v) => $"{Number(v)}%";

    /// <summary>The same controls as RealGlass's, in its order (Frosting is Blur, Glass opacity the inverse of Glass).</summary>
    private static readonly GlassSetting[] GlassSettings =
    [
        new("Chromatic aberration", "Splits light into its colours at the edge, like a prism", 0, 100,
            s => s.Aberration, (s, v) => s.Aberration = v, Number),
        new("Frosting", "How much the glass softens what's behind it", 0, 100, s => s.Blur, (s, v) => s.Blur = v, Number),
        new("Glass opacity", "How solid the glass and the cards are", 0, 100,
            s => 100 - s.Glass, (s, v) => s.Glass = 100 - v, Percent),
        new("Light strength", "How brightly the edge catches the light", 0, 300, s => s.Light, (s, v) => s.Light = v, Percent),
        new("Edge smoothness", "How softly the glass fades in at its edge", 0, 100, s => s.Softness, (s, v) => s.Softness = v, Percent),
        new("Refraction index", "How strongly the edge bends what's behind it. Glass is 1.50, diamond 2.42", 110, 250,
            s => s.Ior, (s, v) => s.Ior = v, v => (v / 100.0).ToString("0.00", CultureInfo.InvariantCulture)),
        new("Corner radius", "How round the corners are, as a share of each shape's own", 0, 100,
            s => s.Corners, (s, v) => s.Corners = v, Percent),
        new("Specular shininess", "Higher makes the highlight smaller and sharper", 1, 100,
            s => s.Shininess, (s, v) => s.Shininess = v, Number),
        new("Thickness", "How tall the edge stands. Thicker glass bends light further", 10, 300,
            s => s.Thickness, (s, v) => s.Thickness = v, Percent),
        new("Tint strength", "How much the tint colour shows", 0, 100, s => s.Tint, (s, v) => s.Tint = v, Percent),
    ];

    private void BuildGlassControls()
    {
        var cells = GlassSettings.Select(GlassRow).ToList();
        cells.Insert(cells.Count - 1, TintColourRow()); // the colour sits just before its strength
        for (var i = 0; i < cells.Count; i++)
        {
            if (i % 3 == 0) GlassGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(cells[i], i / 3);
            Grid.SetColumn(cells[i], i % 3);
            GlassGrid.Children.Add(cells[i]);
        }
    }

    private Border GlassRow(GlassSetting def)
    {
        var value = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var slider = new Slider { Minimum = def.Min, Maximum = def.Max, StepFrequency = 1 };
        AutomationProperties.SetName(slider, def.Label);
        slider.ValueChanged += (_, e) =>
        {
            if (!_loading) Appearance.Update(s => def.Set(s, (int)Math.Round(e.NewValue)));
        };
        _glassRows.Add((def, slider, value));
        return Cell(def.Label, def.Caption, value, slider);
    }

    private Border TintColourRow()
    {
        _tintSwatch = new Rectangle
        {
            Width = 18, Height = 18, RadiusX = 9, RadiusY = 9, StrokeThickness = 1,
            Stroke = new SolidColorBrush(Appearance.WithAlpha(Microsoft.UI.Colors.Gray, 0.35)),
        };
        _tintHex = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        _tintPicker = new ColorPicker
        {
            IsAlphaEnabled = false, IsMoreButtonVisible = false, IsColorSliderVisible = true,
            IsColorChannelTextInputVisible = false, IsHexInputVisible = true, ColorSpectrumShape = ColorSpectrumShape.Ring,
        };
        _tintPicker.ColorChanged += (_, e) =>
        {
            if (!_loading) Appearance.Update(s => s.TintColor = Appearance.ToHex(e.NewColor));
        };
        var pick = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _tintSwatch, _tintHex } },
            Flyout = new Flyout { Content = _tintPicker },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(pick, "Tint colour");
        return Cell("Tint colour", "What the glass is tinted with", null, pick);
    }

    private static Border Cell(string label, string caption, TextBlock? value, FrameworkElement control)
    {
        var title = new Grid { Children = { new TextBlock { Text = label } } };
        if (value is not null) title.Children.Add(value);
        return new Border
        {
            Style = (Style)Application.Current.Resources["Row"],
            Child = new StackPanel
            {
                Spacing = 4,
                Children = { title, new TextBlock { Text = caption, Style = (Style)Application.Current.Resources["Caption"] }, control },
            },
        };
    }

    private void OnResetGlass(object sender, RoutedEventArgs e) => Appearance.Update(s => s.ResetGlass());

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
        PresenceBox.IsOn = s.GetValueOrDefault(SettingKeys.AlertDevicePresence) == "true";
        TunnelBox.Text = s.GetValueOrDefault(SettingKeys.TunnelProcesses, "");
        PriorityPick.SelectedIndex = Math.Max(0, Array.IndexOf(PriorityValues, s.GetValueOrDefault(SettingKeys.Priority)));
        var day = Cycle.ParseMonthDay(s.GetValueOrDefault(SettingKeys.MonthStartDay));
        MonthDayPick.SelectedIndex = day - 1;
        WeekPick.SelectedIndex = Math.Max(0, Array.IndexOf(Cycle.WeekStarts, Cycle.ParseWeekStart(s.GetValueOrDefault(SettingKeys.WeekStart))));
        MonthlyResetText.Text = $"Starts again on the {Cycle.Ordinal(day)}";
        _loading = false;
        _loaded = true;
    }

    private async void OnCycle(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !_loaded || MonthDayPick.SelectedIndex < 0 || WeekPick.SelectedIndex < 0) return;
        var day = MonthDayPick.SelectedIndex + 1;
        var values = new Dictionary<string, string>
        {
            [SettingKeys.MonthStartDay] = day.ToString(CultureInfo.InvariantCulture),
            [SettingKeys.WeekStart] = Cycle.WeekStartKey(Cycle.WeekStarts[WeekPick.SelectedIndex]),
        };
        var error = await Save(values);
        if (error is null) Periods.Use(values);
        MonthlyResetText.Text = $"Starts again on the {Cycle.Ordinal(day)}";
        var (from, to, _) = Periods.Range(Periods.Kind.Month, DateTime.Today);
        CycleNote.Text = error ?? $"Saved. This month now runs {Fmt.Date(from)} to {Fmt.Date(to - 86400)}. A reset day of 29 to 31 falls on the last day of shorter months.";
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
            ?? "Saved. Counted as your internet provider counts it, a VPN's overhead included; leave empty for no limit.";
    }

    private async void OnAlerts(object sender, RoutedEventArgs e)
    {
        if (_loading || !_loaded) return;
        await Save(new()
        {
            [SettingKeys.AlertNewApp] = NewAppBox.IsOn ? "true" : "false",
            [SettingKeys.AlertNewDevice] = NewDeviceBox.IsOn ? "true" : "false",
            [SettingKeys.AlertDevicePresence] = PresenceBox.IsOn ? "true" : "false",
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
