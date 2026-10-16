using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace GlassRea.App.Support;

public enum ThemeChoice { System, Light, Dark }

/// <summary>An accent pair: the main colour (download, selection) and its partner (upload), for light and dark.</summary>
public sealed record PaletteDef(string Key, string Name,
    Color LightAccent, Color LightUp, Color DarkAccent, Color DarkUp,
    Color LightHueA, Color LightHueB, Color DarkHueA, Color DarkHueB);

/// <summary>What Settings > Appearance stores. Blur and Glass run 0..100.</summary>
public sealed class AppearanceState
{
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;
    public bool FollowWindowsAccent { get; set; }
    public string Palette { get; set; } = "blue";
    public int Blur { get; set; } = 60;
    public int Glass { get; set; } = 65;
}

/// <summary>
/// Owns the look: theme, accent palette, how blurred and how clear the glass is. Colours live in shared brushes
/// declared in App.xaml; changing a setting rewrites those brushes, so every screen follows at once.
/// </summary>
public static class Appearance
{
    public static readonly IReadOnlyList<PaletteDef> Palettes =
    [
        P("blue", "Blue", "#0F62D6", "#B5472F", "#6CB4FF", "#FF9B85", "#5B9BFF", "#0A3F9E", "#A9D1FF", "#2F7BEA"),
        P("iris", "Iris", "#6150D1", "#13857A", "#ADA2FF", "#5ED8C6", "#8B7CF0", "#3E2FA8", "#C9C1FF", "#6F5FE8"),
        P("jade", "Jade", "#0B7A5E", "#3A63C9", "#46D6AA", "#8EB2FF", "#2FBF94", "#075C47", "#8FF0CF", "#149C76"),
        P("sapphire", "Sapphire", "#2F4FC8", "#915C12", "#93A9FF", "#F2C46D", "#6A86F0", "#1E3592", "#C3D0FF", "#4C68E0"),
        P("rose", "Rose", "#B8336A", "#2B78B8", "#FF8DB6", "#7CC4FF", "#E86A9C", "#85204A", "#FFC0D6", "#D84A82"),
        P("saffron", "Saffron", "#915C12", "#4F58C9", "#F5BC52", "#A3A9FF", "#E0A33A", "#6A420B", "#FFDB94", "#C98A1E"),
        P("copper", "Copper", "#B4592A", "#1F7F8C", "#E58A57", "#63C5D1", "#F0A273", "#8E3F18", "#F7BE98", "#B8582A"),
    ];

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glass-Rea", "appearance.json");
    private static readonly UISettings System = new();

    public static AppearanceState State { get; private set; } = new();
    public static bool IsDark { get; private set; }
    /// <summary>Windows' own "Transparency effects" switch. When off, glass turns solid.</summary>
    public static bool EffectsEnabled => System.AdvancedEffectsEnabled;
    /// <summary>Blur radius for the app's own glass (the tab bar, the rate chip, the scroll edge).</summary>
    public static float GlassBlur => EffectsEnabled ? State.Blur * 0.3f : 0f;

    /// <summary>Raised after the look changed, on the UI thread.</summary>
    public static event Action? Changed;

