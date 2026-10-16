using System.Numerics;
using GlassRea.App.Support;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace GlassRea.App.Controls;

internal static class Ink
{
    public static Color Brush(string key) => Application.Current.Resources[key] is SolidColorBrush b ? b.Color : Microsoft.UI.Colors.Gray;
    public static Color Text(FrameworkElement e, double alpha) =>
        Appearance.WithAlpha(e.ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black, alpha);
}

public sealed record Bar(string Label, long Down, long Up, string Tip);

/// <summary>
/// Stacked bars: download in the accent, upload in its partner on top. New data grows in from the baseline,
/// the bar under the pointer stays bright while the others dim, and its figures show as a tooltip.
/// </summary>
public sealed class BarChart : Grid
{
    private readonly CanvasControl _canvas = new();
    private IReadOnlyList<Bar> _bars = [];
    private int _hover = -1;
    private double _grow = 99;
    private readonly ToolTip _tip = new();

    public BarChart()
    {
        Children.Add(_canvas);
        ToolTipService.SetToolTip(_canvas, _tip);
        ToolTipService.SetPlacement(_canvas, Microsoft.UI.Xaml.Controls.Primitives.PlacementMode.Mouse);
        _canvas.Draw += OnDraw;
        _canvas.PointerMoved += (_, e) =>
        {
            if (_bars.Count == 0) return;
            var i = (int)(e.GetCurrentPoint(_canvas).Position.X / (_canvas.ActualWidth / _bars.Count));
            i = Math.Clamp(i, 0, _bars.Count - 1);
            if (i == _hover) return;
            _hover = i;
            _tip.Content = _bars[i].Tip;
            _canvas.Invalidate();
        };
        _canvas.PointerExited += (_, _) => { _hover = -1; _tip.IsOpen = false; _canvas.Invalidate(); };
        Loaded += (_, _) => Appearance.Changed += Redraw;
        // Pages are kept and shown again, so the canvas must survive being unloaded (no RemoveFromVisualTree here).
        Unloaded += (_, _) => Appearance.Changed -= Redraw;
        ActualThemeChanged += (_, _) => _canvas.Invalidate();
    }

    private void Redraw() => _canvas.Invalidate();

    /// <param name="animate">Grow the bars in (first load, or a different period); a plain refresh just redraws.</param>
    public void SetBars(IReadOnlyList<Bar> bars, bool animate)
    {
        _bars = bars;
        if (animate && Motion.Enabled)
        {
            _grow = 0;
            Motion.Run(DispatcherQueue, TimeSpan.FromMilliseconds(900 + _bars.Count * 22), p =>
            {
                _grow = p * (900 + _bars.Count * 22) / 900.0;
                _canvas.Invalidate();
            });
        }
        _canvas.Invalidate();
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        if (_bars.Count == 0) return;
        const float labelH = 20;
        float w = (float)sender.ActualWidth, h = (float)sender.ActualHeight - labelH;
        var max = Math.Max(1, _bars.Max(b => b.Down + b.Up));
        var slot = w / _bars.Count;
        var bw = Math.Max(2, Math.Min(30, slot * 0.66f));
        var down = Ink.Brush("AccentBrush");
        var up = Ink.Brush("UpBrush");
        var grid = Ink.Text(this, 0.07);
        for (var k = 1; k <= 3; k++) ds.DrawLine(0, h * k / 4f, w, h * k / 4f, grid, 1);
        ds.DrawLine(0, h + 0.5f, w, h + 0.5f, Ink.Text(this, 0.12), 1);
        using var fmt = new CanvasTextFormat { FontSize = 11, FontFamily = "Segoe UI Variable Text", HorizontalAlignment = CanvasHorizontalAlignment.Center };
        var every = Math.Max(1, (int)Math.Ceiling(_bars.Count / (w / 48)));
        var label = Ink.Text(this, 0.5);
        for (var i = 0; i < _bars.Count; i++)
        {
            var b = _bars[i];
            // Bars grow one after another, 22 ms apart, each easing out.
            var local = Math.Clamp((_grow * 900 - i * 22) / 700.0, 0, 1);
            var g = (float)(1 - Math.Pow(1 - local, 4));
            var x = i * slot + (slot - bw) / 2;
            var dh = h * b.Down / max * g;
            var uh = h * b.Up / max * g;
            var dim = _hover >= 0 && _hover != i ? 0.45f : 1f;
            if (dh > 0.5f) ds.FillRoundedRectangle(x, h - dh, bw, dh, 2.5f, 2.5f, Fade(down, dim));
            if (uh > 0.5f) ds.FillRoundedRectangle(x, h - dh - uh - (dh > 0.5f ? 1.5f : 0), bw, uh, 2.5f, 2.5f, Fade(up, dim));
            if (i % every == 0) ds.DrawText(b.Label, i * slot + slot / 2, h + 4, label, fmt);
        }
    }

