using Gallery.Core.Models;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.Formats.Png;
using MetadataExtractor.Formats.QuickTime;
using MetadataExtractor.Formats.WebP;
using MetadataExtractor.Formats.Heif;
using MetadataExtractor.Formats.Gif;
using MetadataExtractor.Formats.Bmp;

namespace Gallery.Core.Services;

public sealed record MediaMetadata(DateTime? TakenAt, int? Width, int? Height, double? DurationSec,
    string? VideoCodec, string? AudioCodec, string? Container, PanoramaHint Panorama = PanoramaHint.None)
{
    /// <summary>"equirectangular" for a 360° photo, else null. It depends on the size, which can be corrected after reading.</summary>
    public string? Projection => Services.Projection.Decide(Panorama, Width, Height);
}

/// <summary>Reads EXIF and container metadata. Images go through MetadataExtractor, which only reads the header and so is fast; video and audio use ffprobe.</summary>
public sealed class MetadataService
{
    private readonly Ffmpeg _ffmpeg;
    private readonly HeifDecoder? _heif;
    private readonly ILogger<MetadataService> _log;

    /// <summary>Picture formats ImageSharp can't read. When the metadata has no size, ffprobe supplies it.</summary>
    private static readonly HashSet<string> FfmpegImageFormats = new(StringComparer.OrdinalIgnoreCase) { "heic", "heif", "avif" };

    /// <param name="heif">libheif, which reports a HEIC photo's size as displayed. Without it the size comes from the metadata or ffprobe.</param>
    public MetadataService(Ffmpeg ffmpeg, ILogger<MetadataService> log, HeifDecoder? heif = null)
    {
        _ffmpeg = ffmpeg;
        _heif = heif;
        _log = log;
    }

    public async Task<MediaMetadata> ReadAsync(string path, string kind, CancellationToken ct = default)
    {
        if (kind == MediaKind.Image)
        {
            var m = ReadImage(path);
            var ext = Path.GetExtension(path).TrimStart('.');
            // libheif reports the size its decoder produces: the whole grid, turned the way the photo is displayed.
            if (_heif is not null && HeifDecoder.Formats.Contains(ext) && await _heif.ProbeSizeAsync(path, ct) is { } size)
                return m with { Width = size.Width, Height = size.Height };
            if (m.Width is null && _ffmpeg.IsAvailable && FfmpegImageFormats.Contains(ext))
            {
                // A phone's HEIC or AVIF is usually a grid of tiles, so take the size of the whole grid.
                if (await _ffmpeg.ProbeTileGridAsync(path, ct) is { } grid)
                    m = grid.Rotation is 90 or 270 ? m with { Width = grid.Height, Height = grid.Width } : m with { Width = grid.Width, Height = grid.Height };
                else if (await _ffmpeg.ProbeAsync(path, ct) is { } p)
                    m = m with { Width = p.Width, Height = p.Height };
            }
            return m;
        }
        // An AVI keeps its capture date in its header (EXIF, IDIT or ICRD), and ffprobe reads few of those.
        var avi = AviDate.IsAvi(path) ? AviDate.Read(path) : null;
        var probe = await _ffmpeg.ProbeAsync(path, ct);
        if (probe is null)
        {
            // ffprobe is missing or failed, so for mov/mp4 fall back to the QuickTime atoms via MetadataExtractor
            var qt = avi ?? ReadQuickTime(path);
            return new MediaMetadata(qt, null, null, null, null, null, null);
        }
        return new MediaMetadata(avi ?? probe.CreationTime, probe.Width, probe.Height, probe.DurationSec, probe.VideoCodec, probe.AudioCodec, probe.Container);
    }

    /// <summary>
    /// Reads all metadata for the info panel straight from the file on request (nothing is stored). Images use
    /// MetadataExtractor (EXIF, maker notes, GPS, XMP, IPTC…). Video and audio use ffprobe, or MetadataExtractor when
    /// ffprobe is missing.
    /// </summary>
    public async Task<AssetMetadata> ReadDetailsAsync(Asset asset, CancellationToken ct = default)
    {
        if (asset.Kind != MediaKind.Image)
        {
            var json = await _ffmpeg.ProbeJsonAsync(asset.Path, ct);
            if (json is not null)
            {
                try { return MetadataDetails.FromProbeJson(json); }
                catch (Exception ex) { _log.LogDebug(ex, "ffprobe metadata parse failed for {Path}", asset.Path); }
            }
        }
        try
        {
            var dirs = await Task.Run(() => ImageMetadataReader.ReadMetadata(asset.Path), ct);
            return MetadataDetails.FromDirectories(dirs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "metadata read failed for {Path}", asset.Path);
            return new AssetMetadata(new(), null, new());
        }
    }

