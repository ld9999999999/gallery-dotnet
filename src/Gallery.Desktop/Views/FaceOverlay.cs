using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using Gallery.Core.Services.Faces;

namespace Gallery.Desktop.Views;

/// <summary>
/// Boxes over the faces in the viewer's photo, with names under them. Place it over an Image with Stretch="Uniform" and
/// the same margin, and it puts the boxes where the image draws the picture. Clicking a box runs
/// <see cref="Command"/> with the face. Clicks anywhere else go through to what is below.
/// </summary>
public sealed class FaceOverlay : Control, ICustomHitTest
{
    public static readonly StyledProperty<IReadOnlyList<FaceInfo>?> FacesProperty = AvaloniaProperty.Register<FaceOverlay, IReadOnlyList<FaceInfo>?>(nameof(Faces));
    public static readonly StyledProperty<IImage?> SourceProperty = AvaloniaProperty.Register<FaceOverlay, IImage?>(nameof(Source));
    public static readonly StyledProperty<FaceInfo?> SelectedFaceProperty = AvaloniaProperty.Register<FaceOverlay, FaceInfo?>(nameof(SelectedFace));
    public static readonly StyledProperty<ICommand?> CommandProperty = AvaloniaProperty.Register<FaceOverlay, ICommand?>(nameof(Command));

    private static readonly IPen Known = new Pen(Brushes.White, 2);
    private static readonly IPen Unknown = new Pen(new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)), 2, DashStyle.Dash);
    private static readonly IPen Selected = new Pen(new SolidColorBrush(Color.FromRgb(0x4f, 0x8c, 0xff)), 3);
    private static readonly IPen Shadow = new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 4);
    private static readonly IBrush LabelBack = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0));

    static FaceOverlay() => AffectsRender<FaceOverlay>(FacesProperty, SourceProperty, SelectedFaceProperty);

    public IReadOnlyList<FaceInfo>? Faces { get => GetValue(FacesProperty); set => SetValue(FacesProperty, value); }
    public IImage? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public FaceInfo? SelectedFace { get => GetValue(SelectedFaceProperty); set => SetValue(SelectedFaceProperty, value); }
    public ICommand? Command { get => GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

    /// <summary>Where the picture is drawn: fitted inside the bounds and centred, like Stretch="Uniform".</summary>
    private Rect PictureRect()
    {
        if (Source is not { } src || src.Size.Width <= 0 || src.Size.Height <= 0) return default;
        var b = Bounds.Size;
        var scale = Math.Min(b.Width / src.Size.Width, b.Height / src.Size.Height);
        var w = src.Size.Width * scale;
        var h = src.Size.Height * scale;
        return new Rect((b.Width - w) / 2, (b.Height - h) / 2, w, h);
    }

    private static Rect BoxOf(FaceInfo f, Rect picture) =>
        new(picture.X + f.X1 * picture.Width, picture.Y + f.Y1 * picture.Height, (f.X2 - f.X1) * picture.Width, (f.Y2 - f.Y1) * picture.Height);

    public override void Render(DrawingContext context)
    {
        var picture = PictureRect();
        if (Faces is not { Count: > 0 } faces || picture.Width <= 0) return;
        foreach (var f in faces)
        {
            var box = BoxOf(f, picture);
            context.DrawRectangle(null, Shadow, box, 3, 3);
            context.DrawRectangle(null, SelectedFace?.Id == f.Id ? Selected : f.PersonId is null ? Unknown : Known, box, 3, 3);
            if (f.PersonName is not { } name) continue;
            var text = new FormattedText(name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.White);
            var label = new Rect(box.Center.X - text.Width / 2 - 6, box.Bottom + 3, text.Width + 12, text.Height + 2);
            context.DrawRectangle(LabelBack, null, label, 4, 4);
            context.DrawText(text, new Point(label.X + 6, label.Y + 1));
        }
    }

    private FaceInfo? FaceAt(Point p)
    {
        var picture = PictureRect();
        if (Faces is not { Count: > 0 } faces || picture.Width <= 0) return null;
        // Pick the smallest box under the pointer, so a face inside a larger box can still be picked.
        return faces.Where(f => BoxOf(f, picture).Contains(p)).OrderBy(f => (f.X2 - f.X1) * (f.Y2 - f.Y1)).FirstOrDefault();
    }

    public bool HitTest(Point point) => IsVisible && FaceAt(point) is not null;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (FaceAt(e.GetPosition(this)) is not { } face) return;
        if (Command?.CanExecute(face) == true) Command.Execute(face);
        e.Handled = true;
    }
}