    private static Color Fade(Color c, float a) => Color.FromArgb((byte)(c.A * a), c.R, c.G, c.B);
}

/// <summary>
/// The live strip: download as a filled line, upload as a thinner line, the last two minutes, scrolling smoothly
/// between the once-a-second samples.
/// </summary>
public sealed class LiveGraph : Grid
{
    private readonly CanvasControl _canvas = new();
    private long[] _in = [], _out = [];
    private long _sampleAt;
    private double _max = 1024;

    public LiveGraph()
    {
        Children.Add(_canvas);
        IsHitTestVisible = false;
        _canvas.Draw += OnDraw;
        Loaded += (_, _) => CompositionTarget.Rendering += OnFrame;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
    }

    public void SetData(long[] inbound, long[] outbound)
    {
        _in = inbound;
        _out = outbound;
        _sampleAt = Environment.TickCount64;
        var peak = Math.Max(1024, Math.Max(inbound.DefaultIfEmpty().Max(), outbound.DefaultIfEmpty().Max())) * 1.2;
        _max = peak > _max ? peak : _max + (peak - _max) * 0.25; // the scale eases instead of jumping
    }

    private void OnFrame(object? sender, object e)
    {
        if (Visibility == Visibility.Visible && XamlRoot is not null) _canvas.Invalidate();
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_in.Length < 2) return;
        var ds = args.DrawingSession;
        float w = (float)sender.ActualWidth, h = (float)sender.ActualHeight;
        var n = _in.Length;
        var dx = w / (n - 2);
        // Shift left continuously over the second until the next sample lands.
        var shift = Motion.Enabled ? (float)Math.Min(1, (Environment.TickCount64 - _sampleAt) / 1000.0) * dx : 0;
        var accent = Ink.Brush("AccentBrush");
        var up = Ink.Brush("UpBrush");

        using var area = Path(sender, _in, dx, shift, h, close: true);
        using var fill = new CanvasLinearGradientBrush(sender, Appearance.WithAlpha(accent, 0.35), Appearance.WithAlpha(accent, 0))
        { StartPoint = new Vector2(0, 0), EndPoint = new Vector2(0, h) };
        ds.FillGeometry(area, fill);
        using var line = Path(sender, _in, dx, shift, h, close: false);
        var style = new CanvasStrokeStyle { LineJoin = CanvasLineJoin.Round };
        ds.DrawGeometry(line, accent, 1.6f, style);
        using var upLine = Path(sender, _out, dx, shift, h, close: false);
        ds.DrawGeometry(upLine, up, 1.4f, style);
    }

    private CanvasGeometry Path(ICanvasResourceCreator rc, long[] data, float dx, float shift, float h, bool close)
    {
        using var pb = new CanvasPathBuilder(rc);
        float Y(int i) => h - (float)(h * 0.86 * data[i] / _max) - 1;
        pb.BeginFigure(-shift, close ? h : Y(0));
        if (close) pb.AddLine(-shift, Y(0));
        for (var i = 1; i < data.Length; i++) pb.AddLine(i * dx - shift, Y(i));
        if (close) { pb.AddLine((data.Length - 1) * dx - shift, h); pb.EndFigure(CanvasFigureLoop.Closed); }
        else pb.EndFigure(CanvasFigureLoop.Open);
        return CanvasGeometry.CreatePath(pb);
    }
}

/// <summary>A thin proportional bar in list rows: an app's share of the total. Hatched grey for a VPN tunnel.</summary>
public sealed class ShareBar : Grid
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(ShareBar),
        new PropertyMetadata(0.0, (d, _) => ((ShareBar)d).Layout()));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    private readonly Microsoft.UI.Xaml.Shapes.Rectangle _fill = new() { RadiusX = 1.5, RadiusY = 1.5, HorizontalAlignment = HorizontalAlignment.Left };

    public ShareBar()
    {
        Height = 3;
        CornerRadius = new CornerRadius(1.5);
        Background = (Brush)Application.Current.Resources["CardStrokeBrush"];
        _fill.Fill = (Brush)Application.Current.Resources["AccentBrush"];
        Children.Add(_fill);
        SizeChanged += (_, _) => Layout();
    }

    private void Layout() => _fill.Width = Math.Max(Value > 0 ? 2 : 0, ActualWidth * Math.Clamp(Value, 0, 1));
}
