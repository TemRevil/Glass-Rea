using System.Numerics;
using GlassRea.App.Support;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace GlassRea.App.Controls;

/// <summary>
/// The pill behind the selected tab. It slides to the new tab with a slight overshoot and squashes a little as it
/// travels. Windows' compositor moves and resizes it on its own thread, so a page being built at the same moment
/// can't make it stall halfway and then jump, which a XAML Width animation did.
/// </summary>
public sealed class TabLens
{
    private const float Squash = 0.9f;
    private static readonly TimeSpan Time = TimeSpan.FromMilliseconds(560);

    /// <summary>By half its time the pill is within a few pixels of its tab; what's left is the gentle settle.</summary>
    public static readonly TimeSpan Arrives = Time / 2;

    private readonly FrameworkElement _host;
    private readonly CompositionRoundedRectangleGeometry _shape;
    private readonly CompositionColorBrush _fill;
    private readonly CompositionLinearGradientBrush _rim;
    private readonly CompositionColorGradientStop[] _rimStops;
    private bool _placed;
    private (Vector2 At, Vector2 Size) _target;

    /// <param name="host">An empty element under the tabs, as big as the row of tabs; the pill is drawn into it.</param>
    public TabLens(FrameworkElement host)
    {
        _host = host;
        var c = ElementCompositionPreview.GetElementVisual(host).Compositor;
        _shape = c.CreateRoundedRectangleGeometry();
        _fill = c.CreateColorBrush();
        _rim = c.CreateLinearGradientBrush();
        _rim.StartPoint = Vector2.Zero;
        _rim.EndPoint = new Vector2(0, 1);
        _rimStops = [c.CreateColorGradientStop(0, default), c.CreateColorGradientStop(0.5f, default), c.CreateColorGradientStop(1, default)];
        foreach (var stop in _rimStops) _rim.ColorStops.Add(stop);
        var pill = c.CreateSpriteShape(_shape);
        pill.FillBrush = _fill;
        pill.StrokeBrush = _rim;
        pill.StrokeThickness = 1;
        var visual = c.CreateShapeVisual();
        visual.RelativeSizeAdjustment = Vector2.One;
        visual.Shapes.Add(pill);
        visual.Opacity = 0; // until it knows where the selected tab is
        ElementCompositionPreview.SetElementChildVisual(host, visual);
        Recolour();
        host.Loaded += (_, _) => { Recolour(); Appearance.Changed += Recolour; };
        host.Unloaded += (_, _) => Appearance.Changed -= Recolour;
    }

    /// <summary>The same fill and rim the XAML resources give the glass, so the pill follows light and dark.</summary>
    private void Recolour()
    {
        var res = Application.Current.Resources;
        if (res["InkBrush"] is SolidColorBrush ink) _fill.Color = ink.Color;
        if (res["GlassRimBrush"] is LinearGradientBrush rim)
            for (var i = 0; i < _rimStops.Length && i < rim.GradientStops.Count; i++) _rimStops[i].Color = rim.GradientStops[i].Color;
    }

    /// <summary>Moves the pill to a tab: <paramref name="x"/> and <paramref name="width"/> across the host, full tab height.</summary>
    public void MoveTo(float x, float width, float height, bool animate)
    {
        // The rim is stroked on the outline, so the outline sits half a pixel in, keeping the rim inside the tab like a border.
        var at = new Vector2(x + 0.5f, 0.5f);
        var size = new Vector2(Math.Max(1, width - 1), Math.Max(1, height - 1));
        if (_placed && _target == (at, size)) return; // already there or on its way; starting again would jerk it
        _target = (at, size);
        _shape.CornerRadius = new Vector2(size.Y / 2);
        var visual = ElementCompositionPreview.GetElementChildVisual(_host);
        if (!animate || !_placed || !Motion.Enabled)
        {
            foreach (var p in new[] { "Offset.X", "Offset.Y", "Size.X", "Size.Y" }) _shape.StopAnimation(p);
            _shape.Offset = at;
            _shape.Size = size;
            visual.Opacity = 1;
            _placed = true;
            return;
        }
        var c = _shape.Compositor;
        // The slide settles past its mark and back, like the BackEase (amplitude 0.32) it replaces.
        var gel = c.CreateCubicBezierEasingFunction(new Vector2(0.34f, 1.3f), new Vector2(0.64f, 1f));
        _shape.StartAnimation("Offset.X", To(c, at.X, gel));
        _shape.StartAnimation("Size.X", To(c, size.X, gel));
        // Height dips while it travels and springs back, centred on the tab.
        var dip = size.Y * Squash;
        _shape.StartAnimation("Size.Y", Dip(c, size.Y, dip));
        _shape.StartAnimation("Offset.Y", Dip(c, at.Y, at.Y + (size.Y - dip) / 2));
    }

    private static ScalarKeyFrameAnimation To(Compositor c, float value, CompositionEasingFunction ease)
    {
        var a = c.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(1, value, ease);
        a.Duration = Time;
        return a;
    }

    private static ScalarKeyFrameAnimation Dip(Compositor c, float rest, float mid)
    {
        var a = c.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0.3f, mid, c.CreateCubicBezierEasingFunction(new Vector2(0.37f, 0f), new Vector2(0.63f, 1f)));
        a.InsertKeyFrame(1, rest, c.CreateCubicBezierEasingFunction(new Vector2(0.34f, 1.4f), new Vector2(0.64f, 1f)));
        a.Duration = Time;
        return a;
    }
}
