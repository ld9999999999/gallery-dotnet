using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using Gallery.Desktop.ViewModels;

namespace Gallery.Desktop.Views;

/// <summary>
/// The crop frame over the editor's picture in crop mode: the outside is dimmed, a thin line goes around it, thirds show
/// inside while dragging and a finer grid while straightening, and there are eight handles big enough for a finger. It
/// knows only where the frame is on screen (<see cref="Box"/>). A drag of the frame or a handle is reported from where
/// it began, in screen units, and the editor's view turns that into picture pixels. Only the frame and its handles
/// take pointer input.
/// </summary>
public sealed class CropOverlay : Control, ICustomHitTest
{
    public static readonly StyledProperty<Rect> BoxProperty = AvaloniaProperty.Register<CropOverlay, Rect>(nameof(Box));
    public static readonly StyledProperty<bool> IsFineProperty = AvaloniaProperty.Register<CropOverlay, bool>(nameof(IsFine));

    private const double Grab = 14; // half the side of the square around a handle that you can grab
    private static readonly IBrush Dim = new SolidColorBrush(Color.FromArgb(153, 0, 0, 0));
    private static readonly IPen Edge = new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), 1);
    private static readonly IPen ThirdsPen = new Pen(new SolidColorBrush(Color.FromArgb(89, 255, 255, 255)), 1);
    private static readonly IPen FinePen = new Pen(new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)), 1);
    private static readonly IBrush HandleShadow = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));

    /// <summary>Each handle: the edges it moves, where it sits as fractions of the frame, and its pointer.</summary>
    private static readonly (CropHandle Handle, double X, double Y, Cursor Cursor)[] Handles =
    [
        (CropHandle.TopLeft, 0, 0, new Cursor(StandardCursorType.TopLeftCorner)),
        (CropHandle.Top, 0.5, 0, new Cursor(StandardCursorType.TopSide)),
        (CropHandle.TopRight, 1, 0, new Cursor(StandardCursorType.TopRightCorner)),
        (CropHandle.Right, 1, 0.5, new Cursor(StandardCursorType.RightSide)),
        (CropHandle.BottomRight, 1, 1, new Cursor(StandardCursorType.BottomRightCorner)),
        (CropHandle.Bottom, 0.5, 1, new Cursor(StandardCursorType.BottomSide)),
        (CropHandle.BottomLeft, 0, 1, new Cursor(StandardCursorType.BottomLeftCorner)),
        (CropHandle.Left, 0, 0.5, new Cursor(StandardCursorType.LeftSide)),
    ];
    private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);

    private (CropHandle Handle, Point From)? _drag;

    static CropOverlay() => AffectsRender<CropOverlay>(BoxProperty, IsFineProperty);

    /// <summary>The frame, in this control's coordinates.</summary>
    public Rect Box { get => GetValue(BoxProperty); set => SetValue(BoxProperty, value); }
    /// <summary>Show the fine grid (while straightening).</summary>
    public bool IsFine { get => GetValue(IsFineProperty); set => SetValue(IsFineProperty, value); }
    public bool IsDragging => _drag is not null;

    /// <summary>The frame (<see cref="CropHandle.Move"/>) or a handle was grabbed.</summary>
    public event EventHandler<CropHandle>? DragStarted;
    /// <summary>The pointer moved while dragging: the distance from where the drag began.</summary>
    public event EventHandler<Vector>? Dragged;
    public event EventHandler? DragEnded;

    /// <summary>Where a handle sits on screen.</summary>
    public Point PointOf(CropHandle handle)
    {
        var h = Handles.First(x => x.Handle == handle);
        return new Point(Box.X + h.X * Box.Width, Box.Y + h.Y * Box.Height);
    }

    /// <summary>What a press at <paramref name="p"/> grabs: the nearest handle in reach, else the frame, else nothing.</summary>
    private (CropHandle Handle, Cursor Cursor)? HandleAt(Point p)
    {
        if (Box.Width <= 0 || Box.Height <= 0) return null;
        (CropHandle, Cursor)? best = null;
        var nearest = double.MaxValue;
        foreach (var h in Handles)
        {
            double dx = Math.Abs(p.X - (Box.X + h.X * Box.Width)), dy = Math.Abs(p.Y - (Box.Y + h.Y * Box.Height));
            if (dx > Grab || dy > Grab || dx + dy >= nearest) continue;
            nearest = dx + dy;
            best = (h.Handle, h.Cursor);
        }
        return best ?? (Box.Contains(p) ? (CropHandle.Move, MoveCursor) : null);
    }

    public bool HitTest(Point point) => IsVisible && HandleAt(point) is not null;

    public override void Render(DrawingContext context)
    {
        var b = Box;
        if (b.Width <= 0 || b.Height <= 0) return;
        double w = Bounds.Width, h = Bounds.Height;
        // Outside the frame: above, below, then left and right of it.
        context.FillRectangle(Dim, new Rect(0, 0, w, Math.Max(0, b.Y)));
        context.FillRectangle(Dim, new Rect(0, b.Bottom, w, Math.Max(0, h - b.Bottom)));
        context.FillRectangle(Dim, new Rect(0, b.Y, Math.Max(0, b.X), b.Height));
        context.FillRectangle(Dim, new Rect(b.Right, b.Y, Math.Max(0, w - b.Right), b.Height));
        void Grid(IPen pen, int parts)
        {
            for (var i = 1; i < parts; i++)
            {
                context.DrawLine(pen, new Point(b.X + i * b.Width / parts, b.Y), new Point(b.X + i * b.Width / parts, b.Bottom));
                context.DrawLine(pen, new Point(b.X, b.Y + i * b.Height / parts), new Point(b.Right, b.Y + i * b.Height / parts));
            }
        }
        if (IsFine) Grid(FinePen, 8);
        if (IsDragging) Grid(ThirdsPen, 3);
        context.DrawRectangle(null, Edge, b);
        foreach (var handle in Handles)
        {
            var at = new Point(b.X + handle.X * b.Width, b.Y + handle.Y * b.Height);
            context.FillRectangle(HandleShadow, new Rect(at.X - 6, at.Y - 6, 12, 12), 3);
            context.FillRectangle(Brushes.White, new Rect(at.X - 5, at.Y - 5, 10, 10), 2);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_drag is not null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        if (HandleAt(p) is not { } hold) return;
        e.Handled = true;
        _drag = (hold.Handle, p);
        Cursor = hold.Cursor;
        e.Pointer.Capture(this);
        DragStarted?.Invoke(this, hold.Handle);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_drag is { } d) Dragged?.Invoke(this, p - d.From);
        else Cursor = HandleAt(p)?.Cursor;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is null) return;
        e.Handled = true;
        End();
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        End();
    }

    private void End()
    {
        if (_drag is null) return;
        _drag = null;
        DragEnded?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }
}
