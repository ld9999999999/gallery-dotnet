using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Gallery.Desktop.Services;

namespace Gallery.Desktop.Views;

/// <summary>
/// A 360° photo seen from the middle of its sphere (<see cref="SphereRenderer"/>), looking <see cref="Yaw"/> degrees
/// right and <see cref="Pitch"/> up, with <see cref="Fov"/> degrees across the longer side. Flow for each change:
/// 1. Draw at once at a smaller size (picked so a frame takes about 20 ms on this machine) and scale it up.
/// 2. When the view stays still for a moment, draw a frame at the screen's full resolution, off the UI thread.
/// <see cref="Revision"/> asks for a new frame when the renderer's picture changed but the renderer did not (the seam
/// distances arrived).
/// </summary>
public sealed class SphereView : Control
{
    public static readonly StyledProperty<SphereRenderer?> RendererProperty = AvaloniaProperty.Register<SphereView, SphereRenderer?>(nameof(Renderer));
    public static readonly StyledProperty<double> YawProperty = AvaloniaProperty.Register<SphereView, double>(nameof(Yaw));
    public static readonly StyledProperty<double> PitchProperty = AvaloniaProperty.Register<SphereView, double>(nameof(Pitch));
    public static readonly StyledProperty<double> FovProperty = AvaloniaProperty.Register<SphereView, double>(nameof(Fov), SphereRenderer.StartFov);
    public static readonly StyledProperty<int> RevisionProperty = AvaloniaProperty.Register<SphereView, int>(nameof(Revision));

    public SphereRenderer? Renderer { get => GetValue(RendererProperty); set => SetValue(RendererProperty, value); }
    public double Yaw { get => GetValue(YawProperty); set => SetValue(YawProperty, value); }
    public double Pitch { get => GetValue(PitchProperty); set => SetValue(PitchProperty, value); }
    public double Fov { get => GetValue(FovProperty); set => SetValue(FovProperty, value); }
    public int Revision { get => GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }

    private const double QuickBudgetMs = 20;
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(150);
    private WriteableBitmap? _frame;
    private bool _frameSharp;
    /// <summary>Side length of the quick frames, as a share of the screen's pixels; adjusted to fit <see cref="QuickBudgetMs"/>.</summary>
    private double _quickScale = 0.5;
    private int _generation;
    private DispatcherTimer? _settle;

    static SphereView()
    {
        AffectsRender<SphereView>(RendererProperty);
        ClipToBoundsProperty.OverrideDefaultValue<SphereView>(true);
    }

    /// <summary>The size of the last frame drawn, in pixels, and whether it was at full resolution (for tests).</summary>
    public (int Width, int Height, bool Sharp) LastFrame => _frame is { } f ? (f.PixelSize.Width, f.PixelSize.Height, _frameSharp) : (0, 0, false);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == YawProperty || change.Property == PitchProperty || change.Property == FovProperty) Moved();
        else if (change.Property == RendererProperty || change.Property == RevisionProperty || change.Property == BoundsProperty) Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _generation++;
        _settle?.Stop();
        _frame?.Dispose();
        _frame = null;
    }

    private double Scaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    /// <summary>The view moved: draw a quick frame now, and the sharp one once it stays still.</summary>
    private void Moved()
    {
        if (Renderer is null || Bounds.Width < 1) return;
        _generation++;
        DrawQuick();
        _settle ??= new DispatcherTimer(SettleDelay, DispatcherPriority.Background, (_, _) => { _settle!.Stop(); _ = DrawSharpAsync(); });
        _settle.Stop();
        _settle.Start();
    }

    /// <summary>A new picture or size: a quick frame at once (so the screen is never blank), then the sharp one.</summary>
    private void Refresh()
    {
        _generation++;
        _settle?.Stop();
        if (Renderer is null || Bounds.Width < 1) { InvalidateVisual(); return; }
        DrawQuick();
        _ = DrawSharpAsync();
    }

    private (int W, int H) Pixels(double scale) =>
        (Math.Max(1, (int)Math.Round(Bounds.Width * Scaling * scale)), Math.Max(1, (int)Math.Round(Bounds.Height * Scaling * scale)));

    private void DrawQuick()
    {
        if (Renderer is not { } r) return;
        var (w, h) = Pixels(_quickScale);
        var pixels = new int[w * h];
        var clock = Stopwatch.StartNew();
        r.Render(pixels, w, h, w, Yaw, Pitch, Fov, Environment.ProcessorCount);
        // Keep frames near the time budget: smaller when slow, larger again (up to half the screen's pixels across) when fast.
        var ms = clock.Elapsed.TotalMilliseconds;
        _quickScale = Math.Clamp(_quickScale * Math.Sqrt(QuickBudgetMs / Math.Max(1, ms)), 0.2, 0.5);
        Show(pixels, w, h, sharp: false);
    }

    private async Task DrawSharpAsync()
    {
        if (Renderer is not { } r) return;
        var gen = _generation;
        var (w, h) = Pixels(1);
        double yaw = Yaw, pitch = Pitch, fov = Fov;
        var pixels = await Task.Run(() =>
        {
            var p = new int[w * h];
            r.Render(p, w, h, w, yaw, pitch, fov, Environment.ProcessorCount);
            return p;
        });
        if (gen == _generation && Renderer == r) Show(pixels, w, h, sharp: true);
    }

    private void Show(int[] pixels, int w, int h, bool sharp)
    {
        if (_frame is null || _frame.PixelSize.Width != w || _frame.PixelSize.Height != h)
        {
            _frame?.Dispose();
            _frame = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        }
        using (var fb = _frame.Lock())
        {
            if (fb.RowBytes == w * 4) Marshal.Copy(pixels, 0, fb.Address, w * h);
            else for (var y = 0; y < h; y++) Marshal.Copy(pixels, y * w, fb.Address + y * fb.RowBytes, w);
        }
        _frameSharp = sharp;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var area = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.Black, area);
        if (_frame is not { } f || Renderer is null) return;
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.MediumQuality }))
            context.DrawImage(f, new Rect(f.Size), area);
    }
}
