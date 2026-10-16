using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Gallery.Desktop.ViewModels;

namespace Gallery.Desktop.Views;

/// <summary>
/// The viewer's view. It lays out the photo as the view model's <see cref="ViewerViewModel.View"/> says (fitted, or
/// zoomed in a scroller), keeps the zoom point in place, and turns double-clicks, ⌘/Ctrl + scrolling, pinches and drags
/// on the photo into zooming and panning. On a 360° view, dragging turns it (the picture follows the pointer),
/// scrolling and pinching change the field of view, and a double-click looks straight ahead again.
/// </summary>
public partial class ViewerView : UserControl
{
    private ViewerViewModel? _vm;
    private TopLevel? _top;
    /// <summary>The layout on screen.</summary>
    private ZoomView _laid;
    /// <summary>A zoomed picture not yet scrolled into place, because the scroller had no size yet.</summary>
    private bool _unplaced;
    /// <summary>The point to keep in place when the zoom level changes: fractions of the picture, and its spot in the scroller.</summary>
    private (double Fx, double Fy, Point At)? _anchor;
    private (Point From, Vector Offset, bool Moved)? _drag;
    private double _wheel, _pinch;
    private Point? _turning;

    public ViewerView()
    {
        InitializeComponent();
        PhotoScroller.SizeChanged += (_, _) => PushStage();
        PhotoScroller.DoubleTapped += OnDoubleTapped;
        PhotoScroller.AddHandler(PointerPressedEvent, OnPressed);
        PhotoScroller.AddHandler(PointerMovedEvent, OnMoved);
        // The scroller marks the release as handled, but the drag must still end.
        PhotoScroller.AddHandler(PointerReleasedEvent, (_, _) => EndDrag(), handledEventsToo: true);
        // Use tunnel routing to get the wheel before the scroller uses it for scrolling.
        PhotoScroller.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        PhotoScroller.AddHandler(PointerTouchPadGestureMagnifyEvent, OnPinch);
        Sphere.PointerPressed += (_, e) => { if (e.GetCurrentPoint(Sphere).Properties.IsLeftButtonPressed) _turning = e.GetPosition(Sphere); };
        Sphere.PointerMoved += OnSphereMoved;
        Sphere.PointerReleased += (_, _) => _turning = null;
        Sphere.PointerCaptureLost += (_, _) => _turning = null;
        Sphere.PointerWheelChanged += (_, e) =>
        {
            if (_vm is not { IsSphere: true } vm) return;
            vm.ZoomSphereTo(vm.Fov * Math.Exp(-e.Delta.Y * 0.2)); // one wheel notch: about a fifth
            e.Handled = true;
        };
        Sphere.AddHandler(PointerTouchPadGestureMagnifyEvent, (_, e) =>
        {
            if (_vm is not { IsSphere: true } vm) return;
            vm.ZoomSphereTo(vm.Fov / (1 + e.Delta.X));
            e.Handled = true;
        });
        Sphere.DoubleTapped += (_, e) => { _vm?.ResetSphere(); e.Handled = true; };
    }

