using Dapper;
using Gallery.Core.Data;

namespace Gallery.Core.Services;

public sealed record ScanActivity(
    bool Running, string Phase, long Discovered, long Unchanged, long Queued, long Processed, long Pending,
    long Added, long Updated, long Failed, long Missing, string? StartedAt, double? FilesPerSecond, double? EtaSeconds,
    string? CurrentPath, string? LastFinishedAt, string? LastStatus);

public sealed record ThumbnailActivity(bool Running, long Total, long Done, long Pending);

/// <summary>Conversions someone asked for (Queued, Active), and those made while the server is idle (Idle, which doesn't count as "busy").</summary>
public sealed record TranscodeActivity(int Queued, string? Active, IdleTranscodeStatus? Idle = null);

public sealed record ExportActivity(int Queued, string? Active);

public sealed record LibraryStatus(bool Busy, ItemRepository.Stats Stats, ScanActivity Scan, ThumbnailActivity Thumbnails, TranscodeActivity Transcodes,
    ExportActivity? Exports = null, Faces.FaceActivity? Faces = null);

/// <summary>
/// One snapshot of what the server is doing (scan, thumbnail, transcode, export and face queues) plus library totals.
/// Totals are cached, recomputed at most every 2 s while the library is changing and otherwise every 5 minutes, so many
/// open browser tabs polling this endpoint don't each run the heavy count queries.
/// </summary>
public sealed class StatusService
{
    private static readonly TimeSpan MinRefresh = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    private readonly Db _db;
    private readonly ItemRepository _repo;
    private readonly Scanner _scanner;
    private readonly ThumbnailService _thumbs;
    private readonly TranscodeService _transcodes;
    private readonly Develop.ExportService? _exports;
    private readonly Faces.FaceService? _faces;
    private readonly Faces.PeopleService? _people;
    private readonly object _statsLock = new();
    private ItemRepository.Stats? _stats;
    private long _statsVersion = -1;
    private DateTime _statsAt;

    public StatusService(Db db, ItemRepository repo, Scanner scanner, ThumbnailService thumbs, TranscodeService transcodes, Develop.ExportService? exports = null,
        Faces.FaceService? faces = null, Faces.PeopleService? people = null)
    {
        _exports = exports;
        _faces = faces;
        _people = people;
        _db = db;
        _repo = repo;
        _scanner = scanner;
        _thumbs = thumbs;
        _transcodes = transcodes;
    }

    public ItemRepository.Stats GetStats()
    {
        lock (_statsLock)
        {
            var version = _db.Version;
            var age = DateTime.UtcNow - _statsAt;
            if (_stats is null || (version != _statsVersion && age >= MinRefresh) || age >= MaxAge)
            {
                _stats = _repo.GetStats();
                _statsVersion = version;
                _statsAt = DateTime.UtcNow;
            }
            return _stats;
        }
    }

    /// <summary>What the server is doing in the background, or null if nothing. Work that waits for the server to be idle checks this.</summary>
    public string? BackgroundWork()
    {
        var s = Snapshot(includePaths: false);
        if (s.Scan.Running) return "a scan is running";
        if (s.Thumbnails.Running) return "thumbnails are being made";
        if (s.Faces is { Running: true } or { Grouping: true }) return "faces are being found";
        if (s.Exports?.Queued > 0) return "photos are being exported";
        if (s.Transcodes.Queued > 0) return "a video someone played is being converted";
        return null;
    }

    public LibraryStatus Snapshot(bool includePaths)
    {
        ScanActivity scan;
        lock (_scanner.Progress)
        {
            var p = _scanner.Progress;
            double? rate = null, eta = null;
            if (p.Running && p.Phase == "indexing" && p.IndexingStartedUtc is { } started && p.FilesProcessed > 0)
            {
                var secs = (DateTime.UtcNow - started).TotalSeconds;
                if (secs > 1)
                {
                    rate = p.FilesProcessed / secs;
                    eta = p.Pending / rate;
                }
            }
            scan = new ScanActivity(p.Running, p.Phase, p.FilesSeen, p.FilesSkipped, p.FilesQueued, p.FilesProcessed, p.Pending,
                p.FilesAdded, p.FilesUpdated, p.FilesFailed, p.FilesMissing, p.StartedAt, rate, eta,
                includePaths ? p.CurrentPath : null, null, null);
        }
        if (!scan.Running)
        {
            using var c = _db.Open();
            var last = c.QueryFirstOrDefault<(string finished_at, string status)?>(
                "SELECT finished_at, status FROM scan_runs WHERE finished_at IS NOT NULL ORDER BY id DESC LIMIT 1");
            if (last is { } l) scan = scan with { LastFinishedAt = l.finished_at, LastStatus = l.status };
        }

        var (tRunning, tDone, tPending) = _thumbs.QueueProgress;
        var thumbs = new ThumbnailActivity(tRunning, tDone + tPending, tDone, tPending);
        var transcodes = new TranscodeActivity(_transcodes.QueuedCount, _transcodes.ActiveFile, _transcodes.Idle);
        var exports = _exports is null ? null : new ExportActivity(_exports.Progress.Queued, _exports.Progress.Active);
        var faces = _faces?.Activity;
        if (faces is not null && _people is { Grouping: true }) faces = faces with { Grouping = true };
        if (faces is not null && !includePaths) faces = faces with { Detail = null }; // may name server paths
        var busy = scan.Running || thumbs.Running || transcodes.Queued > 0;
        busy |= exports?.Queued > 0;
        busy |= faces is { Running: true } || faces is { Grouping: true } || faces?.State == "downloading";
        return new LibraryStatus(busy, GetStats(), scan, thumbs, transcodes, exports, faces);
    }
}
