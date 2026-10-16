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
/// Liquid Glass, as far as Windows' compositor allows: the content behind is blurred and saturated, a thin rim
/// catches light from above, a soft highlight follows the pointer, and pressing sends a glow out from the touch point.
/// Windows can't bend light through a shape (Apple's lensing), so the glass reads through blur, rim and highlight.
/// The blur samples a live copy of <see cref="BlurSource"/> (Windows' backdrop brush can't see through to the
/// window material and renders a pale wash instead). Used only for the navigation layer, never glass on glass.
/// </summary>
public sealed class Glass : ContentControl
{
    public static readonly DependencyProperty PressableProperty =
        DependencyProperty.Register(nameof(Pressable), typeof(bool), typeof(Glass), new PropertyMetadata(false));

    /// <summary>Squeezes slightly when pressed, like a button made of gel.</summary>
    public bool Pressable { get => (bool)GetValue(PressableProperty); set => SetValue(PressableProperty, value); }

    private Border? _backdrop;
    private RadialGradientBrush? _highlight;
    private Canvas? _glow;
    private SpriteVisual? _sprite;
    private CompositionEffectBrush? _brush;
    private CompositionRoundedRectangleGeometry? _shape;
    private bool _capsule;
    private CompositionVisualSurface? _surface;

    /// <summary>The element this glass floats over; what's under the glass is copied from it and blurred.</summary>
    public UIElement? BlurSource { get; set; }

    public Glass()
    {
        Loaded += (_, _) => { Build(); Appearance.Changed += OnAppearance; LayoutUpdated += Track; };
        Unloaded += (_, _) => { Appearance.Changed -= OnAppearance; LayoutUpdated -= Track; };
        SizeChanged += (_, _) => Resize();
        PointerMoved += OnPointerMoved;
        PointerExited += (_, _) => { if (_highlight is not null) _highlight.Center = _highlight.GradientOrigin = new Point(0.25, -0.1); };
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => Squeeze(false)), true);
        AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => Squeeze(false)), true);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _backdrop = GetTemplateChild("Backdrop") as Border;
        _highlight = (GetTemplateChild("Highlight") as Border)?.Background as RadialGradientBrush;
        _glow = GetTemplateChild("GlowLayer") as Canvas;
        Build();
    }

    private void Build()
    {
        if (_backdrop is null || _sprite is not null || !IsLoaded || BlurSource is null) return;
        var c = ElementCompositionPreview.GetElementVisual(this).Compositor;
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
        ElementCompositionPreview.SetElementChildVisual(_backdrop, _sprite);
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
        if (_sprite is null || _shape is null) return;
        var size = new Vector2((float)ActualWidth, (float)ActualHeight);
        _sprite.Size = size;
        _shape.Size = size;
        var r = (float)Math.Min(CornerRadius.TopLeft, Math.Min(ActualWidth, ActualHeight) / 2);
        _shape.CornerRadius = new Vector2(r, r);
    }

    private void OnAppearance() => _brush?.Properties.InsertScalar("Blur.BlurAmount", Appearance.GlassBlur);

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_highlight is null || ActualWidth == 0) return;
        var p = e.GetCurrentPoint(this).Position;
        _highlight.Center = _highlight.GradientOrigin = new Point(p.X / ActualWidth, p.Y / ActualHeight);
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
}

/// <summary>
/// Scroll edge effect: content sliding under the floating tab bar softens into blur and fades into the window
/// colour, so the bar stays legible without a hard divider.
/// </summary>
public sealed class EdgeBlur : Grid
{
    private ContainerVisual? _root;
    private SpriteVisual? _blurred, _tint;
    private CompositionEffectBrush? _blur;
    private CompositionColorGradientStop? _tintStop;
    private CompositionVisualSurface? _surface;

    /// <summary>The content that slides underneath.</summary>
    public UIElement? BlurSource { get; set; }

    public EdgeBlur()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => { Build(); OnAppearance(); Appearance.Changed += OnAppearance; LayoutUpdated += Track; };
        Unloaded += (_, _) => { Appearance.Changed -= OnAppearance; LayoutUpdated -= Track; };
        SizeChanged += (_, _) => Track(null, null);
    }

    private void Build()
    {
        if (_root is not null || BlurSource is null) return;
        var c = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _surface = c.CreateVisualSurface();
        _surface.SourceVisual = ElementCompositionPreview.GetElementVisual(BlurSource);
        var copy = c.CreateSurfaceBrush(_surface);
        copy.Stretch = CompositionStretch.None;
        copy.HorizontalAlignmentRatio = 0;
        copy.VerticalAlignmentRatio = 0;
        var effect = new GaussianBlurEffect { Name = "Blur", BlurAmount = 8, BorderMode = EffectBorderMode.Hard, Source = new CompositionEffectSourceParameter("behind") };
        _blur = c.CreateEffectFactory(effect, ["Blur.BlurAmount"]).CreateBrush();
        _blur.SetSourceParameter("behind", copy);
        var fade = c.CreateLinearGradientBrush();
        fade.StartPoint = new Vector2(0, 0);
        fade.EndPoint = new Vector2(0, 1);
        fade.ColorStops.Add(c.CreateColorGradientStop(0, Microsoft.UI.Colors.Transparent));
        fade.ColorStops.Add(c.CreateColorGradientStop(0.55f, Microsoft.UI.Colors.White));
        var mask = c.CreateMaskBrush();
        mask.Source = _blur;
        mask.Mask = fade;
        _blurred = c.CreateSpriteVisual();
        _blurred.Brush = mask;
        var tint = c.CreateLinearGradientBrush();
        tint.StartPoint = new Vector2(0, 0);
        tint.EndPoint = new Vector2(0, 1);
        tint.ColorStops.Add(c.CreateColorGradientStop(0, Microsoft.UI.Colors.Transparent));
        _tintStop = c.CreateColorGradientStop(1, Microsoft.UI.Colors.Transparent);
        tint.ColorStops.Add(_tintStop);
        _tint = c.CreateSpriteVisual();
        _tint.Brush = tint;
        _root = c.CreateContainerVisual();
        _root.Children.InsertAtTop(_blurred);
        _root.Children.InsertAtTop(_tint);
        ElementCompositionPreview.SetElementChildVisual(this, _root);
        Track(null, null);
    }

    private void Track(object? sender, object? e)
    {
        if (_surface is null || BlurSource is null || _blurred is null || _tint is null) return;
        var size = new Vector2((float)ActualWidth, (float)ActualHeight);
        var at = TransformToVisual(BlurSource).TransformPoint(default);
        _surface.SourceOffset = new Vector2((float)at.X, (float)at.Y);
        _surface.SourceSize = size;
        _blurred.Size = size;
        _tint.Size = size;
    }

    private void OnAppearance()
    {
        _blur?.Properties.InsertScalar("Blur.BlurAmount", Math.Max(2, Appearance.GlassBlur * 0.4f));
        if (_tintStop is not null) _tintStop.Color = ((SolidColorBrush)Application.Current.Resources["EdgeBrush"]).Color;
    }
}