    /// <summary>The 360° view follows the pointer: degrees per unit at the middle of the view.</summary>
    private void OnSphereMoved(object? sender, PointerEventArgs e)
    {
        if (_turning is not { } from || _vm is not { IsSphere: true } vm) return;
        if (!e.GetCurrentPoint(Sphere).Properties.IsLeftButtonPressed) { _turning = null; return; } // released elsewhere
        var at = e.GetPosition(Sphere);
        _turning = at;
        var k = vm.Fov / Math.Max(1, Math.Max(Sphere.Bounds.Width, Sphere.Bounds.Height));
        vm.Turn(-(at.X - from.X) * k, (at.Y - from.Y) * k);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as ViewerViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        PushStage();
        Lay();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        if (_top is not null) _top.ScalingChanged += OnScalingChanged;
        PushStage();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_top is not null) _top.ScalingChanged -= OnScalingChanged;
        _top = null;
    }

    private void OnScalingChanged(object? sender, EventArgs e) => PushStage();

    private double Scaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    private void PushStage()
    {
        if (_vm is null || PhotoScroller.Bounds.Width < 1) return;
        _vm.SetStage(PhotoScroller.Bounds.Width, PhotoScroller.Bounds.Height, Scaling);
        if (_unplaced) Lay();
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewerViewModel.View)) Lay();
    }

    /// <summary>The picture's position in the scroller's viewport.</summary>
    private Rect PictureRect() =>
        Photo.Bounds.Width > 0 && Photo.TranslatePoint(default, PhotoScroller) is { } p ? new Rect(p, Photo.Bounds.Size) : default;

    /// <summary>
    /// Lays out the photo as the view model says. When zoomed, the picture is scrolled so the zoom point stays in place:
    /// the point under the pointer, or the middle of the window (for a new photo, its own middle goes there).
    /// </summary>
    private void Lay()
    {
        if (_vm is not { } vm) return;
        var view = vm.View;
        var centre = new Point(PhotoScroller.Bounds.Width / 2, PhotoScroller.Bounds.Height / 2);
        var before = PictureRect();
        var (fx, fy, at) = _anchor ?? (view.Picture != _laid.Picture || _unplaced || before.Width <= 0
            ? (0.5, 0.5, centre)
            : (Math.Clamp((centre.X - before.X) / before.Width, 0, 1), Math.Clamp((centre.Y - before.Y) / before.Height, 0, 1), centre));
        _laid = view;
        _unplaced = false;
        // When zoomed and scrolled to the top, the photo still starts below the buttons.
        PhotoSheet.Margin = view.Zoomed ? new Thickness(0, ViewerViewModel.FitMargin.Top, 0, 0) : ViewerViewModel.FitMargin;
        PhotoSheet.Width = view.Zoomed ? view.Width : double.NaN;
        PhotoSheet.Height = view.Zoomed ? view.Height : double.NaN;
        var bars = view.Zoomed ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        PhotoScroller.HorizontalScrollBarVisibility = bars;
        PhotoScroller.VerticalScrollBarVisibility = bars;
        // Set in code because the interpolation mode is not a property that can be bound.
        RenderOptions.SetBitmapInterpolationMode(Photo, view.Pixels ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
        Photo.InvalidateVisual();
        if (!view.Zoomed) { PhotoScroller.Offset = default; return; }
        if (PhotoScroller.Bounds.Width < 1) { _unplaced = true; return; } // placed once the scroller has a size
        PhotoScroller.UpdateLayout();
        var now = PictureRect();
        PhotoScroller.Offset = new Vector(PhotoScroller.Offset.X + now.X + fx * now.Width - at.X, PhotoScroller.Offset.Y + now.Y + fy * now.Height - at.Y);
    }

    /// <summary>Zooms to a level, keeping the point of the picture at <paramref name="at"/> (in the scroller) where it is.</summary>
    private void ZoomAt(double level, Point at)
    {
        if (_vm is null) return;
        var r = PictureRect();
        _anchor = r.Width > 0 ? (Math.Clamp((at.X - r.X) / r.Width, 0, 1), Math.Clamp((at.Y - r.Y) / r.Height, 0, 1), at) : null;
        try { _vm.SetZoom(level); }
        finally { _anchor = null; }
    }

    /// <summary>Double-click on the photo (not a face box): zoom to 100% with the clicked point under the pointer, or back to fit.</summary>
    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        var at = e.GetPosition(PhotoScroller);
        if (_vm is not { CanZoom: true } vm || e.Source is FaceOverlay || !PictureRect().Contains(at)) return;
        if (vm.IsZoomed) vm.SetZoom(0);
        else ZoomAt(1, at);
        e.Handled = true;
    }

    /// <summary>⌘/Ctrl + scroll: zoom one level in or out at the pointer for each wheel notch (a trackpad sends small amounts).</summary>
    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) == 0 || _vm is not { CanZoom: true } vm) return;
        e.Handled = true;
        if (Math.Sign(e.Delta.Y) != Math.Sign(_wheel)) _wheel = 0;
        _wheel += e.Delta.Y;
        if (Math.Abs(_wheel) < 1) return;
        var step = Math.Sign(_wheel);
        _wheel = 0;
        ZoomAt(vm.NextLevel(step), e.GetPosition(PhotoScroller));
    }

    /// <summary>A trackpad pinch: zoom one level in or out at the pointer for about each fifth of magnification.</summary>
    private void OnPinch(object? sender, PointerDeltaEventArgs e)
    {
        if (_vm is not { CanZoom: true } vm) return;
        e.Handled = true;
        if (Math.Sign(e.Delta.X) != Math.Sign(_pinch)) _pinch = 0;
        _pinch += e.Delta.X;
        if (Math.Abs(_pinch) < 0.2) return;
        var step = Math.Sign(_pinch);
        _pinch = 0;
        ZoomAt(vm.NextLevel(step), e.GetPosition(PhotoScroller));
    }

    /// <summary>
    /// Dragging a zoomed photo pans it (scroll bars, face boxes and double-clicks are left alone). Avalonia already keeps
    /// the pointer captured by the pressed control. Capturing it here would make double-clicks get missed.
    /// </summary>
    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is not { IsZoomed: true } || !e.GetCurrentPoint(PhotoScroller).Properties.IsLeftButtonPressed || e.ClickCount > 1) return;
        if (e.Source is Visual v && v.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null) return;
        _drag = (e.GetPosition(PhotoScroller), PhotoScroller.Offset, false);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_drag is not { } d) return;
        if (!e.GetCurrentPoint(PhotoScroller).Properties.IsLeftButtonPressed) { EndDrag(); return; } // released elsewhere
        var delta = e.GetPosition(PhotoScroller) - d.From;
        if (!d.Moved && Math.Abs(delta.X) + Math.Abs(delta.Y) < 4) return;
        if (!d.Moved)
        {
            _drag = d with { Moved = true };
            PhotoScroller.Cursor = new Cursor(StandardCursorType.SizeAll);
        }
        PhotoScroller.Offset = d.Offset - delta;
    }

    private void EndDrag()
    {
        if (_drag is null) return;
        _drag = null;
        PhotoScroller.Cursor = null;
    }
}
