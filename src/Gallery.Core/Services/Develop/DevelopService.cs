using System.Collections.Concurrent;
using System.IO.Compression;
using Gallery.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

using Gallery.Develop;

namespace Gallery.Core.Services.Develop;

/// <summary>What the editor needs to know before fetching an editing copy.</summary>
/// <param name="Width">The picture's width as edited: rotated, and stretched by the anamorphic <paramref name="Squeeze"/>.</param>
public sealed record DevelopInfo(
    long AssetId, string SourceKind, int Width, int Height, int Rotation, string? Camera, int? Iso, string? Shutter, string? Aperture,
    double[]? AsShotMultipliers, bool HighlightHeadroom, int[] Sizes, double Squeeze = 1);

/// <summary>Why an editing copy can't be made, worded for the gallery user.</summary>
public sealed class DevelopException(string message) : Exception(message);

/// <summary>
/// Makes editing copies (<see cref="DevelopFile"/>): LibRaw decodes RAWs, other photos are converted from sRGB. All
/// become linear Rec.2020 half floats, upright, shrunk to the editor's sizes. Copies are cached under
/// <c>{derived}/develop</c>, which is trimmed to <see cref="GalleryOptions.DevelopCacheMB"/> by removing the least
/// recently used first. A full-resolution copy (uncompressed, for 100% crops) is made the first time it is needed.
/// </summary>
public sealed class DevelopService
{
    /// <summary>Long-edge sizes of the editing copies: phones, desktops.</summary>
    public static readonly int[] Sizes = [1600, 2560];
    /// <summary>Bump this when decoding changes, so old cached copies are discarded.</summary>
    public const int PipelineVersion = 2;

    private readonly SettingsService _settings;
    private readonly RawTools _raw;
    private readonly Ffmpeg _ffmpeg;
    private readonly HeifDecoder? _heif;
    private readonly GalleryOptions _options;
    private readonly ILogger<DevelopService> _log;
    private readonly SemaphoreSlim _gate = new(2); // decodes at once: each one can hold a full-size image
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _inflight = new();
    private static readonly Lazy<float[]> SrgbTable = new(ColorMath.SrgbDecodeTable16);

    /// <param name="heif">libheif for HEIC photos; without it (or if its tools are missing) ffmpeg decodes them.</param>
    public DevelopService(SettingsService settings, RawTools raw, Ffmpeg ffmpeg, GalleryOptions options, ILogger<DevelopService> log, HeifDecoder? heif = null)
    {
        _settings = settings;
        _raw = raw;
        _ffmpeg = ffmpeg;
        _heif = heif;
        _options = options;
        _log = log;
    }

    public string CacheDir => Path.Combine(_settings.GetDerivedDir(), "develop");

    /// <summary>Whether this asset can be edited at all (a photo, RAW or not, and the tools it needs are present).</summary>
    public string? WhyNot(Asset asset)
    {
        if (asset.Kind != MediaKind.Image) return "only photos can be edited";
        if (asset.Missing == 1 || !File.Exists(asset.Path)) return "the file is missing";
        if (asset.IsRaw == 1 && !_raw.IsAvailable) return "LibRaw's dcraw_emu and raw-identify are not installed (install libraw-bin, or brew install libraw)";
        return null;
    }

    public Task<DevelopInfo> GetInfoAsync(Asset asset, int rotation, CancellationToken ct = default) => GetInfoAsync(asset, rotation, 1, ct);

