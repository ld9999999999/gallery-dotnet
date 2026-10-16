using Dapper;
using Gallery.Core.Data;
using Gallery.Core.Models;

namespace Gallery.Core.Services;

/// <summary>
/// Finds 360° photos that were indexed before detection existed. A scan skips unchanged files, so it would never revisit
/// them. Only photos exactly twice as wide as high can be 360°, so only those get their metadata (not their pixels) read
/// again, whatever was recorded for them before. Runs once per library, and again whenever the key changes. Bump the key
/// when detection improves so earlier misreads get rechecked (for example an Insta360's two fisheye circles, in JPEG or
/// DNG, taken for a stitched photo).
/// </summary>
public sealed class ProjectionBackfill : BackgroundService
{
    public const string DoneKey = "projection_detected_3";

    private readonly Db _db;
    private readonly SettingsService _settings;
    private readonly MetadataService _meta;
    private readonly ILogger<ProjectionBackfill> _log;

    public ProjectionBackfill(Db db, SettingsService settings, MetadataService meta, ILogger<ProjectionBackfill> log)
    {
        _db = db;
        _settings = settings;
        _meta = meta;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // off the startup path
        try { await RunAsync(stoppingToken); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogError(ex, "Looking for 360° photos failed"); }
    }

    /// <summary>Runs the check if it's due. Returns how many 360° photos were found.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        if (_settings.Get(DoneKey) == "1") return 0;
        List<Asset> assets;
        using (var c = _db.Open())
            assets = c.Query<Asset>("SELECT * FROM assets WHERE kind=@kind AND missing=0 AND height>0 AND width=2*height",
                new { kind = MediaKind.Image }).ToList();
        var changed = new List<(long Id, string? Projection)>();
        var found = 0;
        await Parallel.ForEachAsync(assets, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2), CancellationToken = ct }, (a, _) =>
        {
            if (!File.Exists(a.Path)) return ValueTask.CompletedTask;
            // Use the recorded size: for a HEIC photo it came from libheif, not from the metadata.
            var projection = Projection.Decide(_meta.ReadImage(a.Path).Panorama, a.Width, a.Height);
            if (projection is not null) Interlocked.Increment(ref found);
            if (projection != a.Projection) lock (changed) changed.Add((a.Id, projection));
            return ValueTask.CompletedTask;
        });
        if (changed.Count > 0)
        {
            using var c = _db.Open();
            using var tx = c.BeginTransaction();
            c.Execute("UPDATE assets SET projection=@projection WHERE id=@id", changed.Select(x => new { id = x.Id, projection = x.Projection }), tx);
            tx.Commit();
            _db.Touch();
        }
        _settings.Set(DoneKey, "1");
        if (assets.Count > 0)
            _log.LogInformation("Checked {Count:N0} photos twice as wide as high for 360° photos: {Found:N0} are, {Changed:N0} recorded differently before",
                assets.Count, found, changed.Count);
        return found;
    }
}
