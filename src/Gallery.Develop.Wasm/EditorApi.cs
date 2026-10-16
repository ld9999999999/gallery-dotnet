using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

namespace Gallery.Develop.Wasm;

/// <summary>
/// The functions the editor's Web Worker calls (wwwroot/develop-worker.js): load an editing copy, fit it to the view,
/// render settings to RGBA, read the histogram. One picture at a time. The picture shown is cropped and straightened as
/// the settings say (the copy is made again only when the crop changes). While the crop is being set, show the whole picture.
/// </summary>
public static partial class EditorApi
{
    private static LinearImage? _source;
    private static LinearImage? _shaped; // the source, cropped and straightened: the picture as edited
    private static CropSettings _shapedFor = new();
    private static int _maxWidth = int.MaxValue, _maxHeight = int.MaxValue;
    // Copies fitted to the view, and at half that size for frames while a slider is dragged.
    // One set for the cropped picture, one for the whole.
    private static LinearImage? _view, _quick, _wholeView, _wholeQuick;
    private static readonly Renderer Renderer = new();
    private static readonly Histogram Histogram = new();
    private static byte[] _rgba = [];
    private static string? _settingsJson;
    private static DevelopSettings _settings = DevelopSettings.Default;

    /// <summary>Reads a .gdev file (already decompressed) and returns its header as JSON.</summary>
    [JSExport]
    public static string Load(byte[] gdev)
    {
        _source = _shaped = LinearImage.FromGdev(gdev);
        _shapedFor = new CropSettings();
        _view = _quick = _wholeView = _wholeQuick = null;
        return JsonSerializer.Serialize(_source.Header, DevelopJson.Default.DevelopHeader);
    }

    /// <summary>Sets the space for the picture (it is never made larger). Returns "width,height" of the cropped, fitted picture.</summary>
    [JSExport]
    public static string Fit(int maxWidth, int maxHeight)
    {
        if (_source is null) throw new InvalidOperationException("no picture loaded");
        (_maxWidth, _maxHeight) = (Math.Max(1, maxWidth), Math.Max(1, maxHeight));
        _view = _quick = _wholeView = _wholeQuick = null;
        var view = Views(whole: false).View;
        return $"{view.Width},{view.Height}";
    }

    /// <summary>The fitted copies of the whole picture or the cropped picture (made when first needed).</summary>
    private static (LinearImage View, LinearImage Quick) Views(bool whole)
    {
        var picture = whole ? _source! : _shaped!;
        ref var view = ref whole ? ref _wholeView : ref _view;
        ref var quick = ref whole ? ref _wholeQuick : ref _quick;
        view ??= picture.FitWithin(_maxWidth, _maxHeight);
        quick ??= view.FitWithin(Math.Max(1, view.Width / 2), Math.Max(1, view.Height / 2));
        return (view, quick);
    }

    /// <summary>Crops and straightens the picture as the settings say, but only when the crop changed.</summary>
    private static void Shape(CropSettings crop)
    {
        var key = crop with { Aspect = null }; // the ratio lock does not change what is drawn
        if (key == _shapedFor) return;
        _shaped = Geometry.Apply(_source!, key);
        _shapedFor = key;
        _view = _quick = null;
    }

    private static void Use(string settingsJson)
    {
        if (settingsJson == _settingsJson) return;
        _settings = DevelopSettings.Parse(settingsJson);
        _settingsJson = settingsJson;
    }

    /// <summary>
    /// Renders the fitted picture with the settings (JSON) and returns its RGBA bytes. <paramref name="quick"/> renders
    /// the half-size copy (while a slider moves). <paramref name="whole"/> renders the whole picture, not cropped or
    /// turned (while the crop is set). The rendered size is in <see cref="LastSize"/>, the full fitted size in
    /// <see cref="ViewSize"/>.
    /// </summary>
    [JSExport]
    public static byte[] Render(string settingsJson, bool quick, bool whole)
    {
        if (_source is null) throw new InvalidOperationException("no picture loaded");
        Use(settingsJson);
        if (!whole) Shape(_settings.Crop);
        var (full, half) = Views(whole);
        var view = quick ? half : full;
        _lastSize = $"{view.Width},{view.Height}";
        _viewSize = $"{full.Width},{full.Height}";
        var size = view.Width * view.Height * 4;
        if (_rgba.Length != size) _rgba = new byte[size];
        Renderer.Render(view, _settings, _rgba, Histogram, whole ? _source : _shaped);
        return _rgba;
    }

    private static string _lastSize = "0,0", _viewSize = "0,0";

    /// <summary>"width,height" of the full-size frame of the last render (a quick render is half of this).</summary>
    [JSExport]
    public static string ViewSize() => _viewSize;

    private static LinearImage? _detail;
    private static byte[] _detailRgba = [];

    /// <summary>Reads a full-resolution crop (.gdev) for the 100% view. Returns "width,height".</summary>
    [JSExport]
    public static string LoadDetail(byte[] gdev)
    {
        _detail = LinearImage.FromGdev(gdev);
        return $"{_detail.Width},{_detail.Height}";
    }

    /// <summary>Renders the 100% crop. Highlights and shadows are judged on the whole picture, as in the preview.</summary>
    [JSExport]
    public static byte[] RenderDetail(string settingsJson)
    {
        var detail = _detail ?? throw new InvalidOperationException("no crop loaded");
        Use(settingsJson);
        Shape(_settings.Crop); // the crop was cut as the settings say, so judge it against the same picture
        var size = detail.Width * detail.Height * 4;
        if (_detailRgba.Length != size) _detailRgba = new byte[size];
        Renderer.Render(detail, _settings, _detailRgba, null, _shaped);
        return _detailRgba;
    }

    /// <summary>"width,height" of the last render.</summary>
    [JSExport]
    public static string LastSize() => _lastSize;

    /// <summary>The last render's histogram: red, green, blue, brightness (256 counts each), then pixels counted, clipped highlights, clipped shadows.</summary>
    [JSExport]
    [return: JSMarshalAs<JSType.Array<JSType.Number>>]
    public static int[] LastHistogram()
    {
        var h = Histogram;
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
