using GlassRea.App.Support;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace GlassRea.App.Controls;

/// <summary>
/// A chart's hover readout, made of the same glass as the tab bar: it blurs the chart under it, glides after the
/// pointer and fades in and out. Lives in its own layer above the chart so the chart stays the thing it blurs.
/// </summary>
internal sealed class GlassTip
{
    /// <summary>Put this above the chart, in the same cell.</summary>
    public Canvas Layer { get; } = new() { IsHitTestVisible = false };

    private readonly Glass _glass;
    private readonly TextBlock _title = new() { Style = (Style)Application.Current.Resources["Caption"] };
    private readonly TextBlock _down = Value(), _up = Value();
    private Point _at, _target;
    private bool _shown, _gliding;

    /// <param name="blurSource">The chart; the glass shows it blurred.</param>
    /// <param name="inline">One line (time, download, upload) for the short live strip; otherwise the title sits on top.</param>
    public GlassTip(UIElement blurSource, bool inline)
    {
        var panel = new StackPanel { Orientation = inline ? Orientation.Horizontal : Orientation.Vertical, Spacing = inline ? 14 : 3 };
        panel.Children.Add(_title);
        panel.Children.Add(Row("AccentBrush", _down));
        panel.Children.Add(Row("UpBrush", _up));
        _title.VerticalAlignment = VerticalAlignment.Center;
        _glass = new Glass
        {
            BlurSource = blurSource, CornerRadius = new CornerRadius(inline ? 13 : 12),
            Padding = inline ? new Thickness(12, 5, 12, 5) : new Thickness(12, 8, 14, 9),
            IsHitTestVisible = false, Opacity = 0, Content = panel, SmoothWidth = true,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(180) },
        };
        Layer.Children.Add(_glass);
    }

    private static TextBlock Value() => new() { FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };

    private static StackPanel Row(string brush, TextBlock text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        row.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = (Brush)Application.Current.Resources[brush], VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(text);
        return row;
    }

    public void Set(string title, string down, string up)
    {
        if (_title.Text != title) _title.Text = title;
        if (_down.Text != down) _down.Text = down;
        if (_up.Text != up) _up.Text = up;
    }

    public Size Size
    {
        get
        {
            _glass.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return _glass.DesiredSize;
        }
    }

    /// <summary>Shows the tip with its top-left corner heading for <paramref name="topLeft"/>.</summary>
    public void Show(Point topLeft)
    {
        _target = topLeft;
        if (!_shown || !Motion.Enabled)
        {
            // Appear in place; only later moves glide.
            _shown = true;
            _at = topLeft;
            Place();
            _glass.Opacity = 1;
            return;
        }
        if (_gliding) return;
        _gliding = true;
        CompositionTarget.Rendering += Glide;
    }

    public void Hide()
    {
        _shown = false;
        _glass.Opacity = 0;
    }

    private long _lastFrame;

    private void Glide(object? sender, object e)
    {
        var now = Environment.TickCount64;
        var dt = _lastFrame == 0 ? 16 : Math.Min(50, now - _lastFrame);
        _lastFrame = now;
        // Exponential approach with a 70 ms time constant: quick to follow, never a jump.
        var k = 1 - Math.Exp(-dt / 70.0);
        _at = new Point(_at.X + (_target.X - _at.X) * k, _at.Y + (_target.Y - _at.Y) * k);
        if (Math.Abs(_target.X - _at.X) < 0.3 && Math.Abs(_target.Y - _at.Y) < 0.3)
        {
            _at = _target;
            _gliding = false;
            _lastFrame = 0;
            CompositionTarget.Rendering -= Glide;
        }
        Place();
    }

    private void Place()
    {
        Canvas.SetLeft(_glass, Math.Round(_at.X));
        Canvas.SetTop(_glass, Math.Round(_at.Y));
    }
}
