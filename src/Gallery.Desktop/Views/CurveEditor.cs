using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Gallery.Develop;

namespace Gallery.Desktop.Views;

/// <summary>
/// A tone curve to edit: click to add a point, drag to move it, double-click to remove it. The curve is drawn as
/// Gallery.Develop renders it (a monotone spline), over the picture's brightness histogram. Every edit makes new point
/// arrays (the old ones belong to the undo history) and is reported through <see cref="PointsEdited"/>.
/// </summary>
public sealed class CurveEditor : Control
{
    public static readonly StyledProperty<double[][]?> PointsProperty = AvaloniaProperty.Register<CurveEditor, double[][]?>(nameof(Points));
    public static readonly StyledProperty<string> StrokeProperty = AvaloniaProperty.Register<CurveEditor, string>(nameof(Stroke), "#e6e8ee");
    public static readonly StyledProperty<int[]?> HistogramProperty = AvaloniaProperty.Register<CurveEditor, int[]?>(nameof(Histogram));

    private const double Pad = 10, HitRadius = 12;
    private static readonly IBrush Back = new SolidColorBrush(Color.FromRgb(0x15, 0x17, 0x1c));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromRgb(0x2f, 0x35, 0x42)), 1);
    private static readonly IPen DiagonalPen = new Pen(new SolidColorBrush(Color.FromRgb(0x3a, 0x41, 0x50)), 1);
    private static readonly IBrush Bars = new SolidColorBrush(Color.FromArgb(46, 140, 147, 167));
    private int _drag = -1;

    static CurveEditor() => AffectsRender<CurveEditor>(PointsProperty, StrokeProperty, HistogramProperty);

    public double[][]? Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    /// <summary>The curve's colour (the channel's), as a colour string.</summary>
    public string Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    /// <summary>Histogram values (as <see cref="HistogramView.Values"/>); the brightness part is drawn behind the curve.</summary>
    public int[]? Histogram { get => GetValue(HistogramProperty); set => SetValue(HistogramProperty, value); }

    /// <summary>The points after an edit: a point added, moved or removed.</summary>
    public event EventHandler<double[][]>? PointsEdited;

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = Math.Min(256, double.IsInfinity(availableSize.Width) ? 256 : availableSize.Width);
        return new Size(side, side);
    }

    private double Side => Math.Min(Bounds.Width, Bounds.Height) - 2 * Pad;
    private Point ToScreen(double[] p) => new(Pad + p[0] * Side, Pad + (1 - p[1]) * Side);
    private (double X, double Y) FromScreen(Point p) => (Math.Clamp((p.X - Pad) / Side, 0, 1), Math.Clamp(1 - (p.Y - Pad) / Side, 0, 1));

    public override void Render(DrawingContext context)
    {
        var s = Side;
        if (s <= 0) return;
        context.DrawRectangle(Back, null, new Rect(0, 0, Bounds.Width, Bounds.Height), 4, 4);
        for (var i = 0; i <= 4; i++)
        {
            var v = Pad + i * s / 4;
            context.DrawLine(GridPen, new Point(v, Pad), new Point(v, Pad + s));
            context.DrawLine(GridPen, new Point(Pad, v), new Point(Pad + s, v));
        }
        if (Histogram is { Length: >= 1024 } h)
        {
            var max = 1;
            for (var i = 1; i < 255; i++) max = Math.Max(max, h[768 + i]);
            for (var i = 0; i < 256; i++)
            {
                var bar = Math.Sqrt(h[768 + i] / (double)max) * s;
                context.FillRectangle(Bars, new Rect(Pad + i / 256.0 * s, Pad + s - bar, s / 256 + 0.5, bar));
            }
        }
        context.DrawLine(DiagonalPen, new Point(Pad, Pad + s), new Point(Pad + s, Pad));
        if (Points is not { Length: >= 2 } points) return;
        var brush = new SolidColorBrush(Color.Parse(Stroke));
        var table = ToneCurve.Spline(points, 1024);
        var curve = new StreamGeometry();
        using (var g = curve.Open())
        {
            for (var i = 0; i <= 200; i++)
            {
                var x = i / 200.0;
                var p = ToScreen([x, ToneCurve.Lookup(table, x)]);
                if (i == 0) g.BeginFigure(p, false); else g.LineTo(p);
            }
            g.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(brush, 2), curve);
        foreach (var p in points) context.DrawEllipse(brush, null, ToScreen(p), 4.5, 4.5);
    }

    private int Nearest(Point at)
    {
        if (Points is not { } points) return -1;
        var best = -1;
        var dist = HitRadius;
        for (var i = 0; i < points.Length; i++)
        {
            var p = ToScreen(points[i]);
            var d = Math.Sqrt((p.X - at.X) * (p.X - at.X) + (p.Y - at.Y) * (p.Y - at.Y));
            if (d < dist) { dist = d; best = i; }
        }
        return best;
    }

    private void Edited(double[][] points) => PointsEdited?.Invoke(this, points);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Points is not { } points || !IsEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var at = e.GetPosition(this);
        var i = Nearest(at);
        e.Handled = true;
        if (e.ClickCount >= 2)
        {
            // Double-click: remove a point (never the two ends).
            _drag = -1;
            if (i > 0 && i < points.Length - 1) Edited(points.Where((_, k) => k != i).ToArray());
            return;
        }
        if (i < 0)
        {
            var (x, y) = FromScreen(at);
            if (x <= 0.01 || x >= 0.99) return;
            var added = points.Append([Math.Round(x, 4), Math.Round(y, 4)]).OrderBy(p => p[0]).ToArray();
            i = Array.FindIndex(added, p => p[0] == Math.Round(x, 4));
            Edited(added);
        }
        _drag = i;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag < 0 || Points is not { } points || _drag >= points.Length) return;
        var (x, y) = FromScreen(e.GetPosition(this));
        var i = _drag;
        if (i == 0) x = 0;
        else if (i == points.Length - 1) x = 1;
        else x = Math.Min(points[i + 1][0] - 0.01, Math.Max(points[i - 1][0] + 0.01, x));
        var moved = points.Select(p => p).ToArray();
        moved[i] = [Math.Round(x, 4), Math.Round(y, 4)];
        Edited(moved);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _drag = -1;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag = -1;
    }
}
