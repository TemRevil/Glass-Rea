using System.Numerics;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace GlassRea.App.Controls;

/// <summary>
/// The motion grammar: things leave with a quick fade into blur (160 ms, accelerating) and arrive by sharpening out
/// of blur (420 ms, decelerating), lists arrive one item after another, numbers count up. All of it is skipped
/// when Windows' "Animation effects" is off.
/// </summary>
public static class Motion
{
    private static readonly UISettings Settings = new();
    public static bool Enabled => Settings.AnimationsEnabled;

    public static readonly TimeSpan OutTime = TimeSpan.FromMilliseconds(160), InTime = TimeSpan.FromMilliseconds(420);

    public static CubicBezierEasingFunction Decelerate(Compositor c) => c.CreateCubicBezierEasingFunction(new(0.1f, 0.9f), new(0.2f, 1f));
    public static CubicBezierEasingFunction Accelerate(Compositor c) => c.CreateCubicBezierEasingFunction(new(0.7f, 0f), new(1f, 0.5f));

    /// <summary>
    /// Starts softly, then settles: for a whole page arriving. <see cref="Decelerate"/> is nearly done a tenth of the
    /// way in, which brought the new page in over two frames, a flash under the floating bar's glass.
    /// </summary>
    public static CubicBezierEasingFunction Settle(Compositor c) => c.CreateCubicBezierEasingFunction(new(0.3f, 0f), new(0.2f, 1f));

    /// <summary>
    /// True just before a page change starts moving what's behind the floating bar, false once it has settled. The
    /// bar's glass fades its refraction out meanwhile: each step redraws the blur whenever its backdrop moves, and with
    /// all of them redrawing every frame the page change ran at a fraction of the screen's rate.
    /// </summary>
    public static event Action<bool>? BackdropMoving;

    /// <summary>How long the glass takes to let go of its refraction; a page change starts moving only after it.</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(140);

    private static Task _swapping = Task.CompletedTask;

    /// <summary>
    /// Replaces the page inside <paramref name="host"/>: the old page dissolves into blur while the new one sharpens
    /// out of it, at the same time, so the page area is never empty in between. The blur is drawn from live copies
    /// of the pages in <paramref name="ghost"/>, a sibling layer above the host; the new page is laid out and drawn,
    /// hidden, before either starts to move, and is interactive underneath. The children of <paramref name="stagger"/>
    /// then arrive one after another (<see cref="Stagger"/>). One swap runs at a time; one that is no longer
    /// <paramref name="wanted"/> by its turn (another tab was picked meanwhile) is skipped.
    /// </summary>
    public static async Task SwapPage(Grid host, Grid ghost, UIElement next, Panel? stagger = null, Func<bool>? wanted = null)
    {
        var turn = new TaskCompletionSource();
        var before = _swapping;
        _swapping = turn.Task;
        try
        {
            await before;
            if (wanted?.Invoke() == false) return;
            await Swap(host, ghost, next, stagger);
        }
        finally { turn.SetResult(); }
    }

    private static async Task Swap(Grid host, Grid ghost, UIElement next, Panel? stagger)
    {
        var current = host.Children.Count > 0 ? host.Children[0] : null;
        if (current == next) return;
        if (!Enabled || current is null)
        {
            host.Children.Clear();
            host.Children.Add(next);
            if (stagger is not null) Stagger(stagger);
            return;
        }
        var hostVisual = ElementCompositionPreview.GetElementVisual(host);
        var c = hostVisual.Compositor;

        BackdropMoving?.Invoke(true);
        var settled = Task.Delay(SettleTime); // runs alongside building the new page, so it rarely adds a wait
        try
        {
            var outgoing = Ghost(c, host, current, ghost);
            hostVisual.Clip = c.CreateInsetClip(0, 0, 100000, 100000); // hidden while the ghosts stand in for it
            host.Children.Add(next);
            // Hidden now, while the page is being drawn anyway: hiding them as the page set off held its first frames.
            if (stagger is not null) Hide(stagger);
            await Rendered(next);
            await settled;

            if (stagger is not null) Stagger(stagger);
            var incoming = Ghost(c, host, next, ghost);
            // The old page stays in the tree until it has faded: its ghost is a live copy of it.
            await Task.WhenAll(
                Animate(c, outgoing, from: 0, to: 8, opacityFrom: 1, opacityTo: 0, dy: -4, OutTime, Accelerate(c)),
                Animate(c, incoming, from: 12, to: 0, opacityFrom: 0, opacityTo: 1, dy: 8, InTime, Settle(c)));
            Remove(ghost, outgoing.Sprite);
            host.Children.Remove(current);
            hostVisual.Clip = null;
            Remove(ghost, incoming.Sprite);
        }
        finally { BackdropMoving?.Invoke(false); }
    }

