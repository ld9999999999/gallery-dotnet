using System.Collections.Concurrent;
using System.Threading.Channels;
using Dapper;
using Gallery.Core.Data;
using Gallery.Core.Models;
using MetadataExtractor;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Directory = System.IO.Directory;

namespace Gallery.Core.Services;

/// <summary>
/// Makes and caches JPEG thumbnails and previews under {DataDir}/thumbs/{hh}/{hash}_{size}.jpg.
/// Only sizes in <see cref="AllowedSizes"/> are accepted, which bounds the cache; 360° photos
/// may also use the larger <see cref="PanoramaSizes"/>.
/// How a file is decoded:
/// - Images: ImageSharp (a JPEG decodes at a smaller size directly).
/// - RAW: the preview JPEG inside the file.
/// - HEIC: libheif's heif-dec when it's installed, else ffmpeg.
/// - Anything else ImageSharp can't read (video, audio cover art, AVIF): ffmpeg.
///
/// Two lanes share the cache:
/// - Interactive: thumbnails the browser asks for right now. They have their own limit on parallel work, so they don't
///   wait behind background work.
/// - Background: a queue the scanner fills after each saved batch (plus a sweep at startup and after a scan).
///   Workers drain it while the scan goes on, and the files are usually still in the OS cache from hashing.
/// Grid sizes (256 and 512) are always written together from one decode, so Retina and normal screens both find them.
/// </summary>
public sealed class ThumbnailService : BackgroundService
{
    public static readonly int[] AllowedSizes = { 256, 512, 1024, 2048 };

    /// <summary>
    /// Larger previews, made on demand for 360° photos only. The viewer shows a small part of the sphere at a time, so a
    /// 2048 px wide picture of all of it looks soft. 8192 px wide means 8192 × 4096.
    /// </summary>
    public static readonly int[] PanoramaSizes = { 4096, 8192 };

    private static readonly int[] AllSizes = [.. AllowedSizes, .. PanoramaSizes];

    /// <summary>Sizes the grid uses (512 on high-DPI screens). Generated together.</summary>
    public static readonly int[] GridSizes = { 512, 256 };

    private readonly Db _db;
    private readonly SettingsService _settings;
    private readonly Ffmpeg _ffmpeg;
    private readonly HeifDecoder? _heif;
    private readonly ILogger<ThumbnailService> _log;
    private readonly SemaphoreSlim _interactiveGate = new(Math.Max(2, Environment.ProcessorCount / 2));
    private readonly int _backgroundWorkers = Math.Max(1, Environment.ProcessorCount / 2);
    private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> _inflight = new();

    private readonly Channel<Asset> _queue = Channel.CreateUnbounded<Asset>(new UnboundedChannelOptions { SingleWriter = false, SingleReader = false });
    private readonly ConcurrentDictionary<string, byte> _queued = new(); // hashes of assets waiting or in work
    // Files that failed in this run, by content hash. They aren't retried or logged again until a restart or
    // ForgetFailures(), so a broken file costs one try and one warning rather than one per sweep and page view.
    private readonly ConcurrentDictionary<string, byte> _failed = new();
    private long _doneSinceIdle;
    private int _sweeping;
    private int _sweepRequested;
    private CancellationToken _stopping = CancellationToken.None;

    /// <param name="heif">libheif for HEIC photos. Without it (or if its tools are missing), ffmpeg decodes them.</param>
    public ThumbnailService(Db db, SettingsService settings, Ffmpeg ffmpeg, ILogger<ThumbnailService> log, HeifDecoder? heif = null)
    {
        _db = db;
        _settings = settings;
        _ffmpeg = ffmpeg;
        _heif = heif;
        _log = log;
    }

    /// <param name="panorama">A 360° photo, which may have the <see cref="PanoramaSizes"/> too.</param>
    public static int NormalizeSize(int requested, bool panorama = false)
    {
        var sizes = panorama ? AllSizes : AllowedSizes;
        foreach (var s in sizes) if (requested <= s) return s;
        return sizes[^1];
    }

