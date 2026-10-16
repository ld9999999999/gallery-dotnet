using System.Collections.Concurrent;
using System.Threading.Channels;
using Dapper;
using Gallery.Core.Data;
using Gallery.Core.Models;
using Gallery.Develop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace Gallery.Core.Services.Develop;

/// <summary>How an edit is written out (docs/raw-editor.md, "JPEG export").</summary>
public sealed record ExportOptions
{
    /// <summary>Long edge in pixels; null or 0 for the original size. Capped at the original size.</summary>
    public int? LongEdge { get; init; }
    /// <summary>Size as a percentage of the original (1–100); used when LongEdge is not given.</summary>
    public int? Percent { get; init; }
    /// <summary>JPEG quality, 1–100.</summary>
    public int Quality { get; init; } = 90;
    /// <summary>Keep colour detail at full resolution (4:4:4) instead of half (4:2:0, smaller files).</summary>
    public bool FullChroma { get; init; } = true;
    /// <summary>Keep the GPS location in the EXIF.</summary>
    public bool KeepLocation { get; init; } = true;
    /// <summary>Add the JPEG to the gallery as a variation of the photo.</summary>
    public bool SaveToGallery { get; init; } = true;
    /// <summary>Make it the photo's main file (only with SaveToGallery).</summary>
    public bool UseAsPrimary { get; init; }
    /// <summary>Offer the file for download when done.</summary>
    public bool Download { get; init; }

    public ExportOptions Clamped() => this with
    {
        LongEdge = LongEdge is > 0 ? Math.Clamp(LongEdge.Value, 16, 65535) : null,
        Percent = Percent is > 0 ? Math.Clamp(Percent.Value, 1, 100) : null,
        Quality = Math.Clamp(Quality, 1, 100),
        UseAsPrimary = UseAsPrimary && SaveToGallery,
    };

    /// <summary>Long edge to render for a picture whose long edge is <paramref name="full"/>.</summary>
    public int TargetLongEdge(int full) =>
        LongEdge is { } e ? Math.Min(e, full) : Percent is { } p ? Math.Max(16, (int)Math.Round(full * p / 100.0)) : full;
}

public sealed class ExportJob
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    public required long AssetId { get; init; }
    public required DevelopSettings Settings { get; init; }
    public required ExportOptions Options { get; init; }
    /// <summary>queued | running | done | failed</summary>
    public string State { get; set; } = "queued";
    public string? Message { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public long Bytes { get; set; }
    /// <summary>The variation added to the gallery, when saved there.</summary>
    public long? ResultAssetId { get; set; }
    public string? FileName { get; set; }
    internal string? FilePath { get; set; }
    public DateTime CreatedUtc { get; } = DateTime.UtcNow;
}

/// <summary>
/// Renders edits to JPEG at full quality on the server, one at a time:
/// decode at the export size (RAWs through LibRaw) -> run the same Gallery.Develop renderer as the browser -> encode with
/// ImageSharp -> add the JPEG to the gallery as a variation of the photo (in {derived}/edits; exporting again replaces
/// it), keep it for download, or both.
/// </summary>
public sealed class ExportService : BackgroundService
{
    private readonly Db _db;
    private readonly DevelopService _develop;
    private readonly ItemRepository _items;
    private readonly ThumbnailService _thumbs;
    private readonly SettingsService _settings;
    private readonly ILogger<ExportService> _log;
    private readonly Channel<ExportJob> _queue = Channel.CreateUnbounded<ExportJob>();
    private readonly ConcurrentDictionary<string, ExportJob> _jobs = new();
    private string? _active;

    public ExportService(Db db, DevelopService develop, ItemRepository items, ThumbnailService thumbs, SettingsService settings, ILogger<ExportService> log)
    {
        _db = db;
        _develop = develop;
        _items = items;
        _thumbs = thumbs;
        _settings = settings;
        _log = log;
    }

    /// <summary>Exports waiting or running, and the file being exported now.</summary>
    public (int Queued, string? Active) Progress => (_jobs.Values.Count(j => j.State is "queued" or "running"), _active);

