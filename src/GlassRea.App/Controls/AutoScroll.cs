using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace GlassRea.App.Controls;

/// <summary>
/// Middle-click scrolling, as in a browser: middle-click a scrollable area and a marker drops where you clicked;
/// moving the pointer away from it scrolls that way, faster the further you go. Releasing after a drag ends it;
/// after a plain click it keeps going until any click, the wheel, or Esc.
/// Lives as a layer over the whole window, invisible until it starts, so while it runs every click ends it.
/// </summary>
public sealed class AutoScroll : Grid
{
    private const double DeadZone = 10;   // px around the marker that don't scroll
    private const double DragToStop = 12; // a middle release this far from the marker ends it

    private readonly Canvas _canvas = new();
    private readonly Grid _marker;
    private readonly FontIcon _left, _right;
    private ScrollViewer? _target;
    private Point _anchor, _pointer;
    private double _x, _y;
    private long _lastFrame;
    private bool _horizontal, _vertical;

    public bool IsActive { get; private set; }

    public AutoScroll()
    {
        Visibility = Visibility.Collapsed;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Children.Add(_canvas);

        _marker = new Grid { Width = 34, Height = 34, IsHitTestVisible = false };
        _marker.Children.Add(new Ellipse
        {
            Fill = (Brush)Application.Current.Resources["CardBrush"],
            Stroke = (Brush)Application.Current.Resources["CardStrokeBrush"], StrokeThickness = 1,
        });
        _marker.Children.Add(new Ellipse { Width = 4, Height = 4, Fill = (Brush)Application.Current.Resources["TextBrush"] });
        _marker.Children.Add(Arrow("", VerticalAlignment.Top, HorizontalAlignment.Center));
        _marker.Children.Add(Arrow("", VerticalAlignment.Bottom, HorizontalAlignment.Center));
        _marker.Children.Add(_left = Arrow("", VerticalAlignment.Center, HorizontalAlignment.Left));
        _marker.Children.Add(_right = Arrow("", VerticalAlignment.Center, HorizontalAlignment.Right));
        _canvas.Children.Add(_marker);

        PointerMoved += (_, e) => _pointer = e.GetCurrentPoint(this).Position;
        PointerPressed += (_, e) => { Stop(); e.Handled = true; };
        PointerReleased += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.MiddleButtonReleased) return;
            var p = e.GetCurrentPoint(this).Position;
            if (Math.Abs(p.X - _anchor.X) > DragToStop || Math.Abs(p.Y - _anchor.Y) > DragToStop) Stop();
            e.Handled = true;
        };
        PointerWheelChanged += (_, e) => { Stop(); e.Handled = true; };
    }

    private static FontIcon Arrow(string glyph, VerticalAlignment v, HorizontalAlignment h) => new()
    {
        Glyph = glyph, FontSize = 8, VerticalAlignment = v, HorizontalAlignment = h, Margin = new Thickness(4),
        Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
    };

    /// <summary>Connects to the window's root: middle presses anywhere inside it can start a scroll.</summary>
    public void Attach(UIElement root)
    {
        root.AddHandler(PointerPressedEvent, new PointerEventHandler(OnRootPressed), true);
        root.AddHandler(KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (IsActive && e.Key == Windows.System.VirtualKey.Escape) { Stop(); e.Handled = true; }
        }), true);
    }

    private void OnRootPressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsActive) return; // the layer itself handles presses while running
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsMiddleButtonPressed) return;
        if (FindScrollable(e.OriginalSource as DependencyObject) is not { } target) return;
        Start(target, point.Position);
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollable(DependencyObject? from)
    {
        for (var o = from; o is not null; o = VisualTreeHelper.GetParent(o))
            if (o is ScrollViewer sv && (sv.ScrollableHeight > 0 || sv.ScrollableWidth > 0)) return sv;
        return null;
    }

    private void Start(ScrollViewer target, Point at)
    {
        _target = target;
        _anchor = _pointer = at;
        _x = target.HorizontalOffset;
        _y = target.VerticalOffset;
        _vertical = target.ScrollableHeight > 0;
        _horizontal = target.ScrollableWidth > 0 && target.HorizontalScrollMode != ScrollMode.Disabled;
        _left.Visibility = _right.Visibility = _horizontal ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(_marker, at.X - _marker.Width / 2);
        Canvas.SetTop(_marker, at.Y - _marker.Height / 2);
        ProtectedCursor = InputSystemCursor.Create(_horizontal && _vertical ? InputSystemCursorShape.SizeAll
            : _horizontal ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.SizeNorthSouth);
        Visibility = Visibility.Visible;
        IsActive = true;
        _lastFrame = Environment.TickCount64;
        target.Unloaded += OnTargetUnloaded;
        CompositionTarget.Rendering += OnFrame;
    }

    public void Stop()
    {
        if (!IsActive) return;
        IsActive = false;
        CompositionTarget.Rendering -= OnFrame;
        if (_target is not null) _target.Unloaded -= OnTargetUnloaded;
        _target = null;
        Visibility = Visibility.Collapsed;
    }

    private void OnTargetUnloaded(object sender, RoutedEventArgs e) => Stop();

    /// <summary>Pixels per second for a pointer this far from the marker: nothing inside the dead zone, then
    /// growing a little faster than the distance, so a short reach reads and a long one flies.</summary>
    public static double Speed(double distance)
    {
        var beyond = Math.Abs(distance) - DeadZone;
        return beyond <= 0 ? 0 : Math.Sign(distance) * Math.Pow(beyond, 1.25) * 4;
    }

    private void OnFrame(object? sender, object e)
    {
        if (_target is null) return;
        var now = Environment.TickCount64;
        var dt = Math.Min(50, now - _lastFrame) / 1000.0;
        _lastFrame = now;
        if (_vertical) _y = Math.Clamp(_y + Speed(_pointer.Y - _anchor.Y) * dt, 0, _target.ScrollableHeight);
        if (_horizontal) _x = Math.Clamp(_x + Speed(_pointer.X - _anchor.X) * dt, 0, _target.ScrollableWidth);
        _target.ChangeView(_horizontal ? _x : null, _vertical ? _y : null, null, disableAnimation: true);
    }
}