    /// <summary>
    /// Cache file for a size, manual rotation (see <see cref="Rotation"/>) and anamorphic squeeze (<see cref="Squeeze"/>).
    /// A file with neither gets the plain name.
    /// </summary>
    public string CachePath(string hash, int size, int rotation = 0, double squeeze = 1)
    {
        var dir = Path.Combine(_settings.ThumbsDir, hash.Substring(0, 2));
        var turn = rotation == 0 ? "" : $"_r{rotation}";
        var stretch = Squeeze.Normalize(squeeze) == 1 ? "" : $"_a{Squeeze.Format(squeeze)}";
        return Path.Combine(dir, $"{hash}{turn}{stretch}_{size}.jpg");
    }

    /// <summary>Deletes every cached thumbnail and preview of a file's content: all sizes and turns. Returns how many.</summary>
    public int DeleteCached(string hash)
    {
        if (hash.Length < 2) return 0;
        var dir = Path.Combine(_settings.ThumbsDir, hash[..2]);
        if (!Directory.Exists(dir)) return 0;
        var n = 0;
        foreach (var f in Directory.EnumerateFiles(dir, hash + "_*.jpg"))
            try { File.Delete(f); n++; } catch (IOException) { }
        return n;
    }

    private bool HasGridThumbs(string hash) => GridSizes.All(s => File.Exists(CachePath(hash, s)));

    // ---------------------------------------------------------------- interactive lane

    /// <summary>Returns the path to a cached thumbnail, generating it if needed. Null when the asset can't be rendered.</summary>
    public Task<string?> GetAsync(Asset asset, int size, CancellationToken ct = default) => GetAsync(asset, size, 0, ct);

    /// <summary>As <see cref="GetAsync(Asset,int,CancellationToken)"/>, turned clockwise by a manual <paramref name="rotation"/> after EXIF orientation.</summary>
    public Task<string?> GetAsync(Asset asset, int size, int rotation, CancellationToken ct = default) => GetAsync(asset, size, rotation, panorama: false, ct);

    /// <param name="panorama">The caller has checked that this is a 360° photo: sizes up to 8192 are made.</param>
    public Task<string?> GetAsync(Asset asset, int size, int rotation, bool panorama, CancellationToken ct = default) =>
        GetAsync(asset, size, rotation, 1, panorama, ct);

    /// <summary>As <see cref="GetAsync(Asset,int,int,bool,CancellationToken)"/>, with the long side stretched by an anamorphic <paramref name="squeeze"/>.</summary>
    public async Task<string?> GetAsync(Asset asset, int size, int rotation, double squeeze, bool panorama = false, CancellationToken ct = default)
    {
        size = NormalizeSize(size, panorama);
        rotation = Rotation.Normalize(rotation);
        squeeze = Squeeze.Normalize(squeeze);
        var path = CachePath(asset.Hash, size, rotation, squeeze);
        if (File.Exists(path)) return path;
        if (_failed.ContainsKey(asset.Hash)) return null;
        var sizes = GridSizes.Contains(size) ? GridSizes : new[] { size };
        // The shared job outlives this request. If the browser drops it (scrolling, grid reload), other requests
        // waiting for the same thumbnail still get it, and the result goes into the cache either way.
        var ok = await RunOnceAsync(asset, sizes, rotation, squeeze, _interactiveGate).WaitAsync(ct);
        return ok && File.Exists(path) ? path : null;
    }

    /// <summary>Makes the given sizes for an asset. Concurrent requests for the same asset, sizes, rotation and squeeze share one job.</summary>
    private Task<bool> RunOnceAsync(Asset asset, int[] sizes, int rotation, double squeeze, SemaphoreSlim? gate)
    {
        var key = asset.Hash + ":" + string.Join(',', sizes) + ":r" + rotation + ":a" + Squeeze.Format(squeeze);
        var lazy = _inflight.GetOrAdd(key, k => new Lazy<Task<bool>>(async () =>
        {
            try
            {
                if (gate is not null) await gate.WaitAsync(_stopping);
                try { return await RenderSizesAsync(asset, sizes, rotation, squeeze, _stopping); }
                finally { gate?.Release(); }
            }
            finally { _inflight.TryRemove(key, out _); }
        }));
        return lazy.Value;
    }

