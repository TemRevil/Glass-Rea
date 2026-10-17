using System.Numerics;
using GlassRea.App.Support;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace GlassRea.App.Controls;

/// <summary>
/// Liquid Glass: the content behind is blurred and saturated, the bezel refracts it (the edge shows what sits a
/// little further in, worked out with Snell's law over a squircle-shaped rim, see <see cref="Core.Look.LiquidGlass"/>),
/// the rim catches a highlight top left and a glint bottom right, a small light follows the pointer, and pressing
/// sends a glow out from the touch point.
/// Windows' compositor can't displace pixels, so the refraction is built from <see cref="GlassLens.Levels"/> copies of
/// the blurred backdrop, each magnified so the edge samples one step further in, and each masked to the part of the
/// bezel whose computed shift reaches that step; neighbouring steps blend, so the bend reads as continuous. With
/// chromatic aberration on, each step is three copies, one per colour, each magnified by its own colour's shift and
/// added back together, so the rim fringes the way a prism does. Settings > Appearance > Glass sets the optics
/// (<see cref="Appearance.Optics"/>), the tint, and how round the corners are.
/// The blur samples a live copy of <see cref="BlurSource"/> (Windows' backdrop brush can't see through to the
/// window material and renders a pale wash instead). Used only for the navigation layer, never glass on glass.
/// </summary>
public sealed class Glass : ContentControl
{
    public static readonly DependencyProperty PressableProperty =
        DependencyProperty.Register(nameof(Pressable), typeof(bool), typeof(Glass), new PropertyMetadata(false));

    public static readonly DependencyProperty SmoothWidthProperty =
        DependencyProperty.Register(nameof(SmoothWidth), typeof(bool), typeof(Glass), new PropertyMetadata(false));

    /// <summary>Squeezes slightly when pressed, like a button made of gel.</summary>
    public bool Pressable { get => (bool)GetValue(PressableProperty); set => SetValue(PressableProperty, value); }

    /// <summary>
    /// Width follows the content smoothly instead of jumping: it grows at once (quickly), and shrinks only after the
    /// narrower content has held for a moment, so figures flickering between widths don't make it breathe.
    /// </summary>
    public bool SmoothWidth { get => (bool)GetValue(SmoothWidthProperty); set => SetValue(SmoothWidthProperty, value); }

    /// <summary>The element this glass floats over; what's under the glass is copied from it and blurred.</summary>
    public UIElement? BlurSource { get; set; }

    /// <summary>
    /// Bends the backdrop at the rim. Each refraction step blurs its own copy of what's behind, so it's for the tab
    /// bar, whose backdrop rarely moves; small glass over moving content (the rate chip over the live strip, the chart
    /// readouts) would redraw them all every frame for an edge too small to see. Set before the glass is shown.
    /// </summary>
    public bool Refracts { get; set; } = true;

    private Border? _backdrop, _rimHost;
    private Canvas? _glow;
    private Ellipse? _spot;
    private Visual? _spotVisual;
    private RadialGradientBrush? _spotBrush;
    private SpriteVisual? _sprite, _rim;
    private ContainerVisual? _layers;
    private CompositionEffectBrush? _brush;
    private CompositionRoundedRectangleGeometry? _shape;
    private CompositionVisualSurface? _surface, _blurred;
    private CompositionMaskBrush? _body, _wash;
    private CompositionColorBrush? _washColour;
    private CompositionEffectFactory? _split;
    private readonly List<Band> _bands = [];
    private (float Radius, float Scale, bool Dark, Core.Look.LiquidGlass Optics) _lensFor;
    private bool _capsule, _designKnown;
    private double _design;

    /// <summary>One refraction step: a copy of the blurred backdrop per colour, and the mask that places the step.</summary>
    private sealed record Band(SpriteVisual Sprite, CompositionSurfaceBrush Red, CompositionSurfaceBrush Green,
        CompositionSurfaceBrush Blue, CompositionMaskBrush Mask)
    {
        public CompositionEffectBrush? Split { get; set; }
    }

