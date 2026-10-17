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
/// bezel whose computed shift reaches that step; neighbouring steps blend, so the bend reads as continuous.
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

    private Border? _backdrop, _rimHost;
    private Canvas? _glow;
    private Ellipse? _spot;
    private RadialGradientBrush? _spotBrush;
    private SpriteVisual? _sprite, _rim;
    private CompositionEffectBrush? _brush;
    private CompositionRoundedRectangleGeometry? _shape;
    private CompositionVisualSurface? _surface, _blurred;
    private readonly List<(SpriteVisual Sprite, CompositionSurfaceBrush Copy, CompositionMaskBrush Mask)> _bands = [];
    private (float Radius, float Scale, bool Dark) _lensFor;
    private bool _capsule;

    public Glass()
    {
        Loaded += (_, _) =>
        {
            Build();
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
        PointerEntered += OnPointerEntered;
        PointerMoved += OnPointerMoved;
        PointerExited += (_, _) => { if (_spot is not null) _spot.Opacity = 0; };
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => Squeeze(false)), true);
        AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => Squeeze(false)), true);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
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
        var saturate = new SaturationEffect { Saturation = 1.8f, Source = blur };
        _brush = c.CreateEffectFactory(saturate, ["Blur.BlurAmount"]).CreateBrush();
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

        // The refraction steps sample the blurred backdrop itself, so the blur runs once, not once per step.
        _blurred = c.CreateVisualSurface();
        _blurred.SourceVisual = _sprite;
        var root = c.CreateContainerVisual();
        root.Children.InsertAtTop(_sprite);
        for (var level = 1; level <= GlassLens.Levels; level++)
        {
            var bandCopy = c.CreateSurfaceBrush(_blurred);
            bandCopy.Stretch = CompositionStretch.None;
            bandCopy.HorizontalAlignmentRatio = 0;
            bandCopy.VerticalAlignmentRatio = 0;
            var mask = c.CreateMaskBrush();
            mask.Source = bandCopy;
            var band = c.CreateSpriteVisual();
            band.Brush = mask;
            root.Children.InsertAtTop(band);
            _bands.Add((band, bandCopy, mask));
        }
        ElementCompositionPreview.SetElementChildVisual(_backdrop, root);

        if (_rimHost is not null)
        {
            _rim = c.CreateSpriteVisual();
            ElementCompositionPreview.SetElementChildVisual(_rimHost, _rim);
        }
        Resize();
        Track(null, null);
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
        // A huge radius means "capsule". WinUI scales oversized radii into an ellipse, so use exactly half the height.
        if (CornerRadius.TopLeft >= 999 || _capsule)
        {
            _capsule = true;
            var half = ActualHeight / 2;
            if (half > 0 && CornerRadius.TopLeft != half) CornerRadius = new CornerRadius(half);
        }
        if (_sprite is null || _shape is null || _blurred is null || ActualWidth == 0) return;
        float w = (float)ActualWidth, h = (float)ActualHeight;
        var size = new Vector2(w, h);
        _sprite.Size = size;
        _shape.Size = size;
        _blurred.SourceSize = size;
        var r = (float)Math.Min(CornerRadius.TopLeft, Math.Min(w, h) / 2);
        _shape.CornerRadius = new Vector2(r, r);
        if (_rim is not null) _rim.Size = size;
        RefreshLens(r);

        // Each step magnifies the backdrop about the centre so the edge samples its shift further in.
        var centre = size / 2;
        for (var i = 0; i < _bands.Count; i++)
        {
            var (band, copy, _) = _bands[i];
            band.Size = size;
            var shift = GlassLens.StepShift(r, i + 1);
            copy.CenterPoint = centre;
            copy.Scale = new Vector2(centre.X / Math.Max(1, centre.X - shift), centre.Y / Math.Max(1, centre.Y - shift));
        }
    }

    /// <summary>Hands out the lens images for the current radius, screen scale and theme (drawn once, then shared).</summary>
    private void RefreshLens(float radius)
    {
        var scale = (float)(XamlRoot?.RasterizationScale ?? 1);
        var key = (MathF.Round(radius, 1), scale, Appearance.IsDark);
        if (key == _lensFor || _sprite is null || radius < 2) return;
        _lensFor = key;
        var c = _sprite.Compositor;
        for (var i = 0; i < _bands.Count; i++) _bands[i].Mask.Mask = GlassLens.Mask(c, radius, scale, i + 1);
        if (_rim is not null) _rim.Brush = GlassLens.Rim(c, radius, scale, Appearance.IsDark);
    }

    private void OnLensLost() { _lensFor = default; Resize(); }

    private void OnAppearance()
    {
        _brush?.Properties.InsertScalar("Blur.BlurAmount", Appearance.GlassBlur);
        if (_spotBrush is not null) SpotColours(_spotBrush);
        Resize();
    }

    // ---------- the pointer light ----------

    private const double SpotSize = 56;

    /// <summary>A soft light about two cursors wide, trailing the pointer and fading in and out.</summary>
    private void BuildSpot()
    {
        if (_glow is null || _spot is not null) return;
        _spotBrush = new RadialGradientBrush();
        SpotColours(_spotBrush);
        _spot = new Ellipse
        {
            Width = SpotSize, Height = SpotSize, IsHitTestVisible = false, Opacity = 0, Fill = _spotBrush,
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(260) },
            TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(140) },
        };
        _glow.Children.Add(_spot);
    }

    private static void SpotColours(RadialGradientBrush b)
    {
        var peak = Appearance.IsDark ? 0.16 : 0.42;
        b.GradientStops.Clear();
        b.GradientStops.Add(new GradientStop { Offset = 0, Color = Appearance.WithAlpha(Microsoft.UI.Colors.White, peak) });
        b.GradientStops.Add(new GradientStop { Offset = 0.45, Color = Appearance.WithAlpha(Microsoft.UI.Colors.White, peak * 0.45) });
        b.GradientStops.Add(new GradientStop { Offset = 1, Color = Appearance.WithAlpha(Microsoft.UI.Colors.White, 0) });
    }

    private Vector3 SpotAt(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_glow).Position;
        return new Vector3((float)(p.X - SpotSize / 2), (float)(p.Y - SpotSize / 2), 0);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_spot is null) return;
        // Appear where the pointer came in, not slide over from where it last left.
        var move = _spot.TranslationTransition;
        _spot.TranslationTransition = null;
        _spot.Translation = SpotAt(e);
        _spot.TranslationTransition = move;
        _spot.Opacity = 1;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_spot is null) return;
        if (!Motion.Enabled) _spot.TranslationTransition = null;
        _spot.Translation = SpotAt(e);
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