    public ExportJob Queue(long assetId, DevelopSettings settings, ExportOptions options)
    {
        var job = new ExportJob { AssetId = assetId, Settings = settings.Clamped(), Options = options.Clamped() };
        _jobs[job.Id] = job;
        _queue.Writer.TryWrite(job);
        Forget();
        return job;
    }

    public ExportJob? Get(string id) => _jobs.GetValueOrDefault(id);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken)) await RunAsync(job, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    internal async Task RunAsync(ExportJob job, CancellationToken ct = default)
    {
        job.State = "running";
        try
        {
            var asset = _items.GetAsset(job.AssetId) ?? throw new DevelopException("the photo is no longer in the library");
            _active = asset.FileName;
            var started = DateTime.UtcNow;
            var (rotation, squeeze) = _items.GetViewForAsset(asset.Id);
            var info = await _develop.GetInfoAsync(asset, rotation, squeeze, ct);
            var image = await DecodeAsync(asset, info, job.Settings.Crop, job.Options, rotation, squeeze, ct);
            var rgba = new byte[image.Width * image.Height * 4];
            await Task.Run(() => new Renderer { Parallelism = RenderThreads }.Render(image, job.Settings, rgba), ct);
            using var jpeg = Image.LoadPixelData<Rgba32>(rgba, image.Width, image.Height);
            ExportMetadata.Apply(jpeg.Metadata, asset.Path, job.Options.KeepLocation, image.Width, image.Height);
            var encoder = Encoder(job.Options);
            var stem = Path.GetFileNameWithoutExtension(asset.FileName);
            job.FileName = $"{stem}-edit.jpg";
            var target = job.Options.SaveToGallery ? GalleryPath(asset, stem) : Path.Combine(TempDir(), $"{job.Id}.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var tmp = target + ".tmp";
            await jpeg.SaveAsJpegAsync(tmp, encoder, ct);
            File.Move(tmp, target, overwrite: true);
            (job.Width, job.Height, job.Bytes, job.FilePath) = (image.Width, image.Height, new FileInfo(target).Length, target);
            if (job.Options.SaveToGallery) job.ResultAssetId = Register(asset, target, image.Width, image.Height, job.Options.UseAsPrimary, job.Settings.Crop.IsIdentity);
            job.State = "done";
            _log.LogInformation("Exported {Source} to {Target}: {W}×{H}, {MB:N1} MB, quality {Q}, in {Ms:N0} ms", asset.Path, target,
                image.Width, image.Height, job.Bytes / 1048576.0, job.Options.Quality, (DateTime.UtcNow - started).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is DevelopException or IOException or UnauthorizedAccessException or InvalidDataException or ImageFormatException)
        {
            job.State = "failed";
            job.Message = ex.Message;
            _log.LogWarning("Export of asset {Id} failed: {Message}", job.AssetId, ex.Message);
        }
        finally { _active = null; }
    }

    /// <summary>
    /// The picture to render, at the export's size: the whole picture, or the crop if there is one. The size options apply
    /// to the crop, so the whole picture is decoded only as large as the crop needs, then turned and cut at that size.
    /// </summary>
    private async Task<LinearImage> DecodeAsync(Asset asset, DevelopInfo info, CropSettings crop, ExportOptions options, int rotation, double squeeze, CancellationToken ct)
    {
        var whole = Math.Max(info.Width, info.Height);
        if (crop.IsIdentity) return await _develop.DecodeLinearAsync(asset, options.TargetLongEdge(whole), rotation, squeeze, ct);
        var (cw, ch) = Geometry.Size(crop, info.Width, info.Height);
        var k = (double)options.TargetLongEdge(Math.Max(cw, ch)) / Math.Max(cw, ch);
        if (k >= 1) return Geometry.Apply(await _develop.DecodeLinearAsync(asset, whole, rotation, squeeze, ct), crop, RenderThreads); // cut the same way as the 100% view
        var image = await _develop.DecodeLinearAsync(asset, (int)Math.Ceiling(whole * k), rotation, squeeze, ct);
        return Geometry.Apply(image, crop, Math.Max(1, (int)Math.Round(cw * k)), Math.Max(1, (int)Math.Round(ch * k)), RenderThreads);
    }

    /// <summary>An export uses half the processor's threads: still quick, and the rest stay free while a batch runs.</summary>
    private static int RenderThreads => Math.Max(1, Environment.ProcessorCount / 2);

    private static JpegEncoder Encoder(ExportOptions o) => new()
    {
        Quality = o.Quality,
        ColorType = o.FullChroma ? JpegEncodingColor.YCbCrRatio444 : JpegEncodingColor.YCbCrRatio420,
    };

    /// <summary>
    /// About how large the JPEG will be: renders and encodes the editing copy with these options, then scales by pixel count.
    /// </summary>
    public async Task<(int Width, int Height, long Bytes)> EstimateAsync(Asset asset, DevelopSettings settings, ExportOptions options, CancellationToken ct = default)
    {
        options = options.Clamped();
        var (rotation, squeeze) = _items.GetViewForAsset(asset.Id);
        var info = await _develop.GetInfoAsync(asset, rotation, squeeze, ct);
        var crop = settings.Clamped().Crop;
        var (cw, ch) = Geometry.Size(crop, info.Width, info.Height); // the part exported: the crop, if there is one
        var full = Math.Max(cw, ch);
        var target = options.TargetLongEdge(full);
        var path = await _develop.GetSourceAsync(asset, DevelopService.Sizes[0], rotation, squeeze, ct);
        byte[] bytes;
        await using (var fs = File.OpenRead(path))
        await using (var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress))
        using (var ms = new MemoryStream()) { await gz.CopyToAsync(ms, ct); bytes = ms.ToArray(); }
        var image = Geometry.Apply(LinearImage.FromGdev(bytes), crop, RenderThreads);
        if (Math.Max(image.Width, image.Height) > target) image = image.FitWithin(target, target);
        var rgba = new byte[image.Width * image.Height * 4];
        new Renderer { Parallelism = RenderThreads }.Render(image, settings, rgba);
        using var jpeg = Image.LoadPixelData<Rgba32>(rgba, image.Width, image.Height);
        using var encoded = new MemoryStream();
        await jpeg.SaveAsJpegAsync(encoded, Encoder(options), ct);
        var scale = (double)target / full;
        int w = Math.Max(1, (int)Math.Round(cw * scale)), h = Math.Max(1, (int)Math.Round(ch * scale));
        return (w, h, (long)(encoded.Length * ((double)w * h / (image.Width * image.Height))));
    }

    /// <summary>Where the gallery keeps this photo's export. A new export of the same file replaces the last one.</summary>
    private string GalleryPath(Asset source, string stem)
    {
        using var c = _db.Open();
        var previous = c.QueryFirstOrDefault<string>("SELECT path FROM assets WHERE edit_of=@id ORDER BY id DESC LIMIT 1", new { id = source.Id });
        return previous ?? Path.Combine(_settings.GetDerivedDir(), "edits", source.Hash[..2], $"{stem}-{source.Id}-edit.jpg");
    }

    private string TempDir() => Path.Combine(_settings.GetDerivedDir(), "exports-tmp");

    /// <summary>Adds (or refreshes) the export as a variation of the source's item and queues its thumbnails.</summary>
    private long Register(Asset source, string path, int width, int height, bool primary, bool uncropped)
    {
        var fi = new FileInfo(path);
        var hash = Hasher.HashFile(path);
        long id;
        using (var c = _db.Open())
        using (var tx = c.BeginTransaction())
        {
            var row = new
            {
                itemId = source.ItemId, path, name = fi.Name, size = fi.Length, mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds(),
                hash, width, height, takenAt = source.TakenAt, takenSource = source.TakenSource, editOf = source.Id,
                // An edit of a 360° photo is also a 360° photo, stitched or as two fisheye circles (the lens calibration
                // goes with it in the EXIF), if it is not cropped or turned. A RAW's developed frame can be a few pixels off.
                projection = uncropped && Projection.IsSphere(source.Projection) && Math.Abs((double)width / height - 2) < 0.01 ? source.Projection : null,
            };
            id = c.ExecuteScalar<long?>("SELECT id FROM assets WHERE path=@path", new { path }, tx) ?? 0;
            if (id > 0)
                c.Execute("""
                    UPDATE assets SET item_id=@itemId, size=@size, mtime=@mtime, hash=@hash, width=@width, height=@height, missing=0, edit_of=@editOf,
                        projection=@projection
                    WHERE path=@path
                    """, row, tx);
            else
                id = c.ExecuteScalar<long>("""
                    INSERT INTO assets(item_id, path, file_name, ext, size, mtime, hash, kind, is_raw, width, height, taken_at, taken_source, last_seen_scan, missing, edit_of, projection)
                    VALUES(@itemId, @path, @name, 'jpg', @size, @mtime, @hash, 'image', 0, @width, @height, @takenAt, @takenSource, 0, 0, @editOf, @projection);
                    SELECT last_insert_rowid();
                    """, row, tx);
            c.Execute("INSERT OR IGNORE INTO derived(asset_id, kind, path, created_at) VALUES(@source, 'edit', @path, @now)", new { source = source.Id, path, now = Clock.NowIso() }, tx);
            if (primary) c.Execute("UPDATE items SET preferred_asset_id=@id WHERE id=@itemId", new { id, itemId = source.ItemId }, tx);
            Scanner.RebuildItemColumns(c, tx, [source.ItemId]);
            tx.Commit();
        }
        _db.Touch();
        if (_items.GetAsset(id) is { } added) _thumbs.Enqueue([added]);
        return id;
    }

    /// <summary>
    /// Deletes an edit exported to the gallery: its file, its thumbnails, its place among the photo's files, and the
    /// faces found in it. Only a file the editor wrote (recorded as an edit in derived) is deleted.
    /// Exporting again makes a new one, with the original's metadata as it is read at that time. Returns the item it was
    /// in, or null when the asset is not such an edit.
    /// </summary>
    public long? DeleteEdit(long assetId)
    {
        if (_items.GetAsset(assetId) is not { EditOf: not null } edit) return null;
        using (var c = _db.Open())
        using (var tx = c.BeginTransaction())
        {
            if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM derived WHERE kind='edit' AND path=@path", new { path = edit.Path }, tx) == 0) return null;
            c.Execute("UPDATE items SET preferred_asset_id=NULL WHERE preferred_asset_id=@assetId", new { assetId }, tx);
            c.Execute("DELETE FROM derived WHERE kind='edit' AND path=@path", new { path = edit.Path }, tx);
            c.Execute("DELETE FROM assets WHERE id=@assetId", new { assetId }, tx);
            Scanner.RebuildItemColumns(c, tx, [edit.ItemId]);
            tx.Commit();
        }
        try { File.Delete(edit.Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.LogWarning("Could not delete {Path}: {Message}", edit.Path, ex.Message); }
        using (var c = _db.Open())
            if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM assets WHERE hash=@hash", new { hash = edit.Hash }) == 0) _thumbs.DeleteCached(edit.Hash);
        _db.Touch();
        _items.OnPictureChanged([edit.ItemId]); // its main file may have changed
        _log.LogInformation("Deleted the exported edit {Path}", edit.Path);
        return edit.ItemId;
    }

    /// <summary>Removes finished jobs and download-only files after an hour.</summary>
    private void Forget()
    {
        foreach (var j in _jobs.Values.Where(j => j.State is "done" or "failed" && DateTime.UtcNow - j.CreatedUtc > TimeSpan.FromHours(1)).ToList())
            _jobs.TryRemove(j.Id, out _);
        try
        {
            var dir = new DirectoryInfo(TempDir());
            if (dir.Exists) foreach (var f in dir.GetFiles().Where(f => DateTime.UtcNow - f.LastWriteTimeUtc > TimeSpan.FromHours(1))) f.Delete();
        }
        catch (IOException) { }
    }
}
