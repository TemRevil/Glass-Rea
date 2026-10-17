using GlassRea.App.Support;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GlassRea.App.Controls;

/// <summary>A program's icon; until it loads (or when there is none) a tile with the program's first letter.</summary>
public sealed class AppIconView : Grid
{
    public static readonly DependencyProperty AppPathProperty = DependencyProperty.Register(nameof(AppPath), typeof(string), typeof(AppIconView),
        new PropertyMetadata(null, (d, _) => ((AppIconView)d).Load()));
    public static readonly DependencyProperty AppNameProperty = DependencyProperty.Register(nameof(AppName), typeof(string), typeof(AppIconView),
        new PropertyMetadata("", (d, _) => ((AppIconView)d).Load()));

    public string? AppPath { get => (string?)GetValue(AppPathProperty); set => SetValue(AppPathProperty, value); }
    public string AppName { get => (string)GetValue(AppNameProperty); set => SetValue(AppNameProperty, value); }

    private static readonly string[] Tints = ["#3E82F7", "#0F6CBD", "#1BA39C", "#7A5AF8", "#C2410C", "#2D7DD2", "#9333EA", "#0E7C66"];
    private readonly Border _tile = new() { CornerRadius = new CornerRadius(7) };
    private readonly TextBlock _letter = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold, FontSize = 12, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) };
    private readonly Image _image = new() { Width = 24, Height = 24 };
    private int _version;

    public AppIconView()
    {
        Width = 30; Height = 30;
        _tile.Child = _letter;
        Children.Add(_tile);
        Children.Add(_image);
    }

    private async void Load()
    {
        var v = ++_version;
        var name = string.IsNullOrWhiteSpace(AppName) ? "?" : AppName.Trim();
        _letter.Text = char.ToUpperInvariant(name[0]).ToString();
        _tile.Background = new SolidColorBrush(Appearance.Hex(Tints[name.Aggregate(7u, (h, ch) => h * 31 + char.ToLowerInvariant(ch)) % Tints.Length]));
        _tile.Visibility = Visibility.Visible;
        _image.Source = null;
        var icon = await AppIcons.For(AppPath);
        if (v != _version || icon is null) return;
        _image.Source = icon;
        _tile.Visibility = Visibility.Collapsed;
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