    /// <summary>The picture as edited: its size rotated by <paramref name="rotation"/> and stretched by the anamorphic <paramref name="squeeze"/>.</summary>
    public async Task<DevelopInfo> GetInfoAsync(Asset asset, int rotation, double squeeze, CancellationToken ct = default)
    {
        if (WhyNot(asset) is { } why) throw new DevelopException(why);
        rotation = Rotation.Normalize(rotation);
        squeeze = Squeeze.Normalize(squeeze);
        int w, h;
        if (asset.IsRaw == 1)
        {
            var id = await _raw.IdentifyAsync(asset.Path, ct) ?? throw new DevelopException("LibRaw cannot read this RAW file");
            (w, h) = Squeeze.Apply(Turn(id.Width, id.Height, rotation), squeeze);
            return new DevelopInfo(asset.Id, DevelopFile.RawSource, w, h, rotation, id.Camera, id.Iso, id.Shutter, id.Aperture,
                id.AsShot, id.HeadroomRatio is not null, SizesFor(w, h), squeeze);
        }
        (w, h) = await UprightSizeAsync(asset, ct);
        (w, h) = Squeeze.Apply(Turn(w, h, rotation), squeeze);
        return new DevelopInfo(asset.Id, DevelopFile.ImageSource, w, h, rotation, null, null, null, null, null, false, SizesFor(w, h), squeeze);
    }

    private static (int W, int H) Turn(int w, int h, int rotation) => rotation is 90 or 270 ? (h, w) : (w, h);

