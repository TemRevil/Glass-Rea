using System.Numerics;
using GlassRea.App.Support;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace GlassRea.App.Controls;

internal static class Ink
{
    public static Color Brush(string key) => Application.Current.Resources[key] is SolidColorBrush b ? b.Color : Microsoft.UI.Colors.Gray;
    public static Color Text(FrameworkElement e, double alpha) =>
        Appearance.WithAlpha(e.ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black, alpha);
}

/// <param name="Title">What the bar covers, in words, for the hover readout ("14:00 to 14:59", "Saturday 17 Oct").</param>
public sealed record Bar(string Label, long Down, long Up, string Title);

/// <summary>
/// Stacked bars: download in the accent, upload in its partner on top. New data grows in from the baseline,
/// the bar under the pointer stays bright while the others dim, and its figures show in a glass readout.
/// </summary>
public sealed class BarChart : Grid
{
    private readonly CanvasControl _canvas = new();
    private IReadOnlyList<Bar> _bars = [];
    private int _hover = -1;
    private double _grow = 99;
    private readonly GlassTip _tip;

    public BarChart()
    {
        Children.Add(_canvas);
        _tip = new GlassTip(_canvas, inline: false);
        Children.Add(_tip.Layer);
        _canvas.Draw += OnDraw;
        // The canvas itself isn't hit-testable; a transparent background makes the whole chart answer the pointer.
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        PointerMoved += (_, e) =>
        {
            if (_bars.Count == 0) return;
            var p = e.GetCurrentPoint(_canvas).Position;
            var i = Math.Clamp((int)(p.X / (_canvas.ActualWidth / _bars.Count)), 0, _bars.Count - 1);
            var b = _bars[i];
            _tip.Set(b.Title, $"Download {Fmt.Bytes(b.Down)}", $"Upload {Fmt.Bytes(b.Up)}");
            // Centred above the pointer; below it when there's no room above.
            var size = _tip.Size;
            var x = Math.Clamp(p.X - size.Width / 2, 0, Math.Max(0, ActualWidth - size.Width));
            var y = p.Y - size.Height - 14;
            if (y < 0) y = p.Y + 20;
            _tip.Show(new Windows.Foundation.Point(x, y));
            if (i == _hover) return;
            _hover = i;
            _canvas.Invalidate();
        };
        PointerExited += (_, _) => { _hover = -1; _tip.Hide(); _canvas.Invalidate(); };
        Loaded += (_, _) => { Appearance.Changed += Redraw; if (_grow < GrowEnd) Grow(true); };
        // Pages are kept and shown again, so the canvas must survive being unloaded (no RemoveFromVisualTree here).
        Unloaded += (_, _) => { Appearance.Changed -= Redraw; Grow(false); };
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
            if (IsLoaded) Grow(true);
        }
        else if (!Motion.Enabled) _grow = GrowEnd;
        _canvas.Invalidate();
    }

    // ---------- growing in ----------

    /// <summary>Where the growth ends: the last bar starts 22 ms after the one before it and takes 700 ms.</summary>
    private double GrowEnd => (900 + _bars.Count * 22) / 900.0;
    private bool _growing;
    private long _lastFrame;

    private void Grow(bool on)
    {
        if (on == _growing) return;
        _growing = on;
        _lastFrame = 0;
        if (on) CompositionTarget.Rendering += OnGrowFrame;
        else CompositionTarget.Rendering -= OnGrowFrame;
    }

    /// <summary>
    /// The growth advances by the time between frames the chart is actually drawn in, at most two frames' worth, so it
    /// only starts once the chart is on screen, and a busy moment pauses it rather than skipping to full bars.
    /// </summary>
    private void OnGrowFrame(object? sender, object e)
    {
        if (XamlRoot is null || ActualWidth == 0) { _lastFrame = 0; return; }
        var now = Environment.TickCount64;
        if (_lastFrame != 0) _grow = Math.Min(GrowEnd, _grow + Math.Min(34, now - _lastFrame) / 900.0);
        _lastFrame = now;
        _canvas.Invalidate();
        if (_grow >= GrowEnd) Grow(false);
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
/// between the once-a-second samples. Hovering shows a guide line, the two values where it crosses, and a glass
/// readout of that second.
/// The graph is drawn once per sample, one step wider than the strip, and the compositor slides it left by that step
/// over the second, on its own thread. Redrawing it every frame kept a third of a CPU core busy.
/// </summary>
public sealed class LiveGraph : Grid
{
    private readonly Canvas _track = new();
    private readonly CanvasControl _canvas = new();
    private readonly Microsoft.UI.Composition.Visual _slide;
    private readonly GlassTip _tip;
    private readonly RectangleGeometry _clip = new();
    private CanvasStrokeStyle? _round;
    private long[] _in = [], _out = [];
    private long _sampleAt, _lastSecond;
    private double _max = 1024;
    private double? _hoverX;
    private bool _newSample;

    /// <summary>Something floating over the right of the strip (the rate chip) that the readout should keep left of.</summary>
    public FrameworkElement? TipAvoid { get; set; }

    public LiveGraph()
    {
        // A Canvas, so layout doesn't cut the wider graph to the strip's width; the strip's own clip trims it instead.
        _track.Children.Add(_canvas);
        Children.Add(_track);
        _tip = new GlassTip(_track, inline: true);
        Children.Add(_tip.Layer);
        Clip = _clip;
        _canvas.Draw += OnDraw;
        ElementCompositionPreview.SetIsTranslationEnabled(_canvas, true);
        _slide = ElementCompositionPreview.GetElementVisual(_canvas);
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        PointerMoved += (_, e) => { _hoverX = e.GetCurrentPoint(this).Position.X; _canvas.Invalidate(); };
        PointerExited += (_, _) => { _hoverX = null; _tip.Hide(); _canvas.Invalidate(); };
        SizeChanged += (_, _) => { _clip.Rect = new Windows.Foundation.Rect(0, 0, ActualWidth, ActualHeight); Fit(); };
        ActualThemeChanged += (_, _) => _canvas.Invalidate();
        Loaded += (_, _) => Appearance.Changed += Redraw;
        Unloaded += (_, _) => Appearance.Changed -= Redraw;
    }

    private void Redraw() => _canvas.Invalidate();

    /// <summary>One second's width. The newest second is still filling, so it waits just past the right edge.</summary>
    private float Step => _in.Length > 2 ? (float)ActualWidth / (_in.Length - 2) : 0;

    private void Fit()
    {
        var width = ActualWidth + Step;
        if (double.IsNaN(_canvas.Width) || Math.Abs(_canvas.Width - width) > 0.5) _canvas.Width = width;
        if (double.IsNaN(_canvas.Height) || Math.Abs(_canvas.Height - ActualHeight) > 0.5) _canvas.Height = ActualHeight;
        _canvas.Invalidate();
    }

    /// <param name="lastSecond">Unix second of the newest sample.</param>
    public void SetData(long[] inbound, long[] outbound, long lastSecond)
    {
        var resized = inbound.Length != _in.Length;
        _in = inbound;
        _out = outbound;
        _lastSecond = lastSecond;
        var peak = Math.Max(1024, Math.Max(inbound.DefaultIfEmpty().Max(), outbound.DefaultIfEmpty().Max())) * 1.2;
        _max = peak > _max ? peak : _max + (peak - _max) * 0.25; // the scale eases instead of jumping
        _newSample = true;
        if (resized) Fit();
        else _canvas.Invalidate();
    }

    /// <summary>How far the graph has slid since its sample landed, 0 to one step.</summary>
    private float Shift(float dx) => Motion.Enabled && _sampleAt > 0 ? (float)Math.Min(1, (Environment.TickCount64 - _sampleAt) / 1000.0) * dx : 0;

    /// <summary>
    /// Slides the freshly drawn graph one step left over the next second. Started from the draw itself, so the new
    /// picture and the jump back to the start reach the screen in the same frame.
    /// </summary>
    private void Slide(float dx)
    {
        _sampleAt = Environment.TickCount64;
        _slide.StopAnimation("Translation");
        _slide.Properties.InsertVector3("Translation", Vector3.Zero);
        if (!Motion.Enabled) return;
        var c = _slide.Compositor;
        var a = c.CreateVector3KeyFrameAnimation();
        a.InsertKeyFrame(0, Vector3.Zero);
        // Whole screen pixels at a time: the graph moves about ten a second, and gliding through fractions of a pixel
        // only made the compositor draw every frame (a quarter of a core) for a change nobody can see.
        var steps = c.CreateStepEasingFunction(Math.Max(1, (int)Math.Round(dx * (XamlRoot?.RasterizationScale ?? 1))));
        steps.IsFinalStepSingleFrame = false;
        a.InsertKeyFrame(1, new Vector3(-dx, 0, 0), steps);
        a.Duration = TimeSpan.FromSeconds(1);
        _slide.StartAnimation("Translation", a);
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_in.Length < 2) return;
        var ds = args.DrawingSession;
        float h = (float)sender.ActualHeight;
        var dx = Step;
        var accent = Ink.Brush("AccentBrush");
        var up = Ink.Brush("UpBrush");
        _round ??= new CanvasStrokeStyle { LineJoin = CanvasLineJoin.Round };

        using var area = Path(sender, _in, dx, h, close: true);
        using var fill = new CanvasLinearGradientBrush(sender, Appearance.WithAlpha(accent, 0.35), Appearance.WithAlpha(accent, 0))
        { StartPoint = new Vector2(0, 0), EndPoint = new Vector2(0, h) };
        ds.FillGeometry(area, fill);
        using var line = Path(sender, _in, dx, h, close: false);
        ds.DrawGeometry(line, accent, 1.6f, _round);
        using var upLine = Path(sender, _out, dx, h, close: false);
        ds.DrawGeometry(upLine, up, 1.4f, _round);
        if (_newSample) { _newSample = false; Slide(dx); }
        if (_hoverX is { } hx) DrawHover(ds, (float)hx, dx, Shift(dx), h, accent, up);
    }

    /// <param name="x">The pointer, across the strip.</param>
    /// <param name="shift">How far the graph has slid, so the pointer is over the graph at <paramref name="x"/> plus this.</param>
    private void DrawHover(CanvasDrawingSession ds, float x, float dx, float shift, float h, Color accent, Color up)
    {
        var n = _in.Length;
        var gx = x + shift;
        var fi = Math.Clamp(gx / dx, 0, n - 1);
        var i0 = (int)Math.Floor(fi);
        var i1 = Math.Min(n - 1, i0 + 1);
        var f = fi - i0;
        float Y(long[] data) => h - (float)(h * 0.86 * (data[i0] + (data[i1] - data[i0]) * f) / _max) - 1;
        ds.DrawLine(gx, 0, gx, h, Ink.Text(this, 0.22), 1);
        var ring = Ink.Text(this, 0.85);
        foreach (var (y, c) in new[] { (Y(_in), accent), (Y(_out), up) })
        {
            ds.FillCircle(gx, y, 4.5f, ring);
            ds.FillCircle(gx, y, 3f, c);
        }

        // The readout names the second under the pointer.
        var k = (int)Math.Round(fi);
        var time = DateTimeOffset.FromUnixTimeSeconds(_lastSecond - (n - 1 - k)).ToLocalTime()
            .ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        _tip.Set(time, Fmt.Rate(_in[k]), Fmt.Rate(_out[k]));
        var size = _tip.Size;
        var right = ActualWidth - 4;
        if (TipAvoid is { ActualWidth: > 0 } avoid) right = Math.Min(right, avoid.TransformToVisual(this).TransformPoint(default).X - 8);
        var left = x + 14 + size.Width <= right ? x + 14 : x - 14 - size.Width;
        _tip.Show(new Windows.Foundation.Point(Math.Max(4, left), 6));
    }

    private CanvasGeometry Path(ICanvasResourceCreator rc, long[] data, float dx, float h, bool close)
    {
        using var pb = new CanvasPathBuilder(rc);
        float Y(int i) => h - (float)(h * 0.86 * data[i] / _max) - 1;
        pb.BeginFigure(0, close ? h : Y(0));
        if (close) pb.AddLine(0, Y(0));
        for (var i = 1; i < data.Length; i++) pb.AddLine(i * dx, Y(i));
        if (close) { pb.AddLine((data.Length - 1) * dx, h); pb.EndFigure(CanvasFigureLoop.Closed); }
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
