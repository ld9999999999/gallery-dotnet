using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Gallery.Desktop.Views;

/// <summary>
/// The editor's histogram: brightness in grey behind red, green and blue, on a square-root scale so small counts are
/// visible. Values use the layout of <see cref="Services.EditorFrame.Histogram"/>.
/// </summary>
public sealed class HistogramView : Control
{
    public static readonly StyledProperty<int[]?> ValuesProperty = AvaloniaProperty.Register<HistogramView, int[]?>(nameof(Values));

    private static readonly IBrush Back = new SolidColorBrush(Color.FromRgb(0x15, 0x17, 0x1c));
    private static readonly (int Offset, IBrush Brush)[] Layers =
    [
        (768, new SolidColorBrush(Color.FromArgb(89, 160, 165, 180))),
        (0, new SolidColorBrush(Color.FromArgb(115, 200, 60, 60))),
        (256, new SolidColorBrush(Color.FromArgb(115, 60, 170, 70))),
        (512, new SolidColorBrush(Color.FromArgb(115, 60, 110, 220))),
    ];

    static HistogramView() => AffectsRender<HistogramView>(ValuesProperty);

    public int[]? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        context.DrawRectangle(Back, null, new Rect(0, 0, w, h), 4, 4);
        if (Values is not { Length: >= 1027 } v) return;
        var max = 1;
        for (var i = 1; i < 255; i++) max = Math.Max(max, Math.Max(Math.Max(v[768 + i], v[i]), Math.Max(v[256 + i], v[512 + i])));
        foreach (var (offset, brush) in Layers)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(0, h), true);
                for (var i = 0; i < 256; i++) g.LineTo(new Point(i / 255.0 * w, h - Math.Min(1, Math.Sqrt(v[offset + i] / (double)max)) * (h - 2)));
                g.LineTo(new Point(w, h));
                g.EndFigure(true);
            }
            context.DrawGeometry(brush, null, geometry);
        }
    }
}
