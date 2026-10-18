using GlassRea.App.Support;
using GlassRea.Core.Protocol;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GlassRea.App.Controls;

/// <summary>
/// An app's icon (see <see cref="AppIcons"/>); when it has none, or while it loads for the first time, a quiet tile
/// with the symbol for its kind.
/// </summary>
public sealed class AppIconView : Grid
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(string), typeof(AppIconView),
        new PropertyMetadata(null, (d, _) => ((AppIconView)d).Load()));
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(AppIconView),
        new PropertyMetadata("", (d, _) => ((AppIconView)d).Load()));

    public string? Icon { get => (string?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    private readonly Border _tile = new()
    {
        CornerRadius = new CornerRadius(7),
        BorderThickness = new Thickness(1),
        BorderBrush = (Brush)Application.Current.Resources["CardStrokeBrush"],
        Background = (Brush)Application.Current.Resources["RowBrush"],
    };
    private readonly FontIcon _glyph = new() { FontSize = 14, Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"] };
    private readonly Image _image = new() { Width = 24, Height = 24 };
    private int _version;

    public AppIconView()
    {
        Width = 30; Height = 30;
        _tile.Child = _glyph;
        Children.Add(_tile);
        Children.Add(_image);
        SizeChanged += (_, e) =>
        {
            // Bigger views (the Connections header) get a bigger icon in the same proportion.
            _image.Width = _image.Height = Math.Round(e.NewSize.Width * 0.8);
            _glyph.FontSize = Math.Round(e.NewSize.Width * 0.47);
        };
    }

    private async void Load()
    {
        var v = ++_version;
        var icon = AppIcons.For(Icon);
        if (!icon.IsCompleted)
        {
            // Loading for the first time: an empty tile, so the symbol doesn't flash before the icon.
            _image.Source = null;
            _glyph.Glyph = "";
            _tile.Visibility = Visibility.Visible;
        }
        var source = await icon;
        if (v != _version) return;
        _image.Source = source;
        _glyph.Glyph = source is null ? Glyph : "";
        _tile.Visibility = source is null ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>A row of mutually exclusive choices in one pill, like Day / Week / Month.</summary>
public sealed class Segmented : Grid
{
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly List<Button> _buttons = [];
    private int _selected = -1;

    public event Action<int>? SelectionChanged;

    public Segmented()
    {
        HorizontalAlignment = HorizontalAlignment.Left;
        Children.Add(new Border
        {
            Padding = new Thickness(3),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeBrush"],
            Background = (Brush)Application.Current.Resources["RowBrush"],
            Child = _row,
        });
    }

    public void SetItems(params string[] labels)
    {
        _row.Children.Clear();
        _buttons.Clear();
        for (var i = 0; i < labels.Length; i++)
        {
            var index = i;
            var b = new Button
            {
                Content = labels[i],
                Padding = new Thickness(12, 4, 12, 5),
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                MinWidth = 0,
            };
            AutomationProperties.SetName(b, labels[i]);
            b.Click += (_, _) => Select(index, raise: true);
            _buttons.Add(b);
            _row.Children.Add(b);
        }
        Paint();
    }

    public int SelectedIndex
    {
        get => _selected;
        set => Select(value, raise: false);
    }

    private void Select(int i, bool raise)
    {
        if (i == _selected) return;
        _selected = i;
        Paint();
        if (raise) SelectionChanged?.Invoke(i);
    }

    private void Paint()
    {
        for (var i = 0; i < _buttons.Count; i++)
        {
            var on = i == _selected;
            var b = _buttons[i];
            b.Background = on ? (Brush)Application.Current.Resources["SelectedFillBrush"] : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            b.BorderBrush = on ? (Brush)Application.Current.Resources["CardStrokeBrush"] : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            b.Foreground = on ? (Brush)Application.Current.Resources["TextBrush"] : (Brush)Application.Current.Resources["TextSecondaryBrush"];
            b.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }
}

/// <summary>Day / week / month, with previous and next. Starts on the period given.</summary>
public sealed class PeriodPicker : StackPanel
{
    private readonly Segmented _kind = new();
    private readonly Button _prev, _next;
    private DateTime _anchor = DateTime.Today;

    public Periods.Kind Kind { get; private set; }
    public long From { get; private set; }
    public long To { get; private set; }
    public string Label { get; private set; } = "";
    public event Action? Changed;

    public PeriodPicker() : this(Periods.Kind.Day) { }

    public PeriodPicker(Periods.Kind start)
    {
        Orientation = Orientation.Horizontal;
        Spacing = 6;
        VerticalAlignment = VerticalAlignment.Top;
        _prev = Arrow("", "Previous");
        _next = Arrow("", "Next");
        _prev.Click += (_, _) => { _anchor = Periods.Step(Kind, _anchor, -1); Update(); };
        _next.Click += (_, _) => { _anchor = Periods.Step(Kind, _anchor, 1); Update(); };
        _kind.SetItems("Day", "Week", "Month");
        _kind.SelectionChanged += i => { Kind = (Periods.Kind)i; _anchor = DateTime.Today; Update(); };
        Children.Add(_prev);
        Children.Add(_kind);
        Children.Add(_next);
        Kind = start;
        _kind.SelectedIndex = (int)start;
        Update(raise: false);
        // Pages live as long as the app, so this subscription does too.
        Periods.Changed += () => Update();
    }

    private static Button Arrow(string glyph, string name)
    {
        var b = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 11 },
            Padding = new Thickness(9, 9, 9, 9),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(b, name);
        ToolTipService.SetToolTip(b, name);
        return b;
    }

    private void Update(bool raise = true)
    {
        (From, To, Label) = Periods.Range(Kind, _anchor);
        _next.IsEnabled = Periods.Range(Kind, Periods.Step(Kind, _anchor, 1)).From <= DateTimeOffset.Now.ToUnixTimeSeconds();
        if (raise) Changed?.Invoke();
    }
}

/// <summary>
/// The network every page counts (see <see cref="Networks"/>): all of them, or one. It sits in the floating bar, so the
/// choice is made once, for every page, and is always in sight. <see cref="ShownChanged"/> says when there are two
/// networks to tell apart, the only time it's needed.
/// </summary>
public sealed class NetworkSwitch : Button
{
    private readonly FontIcon _icon = new() { FontSize = 15 };
    private readonly TextBlock _name = new() { MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly MenuFlyout _menu = new() { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top };

    /// <summary>Raised with true when the switch is worth showing, false when it isn't.</summary>
    public event Action<bool>? ShownChanged;

    public NetworkSwitch()
    {
        Style = (Style)Application.Current.Resources["TabButton"];
        Height = 46;
        Padding = new Thickness(16, 0, 14, 0);
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 9,
            Children = { _icon, _name, new FontIcon { Glyph = "\uE70E", FontSize = 10, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center } },
        };
        Flyout = _menu;
        // The window lives as long as the app, so these subscriptions do too.
        Networks.ListChanged += Fill;
        Networks.Changed += ShowSelected;
        Fill();
    }

    private void Fill()
    {
        _menu.Items.Clear();
        _menu.Items.Add(Item(null, "All networks", "\uE81E", null));
        _menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var n in Networks.Known) _menu.Items.Add(Item(n.Id, NetworkLabel.For(n), Glyph(n), n.Current ? "Now" : null));
        ShowSelected();
        ShownChanged?.Invoke(Networks.Known.Count >= 2);
    }

    private static RadioMenuFlyoutItem Item(long? id, string text, string glyph, string? note)
    {
        var item = new RadioMenuFlyoutItem { Text = text, GroupName = "network", Icon = new FontIcon { Glyph = glyph }, Tag = id };
        if (note is not null) item.KeyboardAcceleratorTextOverride = note;
        item.Click += (_, _) => Networks.Select(id);
        return item;
    }

    private void ShowSelected()
    {
        var picked = Networks.Known.FirstOrDefault(n => n.Id == Networks.Selected);
        var label = picked is null ? "All networks" : NetworkLabel.For(picked);
        _name.Text = label;
        _icon.Glyph = picked is null ? "\uE81E" : Glyph(picked);
        foreach (var item in _menu.Items.OfType<RadioMenuFlyoutItem>()) item.IsChecked = (long?)item.Tag == Networks.Selected;
        AutomationProperties.SetName(this, $"Network counted: {label}");
        ToolTipService.SetToolTip(this, picked is null ? "Counting every network. Pick one to count only it." : $"Counting only {label}");
    }

    private static string Glyph(NetworkInfo n) => n.Id == NetworkLabel.Unknown ? "\uE9CE" : n.Kind switch
    {
        NetworkKinds.Wired => "\uE839",
        NetworkKinds.Mobile => "\uE8EA",
        _ => "\uE701",
    };
}

/// <summary>
/// Your own name for something the service keeps (a device, a network): a box, Save, and a button back to its own
/// name. Errors from the service are shown in the flyout.
/// </summary>
public static class RenameFlyout
{
    /// <param name="custom">The name you gave it, or "".</param>
    /// <param name="own">What it's called without your name, shown in the empty box.</param>
    /// <param name="save">Saves a name; "" goes back to its own.</param>
    /// <param name="saved">Runs once it's saved and the flyout has closed, to show the new name.</param>
    public static void Show(FrameworkElement at, string custom, string own, string resetLabel, int maxLength, Func<string, Task> save, Func<Task> saved)
    {
        var box = new TextBox { Header = "Name", Text = custom, PlaceholderText = own, MaxLength = maxLength, Width = 240 };
        var error = new TextBlock { Style = (Style)Application.Current.Resources["Caption"], TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var ok = new Button { Content = "Save", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var reset = new Button { Content = resetLabel, IsEnabled = custom.Length > 0 };
        var flyout = new Flyout
        {
            Content = new StackPanel
            {
                Spacing = 12, Width = 240,
                Children = { box, error, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { ok, reset } } },
            },
        };
        async Task Apply(string name)
        {
            ok.IsEnabled = reset.IsEnabled = false;
            try
            {
                await save(name);
                flyout.Hide();
                await saved();
            }
            catch (Exception ex) when (Service.IsOffline(ex) || ex is ServiceException)
            {
                error.Text = ex is ServiceException ? ex.Message : "The Glass-Rea service isn't answering. Try again in a moment.";
                error.Visibility = Visibility.Visible;
                ok.IsEnabled = true;
                reset.IsEnabled = custom.Length > 0;
            }
        }
        ok.Click += async (_, _) => await Apply(box.Text);
        reset.Click += async (_, _) => await Apply("");
        box.KeyDown += async (_, k) => { if (k.Key == Windows.System.VirtualKey.Enter) { k.Handled = true; await Apply(box.Text); } };
        flyout.Opened += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        flyout.ShowAt(at);
    }
}