    private sealed record GhostCopy(SpriteVisual Sprite, CompositionEffectBrush Brush);

    // Made once: building an effect compiles it, which held up the first frames of every page change.
    private static CompositionEffectFactory? _ghostBlur;

    private static GhostCopy Ghost(Compositor c, FrameworkElement host, UIElement page, Grid ghostLayer)
    {
        // Pad the copy so the blur can spread past the page's edges instead of stopping at a hard line.
        const float pad = 40;
        var size = new Vector2((float)host.ActualWidth, (float)host.ActualHeight);
        var surface = c.CreateVisualSurface();
        surface.SourceVisual = ElementCompositionPreview.GetElementVisual(page);
        surface.SourceOffset = new Vector2(-pad, -pad);
        surface.SourceSize = size + new Vector2(pad * 2);
        _ghostBlur ??= c.CreateEffectFactory(
            new GaussianBlurEffect { Name = "Blur", BlurAmount = 0, BorderMode = EffectBorderMode.Soft, Optimization = EffectOptimization.Speed, Source = new CompositionEffectSourceParameter("page") },
            ["Blur.BlurAmount"]);
        var brush = _ghostBlur.CreateBrush();
        brush.SetSourceParameter("page", c.CreateSurfaceBrush(surface));
        var at = host.TransformToVisual(ghostLayer).TransformPoint(default);
        var sprite = c.CreateSpriteVisual();
        sprite.Brush = brush;
        sprite.Size = surface.SourceSize;
        sprite.Offset = new Vector3((float)at.X - pad, (float)at.Y - pad, 0);
        var container = ElementCompositionPreview.GetElementChildVisual(ghostLayer) as ContainerVisual;
        if (container is null)
        {
            container = c.CreateContainerVisual();
            ElementCompositionPreview.SetElementChildVisual(ghostLayer, container);
        }
        container.Children.InsertAtTop(sprite);
        return new GhostCopy(sprite, brush);
    }

    private static void Remove(Grid ghostLayer, Visual sprite)
    {
        if (ElementCompositionPreview.GetElementChildVisual(ghostLayer) is ContainerVisual container) container.Children.Remove(sprite);
        sprite.Dispose();
    }