    public Glass()
    {
        Loaded += (_, _) =>
        {
            Build();
            OnAppearance(); // the look may have changed while this glass was off screen
            Appearance.Changed += OnAppearance;
            GlassLens.Lost += OnLensLost;
            LayoutUpdated += Track;
        };
        Unloaded += (_, _) =>
        {
            Appearance.Changed -= OnAppearance;
            GlassLens.Lost -= OnLensLost;
            LayoutUpdated -= Track;
            CompositionTarget.Rendering -= OnTweenFrame;
        };
        SizeChanged += (_, _) => Resize();
        PointerEntered += (_, _) => FadeSpot(true);
        PointerExited += (_, _) => FadeSpot(false);
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => Squeeze(false)), true);
        AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => Squeeze(false)), true);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (!_designKnown)
        {
            // The radius the layout asked for; Settings' corner radius is a share of it. A huge radius means "capsule".
            _designKnown = true;
            _design = CornerRadius.TopLeft;
            _capsule = _design >= 999;
        }
        _backdrop = GetTemplateChild("Backdrop") as Border;
        _rimHost = GetTemplateChild("Rim") as Border;
        _glow = GetTemplateChild("GlowLayer") as Canvas;
        BuildSpot();
        Build();
    }

    private void Build()
    {
        if (_backdrop is null || _sprite is not null || !IsLoaded || BlurSource is null) return;
        // Not this element's own visual: WinUI refuses it once an OpacityTransition is set (the chart readout fades).
        var c = CompositionTarget.GetCompositorForCurrentThread();
        var blur = new GaussianBlurEffect
        {
            Name = "Blur", BlurAmount = Appearance.GlassBlur, BorderMode = EffectBorderMode.Hard,
            Source = new CompositionEffectSourceParameter("behind"),
        };
        // The page is see-through (cards over the window material), so its blurred copy is too, and the sharp page
        // under the glass would show straight through it. The copy sits on the window material's colour instead.
        var based = new CompositeEffect { Mode = Microsoft.Graphics.Canvas.CanvasComposite.SourceOver, Sources = { new ColorSourceEffect { Name = "Base", Color = Appearance.WindowTint }, blur } };
        var saturate = new SaturationEffect { Saturation = 1.8f, Source = based };
        var tint = new TintEffect { Name = "Tint", Color = Appearance.GlassTint, Source = saturate };
        _brush = c.CreateEffectFactory(tint, ["Blur.BlurAmount", "Tint.Color", "Base.Color"]).CreateBrush();
        _surface = c.CreateVisualSurface();
        _surface.SourceVisual = ElementCompositionPreview.GetElementVisual(BlurSource);
        var copy = c.CreateSurfaceBrush(_surface);
        copy.Stretch = CompositionStretch.None;
        copy.HorizontalAlignmentRatio = 0;
        copy.VerticalAlignmentRatio = 0;
        _brush.SetSourceParameter("behind", copy);
        _shape = c.CreateRoundedRectangleGeometry();
        _sprite = c.CreateSpriteVisual();
        _sprite.Brush = _brush;
        _sprite.Clip = c.CreateGeometricClip(_shape);

        // The blurred backdrop is drawn once, off screen, and everything shown samples it: the body through the glass's
        // outline (which can fade in softly), and each refraction step magnified. So the blur runs once, not once per step.
        _blurred = c.CreateVisualSurface();
        _blurred.SourceVisual = _sprite;
        _layers = c.CreateContainerVisual();
        _body = c.CreateMaskBrush();
        _body.Source = Copy(c);
        Layer(c, _body);
        for (var level = 1; level <= (Refracts ? GlassLens.Levels : 0); level++)
        {
            var mask = c.CreateMaskBrush();
            var band = new Band(Layer(c, mask), Copy(c), Copy(c), Copy(c), mask);
            mask.Source = band.Green;
            _bands.Add(band);
        }
        // The milky wash over the glass, shaped by the same outline.
        _washColour = c.CreateColorBrush(WashColour());
        _wash = c.CreateMaskBrush();
        _wash.Source = _washColour;
        Layer(c, _wash);
        ElementCompositionPreview.SetElementChildVisual(_backdrop, _layers);

        if (_rimHost is not null)
        {
            _rim = c.CreateSpriteVisual();
            ElementCompositionPreview.SetElementChildVisual(_rimHost, _rim);
        }
        Resize();
        Track(null, null);
    }

    private SpriteVisual Layer(Compositor c, CompositionBrush brush)
    {
        var layer = c.CreateSpriteVisual();
        layer.Brush = brush;
        _layers!.Children.InsertAtTop(layer);
        return layer;
    }

    /// <summary>A copy of the blurred backdrop, pinned to the top left so magnifying it about the centre lines up.</summary>
    private CompositionSurfaceBrush Copy(Compositor c)
    {
        var copy = c.CreateSurfaceBrush(_blurred);
        copy.Stretch = CompositionStretch.None;
        copy.HorizontalAlignmentRatio = 0;
        copy.VerticalAlignmentRatio = 0;
        return copy;
    }

    private static Windows.UI.Color WashColour() =>
        Application.Current.Resources["GlassTintBrush"] is SolidColorBrush b ? b.Color : Microsoft.UI.Colors.Transparent;

    /// <summary>
    /// Adds a red-only, a green-only and a blue-only copy back together. The split works on the raw premultiplied
    /// values (straight alpha mode, so nothing is divided by alpha) and only the red copy keeps the alpha, so the sum
    /// is the glass's own colour and alpha again.
    /// </summary>
    private CompositionEffectBrush SplitBrush(Band band)
    {
        var c = band.Sprite.Compositor;
        _split ??= c.CreateEffectFactory(new ArithmeticCompositeEffect
        {
            MultiplyAmount = 0, Source1Amount = 1, Source2Amount = 1, Offset = 0, ClampOutput = true,
            Source1 = new ArithmeticCompositeEffect
            {
                MultiplyAmount = 0, Source1Amount = 1, Source2Amount = 1, Offset = 0, ClampOutput = true,
                Source1 = Only("r", 0), Source2 = Only("g", 1),
            },
            Source2 = Only("b", 2),
        });
        var brush = _split.CreateBrush();
        brush.SetSourceParameter("r", band.Red);
        brush.SetSourceParameter("g", band.Green);
        brush.SetSourceParameter("b", band.Blue);
        return brush;

        static ColorMatrixEffect Only(string source, int channel) => new()
        {
            Source = new CompositionEffectSourceParameter(source),
            AlphaMode = Microsoft.Graphics.Canvas.CanvasAlphaMode.Straight,
            ColorMatrix = new Matrix5x4
            {
                M11 = channel == 0 ? 1 : 0, M22 = channel == 1 ? 1 : 0, M33 = channel == 2 ? 1 : 0, M44 = channel == 0 ? 1 : 0,
            },
        };
    }

    /// <summary>Keeps the copied region exactly under the glass as things move.</summary>
    private void Track(object? sender, object? e)
    {
        if (_surface is null || BlurSource is null || ActualWidth == 0) return;
        var at = TransformToVisual(BlurSource).TransformPoint(default);
        _surface.SourceOffset = new Vector2((float)at.X, (float)at.Y);
        _surface.SourceSize = new Vector2((float)ActualWidth, (float)ActualHeight);
    }

    private void Resize()
    {
        if (ActualWidth == 0 || ActualHeight == 0) return;
        float w = (float)ActualWidth, h = (float)ActualHeight;
        // Settings' corner radius is a share of the design's; WinUI scales oversized radii into an ellipse, so a capsule
        // uses exactly half the height. Never under 2, where the glass would have no rim left to draw.
        var r = (float)Math.Max(2, Core.Look.LiquidGlass.Corner(_design, _capsule, w, h, Appearance.Roundness));
        if (_designKnown && Math.Abs(CornerRadius.TopLeft - r) > 0.01) CornerRadius = new CornerRadius(r);
        if (_sprite is null || _shape is null || _blurred is null || _layers is null) return;
        var size = new Vector2(w, h);
        _sprite.Size = size;
        _shape.Size = size;
        _blurred.SourceSize = size;
        _shape.CornerRadius = new Vector2(r, r);
        if (_rim is not null) _rim.Size = size;
        foreach (var layer in _layers.Children) layer.Size = size;
        var optics = Appearance.Optics;
        RefreshLens(r, optics);

        // Each step magnifies the backdrop about the centre so the edge samples its shift further in; with chromatic
        // aberration, each colour by its own shift.
        var centre = size / 2;
        var split = optics.Dispersion > 0;
        for (var i = 0; i < _bands.Count; i++)
        {
            var band = _bands[i];
            // With Windows' transparency effects off the glass is solid, and steps nobody can see still cost a blur each.
            band.Sprite.IsVisible = Appearance.EffectsEnabled;
            var (red, green, blue) = GlassLens.StepShift(optics, r, i + 1);
            Magnify(band.Green, green);
            if (split)
            {
                Magnify(band.Red, red);
                Magnify(band.Blue, blue);
                band.Split ??= SplitBrush(band);
            }
            band.Mask.Source = split ? band.Split : band.Green;
        }

        void Magnify(CompositionSurfaceBrush copy, float shift)
        {
            copy.CenterPoint = centre;
            copy.Scale = new Vector2(centre.X / Math.Max(1, centre.X - shift), centre.Y / Math.Max(1, centre.Y - shift));
        }
    }

    /// <summary>Hands out the lens images for the current radius, screen scale, theme and optics (drawn once, then shared).</summary>
    private void RefreshLens(float radius, Core.Look.LiquidGlass optics)
    {
        var scale = (float)(XamlRoot?.RasterizationScale ?? 1);
        var key = (MathF.Round(radius, 1), scale, Appearance.IsDark, optics);
        if (key == _lensFor || _sprite is null) return;
        _lensFor = key;
        var c = _sprite.Compositor;
        var outline = GlassLens.Body(c, optics, radius, scale);
        Release(_body?.Mask);
        if (_body is not null) _body.Mask = outline;
        if (_wash is not null) _wash.Mask = outline;
        for (var i = 0; i < _bands.Count; i++)
        {
            Release(_bands[i].Mask.Mask);
            _bands[i].Mask.Mask = GlassLens.Mask(c, optics, radius, scale, i + 1);
        }
        if (_rim is not null)
        {
            Release(_rim.Brush);
            _rim.Brush = GlassLens.Rim(c, optics, radius, scale, Appearance.IsDark);
        }

        // The brushes are this glass's own (the images under them are shared and freed by GlassLens).
        static void Release(CompositionBrush? brush)
        {
            if (brush is CompositionNineGridBrush grid) grid.Source?.Dispose();
            brush?.Dispose();
        }
    }

    private void OnLensLost() { _lensFor = default; Resize(); }

    private void OnAppearance()
    {
        _brush?.Properties.InsertScalar("Blur.BlurAmount", Appearance.GlassBlur);
        _brush?.Properties.InsertColor("Tint.Color", Appearance.GlassTint);
        _brush?.Properties.InsertColor("Base.Color", Appearance.WindowTint);
        if (_washColour is not null) _washColour.Color = WashColour();
        if (_spotBrush is not null) SpotColours(_spotBrush);
        Resize();
    }

    // ---------- the pointer light ----------

    private const double SpotSize = 56;

    /// <summary>
    /// A soft light about two cursors wide that sits under the pointer and fades in and out. The compositor moves it
    /// from the pointer's own position, on its own thread, so it keeps up with the mouse however busy the app is.
    /// </summary>
    private void BuildSpot()
    {
        if (_glow is null || _spot is not null) return;
        _spotBrush = new RadialGradientBrush();
        SpotColours(_spotBrush);
        _spot = new Ellipse { Width = SpotSize, Height = SpotSize, IsHitTestVisible = false, Fill = _spotBrush };
        _glow.Children.Add(_spot);
        _spotVisual = ElementCompositionPreview.GetElementVisual(_spot);
        _spotVisual.Opacity = 0;
        ElementCompositionPreview.SetIsTranslationEnabled(_spot, true);
        var follow = _spotVisual.Compositor.CreateExpressionAnimation("Vector3(pointer.Position.X - half, pointer.Position.Y - half, 0)");
        follow.SetReferenceParameter("pointer", ElementCompositionPreview.GetPointerPositionPropertySet(_glow));
        follow.SetScalarParameter("half", (float)SpotSize / 2);
        _spotVisual.StartAnimation("Translation", follow);
    }

    private void FadeSpot(bool on)
    {
        if (_spotVisual is null) return;
        var c = _spotVisual.Compositor;
        if (!Motion.Enabled) { _spotVisual.StopAnimation("Opacity"); _spotVisual.Opacity = on ? 1 : 0; return; }
        var a = c.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(1, on ? 1 : 0);
        a.Duration = TimeSpan.FromMilliseconds(on ? 200 : 320);
        _spotVisual.StartAnimation("Opacity", a);
    }

    private static void SpotColours(RadialGradientBrush b)
    {
        var peak = Appearance.IsDark ? 0.16 : 0.42;
        b.GradientStops.Clear();
        b.GradientStops.Add(new GradientStop { Offset = 0, Color = Appearance.WithAlpha(Microsoft.UI.Colors.White, peak) });
        b.GradientStops.Add(new GradientStop { Offset = 0.45, Color = Appearance.WithAlpha(Microsoft.UI.Colors.White, peak * 0.45) });
        b.GradientStops.Add(new GradientStop { Offset = 1, Color = Appearance.WithAlpha(Microsoft.UI.Colors.White, 0) });
    }


    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Squeeze(true);
        if (_glow is null || !Motion.Enabled) return;
        var p = e.GetCurrentPoint(_glow).Position;
        var dot = new Ellipse
        {
            Width = 12, Height = 12, IsHitTestVisible = false,
            Fill = (Brush)Application.Current.Resources["GlowBrush"],
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(),
        };
        Canvas.SetLeft(dot, p.X - 6);
        Canvas.SetTop(dot, p.Y - 6);
        _glow.Children.Add(dot);
        var sb = new Storyboard();
        var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 };
        foreach (var prop in new[] { "ScaleX", "ScaleY" })
        {
            var a = new DoubleAnimation { From = 1, To = 26, Duration = TimeSpan.FromMilliseconds(700), EasingFunction = ease };
            Storyboard.SetTarget(a, dot.RenderTransform);
            Storyboard.SetTargetProperty(a, prop);
            sb.Children.Add(a);
        }
        var fade = new DoubleAnimation { From = 0.9, To = 0, Duration = TimeSpan.FromMilliseconds(700), EasingFunction = ease };
        Storyboard.SetTarget(fade, dot);
        Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);
        sb.Completed += (_, _) => _glow.Children.Remove(dot);
        sb.Begin();
    }

    private void Squeeze(bool down)
    {
        if (!Pressable || !Motion.Enabled) return;
        var v = ElementCompositionPreview.GetElementVisual(this);
        var c = v.Compositor;
        v.CenterPoint = new Vector3((float)ActualWidth / 2, (float)ActualHeight / 2, 0);
        var a = c.CreateVector3KeyFrameAnimation();
        // Release settles with a little overshoot: the gel springing back.
        a.InsertKeyFrame(1, down ? new Vector3(0.94f, 0.94f, 1) : Vector3.One,
            down ? Motion.Decelerate(c) : c.CreateCubicBezierEasingFunction(new(0.34f, 1.56f), new(0.5f, 1f)));
        a.Duration = TimeSpan.FromMilliseconds(down ? 120 : 480);
        v.StartAnimation("Scale", a);
    }

    // ---------- smooth width ----------

    private double _shownWidth = double.NaN, _fromWidth, _targetWidth, _naturalWidth;
    private long _tweenStart, _narrowerSince;
    private double _tweenMs;

    protected override Size MeasureOverride(Size available)
    {
        var natural = base.MeasureOverride(available);
        if (!SmoothWidth) return natural;
        _naturalWidth = natural.Width;
        if (double.IsNaN(_shownWidth) || !Motion.Enabled)
        {
            _shownWidth = _targetWidth = natural.Width;
            return natural;
        }
        if (natural.Width > _targetWidth + 0.5) TweenTo(natural.Width, 220);          // grow now, quickly
        else if (natural.Width < _targetWidth - 0.5) WaitToShrink();                   // shrink once it has settled
        else _narrowerSince = 0;
        return new Size(_shownWidth, natural.Height);
    }

    private void TweenTo(double width, double ms)
    {
        _narrowerSince = 0;
        _fromWidth = _shownWidth;
        _targetWidth = width;
        _tweenMs = ms;
        _tweenStart = Environment.TickCount64;
        CompositionTarget.Rendering -= OnTweenFrame;
        CompositionTarget.Rendering += OnTweenFrame;
    }

    private void WaitToShrink()
    {
        if (_narrowerSince == 0) _narrowerSince = Environment.TickCount64;
        CompositionTarget.Rendering -= OnTweenFrame;
        CompositionTarget.Rendering += OnTweenFrame;
    }

    private void OnTweenFrame(object? sender, object e)
    {
        var now = Environment.TickCount64;
        if (_narrowerSince != 0)
        {
            if (_naturalWidth >= _targetWidth - 0.5) _narrowerSince = 0; // the content grew back in time
            else if (now - _narrowerSince >= 1500) { TweenTo(_naturalWidth, 420); return; }
        }
        if (_shownWidth != _targetWidth)
        {
            var p = Math.Min(1, (now - _tweenStart) / _tweenMs);
            _shownWidth = p >= 1 ? _targetWidth : _fromWidth + (_targetWidth - _fromWidth) * (1 - Math.Pow(1 - p, 3));
            InvalidateMeasure();
        }
        if (_shownWidth == _targetWidth && _narrowerSince == 0) CompositionTarget.Rendering -= OnTweenFrame;
    }
}
