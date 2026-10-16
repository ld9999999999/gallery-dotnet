using System.IO.Compression;
using Gallery.Develop;

namespace Gallery.Desktop.Services;

/// <summary>
/// A rendered frame. <see cref="Pixels"/> (RGBA) is the renderer's own buffer and the next render reuses it, so copy it
/// out before asking for another. The histogram has the same layout as the web editor's: red, green, blue and brightness
/// (256 counts each), then pixels counted, clipped highlights, clipped shadows. A crop for a zoomed view (<see cref="Detail"/>)
/// is shown at <see cref="Scale"/> screen pixels per picture pixel: 1 for 100%, 2 for 200%. Any other frame fills
/// <see cref="ViewWidth"/> × <see cref="ViewHeight"/> device pixels, the size of a full frame (a quick one is half that).
/// It shows the cropped picture, or the whole picture when <see cref="Whole"/> (while the crop is being set).
/// </summary>
public sealed record EditorFrame(byte[] Pixels, int Width, int Height, int[] Histogram, double Milliseconds, bool Quick, bool Detail, int Scale,
    bool Whole, int ViewWidth, int ViewHeight);

/// <summary>
/// The editor's pictures and renderer (docs/raw-editor.md, phase 7), matching the web editor's (Gallery.Develop.Wasm's
/// EditorApi). It holds the editing copy, that copy cropped and straightened by the settings (cut again only when the
/// crop changes), a window-sized copy of each, half-size copies for frames while a slider moves, and a full-size crop
/// for the 100% and 200% views. Renders on all but one of the processor's threads. Fitting and rendering take turns;
/// a zoomed view's crop can be replaced from any thread at any time.
/// </summary>
public sealed class EditorRenderer
{
    private static readonly int Threads = Math.Max(1, Environment.ProcessorCount - 1);
    private readonly Renderer _renderer = new() { Parallelism = Threads };
    private readonly Histogram _histogram = new();
    private readonly Lock _gate = new();
    private LinearImage? _source;
    private LinearImage? _shaped; // the source cropped and straightened: the picture as edited
    private CropSettings _shapedFor = new();
    private int _maxWidth = int.MaxValue, _maxHeight = int.MaxValue;
    // Window-sized copies, and half-size ones for frames while a slider moves: of the shaped picture and of the whole one.
    private LinearImage? _view, _quick, _wholeView, _wholeQuick;
    private Crop? _detail;

    /// <summary>A crop and how many screen pixels each of its pixels takes. Replaced together so a render always sees a matching pair.</summary>
    private sealed record Crop(LinearImage Image, int Scale);
    private byte[] _pixels = [];

    public DevelopHeader? Header => _source?.Header;
    public bool IsLoaded => _source is not null;
    public bool HasDetail => _detail is not null;

    /// <summary>The frame sizes in use (fitted, quick, whole picture, zoomed crop), so buffers of other sizes can be freed.</summary>
    public IReadOnlySet<(int Width, int Height)> FrameSizes =>
        new[] { _view, _quick, _wholeView, _wholeQuick, _detail?.Image }.OfType<LinearImage>().Select(i => (i.Width, i.Height)).ToHashSet();

    /// <summary>Reads an editing copy in the develop service's cache format (a gzip-compressed .gdev file).</summary>
    public static LinearImage ReadEditingCopy(string path)
    {
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return LinearImage.FromGdev(ms.ToArray());
    }

    public void Load(LinearImage source)
    {
        lock (_gate)
        {
            _source = _shaped = source;
            _shapedFor = new CropSettings();
            _view = _quick = _wholeView = _wholeQuick = null;
            _detail = null;
        }
    }

    /// <summary>Sets the space for the picture in device pixels (it is never enlarged). The fitted copies are remade when next needed.</summary>
    public void Fit(int maxWidth, int maxHeight)
    {
        lock (_gate)
        {
            if (_source is null) throw new InvalidOperationException("no picture loaded");
            (_maxWidth, _maxHeight) = (Math.Max(1, maxWidth), Math.Max(1, maxHeight));
            _view = _quick = _wholeView = _wholeQuick = null;
        }
    }

    /// <summary>The fitted copies of the whole picture or of the picture as cropped (made when first needed).</summary>
    private (LinearImage View, LinearImage Quick) Views(bool whole)
    {
        var picture = whole ? _source! : _shaped!;
        ref var view = ref whole ? ref _wholeView : ref _view;
        ref var quick = ref whole ? ref _wholeQuick : ref _quick;
        view ??= picture.FitWithin(_maxWidth, _maxHeight);
        quick ??= view.FitWithin(Math.Max(1, view.Width / 2), Math.Max(1, view.Height / 2));
        return (view, quick);
    }

    /// <summary>Crops and straightens the picture by the settings, if they changed.</summary>
    private void Shape(CropSettings crop)
    {
        var key = crop.Clamped() with { Aspect = null }; // the ratio lock does not change what is drawn
        if (key == _shapedFor) return;
        _shaped = Geometry.Apply(_source!, key, Threads);
        _shapedFor = key;
        _view = _quick = null;
    }

    /// <summary>
    /// Sets a full-size crop for a zoomed view (read from the develop service's .gdev, cut from the cropped picture),
    /// shown at <paramref name="scale"/> screen pixels per picture pixel.
    /// </summary>
    public void LoadDetail(LinearImage crop, int scale = 1) => _detail = new Crop(crop, scale);

    public void ClearDetail() => _detail = null;

    /// <summary>
    /// Renders the zoomed crop when there is one. Otherwise renders the fitted cropped picture (its half-size copy when
    /// <paramref name="quick"/>), or the whole picture, not cropped or rotated, when <paramref name="whole"/> (while the
    /// crop is being set). Highlights and shadows are measured on all of what is shown: the cropped picture (also for a
    /// zoomed crop, which was cut from it), or the whole picture while the crop is being set.
    /// </summary>
    public EditorFrame Render(DevelopSettings settings, bool quick, bool whole = false)
    {
        lock (_gate)
        {
            if (_source is null) throw new InvalidOperationException("no picture loaded");
            var detail = _detail;
            whole &= detail is null;
            if (!whole) Shape(settings.Crop);
            LinearImage image;
            var (viewW, viewH) = (0, 0);
            if (detail is not null) image = detail.Image;
            else
            {
                var (full, half) = Views(whole);
                image = quick ? half : full;
                (viewW, viewH) = (full.Width, full.Height);
            }
            var size = image.Width * image.Height * 4;
            if (_pixels.Length != size) _pixels = new byte[size];
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            _renderer.Render(image, settings, _pixels, detail is null ? _histogram : null, whole ? _source : _shaped);
            var ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return new EditorFrame(_pixels, image.Width, image.Height, detail is null ? HistogramArray() : [], ms, quick && detail is null, detail is not null,
                detail?.Scale ?? 1, whole, viewW, viewH);
        }
    }

    private int[] HistogramArray()
    {
        var h = _histogram;
        var all = new int[256 * 4 + 3];
        h.Red.CopyTo(all, 0);
        h.Green.CopyTo(all, 256);
        h.Blue.CopyTo(all, 512);
        h.Luma.CopyTo(all, 768);
        all[1024] = h.Pixels;
        all[1025] = h.ClippedHighlights;
        all[1026] = h.ClippedShadows;
        return all;
    }
}