    private static Task Animate(Compositor c, GhostCopy g, float from, float to, float opacityFrom, float opacityTo, float dy, TimeSpan time, CompositionEasingFunction ease)
    {
        var done = new TaskCompletionSource();
        var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
        var blur = c.CreateScalarKeyFrameAnimation();
        blur.InsertKeyFrame(0, from); blur.InsertKeyFrame(1, to, ease); blur.Duration = time;
        g.Brush.Properties.StartAnimation("Blur.BlurAmount", blur);
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, opacityFrom); fade.InsertKeyFrame(1, opacityTo, ease); fade.Duration = time;
        g.Sprite.StartAnimation("Opacity", fade);
        var move = c.CreateVector3KeyFrameAnimation();
        var start = g.Sprite.Offset;
        var outward = opacityTo < opacityFrom;
        move.InsertKeyFrame(0, outward ? start : start + new Vector3(0, dy, 0));
        move.InsertKeyFrame(1, outward ? start + new Vector3(0, dy, 0) : start, ease);
        move.Duration = time;
        g.Sprite.StartAnimation("Offset", move);
        batch.End();
        batch.Completed += (_, _) => done.TrySetResult();
        return done.Task;
    }

    /// <summary>
    /// Waits until the next frame has been drawn, so what just changed is on screen; at most a tenth of a second, as a
    /// window that isn't showing draws no frames.
    /// </summary>
    public static Task NextFrame()
    {
        var drawn = new TaskCompletionSource();
        var frames = 0;
        void OnFrame(object? s, object e)
        {
            if (++frames < 2) return; // the frame being drawn when this is called may already be under way
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFrame;
            drawn.TrySetResult();
        }
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnFrame;
        return Task.WhenAny(drawn.Task, Task.Delay(100));
    }

    /// <summary>
    /// Waits until the element is in the tree and the app is drawing at full rate again, so the page has really been
    /// drawn (up to 0.6 s). A page change started while the new page was still being drawn froze for a
    /// few frames, and the tab pill froze with it.
    /// </summary>
    public static async Task Rendered(UIElement e)
    {
        if (e is FrameworkElement { IsLoaded: false } fe)
        {
            var loaded = new TaskCompletionSource();
            void OnLoaded(object s, RoutedEventArgs a) { fe.Loaded -= OnLoaded; loaded.TrySetResult(); }
            fe.Loaded += OnLoaded;
            await loaded.Task;
        }
        var smooth = new TaskCompletionSource();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var last = TimeSpan.Zero;
        var steady = 0;
        void OnFrame(object? s, object a)
        {
            var now = clock.Elapsed;
            // Three frames in a row, each within about one and a half frames of the last.
            steady = now - last <= TimeSpan.FromMilliseconds(25) ? steady + 1 : 0;
            last = now;
            if (steady < 3) return;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFrame;
            smooth.TrySetResult();
        }
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnFrame;
        if (await Task.WhenAny(smooth.Task, Task.Delay(600)) != smooth.Task) Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFrame;
    }

    /// <summary>Children of a panel arrive one after another: rising 12 px and fading in, 45 ms apart.</summary>
    public static void Stagger(Panel panel, int startDelayMs = 60)
    {
        if (!Enabled) return;
        var i = 0;
        foreach (var child in panel.Children)
        {
            if (child.Visibility != Visibility.Visible) continue;
            Arrive(child, startDelayMs + i++ * 45);
        }
    }

    /// <summary>Puts a panel's children where <see cref="Stagger"/> brings them in from, ahead of time.</summary>
    public static void Hide(Panel panel)
    {
        if (!Enabled) return;
        foreach (var child in panel.Children)
        {
            if (child.Visibility != Visibility.Visible) continue;
            ElementCompositionPreview.SetIsTranslationEnabled(child, true);
            var v = ElementCompositionPreview.GetElementVisual(child);
            v.Opacity = 0;
            v.Properties.InsertVector3("Translation", new Vector3(0, Rise, 0));
        }
    }

    private const float Rise = 12;

    public static void Arrive(UIElement e, int delayMs = 0, float rise = Rise)
    {
        if (!Enabled) return;
        var v = ElementCompositionPreview.GetElementVisual(e);
        var c = v.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(e, true);
        var ease = Decelerate(c);
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0); fade.InsertKeyFrame(1, 1, ease);
        fade.Duration = InTime; fade.DelayTime = TimeSpan.FromMilliseconds(delayMs);
        fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        var up = c.CreateVector3KeyFrameAnimation();
        up.InsertKeyFrame(0, new Vector3(0, rise, 0)); up.InsertKeyFrame(1, Vector3.Zero, ease);
        up.Duration = InTime; up.DelayTime = fade.DelayTime;
        up.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        v.StartAnimation("Opacity", fade);
        v.StartAnimation("Translation", up);
    }

    /// <summary>Counts a number up to its value over about a second, easing out.</summary>
    public static void CountUp(DispatcherQueue queue, long to, Func<long, string> format, Action<string> set)
    {
        if (!Enabled) { set(format(to)); return; }
        Run(queue, TimeSpan.FromMilliseconds(1100), p => set(format((long)(to * (1 - Math.Pow(1 - p, 4))))));
    }

    // Running timers are held here; a timer nobody references gets collected and simply stops mid-animation.
    private static readonly HashSet<DispatcherQueueTimer> Running = [];

    /// <summary>Calls <paramref name="frame"/> about 60 times a second with progress 0..1, ending exactly on 1.</summary>
    public static void Run(DispatcherQueue queue, TimeSpan duration, Action<double> frame)
    {
        var start = Environment.TickCount64;
        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.Tick += (t, _) =>
        {
            var p = Math.Min(1, (Environment.TickCount64 - start) / duration.TotalMilliseconds);
            frame(p);
            if (p < 1) return;
            t.Stop();
            Running.Remove(t);
        };
        Running.Add(timer);
        timer.Start();
    }
}