    public MediaMetadata ReadImage(string path)
    {
        try
        {
            var dirs = ImageMetadataReader.ReadMetadata(path);
            DateTime? taken = null;
            int? w = null, h = null;
            int orientation = 1;

            foreach (var sub in dirs.OfType<ExifSubIfdDirectory>())
            {
                // A date in the future means the camera's clock was wrong; skip it and try the next.
                if (taken is null && sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dto) && CaptureDate.IsPlausible(dto)) taken = dto;
                if (taken is null && sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out var dtd) && CaptureDate.IsPlausible(dtd)) taken = dtd;
                if (w is null && sub.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out var ew) && sub.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out var eh) && ew > 0 && eh > 0) { w = ew; h = eh; }
            }
            foreach (var ifd0 in dirs.OfType<ExifIfd0Directory>())
            {
                // When the file was last changed. An editor that fixes a date saves it here too.
                if (taken is null && ifd0.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var dt) && CaptureDate.IsPlausible(dt)) taken = dt;
                if (ifd0.TryGetInt32(ExifDirectoryBase.TagOrientation, out var o)) orientation = o;
                if (w is null && ifd0.TryGetInt32(ExifDirectoryBase.TagImageWidth, out var iw) && ifd0.TryGetInt32(ExifDirectoryBase.TagImageHeight, out var ih) && iw > 0 && ih > 0) { w = iw; h = ih; }
            }
            // The container's size is the real size of the displayed image, so it wins.
            var jpeg = dirs.OfType<JpegDirectory>().FirstOrDefault();
            if (jpeg is not null && jpeg.TryGetInt32(JpegDirectory.TagImageWidth, out var jw) && jpeg.TryGetInt32(JpegDirectory.TagImageHeight, out var jh)) { w = jw; h = jh; }
            var png = dirs.OfType<PngDirectory>().FirstOrDefault(d => d.ContainsTag(PngDirectory.TagImageWidth));
            if (png is not null && png.TryGetInt32(PngDirectory.TagImageWidth, out var pw) && png.TryGetInt32(PngDirectory.TagImageHeight, out var ph)) { w = pw; h = ph; }
            var webp = dirs.OfType<WebPDirectory>().FirstOrDefault();
            if (webp is not null && webp.TryGetInt32(WebPDirectory.TagImageWidth, out var ww) && webp.TryGetInt32(WebPDirectory.TagImageHeight, out var wh)) { w = ww; h = wh; }
            var gif = dirs.OfType<GifHeaderDirectory>().FirstOrDefault();
            if (gif is not null && gif.TryGetInt32(GifHeaderDirectory.TagImageWidth, out var gw) && gif.TryGetInt32(GifHeaderDirectory.TagImageHeight, out var gh)) { w = gw; h = gh; }
            var bmp = dirs.OfType<BmpHeaderDirectory>().FirstOrDefault();
            if (bmp is not null && bmp.TryGetInt32(BmpHeaderDirectory.TagImageWidth, out var bw) && bmp.TryGetInt32(BmpHeaderDirectory.TagImageHeight, out var bh)) { w = bw; h = Math.Abs(bh); }

            if (orientation is >= 5 and <= 8 && w is not null) (w, h) = (h, w);
            return new MediaMetadata(taken, w, h, null, null, null, null, Projection.ReadHint(dirs));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "metadata read failed for {Path}", path);
            return new MediaMetadata(null, null, null, null, null, null, null);
        }
    }

    private DateTime? ReadQuickTime(string path)
    {
        try
        {
            var dirs = ImageMetadataReader.ReadMetadata(path);
            var mvhd = dirs.OfType<QuickTimeMovieHeaderDirectory>().FirstOrDefault();
            if (mvhd is not null && mvhd.TryGetDateTime(QuickTimeMovieHeaderDirectory.TagCreated, out var dt) && dt.Year > 1980) return dt.ToLocalTime();
        }
        catch { }
        return null;
    }
}
