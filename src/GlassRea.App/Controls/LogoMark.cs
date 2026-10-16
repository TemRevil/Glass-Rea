using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace GlassRea.App.Controls;

/// <summary>
/// The Twin flow mark, drawn as vectors: a tile in the accent gradient holding a down arrow and a shorter up
/// arrow, like a meter caught mid-reading. Follows the chosen palette.
/// </summary>
public sealed class LogoMark : Grid
{
    public LogoMark()
    {
        var canvas = new Canvas { Width = 64, Height = 64 };
        var tile = new Rectangle { Width = 54, Height = 54, RadiusX = 15, RadiusY = 15, Fill = (Brush)Application.Current.Resources["LogoBrush"] };
        Canvas.SetLeft(tile, 5); Canvas.SetTop(tile, 5);
        var sheen = new Rectangle
        {
            Width = 54, Height = 54, RadiusX = 15, RadiusY = 15,
            Fill = new LinearGradientBrush
            {
                StartPoint = new(0, 0), EndPoint = new(0, 1),
                GradientStops = { new GradientStop { Offset = 0, Color = ColorHelper.FromArgb(140, 255, 255, 255) }, new GradientStop { Offset = 0.5, Color = Colors.Transparent } },
            },
        };
        Canvas.SetLeft(sheen, 5); Canvas.SetTop(sheen, 5);
        canvas.Children.Add(tile);
        canvas.Children.Add(sheen);
        canvas.Children.Add(Arrow("M24 15 V45 M15.5 37 L24 45.5 L32.5 37", 1));
        canvas.Children.Add(Arrow("M41 49 V27 M33 34.5 L41 26.5 L49 34.5", 0.72));
        Children.Add(new Viewbox { Child = canvas });
    }

    private static Microsoft.UI.Xaml.Shapes.Path Arrow(string data, double opacity) => new()
    {
        Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data),
        Stroke = new SolidColorBrush(Colors.White),
        StrokeThickness = 5.5,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        Opacity = opacity,
    };
}