    // ---------------------------------------------------------------- background lane

    /// <summary>Lets files that failed earlier in this run be retried. Returns how many were forgotten.</summary>
    public int ForgetFailures()
    {
        var n = _failed.Count;
        _failed.Clear();
        return n;
    }

    /// <summary>Background queue state for the status panel.</summary>
    public (bool Running, long Done, long Pending) QueueProgress
    {
        get
        {
            var pending = _queued.Count;
            var sweeping = Volatile.Read(ref _sweeping) == 1;
            return (pending > 0 || sweeping, pending > 0 ? Interlocked.Read(ref _doneSinceIdle) : 0, pending);
        }
    }

    /// <summary>Queues grid thumbnails for these assets, skipping any already cached or queued. Returns how many were added.</summary>
    public int Enqueue(IEnumerable<Asset> assets)
    {
        var n = 0;
        foreach (var a in assets)
        {
            // Audio only has a thumbnail when it has cover art, and most files don't, so the background queue would
            // retry them on every sweep. The grid still asks for them when shown and falls back to a music icon.
            if (a.Kind == MediaKind.Audio) continue;
            if (string.IsNullOrEmpty(a.Hash) || _failed.ContainsKey(a.Hash) || HasGridThumbs(a.Hash)) continue;
            if (!_queued.TryAdd(a.Hash, 0)) continue;
            _queue.Writer.TryWrite(a);
            n++;
        }
        return n;
    }

    /// <summary>Settings key, set once the thumbnails and previews larger than their photo have been deleted.</summary>
    public const string EnlargedForgottenKey = "thumbs_enlarged_forgotten";

