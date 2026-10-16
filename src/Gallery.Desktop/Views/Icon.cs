using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Gallery.Desktop.Views;

/// <summary>
/// A line icon drawn from path data on a 24×24 grid, scaled to the control's size, in the inherited foreground colour.
/// Use it instead of symbol characters, which many fonts (and so many systems) do not have.
/// </summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));
    public static readonly StyledProperty<bool> FilledProperty = AvaloniaProperty.Register<Icon, bool>(nameof(Filled));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<Icon>();

    static Icon() => AffectsRender<Icon>(DataProperty, FilledProperty, ForegroundProperty);

    public Geometry? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    /// <summary>Fill the shape instead of drawing its outline.</summary>
    public bool Filled { get => GetValue(FilledProperty); set => SetValue(FilledProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(double.IsNaN(Width) ? 18 : Width, double.IsNaN(Height) ? 18 : Height);

    public override void Render(DrawingContext context)
    {
        if (Data is not { } data || Foreground is not { } brush) return;
        var size = Math.Min(Bounds.Width, Bounds.Height);
        var scale = size / 24;
        var transform = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation((Bounds.Width - size) / 2, (Bounds.Height - size) / 2);
        using (context.PushTransform(transform))
        {
            if (Filled) context.DrawGeometry(brush, null, data);
            else context.DrawGeometry(null, new Pen(brush, 1.9, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), data);
        }
    }
}