    private static FrameworkElement? _root;
    private static GlassBackdrop? _backdrop;
    private static Action<bool>? _titleBar;

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath)) State = JsonSerializer.Deserialize<AppearanceState>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { State = new(); }
        State.Blur = Math.Clamp(State.Blur, 0, 100);
        State.Glass = Math.Clamp(State.Glass, 0, 100);
        if (Palettes.All(p => p.Key != State.Palette)) State.Palette = "blue";
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(State));
        }
        catch { /* a read-only profile keeps the defaults */ }
    }

    /// <summary>
    /// Sets the palette's accent before any window exists. Controls created with the first window resolve the accent
    /// once, as they're parsed, so setting it only when the window attaches leaves them on Windows' own accent.
    /// </summary>
    public static void Prime()
    {
        IsDark = State.Theme switch
        {
            ThemeChoice.Dark => true,
            ThemeChoice.Light => false,
            _ => Application.Current.RequestedTheme == ApplicationTheme.Dark,
        };
        var (accent, _, _, _, light, dark) = Colours();
        SetSystemAccent(Application.Current.Resources, accent, light, dark);
    }

    /// <summary>Connects the window. Call once, after its content exists.</summary>
    public static void Attach(FrameworkElement root, GlassBackdrop backdrop, Action<bool> titleBar)
    {
        _root = root;
        _backdrop = backdrop;
        _titleBar = titleBar;
        root.ActualThemeChanged += (_, _) => Apply(refreshAccent: false);
        var queue = root.DispatcherQueue;
        System.ColorValuesChanged += (_, _) => queue.TryEnqueue(() => Apply());
        System.AdvancedEffectsEnabledChanged += (_, _) => queue.TryEnqueue(() => Apply(refreshAccent: false));
        Apply();
    }

    public static void Update(Action<AppearanceState> change)
    {
        change(State);
        Save();
        Apply();
    }

    public static void Apply(bool refreshAccent = true)
    {
        if (_root is null) return;
        var wanted = State.Theme switch { ThemeChoice.Light => ElementTheme.Light, ThemeChoice.Dark => ElementTheme.Dark, _ => ElementTheme.Default };
        if (_root.RequestedTheme != wanted) _root.RequestedTheme = wanted;
        IsDark = _root.ActualTheme == ElementTheme.Dark;

        var (accent, up, hueA, hueB, accentLight, accentDark) = Colours();
        var glass = State.Glass / 100.0;
        var res = Application.Current.Resources;

        Set(res, "AccentBrush", accent);
        Set(res, "UpBrush", up);
        Set(res, "AccentSoftBrush", WithAlpha(accent, IsDark ? 0.22 : 0.16));
        Set(res, "OnAccentBrush", IsDark ? Hex("#161616") : Colors.White);
        if (res["LogoBrush"] is LinearGradientBrush logo) { logo.GradientStops[0].Color = hueA; logo.GradientStops[1].Color = hueB; }

        // The content layer (cards) is translucent; the navigation layer (glass) floats above it and is clearer still.
        var solid = !EffectsEnabled;
        Set(res, "CardBrush", IsDark
            ? WithAlpha(Hex("#303036"), solid ? 1 : 0.9 - glass * 0.6)
            : WithAlpha(Colors.White, solid ? 1 : 0.88 - glass * 0.45));
        Set(res, "CardStrokeBrush", IsDark ? WithAlpha(Colors.White, 0.07) : WithAlpha(Colors.Black, 0.06));
        Set(res, "RowBrush", IsDark ? WithAlpha(Colors.White, 0.04) : WithAlpha(Colors.White, 0.55));
        Set(res, "GlassTintBrush", IsDark
            ? WithAlpha(Hex("#3A3A40"), solid ? 1 : 0.72 - glass * 0.55)
            : WithAlpha(Colors.White, solid ? 1 : 0.72 - glass * 0.55));
        Set(res, "TextBrush", IsDark ? Colors.White : WithAlpha(Colors.Black, 0.894));
        Set(res, "TextSecondaryBrush", IsDark ? WithAlpha(Colors.White, 0.786) : WithAlpha(Colors.Black, 0.62));
        Set(res, "SelectedFillBrush", IsDark ? WithAlpha(Colors.White, 0.08) : WithAlpha(Colors.White, 0.85));
        Set(res, "InkBrush", WithAlpha(Colors.White, IsDark ? 0.16 : 0.75));
        Set(res, "GlowBrush", WithAlpha(Mix(accent, Colors.White, IsDark ? 0.35 : 0.55), IsDark ? 0.42 : 0.55));
        Set(res, "EdgeBrush", IsDark ? WithAlpha(Hex("#1C1C1E"), 0.55) : WithAlpha(Hex("#F3F3F3"), 0.6));
        if (res["GlassRimBrush"] is LinearGradientBrush rim)
        {
            var r = IsDark ? 0.38 : 0.95;
            rim.GradientStops[0].Color = WithAlpha(Colors.White, r);
            rim.GradientStops[1].Color = WithAlpha(Colors.White, 0.04);
            rim.GradientStops[2].Color = WithAlpha(Colors.White, r * 0.4);
        }

        if (refreshAccent) SetSystemAccent(res, accent, accentLight, accentDark);
        _backdrop?.Update(IsDark, State.Blur, glass);
        _titleBar?.Invoke(IsDark);
        Changed?.Invoke();
    }

    private static (Color Accent, Color Up, Color HueA, Color HueB, Color Light, Color Dark) Colours()
    {
        if (State.FollowWindowsAccent)
        {
            var a = System.GetColorValue(UIColorType.Accent);
            var light2 = System.GetColorValue(UIColorType.AccentLight2);
            var dark1 = System.GetColorValue(UIColorType.AccentDark1);
            return IsDark
                ? (light2, Hex("#A8A8A8"), System.GetColorValue(UIColorType.AccentLight3), a, light2, dark1)
                : (dark1, Hex("#6B6B6B"), System.GetColorValue(UIColorType.AccentLight1), System.GetColorValue(UIColorType.AccentDark2), light2, dark1);
        }
        var p = Palettes.First(x => x.Key == State.Palette);
        return IsDark
            ? (p.DarkAccent, p.DarkUp, p.DarkHueA, p.DarkHueB, p.DarkAccent, p.LightAccent)
            : (p.LightAccent, p.LightUp, p.LightHueA, p.LightHueB, p.DarkAccent, p.LightAccent);
    }

    /// <summary>
    /// The built-in controls (toggles, sliders, links, focus, selection) read Windows' accent ramp. Override the ramp
    /// so they use the chosen palette: light theme fills with Dark1, dark theme with Light2. The Fluent accent brushes
    /// are pinned per theme as well (some controls resolve them once, so the ramp alone leaves them on Windows'
    /// accent), and re-applying the theme makes already-loaded controls pick everything up.
    /// </summary>
    private static void SetSystemAccent(ResourceDictionary res, Color accent, Color light, Color dark)
    {
        var ramp = new Dictionary<string, Color>
        {
            ["SystemAccentColor"] = Mix(light, dark, 0.5),
            ["SystemAccentColorLight1"] = Mix(light, dark, 0.25),
            ["SystemAccentColorLight2"] = light,
            ["SystemAccentColorLight3"] = Mix(light, Colors.White, 0.35),
            ["SystemAccentColorDark1"] = dark,
            ["SystemAccentColorDark2"] = Mix(dark, Colors.Black, 0.2),
            ["SystemAccentColorDark3"] = Mix(dark, Colors.Black, 0.4),
        };
        foreach (var (k, v) in ramp) res[k] = v;
        foreach (var theme in new[] { "Light", "Dark", "Default" })
        {
            var d = theme == "Light";
            if (!res.ThemeDictionaries.TryGetValue(theme, out var o) || o is not ResourceDictionary dict)
                res.ThemeDictionaries[theme] = dict = new ResourceDictionary();
            var fill = d ? ramp["SystemAccentColorDark1"] : ramp["SystemAccentColorLight2"];
            Pin(dict, "AccentFillColorDefaultBrush", fill);
            Pin(dict, "AccentFillColorSecondaryBrush", WithAlpha(fill, 0.9));
            Pin(dict, "AccentFillColorTertiaryBrush", WithAlpha(fill, 0.8));
            Pin(dict, "AccentTextFillColorPrimaryBrush", d ? ramp["SystemAccentColorDark2"] : ramp["SystemAccentColorLight3"]);
            Pin(dict, "AccentTextFillColorSecondaryBrush", d ? ramp["SystemAccentColorDark3"] : ramp["SystemAccentColorLight3"]);
            Pin(dict, "AccentTextFillColorTertiaryBrush", d ? ramp["SystemAccentColorDark1"] : ramp["SystemAccentColorLight2"]);
            Pin(dict, "AccentFillColorSelectedTextBackgroundBrush", ramp["SystemAccentColor"]);
            // Links resolve their colour inside the control library, past the brushes above.
            Pin(dict, "HyperlinkButtonForeground", d ? ramp["SystemAccentColorDark2"] : ramp["SystemAccentColorLight3"]);
            Pin(dict, "HyperlinkButtonForegroundPointerOver", d ? ramp["SystemAccentColorDark3"] : ramp["SystemAccentColorLight3"]);
            Pin(dict, "HyperlinkButtonForegroundPressed", d ? ramp["SystemAccentColorDark1"] : ramp["SystemAccentColorLight2"]);
            dict["SystemAccentColor"] = ramp["SystemAccentColor"];
        }
        if (_root is null) return;
        var t = _root.RequestedTheme;
        _root.RequestedTheme = IsDark ? ElementTheme.Light : ElementTheme.Dark;
        _root.RequestedTheme = t;
    }

    private static void Pin(ResourceDictionary dict, string key, Color c)
    {
        if (dict.TryGetValue(key, out var o) && o is SolidColorBrush b) b.Color = c;
        else dict[key] = new SolidColorBrush(c);
    }

    private static void Set(ResourceDictionary res, string key, Color c)
    {
        if (res[key] is SolidColorBrush b) b.Color = c;
    }

    public static Color Hex(string hex)
    {
        var v = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static Color WithAlpha(Color c, double a) => Color.FromArgb((byte)Math.Round(Math.Clamp(a, 0, 1) * 255), c.R, c.G, c.B);

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(255,
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static PaletteDef P(string k, string n, params string[] c) =>
        new(k, n, Hex(c[0]), Hex(c[1]), Hex(c[2]), Hex(c[3]), Hex(c[4]), Hex(c[5]), Hex(c[6]), Hex(c[7]));
}

/// <summary>
/// The window's material: Windows' desktop acrylic, so the desktop shows through, blurred. Glass sets how clear it is;
/// Blur picks the thin (lighter blur) or regular kind. Windows fixes the exact blur radius of the window itself.
/// </summary>
public sealed class GlassBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private readonly SystemBackdropConfiguration _config = new() { IsInputActive = true };
    private (bool Dark, int Blur, double Glass) _last = (false, 60, 0.65);

    protected override void OnTargetConnected(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        _controller = new DesktopAcrylicController();
        _controller.SetSystemBackdropConfiguration(_config);
        Apply();
        _controller.AddSystemBackdropTarget(target);
    }

    // The base class re-reads the window's theme here and throws when the theme is re-applied (which a palette change
    // does to refresh controls). The configuration is driven from Apply instead.
    protected override void OnDefaultSystemBackdropConfigurationChanged(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot) { }

    protected override void OnTargetDisconnected(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        _controller?.RemoveAllSystemBackdropTargets();
        _controller?.Dispose();
        _controller = null;
    }

    public void Update(bool dark, int blur, double glass)
    {
        _last = (dark, blur, glass);
        Apply();
    }

    private void Apply()
    {
        var (dark, blur, glass) = _last;
        _config.Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        if (_controller is null) return;
        var tint = dark ? Appearance.Hex("#1C1C1E") : Appearance.Hex("#F3F3F3");
        _controller.Kind = blur < 50 ? DesktopAcrylicKind.Thin : DesktopAcrylicKind.Base;
        _controller.TintColor = tint;
        _controller.FallbackColor = tint;
        _controller.TintOpacity = (float)Math.Clamp(0.95 - glass * 0.75, 0, 1);
        _controller.LuminosityOpacity = (float)Math.Clamp(0.98 - glass * 0.45, 0, 1);
    }
}