    /// <summary>
    /// The picture's size when shown (with its stored rotation). ImageSharp reads it for the formats it knows. For HEIC
    /// and AVIF it comes from the scan, or libheif, or ffprobe (the tile grid's size for a phone photo).
    /// </summary>
    private async Task<(int W, int H)> UprightSizeAsync(Asset asset, CancellationToken ct)
    {
        try
        {
            var info = await Image.IdentifyAsync(asset.Path, ct);
            var orientation = info.Metadata.ExifProfile?.TryGetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Orientation, out var o) == true ? o.Value : (ushort)1;
            return orientation >= 5 ? (info.Height, info.Width) : (info.Width, info.Height);
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or NotSupportedException or ImageFormatException or InvalidImageContentException)
        {
            if (asset.Width is { } aw && asset.Height is { } ah) return (aw, ah);
            if (_heif is not null && HeifDecoder.Formats.Contains(asset.Ext) && await _heif.ProbeSizeAsync(asset.Path, ct) is { } size) return size;
            if (!_ffmpeg.IsAvailable) throw new DevelopException($"this image format needs ffmpeg ({ex.Message})");
            if (await _ffmpeg.ProbeTileGridAsync(asset.Path, ct) is { } grid) return grid.Rotation is 90 or 270 ? (grid.Height, grid.Width) : (grid.Width, grid.Height);
            if (await _ffmpeg.ProbeAsync(asset.Path, ct) is { Width: { } pw, Height: { } ph }) return (pw, ph);
            throw new DevelopException($"the size of this image could not be read ({ex.Message})");
        }
    }

    /// <summary>The standard sizes smaller than the picture, plus the picture's own size.</summary>
    private static int[] SizesFor(int w, int h) => Sizes.Where(s => s < Math.Max(w, h)).Append(Math.Max(w, h)).ToArray();

    /// <summary>
    /// The gzip-compressed editing copy with the given long edge (never made larger), made and cached on first use.
    /// The item's manual rotation is added on top of the file's orientation.
    /// </summary>
    public Task<string> GetSourceAsync(Asset asset, int size, int rotation, CancellationToken ct = default) => GetSourceAsync(asset, size, rotation, 1, ct);

    /// <summary>Same as <see cref="GetSourceAsync(Asset,int,int,CancellationToken)"/>, stretched by an anamorphic <paramref name="squeeze"/>.</summary>
    public Task<string> GetSourceAsync(Asset asset, int size, int rotation, double squeeze, CancellationToken ct = default)
    {
        rotation = Rotation.Normalize(rotation);
        squeeze = Squeeze.Normalize(squeeze);
        var path = Path.Combine(CacheDir, $"{asset.Hash}_v{PipelineVersion}_r{rotation}{Stretched(squeeze)}_{size}.gdev.gz");
        return CachedAsync(path, () => RenderAsync(asset, size, rotation, squeeze, path, compress: true), ct);
    }

    private static string Stretched(double squeeze) => squeeze == 1 ? "" : $"_a{Squeeze.Format(squeeze)}";

    /// <summary>A region of the upright full-resolution picture, as an uncompressed .gdev (for the editor's 100% view).</summary>
    public Task<byte[]> GetCropAsync(Asset asset, int rotation, int x, int y, int width, int height, CancellationToken ct = default) =>
        GetCropAsync(asset, rotation, x, y, width, height, 1, ct);

    /// <summary>Same as <see cref="GetCropAsync(Asset,int,int,int,int,int,CancellationToken)"/>, in the picture stretched by an anamorphic <paramref name="squeeze"/>.</summary>
    public async Task<byte[]> GetCropAsync(Asset asset, int rotation, int x, int y, int width, int height, double squeeze, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(await FullFileAsync(asset, rotation, squeeze, ct));
        var (header, offset) = DevelopFile.ReadHeader(fs); // the full-size file is interleaved, so we can read rows directly
        x = Math.Clamp(x, 0, header.Width - 1);
        y = Math.Clamp(y, 0, header.Height - 1);
        width = Math.Clamp(width, 1, header.Width - x);
        height = Math.Clamp(height, 1, header.Height - y);
        var halfs = new ushort[width * height * 3];
        var rowBytes = new byte[width * 6];
        for (var r = 0; r < height; r++)
        {
            fs.Position = offset + ((long)(y + r) * header.Width + x) * 6;
            await fs.ReadExactlyAsync(rowBytes, ct);
            Buffer.BlockCopy(rowBytes, 0, halfs, r * width * 6, rowBytes.Length);
        }
        using var ms = new MemoryStream();
        DevelopFile.Write(ms, header with { Width = width, Height = height, CropX = x, CropY = y }, halfs, shuffle: true);
        return ms.ToArray();
    }

    /// <summary>
    /// A region of the picture as cropped and straightened by <paramref name="crop"/>, in full-resolution pixels (for the
    /// editor's 100% view of a cropped photo). Flow: read the source part of the upright picture -> rotate -> cut.
    /// </summary>
    public async Task<byte[]> GetCropAsync(Asset asset, int rotation, int x, int y, int width, int height, double squeeze, CropSettings? crop, CancellationToken ct = default)
    {
        if (crop is null || crop.IsIdentity) return await GetCropAsync(asset, rotation, x, y, width, height, squeeze, ct);
        crop = crop.Clamped();
        DevelopHeader header;
        await using (var fs = File.OpenRead(await FullFileAsync(asset, rotation, squeeze, ct))) (header, _) = DevelopFile.ReadHeader(fs);
        var (cw, ch) = Geometry.Size(crop, header.FullWidth, header.FullHeight);
        x = Math.Clamp(x, 0, cw - 1);
        y = Math.Clamp(y, 0, ch - 1);
        width = Math.Clamp(width, 1, cw - x);
        height = Math.Clamp(height, 1, ch - y);
        var (sx, sy, sw, sh) = Geometry.SourceRegion(crop, header.FullWidth, header.FullHeight, x, y, width, height);
        var part = LinearImage.FromGdev(await GetCropAsync(asset, rotation, sx, sy, sw, sh, squeeze, ct));
        var shaped = Geometry.ApplyRegion(part, crop, x, y, width, height);
        using var ms = new MemoryStream();
        DevelopFile.Write(ms, shaped.Header, shaped.ToHalfs(), shuffle: true);
        return ms.ToArray();
    }

    /// <summary>The upright full-resolution copy (interleaved, uncompressed, so rows can be read directly), made the first time it is needed.</summary>
    private Task<string> FullFileAsync(Asset asset, int rotation, double squeeze, CancellationToken ct)
    {
        rotation = Rotation.Normalize(rotation);
        squeeze = Squeeze.Normalize(squeeze);
        var full = Path.Combine(CacheDir, $"{asset.Hash}_v{PipelineVersion}_r{rotation}{Stretched(squeeze)}_full.gdev");
        return CachedAsync(full, () => RenderAsync(asset, 0, rotation, squeeze, full, compress: false), ct);
    }

    /// <summary>Returns the cached file, or makes it once, sharing one job between all requests that ask at the same time.</summary>
    private async Task<string> CachedAsync(string path, Func<Task<string>> make, CancellationToken ct)
    {
        if (File.Exists(path))
        {
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch (IOException) { } // marks it as recently used, so it is not removed
            return path;
        }
        var lazy = _inflight.GetOrAdd(path, key => new Lazy<Task<string>>(async () =>
        {
            try { return await make(); }
            finally { _inflight.TryRemove(path, out _); }
        }));
        return await lazy.Value.WaitAsync(ct);
    }

    private async Task<string> RenderAsync(Asset asset, int size, int rotation, double squeeze, string target, bool compress)
    {
        if (WhyNot(asset) is { } why) throw new DevelopException(why);
        await _gate.WaitAsync();
        try
        {
            var started = DateTime.UtcNow;
            var (header, sink) = asset.IsRaw == 1
                ? await DecodeRawAsync(asset, size, squeeze, (w, h) => new HalfSink(w, h))
                : await DecodeImageAsync(asset, size, squeeze, (w, h) => new HalfSink(w, h));
            var (rotated, halfs) = Rotate(header, sink.Halfs, rotation);
            header = rotated;
            Directory.CreateDirectory(CacheDir);
            var tmp = target + ".tmp";
            await using (var fs = File.Create(tmp))
            {
                if (compress)
                {
                    await using var gz = new GZipStream(fs, CompressionLevel.Optimal);
                    DevelopFile.Write(gz, header, halfs, shuffle: true);
                }
                else DevelopFile.Write(fs, header, halfs);
            }
            File.Move(tmp, target, overwrite: true);
            _log.LogInformation("Editing copy of {Path}: {W}×{H} in {Ms:N0} ms, {Size:N1} MB", asset.Path, header.Width, header.Height,
                (DateTime.UtcNow - started).TotalMilliseconds, new FileInfo(target).Length / 1048576.0);
            Trim(target);
            return target;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// The picture as full-precision linear channels, shrunk to <paramref name="longEdge"/> (0: full size), upright with the
    /// item's rotation. Exports render from this. Not cached.
    /// </summary>
    public Task<LinearImage> DecodeLinearAsync(Asset asset, int longEdge, int rotation, CancellationToken ct = default) =>
        DecodeLinearAsync(asset, longEdge, rotation, 1, ct);

    /// <summary>Same as <see cref="DecodeLinearAsync(Asset,int,int,CancellationToken)"/>, stretched by an anamorphic <paramref name="squeeze"/> (long edge measured after stretching).</summary>
    public async Task<LinearImage> DecodeLinearAsync(Asset asset, int longEdge, int rotation, double squeeze, CancellationToken ct = default)
    {
        if (WhyNot(asset) is { } why) throw new DevelopException(why);
        squeeze = Squeeze.Normalize(squeeze);
        await _gate.WaitAsync(ct);
        try
        {
            var (header, sink) = asset.IsRaw == 1
                ? await DecodeRawAsync(asset, longEdge, squeeze, (w, h) => new PlanarSink(w, h))
                : await DecodeImageAsync(asset, longEdge, squeeze, (w, h) => new PlanarSink(w, h));
            return RotatePlanar(header, sink, Rotation.Normalize(rotation));
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// What decoding a w × h picture gives for <paramref name="size"/> and <paramref name="squeeze"/>: the picture with its
    /// long side stretched and fitted inside the size (Final), and the size to shrink the decode to on the way (Decode,
    /// never larger than the original on either side). If Final is larger, the decode is stretched afterwards.
    /// </summary>
    private static ((int W, int H) Decode, (int W, int H) Final) Plan(int w, int h, int size, double squeeze)
    {
        var (sw, sh) = Squeeze.Apply(w, h, squeeze);
        var final = Target(sw, sh, size);
        return ((Math.Min(final.W, w), Math.Min(final.H, h)), final);
    }

    /// <summary>Shrinks the long edge to <paramref name="size"/>; 0, or a size at least as large as the picture, keeps it.</summary>
    private static (int W, int H) Target(int w, int h, int size)
    {
        var longEdge = Math.Max(w, h);
        if (size <= 0 || size >= longEdge) return (w, h);
        var f = (double)size / longEdge;
        return (Math.Max(1, (int)Math.Round(w * f)), Math.Max(1, (int)Math.Round(h * f)));
    }

    private async Task<(DevelopHeader, TSink)> DecodeRawAsync<TSink>(Asset asset, int size, double squeeze, Func<int, int, TSink> makeSink) where TSink : RowSink
    {
        var id = await _raw.IdentifyAsync(asset.Path) ?? throw new DevelopException("LibRaw cannot read this RAW file");
        // Highlight blend keeps everything above white (scaled by the largest multiplier). Without multipliers, clip at white.
        var ratio = id.HeadroomRatio;
        var scale = (float)((ratio ?? 1) / 65535.0);
        TSink? sink = null;
        AreaDownscaler? down = null;
        var row = Array.Empty<float>();
        int fullW = 0, fullH = 0;
        (int W, int H) final = default;
        await _raw.DecodeAsync(asset.Path, ratio is null ? 0 : 2, (w, h) =>
        {
            (fullW, fullH) = (w, h);
            var ((tw, th), wanted) = Plan(w, h, size, squeeze);
            final = wanted;
            sink = makeSink(tw, th);
            if (tw != w || th != h) down = new AreaDownscaler(w, h, tw, th, sink.Row);
            row = new float[w * 3];
        }, values =>
        {
            for (var i = 0; i < values.Length; i++) row[i] = values[i];
            ColorMath.Transform(row, ColorMath.XyzToRec2020, scale);
            if (down is not null) down.AddRow(row); else sink!.Row(sink.Next, row);
        });
        if (sink is null || !sink.Complete) throw new DevelopException("LibRaw stopped before the end of the picture");
        sink = (TSink)sink.StretchTo(final.W, final.H);
        var (stretchedW, stretchedH) = Squeeze.Apply(fullW, fullH, squeeze);
        return (new DevelopHeader
        {
            Width = sink.Width, Height = sink.Height, SourceKind = DevelopFile.RawSource, AssetId = asset.Id, Hash = asset.Hash,
            FullWidth = stretchedW, FullHeight = stretchedH, Camera = id.Camera, AsShotMultipliers = id.AsShot,
            HighlightHeadroom = ratio is not null,
        }, sink);
    }

    /// <summary>
    /// A picture ImageSharp can't read, as a 16-bit PNG: HEIC through libheif if it is installed, otherwise (and for AVIF,
    /// JPEG XL and unusual TIFFs) through ffmpeg. Both make it upright and join a phone's tile grid into one picture.
    /// </summary>
    private async Task<Image<Rgb48>> DecodeElsewhereAsync(Asset asset, Exception imageSharpError)
    {
        string? heifError = null;
        if (HeifDecoder.Formats.Contains(asset.Ext))
        {
            if (_heif is not null)
            {
                var (bytes, err) = await _heif.DecodeAsync(asset.Path, png: true);
                if (bytes is not null)
                {
                    var decoded = Image.Load<Rgb48>(bytes);
                    decoded.Metadata.ExifProfile = null; // already upright
                    return decoded;
                }
                heifError = _heif.IsAvailable ? $"libheif failed ({err})" : HeifDecoder.NotInstalled;
            }
            else heifError = HeifDecoder.NotInstalled;
        }
        if (!_ffmpeg.IsAvailable) throw new DevelopException(heifError is null ? $"this image format needs ffmpeg ({imageSharpError.Message})" : $"{heifError}, and ffmpeg is not installed");
        // ffmpeg 7.1+ joins a tiled HEIC or AVIF and makes it upright by itself. Leave -vf off: a grid can't take it.
        var r = await _ffmpeg.CaptureAsync(["-i", asset.Path, "-frames:v", "1", "-f", "image2pipe", "-vcodec", "png", "-pix_fmt", "rgb48be", "-"]);
        var also = heifError is null ? "" : $"{heifError}, and ";
        if (r.Bytes is null) throw new DevelopException($"{also}the image could not be decoded ({r.Error})");
        var img = Image.Load<Rgb48>(r.Bytes);
        if (ThumbnailService.TiledFormats.Contains(asset.Ext) && asset.Width is { } aw && asset.Height is { } ah && ThumbnailService.IsPartOf(img.Width, img.Height, aw, ah))
        {
            var (w, h) = (img.Width, img.Height);
            img.Dispose();
            throw new DevelopException($"{also}ffmpeg {_ffmpeg.VersionText} decoded only a {w}×{h} tile of this {aw}×{ah} picture; ffmpeg 7.1 or later puts tiled HEIC and AVIF photos together");
        }
        return img;
    }

    private async Task<(DevelopHeader, TSink)> DecodeImageAsync<TSink>(Asset asset, int size, double squeeze, Func<int, int, TSink> makeSink) where TSink : RowSink
    {
        Image<Rgb48> img;
        try { img = await Image.LoadAsync<Rgb48>(asset.Path); }
        catch (Exception ex) when (ex is UnknownImageFormatException or NotSupportedException or ImageFormatException or InvalidImageContentException)
        {
            img = await DecodeElsewhereAsync(asset, ex);
        }
        using (img)
        {
            img.Mutate(x => x.AutoOrient());
            int w = img.Width, h = img.Height;
            var ((tw, th), final) = Plan(w, h, size, squeeze);
            var sink = makeSink(tw, th);
            var down = tw != w || th != h ? new AreaDownscaler(w, h, tw, th, sink.Row) : null;
            var table = SrgbTable.Value;
            var row = new float[w * 3];
            img.ProcessPixelRows(rows =>
            {
                for (var y = 0; y < rows.Height; y++)
                {
                    var px = rows.GetRowSpan(y);
                    for (var x = 0; x < px.Length; x++)
                    {
                        row[x * 3] = table[px[x].R];
                        row[x * 3 + 1] = table[px[x].G];
                        row[x * 3 + 2] = table[px[x].B];
                    }
                    ColorMath.Transform(row, ColorMath.SrgbToRec2020);
                    if (down is not null) down.AddRow(row); else sink.Row(sink.Next, row);
                }
            });
            sink = (TSink)sink.StretchTo(final.W, final.H);
            var (stretchedW, stretchedH) = Squeeze.Apply(w, h, squeeze);
            return (new DevelopHeader
            {
                Width = sink.Width, Height = sink.Height, SourceKind = DevelopFile.ImageSource, AssetId = asset.Id, Hash = asset.Hash,
                FullWidth = stretchedW, FullHeight = stretchedH,
            }, sink);
        }
    }

    /// <summary>Rotates the copy by the item's manual rotation (clockwise, in quarter turns).</summary>
    internal static (DevelopHeader, ushort[]) Rotate(DevelopHeader header, ushort[] src, int rotation)
    {
        if (rotation == 0) return (header with { Rotation = 0 }, src);
        int w = header.Width, h = header.Height;
        var (nw, nh) = rotation == 180 ? (w, h) : (h, w);
        var dst = new ushort[src.Length];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (nx, ny) = rotation switch { 90 => (h - 1 - y, x), 180 => (w - 1 - x, h - 1 - y), _ => (y, w - 1 - x) };
                var s = (y * w + x) * 3;
                var d = (ny * nw + nx) * 3;
                dst[d] = src[s]; dst[d + 1] = src[s + 1]; dst[d + 2] = src[s + 2];
            }
        var (fw, fh) = rotation == 180 ? (header.FullWidth, header.FullHeight) : (header.FullHeight, header.FullWidth);
        return (header with { Width = nw, Height = nh, FullWidth = fw, FullHeight = fh, Rotation = rotation }, dst);
    }

    private static LinearImage RotatePlanar(DevelopHeader header, PlanarSink sink, int rotation)
    {
        int w = sink.Width, h = sink.Height;
        header = header with { Width = w, Height = h };
        if (rotation == 0) return new LinearImage(header, w, h, sink.R, sink.G, sink.B);
        var (nw, nh) = rotation == 180 ? (w, h) : (h, w);
        var channels = new[] { sink.R, sink.G, sink.B }.Select(src =>
        {
            var dst = new float[src.Length];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var (nx, ny) = rotation switch { 90 => (h - 1 - y, x), 180 => (w - 1 - x, h - 1 - y), _ => (y, w - 1 - x) };
                    dst[ny * nw + nx] = src[y * w + x];
                }
            return dst;
        }).ToArray();
        var (fw, fh) = rotation == 180 ? (header.FullWidth, header.FullHeight) : (header.FullHeight, header.FullWidth);
        return new LinearImage(header with { Width = nw, Height = nh, FullWidth = fw, FullHeight = fh, Rotation = rotation }, nw, nh, channels[0], channels[1], channels[2]);
    }

    /// <summary>Removes the least recently used copies when the cache is over its size limit (never the one just made).</summary>
    private void Trim(string keep)
    {
        try
        {
            var limit = (long)_options.DevelopCacheMB * 1048576;
            var files = new DirectoryInfo(CacheDir).GetFiles("*.gdev*").Where(f => !f.Name.EndsWith(".tmp")).OrderBy(f => f.LastWriteTimeUtc).ToList();
            var total = files.Sum(f => f.Length);
            foreach (var f in files)
            {
                if (total <= limit) break;
                if (f.FullName == keep) continue;
                total -= f.Length;
                f.Delete();
            }
        }
        catch (IOException ex) { _log.LogDebug("Trimming the editing-copy cache: {Message}", ex.Message); }
    }

    /// <summary>Receives the decoded (and shrunk) rows, top to bottom.</summary>
    private abstract class RowSink(int width, int height)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public int Next { get; protected set; }
        public bool Complete => Next == Height;
        public abstract void Row(int y, ReadOnlySpan<float> rgb);

        /// <summary>The finished picture stretched to <paramref name="width"/> × <paramref name="height"/> (an anamorphic squeeze); returns itself if it is already that size.</summary>
        public abstract RowSink StretchTo(int width, int height);
    }

    /// <summary>Keeps rows as half floats, for the editing copy.</summary>
    private sealed class HalfSink(int width, int height) : RowSink(width, height)
    {
        public ushort[] Halfs { get; private init; } = new ushort[width * height * 3];

        public override RowSink StretchTo(int width, int height) => width == Width && height == Height ? this
            : new HalfSink(width, height) { Halfs = LinearStretch.Halfs(Halfs, Width, Height, width, height), Next = height };

        public override void Row(int y, ReadOnlySpan<float> rgb)
        {
            var o = y * Width * 3;
            for (var i = 0; i < Width * 3; i++) Halfs[o + i] = BitConverter.HalfToUInt16Bits((Half)rgb[i]);
            Next = y + 1;
        }
    }

    /// <summary>Keeps rows as full-precision channels, for exports to render from.</summary>
    private sealed class PlanarSink(int width, int height) : RowSink(width, height)
    {
        public float[] R { get; private init; } = new float[width * height];
        public float[] G { get; private init; } = new float[width * height];
        public float[] B { get; private init; } = new float[width * height];

        public override RowSink StretchTo(int width, int height) => width == Width && height == Height ? this
            : new PlanarSink(width, height)
            {
                R = LinearStretch.Plane(R, Width, Height, width, height), G = LinearStretch.Plane(G, Width, Height, width, height),
                B = LinearStretch.Plane(B, Width, Height, width, height), Next = height,
            };

        public override void Row(int y, ReadOnlySpan<float> rgb)
        {
            var o = y * Width;
            for (var x = 0; x < Width; x++) { R[o + x] = rgb[x * 3]; G[o + x] = rgb[x * 3 + 1]; B[o + x] = rgb[x * 3 + 2]; }
            Next = y + 1;
        }
    }
}