    /// <summary>
    /// Deletes cached thumbnails and previews that are larger than their photo, so they're regenerated at the photo's
    /// own size. The cache file's size is in its name and the photo's long side (times its squeeze) is in the index,
    /// so no picture has to be opened. RAWs are skipped, since their previews come from the JPEG inside.
    /// Returns how many were deleted.
    /// </summary>
    public int ForgetEnlarged()
    {
        Dictionary<string, int> longSide;
        using (var c = _db.Open())
            longSide = c.Query<(string Hash, int Long)>("""
                SELECT hash, MAX(MAX(COALESCE(width, 0), COALESCE(height, 0))) FROM assets
                WHERE kind='image' AND is_raw=0 GROUP BY hash
                """).Where(r => r.Long > 0).ToDictionary(r => r.Hash, r => r.Long);
        var name = new System.Text.RegularExpressions.Regex(@"^(?<hash>[0-9a-f]+)(_r\d+)?(_a(?<a>[0-9.]+))?_(?<size>\d+)\.jpg$");
        var n = 0;
        if (!Directory.Exists(_settings.ThumbsDir)) return 0;
        foreach (var dir in Directory.EnumerateDirectories(_settings.ThumbsDir))
            foreach (var file in Directory.EnumerateFiles(dir, "*.jpg"))
            {
                var m = name.Match(Path.GetFileName(file));
                if (!m.Success || !longSide.TryGetValue(m.Groups["hash"].Value, out var photo)) continue;
                var stretched = photo * (m.Groups["a"].Success ? Squeeze.Parse(m.Groups["a"].Value) : 1);
                if (int.Parse(m.Groups["size"].Value) <= Math.Ceiling(stretched)) continue; // not larger than the photo, so keep it
                try { File.Delete(file); n++; } catch (IOException) { }
            }
        return n;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        if (_settings.Get(EnlargedForgottenKey) != "1")
            _ = Task.Run(() =>
            {
                try
                {
                    var n = ForgetEnlarged();
                    _settings.Set(EnlargedForgottenKey, "1");
                    if (n > 0) _log.LogInformation("Deleted {Count:N0} thumbnails and previews made larger than their photo; they are made again at its size", n);
                }
                catch (Exception ex) { _log.LogWarning(ex, "Looking for enlarged thumbnails failed"); }
            }, stoppingToken);
        var workers = Enumerable.Range(0, _backgroundWorkers).Select(_ => Task.Run(() => WorkerAsync(stoppingToken), stoppingToken)).ToArray();
        try { await Task.WhenAll(workers); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task WorkerAsync(CancellationToken ct)
    {
        await foreach (var asset in _queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                if (!HasGridThumbs(asset.Hash)) await RunOnceAsync(asset, GridSizes, 0, 1, gate: null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { _log.LogDebug(ex, "background thumbnail failed for {Path}", asset.Path); }
            finally
            {
                _queued.TryRemove(asset.Hash, out _);
                Interlocked.Increment(ref _doneSinceIdle);
                if (_queued.IsEmpty && Interlocked.Exchange(ref _doneSinceIdle, 0) is var n and > 0)
                    _log.LogInformation("Thumbnail queue empty: {Count:N0} processed", n);
            }
        }
    }

    /// <summary>
    /// Queues grid thumbnails for every visible item that has none. Runs at startup and after each scan to catch what
    /// the per-batch queueing missed (for example, an item whose main file changed at the end of a scan). Sweeps don't
    /// overlap; a request during a sweep triggers one more sweep afterwards.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        Interlocked.Exchange(ref _sweepRequested, 1);
        if (Interlocked.CompareExchange(ref _sweeping, 1, 0) != 0) return 0; // the running sweep will go around once more
        var total = 0;
        try
        {
            while (Interlocked.Exchange(ref _sweepRequested, 0) == 1)
            {
                List<Asset> assets;
                using (var c = _db.Open())
                    assets = c.Query<Asset>("SELECT a.* FROM assets a JOIN items i ON i.primary_asset_id=a.id WHERE a.missing=0 AND i.hidden=0 ORDER BY i.taken_at DESC").ToList();
                var added = 0;
                foreach (var chunk in assets.Chunk(500))
                {
                    ct.ThrowIfCancellationRequested();
                    added += await Task.Run(() => Enqueue(chunk), ct);
                }
                if (added > 0) _log.LogInformation("Thumbnail sweep queued {Count:N0} missing thumbnails", added);
                else _log.LogDebug("Thumbnail sweep: checked {Count:N0} items, nothing new to queue", assets.Count);
                total += added;
            }
        }
        finally { Volatile.Write(ref _sweeping, 0); }
        return total;
    }

    // ---------------------------------------------------------------- rendering

    /// <summary>Writes every missing size in <paramref name="sizes"/> from a single decode of the best available source.</summary>
    private async Task<bool> RenderSizesAsync(Asset asset, int[] sizes, int rotation, double squeeze, CancellationToken ct)
    {
        var missing = sizes.Where(s => !File.Exists(CachePath(asset.Hash, s, rotation, squeeze))).OrderByDescending(s => s).ToList();
        if (missing.Count == 0) return true;
        var largest = missing[0];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var (cached, alreadyTurned) = await LoadCachedSourceAsync(asset.Hash, largest, rotation, squeeze, ct);
            string? reason = null;
            if (cached is null) (cached, reason) = await LoadSourceAsync(asset, largest, ct);
            using var img = cached;
            if (img is null)
            {
                // One log line per file, with the reason. Audio without cover art is normal, so that goes to debug.
                if (asset.Kind == MediaKind.Audio) _log.LogDebug("No thumbnail for {Path}: {Reason}", asset.Path, reason);
                else _log.LogWarning("No thumbnail for {Path}: {Reason}", asset.Path, reason ?? "unknown reason");
                _failed.TryAdd(asset.Hash, 0);
                return false;
            }
            img.Mutate(x => x.AutoOrient()); // EXIF orientation first (a no-op for cached thumbnails, which are already upright)
            img.Metadata.ExifProfile = null;
            img.Metadata.XmpProfile = null;
            if (!alreadyTurned) Turn(img, rotation, squeeze, largest);
            var encoder = new JpegEncoder { Quality = ThumbQuality() };
            foreach (var size in missing)
            {
                if (img.Width > size || img.Height > size)
                    img.Mutate(x => x.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(size, size), Sampler = KnownResamplers.Bicubic }));
                var outPath = CachePath(asset.Hash, size, rotation, squeeze);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                var tmp = outPath + ".tmp" + Guid.NewGuid().ToString("N")[..6];
                await img.SaveAsJpegAsync(tmp, encoder, ct);
                File.Move(tmp, outPath, true);
            }
            _log.LogDebug("Thumbnail {Sizes}{Rot}{Squeeze} for {Path} in {Ms} ms", string.Join('+', missing), rotation == 0 ? "" : $" rotated {rotation}°",
                squeeze == 1 ? "" : $" stretched {Squeeze.Format(squeeze)}×", asset.Path, clock.ElapsedMilliseconds);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning("No thumbnail for {Path}: {Error}", asset.Path, ex.Message);
            _log.LogDebug(ex, "thumbnail failure details for {Path}", asset.Path);
            _failed.TryAdd(asset.Hash, 0);
            return false;
        }
    }

    private static RotateMode RotateModeFor(int rotation) => rotation switch { 90 => RotateMode.Rotate90, 180 => RotateMode.Rotate180, _ => RotateMode.Rotate270 };

    /// <summary>
    /// Turns an upright picture by the manual rotation, then stretches its long side by the squeeze, to fit within
    /// <paramref name="fit"/>. The stretch and the shrink happen in one resize, so a large picture never gets enlarged.
    /// </summary>
    private static void Turn(Image img, int rotation, double squeeze, int fit)
    {
        if (rotation != 0) img.Mutate(x => x.Rotate(RotateModeFor(rotation)));
        if (squeeze == 1) return;
        var (w, h) = Squeeze.Apply(img.Width, img.Height, squeeze);
        var f = Math.Min(1.0, (double)fit / Math.Max(w, h));
        var size = new Size(Math.Max(1, (int)Math.Round(w * f)), Math.Max(1, (int)Math.Round(h * f)));
        img.Mutate(x => x.Resize(new ResizeOptions { Mode = ResizeMode.Stretch, Size = size, Sampler = KnownResamplers.Bicubic }));
    }

    /// <summary>
    /// The asset's picture, upright (EXIF orientation, then the manual <paramref name="rotation"/>) and fitted within
    /// <paramref name="size"/> × <paramref name="size"/>, in memory only. For work that needs pixels but no cached
    /// file, such as finding faces. Uses a cached preview at least that large if there is one, otherwise decodes the
    /// original at a reduced size and writes nothing. Null when the asset can't be rendered.
    /// </summary>
    public Task<Image<Rgb24>?> RenderAsync(Asset asset, int size, int rotation, CancellationToken ct = default) => RenderAsync(asset, size, rotation, 1, ct);

    /// <summary>As <see cref="RenderAsync(Asset,int,int,CancellationToken)"/>, with the long side stretched by an anamorphic <paramref name="squeeze"/>.</summary>
    public async Task<Image<Rgb24>?> RenderAsync(Asset asset, int size, int rotation, double squeeze, CancellationToken ct = default)
    {
        rotation = Rotation.Normalize(rotation);
        squeeze = Squeeze.Normalize(squeeze);
        if (_failed.ContainsKey(asset.Hash)) return null;
        Image? img = null;
        try
        {
            var alreadyTurned = false;
            foreach (var s in AllSizes.Where(s => s >= size).Order())
                if (File.Exists(CachePath(asset.Hash, s, rotation, squeeze))) { img = await Image.LoadAsync(CachePath(asset.Hash, s, rotation, squeeze), ct); alreadyTurned = true; break; }
            if (img is null && (rotation != 0 || squeeze != 1))
                foreach (var s in AllSizes.Where(s => s >= size).Order())
                    if (File.Exists(CachePath(asset.Hash, s))) { img = await Image.LoadAsync(CachePath(asset.Hash, s), ct); break; }
            if (img is null)
            {
                (img, var reason) = await LoadSourceAsync(asset, size, ct);
                if (img is null)
                {
                    _log.LogDebug("Could not render {Path}: {Reason}", asset.Path, reason);
                    return null;
                }
            }
            img.Mutate(x => x.AutoOrient());
            if (!alreadyTurned) Turn(img, rotation, squeeze, size);
            if (img.Width > size || img.Height > size)
                img.Mutate(x => x.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(size, size), Sampler = KnownResamplers.Bicubic }));
            if (img is Image<Rgb24> rgb) { img = null; return rgb; }
            return img.CloneAs<Rgb24>();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not render {Path}", asset.Path);
            return null;
        }
        finally { img?.Dispose(); }
    }

    private int ThumbQuality() => int.TryParse(_settings.Get(SettingsService.ThumbQuality, "82"), out var q) ? Math.Clamp(q, 40, 100) : 82;

    /// <summary>
    /// Finds a cached thumbnail to reuse, which is much cheaper than decoding the original again:
    /// 1. a larger one with the same rotation and squeeze, 2. for a turned or stretched request, a plain one at least as
    /// large, which then only needs the turn and stretch applied.
    /// </summary>
    private async Task<(Image? Image, bool AlreadyTurned)> LoadCachedSourceAsync(string hash, int size, int rotation, double squeeze, CancellationToken ct)
    {
        foreach (var bigger in AllSizes.Where(s => s > size).OrderBy(s => s))
        {
            var src = CachePath(hash, bigger, rotation, squeeze);
            if (File.Exists(src)) return (await Image.LoadAsync(src, ct), true);
        }
        if (rotation != 0 || squeeze != 1)
            foreach (var s in AllSizes.Where(s => s >= size).OrderBy(s => s))
            {
                var src = CachePath(hash, s);
                if (File.Exists(src)) return (await Image.LoadAsync(src, ct), false);
            }
        return (null, false);
    }

    /// <summary>RAW formats ffmpeg can decode itself. Any other RAW without a usable embedded preview gets no thumbnail.</summary>
    private static readonly HashSet<string> FfmpegReadableRaw = new(StringComparer.OrdinalIgnoreCase) { "dng" };

    /// <summary>
    /// Decodes the original at about <paramref name="size"/> (a JPEG decodes at a smaller size directly). If it can't
    /// be rendered, Image is null and Reason says why, for the log.
    /// </summary>
    private async Task<(Image? Image, string? Reason)> LoadSourceAsync(Asset asset, int size, CancellationToken ct)
    {
        if (!File.Exists(asset.Path)) return (null, "the file is missing");
        // A picture smaller than the requested size keeps its own size (DecoderOpts), and so do ffmpeg's frames.
        switch (asset.Kind)
        {
            case MediaKind.Image when asset.IsRaw == 1:
            {
                var data = await File.ReadAllBytesAsync(asset.Path, ct);
                var streams = await Task.Run(() => RawPreviewExtractor.FindJpegs(data), ct);
                var best = RawPreviewExtractor.PickPreview(streams);
                string why;
                if (best is not null)
                {
                    try
                    {
                        var preview = data.AsSpan(best.Offset, best.Length);
                        var img = Image.Load(DecoderOpts(size, Image.Identify(preview)), preview);
                        // Previews inside a RAW usually lack an orientation, so copy it from the RAW's own EXIF.
                        ApplyRawOrientation(img, asset.Path);
                        return (img, null);
                    }
                    catch (Exception ex) { why = $"its {best.Width}×{best.Height} embedded preview could not be decoded ({ex.Message})"; }
                }
                else why = streams.Count == 0
                    ? "it has no embedded JPEG preview"
                    : $"none of its embedded JPEGs can be decoded (found {RawPreviewExtractor.Describe(streams)})";
                // ffmpeg reads DNG but not CR2/NEF/ARW/…, where it would only produce decoder errors.
                if (!FfmpegReadableRaw.Contains(asset.Ext)) return (null, $"{why}, and ffmpeg cannot read .{asset.Ext} files");
                var (frame, err) = await FrameFromFfmpegAsync(asset.Path, size, 0, ct);
                return frame is not null ? (frame, null) : (null, $"{why}, and ffmpeg failed: {err}");
            }
            case MediaKind.Image when HeifDecoder.Formats.Contains(asset.Ext):
            {
                // libheif goes first: ffmpeg 7.1 can't open some recent iPhone files, and before 7.1 it decodes only one tile.
                string heifError;
                if (_heif is not null)
                {
                    var (bytes, err) = await _heif.DecodeAsync(asset.Path, ct: ct);
                    if (bytes is not null)
                    {
                        var img = Image.Load(DecoderOpts(size, Image.Identify(bytes)), bytes);
                        img.Metadata.ExifProfile = null; // heif-dec wrote it upright already
                        return (img, null);
                    }
                    heifError = _heif.IsAvailable ? $"libheif failed ({err})" : HeifDecoder.NotInstalled;
                }
                else heifError = HeifDecoder.NotInstalled;
                var expected = asset.Width is { } w && asset.Height is { } h ? (w, h) : ((int, int)?)null;
                var (frame, ffmpegError) = await FrameFromFfmpegAsync(asset.Path, size, 0, ct, expected);
                return frame is not null ? (frame, null) : (null, $"{heifError}, and ffmpeg failed: {ffmpegError}");
            }
            case MediaKind.Image:
            {
                string imageSharpError;
                try { return (await Image.LoadAsync(DecoderOpts(size, await Image.IdentifyAsync(asset.Path, ct)), asset.Path, ct), null); }
                catch (Exception ex) when (ex is UnknownImageFormatException or NotSupportedException or ImageFormatException or InvalidImageContentException)
                {
                    imageSharpError = ex.Message;
                }
                var expected = asset.Width is { } w && asset.Height is { } h ? (w, h) : ((int, int)?)null;
                var (frame, err) = await FrameFromFfmpegAsync(asset.Path, size, 0, ct, expected); // AVIF, odd TIFFs
                return frame is not null ? (frame, null) : (null, $"the image decoder said \"{imageSharpError}\" and ffmpeg failed: {err}");
            }
            case MediaKind.Video:
            {
                var (frame, err) = await FrameFromFfmpegAsync(asset.Path, size, Math.Min(1.0, (asset.DurationSec ?? 2) / 2), ct);
                return frame is not null ? (frame, null) : (null, $"ffmpeg could not grab a frame: {err}");
            }
            case MediaKind.Audio:
            {
                // Cover art, if the file has any; otherwise there's no thumbnail and the UI shows a placeholder.
                var r = await _ffmpeg.CaptureAsync(new[] { "-i", asset.Path, "-an", "-map", "0:v:0", "-frames:v", "1", "-f", "image2pipe", "-vcodec", "mjpeg", "-q:v", "3", "-" }, ct);
                return r.Bytes is null ? (null, $"no cover art ({r.Error})") : (Image.Load(DecoderOpts(size, Image.Identify(r.Bytes)), r.Bytes), null);
            }
            default:
                return (null, $"unknown kind {asset.Kind}");
        }
    }

    /// <summary>Formats that phones save as a grid of tiles (HEIC, AVIF). ffmpeg 7.1 and later joins the tiles itself.</summary>
    internal static readonly HashSet<string> TiledFormats = new(StringComparer.OrdinalIgnoreCase) { "heic", "heif", "avif" };

    /// <summary>
    /// A frame from ffmpeg, upright (ffmpeg applies the stored turn) and fitted within size × size.
    /// <paramref name="expected"/> is the picture's size from the index, used to catch an ffmpeg older than 7.1 decoding
    /// only one tile of a tiled picture.
    /// </summary>
    private async Task<(Image? Image, string? Error)> FrameFromFfmpegAsync(string path, int size, double seek, CancellationToken ct, (int W, int H)? expected = null)
    {
        if (!_ffmpeg.IsAvailable) return (null, "ffmpeg is not installed");
        var scale = $"scale='min({size},iw)':'min({size},ih)':force_original_aspect_ratio=decrease";
        var tiled = seek == 0 && TiledFormats.Contains(Path.GetExtension(path).TrimStart('.'));
        if (tiled && _ffmpeg.AssemblesTileGrids)
        {
            // ffmpeg joins a tile grid in its own filter graph, and -vf can't be combined with that ("Simple and complex
            // filtering cannot be used together"), so the scaling goes into a graph on the grid (stream group 0).
            var grid = await _ffmpeg.CaptureAsync(["-i", path, "-filter_complex", $"[0:g:0]{scale}[o]", "-map", "[o]", "-frames:v", "1", "-f", "image2pipe", "-vcodec", "mjpeg", "-q:v", "3", "-"], ct);
            if (grid.Bytes is not null) return (Image.Load(grid.Bytes), null);
            // No grid means a single picture (small ones aren't tiled), which the plain path below decodes.
        }
        var args = new List<string>();
        if (seek > 0) { args.Add("-ss"); args.Add(seek.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)); }
        args.AddRange(new[] { "-i", path, "-frames:v", "1", "-vf", scale, "-f", "image2pipe", "-vcodec", "mjpeg", "-q:v", "3", "-" });
        var r = await _ffmpeg.CaptureAsync(args, ct);
        if (r.Bytes is null && seek > 0)
            r = await _ffmpeg.CaptureAsync(args.Skip(2), ct); // very short clip: retry from frame 0
        if (r.Bytes is null) return (null, r.Error);
        var frame = Image.Load(r.Bytes);
        if (tiled && expected is { } e && IsPartOf(frame.Width, frame.Height, e.W, e.H))
        {
            frame.Dispose();
            return (null, $"ffmpeg {_ffmpeg.VersionText} decoded only part of it (a {frame.Width}×{frame.Height} tile of a {e.W}×{e.H} picture); ffmpeg 7.1 or later puts tiled HEIC and AVIF photos together");
        }
        return (frame, null);
    }

    /// <summary>True when a decoded frame is much smaller than the picture and a different shape, i.e. one tile of a grid.</summary>
    internal static bool IsPartOf(int w, int h, int pictureW, int pictureH)
    {
        if (pictureW <= 0 || pictureH <= 0) return false;
        double a = (double)w / h, b = (double)pictureW / pictureH;
        var sameShape = Math.Abs(a - b) / b < 0.03 || Math.Abs(a - 1 / b) * b < 0.03; // a turned frame counts as the same shape
        return !sameShape && (double)w * h < (double)pictureW * pictureH / 4;
    }

    private static void ApplyRawOrientation(Image img, string rawPath)
    {
        try
        {
            var dirs = ImageMetadataReader.ReadMetadata(rawPath);
            var ifd0 = dirs.OfType<MetadataExtractor.Formats.Exif.ExifIfd0Directory>().FirstOrDefault();
            if (ifd0 is null || !ifd0.TryGetInt32(MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagOrientation, out var o) || o is < 1 or > 8) return;
            img.Metadata.ExifProfile ??= new SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifProfile();
            img.Metadata.ExifProfile.SetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Orientation, (ushort)o);
        }
        catch { }
    }

    /// <summary>
    /// Decode options that shrink to <paramref name="size"/> when the picture is larger (a JPEG then decodes much
    /// faster). A smaller picture keeps its own size instead of being enlarged by the decoder.
    /// </summary>
    private static DecoderOptions DecoderOpts(int size, ImageInfo source) =>
        Math.Max(source.Width, source.Height) > size ? new() { TargetSize = new Size(size, size), SkipMetadata = false } : new() { SkipMetadata = false };
}
