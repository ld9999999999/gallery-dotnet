using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Gallery.Desktop.ViewModels;

namespace Gallery.Desktop.Views;

/// <summary>
/// The editor's view. It tells the view model the size of the picture area, when a slider or curve point is held (so
/// frames render fast and the change is one undo step), and when Before is held. It turns double-clicks and drags on
/// the picture into the 100% view and panning. In crop mode it rotates the picture under the crop frame and passes
/// frame drags on in picture pixels.
/// </summary>
public partial class EditorView : UserControl
{
    private bool _gesture, _turned;
    private Point? _panFrom;
    private double _cropScale = 1; // screen units per picture pixel in crop mode
    private EditorViewModel? _vm;

    public EditorView()
    {
        InitializeComponent();
        Stage.SizeChanged += (_, _) => { PushStage(); LayoutCrop(); };
        Controls.AddHandler(PointerPressedEvent, OnControlPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, _) => EndGesture(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, (_, _) => EndGesture(), RoutingStrategies.Tunnel, handledEventsToo: true);
        Curve.PointsEdited += (_, points) => _vm?.SetCurve(points);

        BeforeButton.AddHandler(PointerPressedEvent, (_, _) => Compare(true), RoutingStrategies.Tunnel, handledEventsToo: true);
        BeforeButton.AddHandler(PointerReleasedEvent, (_, _) => Compare(false), RoutingStrategies.Tunnel, handledEventsToo: true);
        BeforeButton.AddHandler(PointerCaptureLostEvent, (_, _) => Compare(false), RoutingStrategies.Tunnel, handledEventsToo: true);

        Picture.DoubleTapped += OnPictureDoubleTapped;
        Picture.PointerPressed += OnPicturePressed;
        Picture.PointerMoved += OnPictureMoved;
        Picture.PointerReleased += OnPictureReleased;

        CropLayer.DragStarted += (_, handle) => _vm?.BeginCropDrag(handle);
        CropLayer.Dragged += (_, d) => _vm?.DragCrop(d.X / _cropScale, d.Y / _cropScale);
        CropLayer.DragEnded += (_, _) => _vm?.EndCropDrag();
        StraightenSlider.AddHandler(DoubleTappedEvent, (_, _) => _vm?.Level(), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as EditorViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        PushStage();
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // A new frame of the 100% view is already where the drag left it, so clear the drag's offset.
        if (e.PropertyName == nameof(EditorViewModel.FramesShown) && _panFrom is null) Picture.RenderTransform = null;
        if (e.PropertyName is nameof(EditorViewModel.FramesShown) or nameof(EditorViewModel.IsCropping) or nameof(EditorViewModel.ShowsWhole)
            or nameof(EditorViewModel.Crop) or nameof(EditorViewModel.FrameWidth) or nameof(EditorViewModel.FrameHeight)
            or nameof(EditorViewModel.IsStraightening)) LayoutCrop();
        // Set in code because the interpolation mode is not a property that can be bound.
        if (e.PropertyName == nameof(EditorViewModel.Interpolation) && _vm is not null)
        {
            RenderOptions.SetBitmapInterpolationMode(Picture, _vm.Interpolation);
            Picture.InvalidateVisual();
        }
        if (e.PropertyName == nameof(EditorViewModel.IsRecipeFormOpen) && _vm?.IsRecipeFormOpen == true)
            Dispatcher.UIThread.Post(() => RecipeNameBox.Focus(), DispatcherPriority.Input);
    }

    private double Scaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    private void PushStage()
    {
        if (_vm is null || Stage.Bounds.Width < 1) return;
        _vm.SetStage(Stage.Bounds.Width, Stage.Bounds.Height, Scaling);
    }

    private void OnControlPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is null || _gesture || e.Source is not Visual source) return;
        if (source.FindAncestorOfType<Slider>(includeSelf: true) is null && source.FindAncestorOfType<CurveEditor>(includeSelf: true) is null) return;
        _gesture = true;
        _vm.BeginGesture();
    }

    private void EndGesture()
    {
        if (!_gesture) return;
        _gesture = false;
        _vm?.EndGesture();
    }

    private void Compare(bool on)
    {
        if (_vm is { IsLoaded: true } vm) vm.IsComparing = on;
    }

    /// <summary>The pointer's position on the picture, as fractions of its width and height.</summary>
    private (double, double) Fraction(Point p) =>
        (Math.Clamp(p.X / Math.Max(1, Picture.Bounds.Width), 0, 1), Math.Clamp(p.Y / Math.Max(1, Picture.Bounds.Height), 0, 1));

    /// <summary>
    /// In crop mode, once the whole picture is on screen: rotates it to the crop's angle, scaled so all of it fits, and
    /// puts the frame over it, like the web editor's layoutCrop. Otherwise hides the frame and keeps the picture level.
    /// </summary>
    private void LayoutCrop()
    {
        if (_vm is not { IsCropping: true, ShowsWhole: true, CropFrame: { } frame } vm || vm.FrameWidth <= 0 || Stage.Bounds.Width < 1)
        {
            CropLayer.IsVisible = false;
            if (_turned) Picture.RenderTransform = null;
            _turned = false;
            return;
        }
        var crop = vm.Crop;
        double cw = vm.FrameWidth, ch = vm.FrameHeight, rw = Stage.Bounds.Width, rh = Stage.Bounds.Height;
        var r = crop.Angle * Math.PI / 180;
        double cos = Math.Abs(Math.Cos(r)), sin = Math.Abs(Math.Sin(r));
        var k = Math.Min(1, Math.Min(rw / (cw * cos + ch * sin), rh / (cw * sin + ch * cos)));
        Picture.RenderTransform = new MatrixTransform(Matrix.CreateScale(k, k) * Matrix.CreateRotation(r)); // about its middle
        _turned = true;
        var s = _cropScale = k * cw / frame.Width;
        // The picture's middle, in the frame layer's coordinates (that layer also covers the stage's margin).
        double mx = Stage.Bounds.X + rw / 2, my = Stage.Bounds.Y + rh / 2;
        var b = frame.Of(crop);
        CropLayer.Box = new Rect(mx + (b.X - frame.Width / 2) * s, my + (b.Y - frame.Height / 2) * s, b.W * s, b.H * s);
        CropLayer.IsFine = vm.IsStraightening;
        CropLayer.IsVisible = true;
    }

    private void OnPictureDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is not { IsLoaded: true, IsCropping: false } vm) return;
        if (vm.IsZoomed) { _ = vm.UnzoomAsync(); return; }
        var (fx, fy) = Fraction(e.GetPosition(Picture));
        if (vm.PointOf(fx, fy) is { } at) _ = vm.ZoomToAsync(at.X, at.Y);
    }

    private void OnPicturePressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is not { IsZoomed: true } || !e.GetCurrentPoint(Picture).Properties.IsLeftButtonPressed || e.ClickCount > 1) return;
        _panFrom = e.GetPosition(Stage);
        e.Pointer.Capture(Picture);
        Picture.Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    private void OnPictureMoved(object? sender, PointerEventArgs e)
    {
        if (_panFrom is not { } from) return;
        var p = e.GetPosition(Stage);
        Picture.RenderTransform = new TranslateTransform(p.X - from.X, p.Y - from.Y);
    }

    private void OnPictureReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_panFrom is not { } from) return;
        _panFrom = null;
        Picture.Cursor = null;
        e.Pointer.Capture(null);
        var p = e.GetPosition(Stage);
        double dx = p.X - from.X, dy = p.Y - from.Y;
        if (Math.Abs(dx) + Math.Abs(dy) < 4) { Picture.RenderTransform = null; return; }
        _ = _vm?.PanByAsync(dx * Scaling, dy * Scaling);
    }
}
