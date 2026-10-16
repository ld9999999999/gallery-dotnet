using System.Data;
using System.Diagnostics;
using System.Threading.Channels;
using Dapper;
using Gallery.Core.Data;
using Gallery.Core.Models;
using Microsoft.Data.Sqlite;

namespace Gallery.Core.Services;

/// <summary>Current scan state. Lock it (lock(this)) for every read and write.</summary>
public sealed class ScanProgress
{
    public bool Running { get; set; }
    public long? RunId { get; set; }

    /// <summary>idle | discovering | indexing | finalizing</summary>
    public string Phase { get; set; } = "idle";

    /// <summary>Media files found on disk so far.</summary>
    public long FilesSeen { get; set; }

    /// <summary>Files unchanged since the last scan (skipped without hashing).</summary>
    public long FilesSkipped { get; set; }

    /// <summary>New or changed files that need hashing and metadata extraction.</summary>
    public long FilesQueued { get; set; }

    /// <summary>Queued files finished (successfully or not).</summary>
    public long FilesProcessed { get; set; }

    public long FilesAdded { get; set; }
    public long FilesUpdated { get; set; }
    public long FilesFailed { get; set; }
    public long FilesMissing { get; set; }

    public long Pending => Math.Max(0, FilesQueued - FilesProcessed);

    public string? CurrentPath { get; set; }
    public string? Message { get; set; }
    public string? StartedAt { get; set; }
    public DateTime? IndexingStartedUtc { get; set; }
}

/// <summary>
/// Adds the files in the source folders to the database. Runs in the background; call <see cref="RequestScan"/> to start a scan.
/// A scan makes two passes so it can show progress:
/// 1. Walk the folders and compare size + mtime with the database (fast) to get the list of new or changed files.
/// 2. Hash those files and read their metadata.
/// Grouping: files with the same name stem and capture time (or the same folder when there's no EXIF date) become one
/// item with several variations (JPEG + RAW, resized copies). Files with the same hash always join the same item.
/// </summary>
public sealed class Scanner : BackgroundService
{
    private const int BatchSize = 64;
    /// <summary>Set once a full scan has reread the AVIs whose date didn't come from their metadata (see <see cref="NeedsDateReread"/>).</summary>
    public const string AviDatesReadKey = "avi_dates_read";

    private readonly Db _db;
    private readonly MetadataService _meta;
    private readonly SettingsService _settings;
    private readonly ThumbnailService _thumbs;
    private readonly ILogger<Scanner> _log;
    private readonly Channel<bool> _trigger = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly List<long> _touched = new();
    private CancellationTokenSource? _scanCts;
    private bool _rereadAvis; // this scan rereads AVIs whose date didn't come from their metadata
    private CancellationToken _appStopping = CancellationToken.None;

    public ScanProgress Progress { get; } = new();

    private readonly FileChangeService? _changes;
    private readonly Faces.FaceService? _faces;

    public Scanner(Db db, MetadataService meta, SettingsService settings, ThumbnailService thumbs, ILogger<Scanner> log, FileChangeService? changes = null,
        Faces.FaceService? faces = null)
    {
        _db = db;
        _meta = meta;
        _settings = settings;
        _thumbs = thumbs;
        _log = log;
        _changes = changes;
        _faces = faces;
    }

    private volatile bool _userCancelled;

    /// <summary>For tests: raised after each batch is saved, with the number of files written.</summary>
    internal event Action<int>? BatchCommitted;

    public bool RequestScan() => _trigger.Writer.TryWrite(true);

    /// <summary>The admin pressed Cancel. The scan is saved as "cancelled" and stays that way across restarts.</summary>
    public void CancelScan()
    {
        _userCancelled = true;
        _scanCts?.Cancel();
    }

    /// <summary>
    /// Call once at startup. A scan still marked "running" means the process died mid-scan (crash, kill -9), so it gets
    /// marked "interrupted". Returns true when the last scan was interrupted (this way or by a normal shutdown) so the
    /// caller can resume it. Resuming is fast because the size + mtime check skips files already in the database.
    /// </summary>
    public bool RecoverInterruptedRuns()
    {
        using var c = _db.Open();
        var stale = c.Execute("UPDATE scan_runs SET status='interrupted', finished_at=COALESCE(finished_at, @now), message=COALESCE(message, 'Server stopped during the scan') WHERE status='running'",
            new { now = Clock.NowIso() });
        if (stale > 0) _log.LogWarning("Found {Count} scan(s) left running by a previous process; marked as interrupted", stale);
        var last = c.QueryFirstOrDefault<(long id, string status)?>("SELECT id, status FROM scan_runs ORDER BY id DESC LIMIT 1");
        if (last is { status: "interrupted" } l)
        {
            _log.LogWarning("Scan #{RunId} did not finish (server stopped); resuming it now. Files already indexed are skipped.", l.id);
            return true;
        }
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _appStopping = stoppingToken;
        // Fix items that an interrupted scan left without a main file, then make any thumbnails the last run didn't finish.
        // This happens before any scan, on a background task so the web server can start without waiting.
        await Task.Run(() =>
        {
            try { RepairIncompleteItems(); }
            catch (Exception ex) { _log.LogError(ex, "Repairing incomplete items failed"); }
        }, stoppingToken);
        // Files deleted while the server was off get hidden now instead of at the end of the next full scan.
        if (_changes is not null)
        {
            try { await _changes.CheckAllAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.LogError(ex, "Checking for deleted files failed"); }
        }
        QueueMissingThumbnails();

        await foreach (var _ in _trigger.Reader.ReadAllAsync(stoppingToken))
        {
            _scanCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            try { await RunScanAsync(_scanCts.Token); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; } // shutdown: saved as interrupted
            catch (OperationCanceledException) { } // admin cancel: RunScanAsync has already logged it
            catch (Exception ex) { _log.LogError(ex, "Scan failed"); }
            finally { _scanCts.Dispose(); _scanCts = null; }
        }
    }

    private bool ThumbnailsEnabled => _settings.GetBool(SettingsService.PregenThumbs, true);

    /// <summary>
    /// Queues thumbnails for every item that has none. Runs at startup, after each scan and after a rebuild.
    /// <paramref name="force"/> overrides the "generate in the background" setting (the admin asked for it).
    /// </summary>
    public void QueueMissingThumbnails(bool force = false)
    {
        if (!force && !ThumbnailsEnabled) return;
        var token = _appStopping;
        _ = Task.Run(async () =>
        {
            try { await _thumbs.SweepAsync(token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogWarning(ex, "Thumbnail sweep failed"); }
        }, token);
    }

    public async Task RunScanAsync(CancellationToken ct)
    {
        long runId;
        List<Source> sources;
        using (var c = _db.Open())
        {
            sources = c.Query<Source>("SELECT * FROM sources WHERE enabled=1").ToList();
            runId = c.ExecuteScalar<long>("INSERT INTO scan_runs(started_at, status) VALUES(@now,'running'); SELECT last_insert_rowid();", new { now = Clock.NowIso() });
        }

        _userCancelled = false;
        _rereadAvis = _settings.Get(AviDatesReadKey) != "1";
        var clock = Stopwatch.StartNew();
        _log.LogInformation("Scan #{RunId} started: {Count} source(s)", runId, sources.Count);

        lock (Progress)
        {
            Progress.Running = true; Progress.RunId = runId; Progress.Phase = "discovering";
            Progress.FilesSeen = 0; Progress.FilesSkipped = 0; Progress.FilesQueued = 0; Progress.FilesProcessed = 0;
            Progress.FilesAdded = 0; Progress.FilesUpdated = 0; Progress.FilesFailed = 0; Progress.FilesMissing = 0;
            Progress.Message = null; Progress.StartedAt = Clock.NowIso(); Progress.IndexingStartedUtc = null; Progress.CurrentPath = null;
        }

        var status = "done";
        string? message = null;
        try
        {
            // Pass 0: remove files deleted since the last check (including while the server was off) first,
            // so "Scan now" clears deleted files even if the scan stops before it finishes.
            if (_changes is not null)
            {
                lock (Progress) Progress.Phase = "checking";
                var removed = await _changes.CheckAllAsync(ct);
                lock (Progress) Progress.FilesMissing += removed.Deleted;
                lock (Progress) Progress.Phase = "discovering";
            }

            // Pass 1: find files with a fast folder walk. Unchanged files are just marked as seen.
            var work = new List<PendingFile>();
            foreach (var src in sources)
            {
                var root = GalleryOptions.ExpandHome(src.Path);
                if (!Directory.Exists(root)) { _log.LogWarning("Source {Path} does not exist, skipping", root); continue; }
                _log.LogInformation("Scan #{RunId}: walking {Path}", runId, root);
                Discover(root, runId, work, ct);
            }
            FlushTouched(runId);
            lock (Progress)
                _log.LogInformation("Scan #{RunId} discovery finished in {Elapsed}: {Seen:N0} media files, {Queued:N0} new or changed, {Skipped:N0} unchanged",
                    runId, Fmt(clock.Elapsed), Progress.FilesSeen, Progress.FilesQueued, Progress.FilesSkipped);
            var indexClock = Stopwatch.StartNew();

            // Pass 2: hash and read metadata for new or changed files, saving in batches so the gallery fills in during the scan.
            lock (Progress) { Progress.Phase = "indexing"; Progress.IndexingStartedUtc = DateTime.UtcNow; }
            for (var i = 0; i < work.Count; i += BatchSize)
            {
                ct.ThrowIfCancellationRequested();
                await ProcessBatchAsync(work.GetRange(i, Math.Min(BatchSize, work.Count - i)), runId, ct);
            }

            lock (Progress)
            {
                Progress.Phase = "finalizing"; Progress.CurrentPath = null;
                if (work.Count > 0)
                    _log.LogInformation("Scan #{RunId} indexing finished in {Elapsed}: {Added:N0} added, {Updated:N0} updated, {Failed:N0} failed ({Rate:0.0} files/s)",
                        runId, Fmt(indexClock.Elapsed), Progress.FilesAdded, Progress.FilesUpdated, Progress.FilesFailed, work.Count / Math.Max(0.001, indexClock.Elapsed.TotalSeconds));
            }
            MarkMissing(runId, sources);
            RebuildItems(ct);
            if (_rereadAvis) _settings.Set(AviDatesReadKey, "1");
        }
        catch (OperationCanceledException)
        {
            // An admin cancel stays cancelled; anything else (shutdown) resumes on the next start.
            status = _userCancelled ? "cancelled" : "interrupted";
            lock (Progress)
                _log.LogWarning(status == "interrupted"
                        ? "Scan #{RunId} interrupted by shutdown after {Processed:N0} of {Queued:N0} files; it resumes on the next start"
                        : "Scan #{RunId} cancelled after {Processed:N0} of {Queued:N0} files",
                    runId, Progress.FilesProcessed, Progress.FilesQueued);
            throw;
        }
        catch (Exception ex) { status = "failed"; message = ex.Message; throw; }
        finally
        {
            ScanProgress snap;
            lock (Progress)
            {
                Progress.Running = false; Progress.Phase = "idle"; Progress.CurrentPath = null; Progress.Message = status;
                snap = Progress;
            }
            using var c = _db.Open();
            c.Execute("UPDATE scan_runs SET finished_at=@now, status=@status, message=@message, files_seen=@seen, files_added=@added, files_updated=@updated, files_missing=@missing WHERE id=@runId",
                new { now = Clock.NowIso(), status, message, seen = snap.FilesSeen, added = snap.FilesAdded, updated = snap.FilesUpdated, missing = snap.FilesMissing, runId });
            _db.Touch();
            _log.LogInformation("Scan #{RunId} {Status} in {Elapsed}: {Seen:N0} files seen, {Skipped:N0} unchanged, {Added:N0} added, {Updated:N0} updated, {Failed:N0} failed, {Missing:N0} missing",
                runId, status, Fmt(clock.Elapsed), snap.FilesSeen, snap.FilesSkipped, snap.FilesAdded, snap.FilesUpdated, snap.FilesFailed, snap.FilesMissing);
        }

        QueueMissingThumbnails();
        _faces?.RequestSweep();
    }

    private sealed record PendingFile(string Path, string Kind, bool IsRaw, long? ExistingId);

    private void Discover(string root, long runId, List<PendingFile> work, CancellationToken ct)
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System, ReturnSpecialDirectories = false };
        var existing = LoadExisting(root);
        // The gallery's own folders (database, thumbnails, transcodes, edits) can sit inside a source and must be skipped.
        var own = new[] { _settings.DataDir, _settings.GetDerivedDir() }.Select(d => Path.GetFullPath(d).TrimEnd('/') + "/").ToArray();

        foreach (var path in Directory.EnumerateFiles(root, "*", opts))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (name.StartsWith("._") || name.StartsWith('.')) continue; // macOS resource forks, dotfiles
            if (path.Contains("/@eaDir/") || path.Contains("/.thumbnails/")) continue;
            if (own.Any(o => path.StartsWith(o, StringComparison.Ordinal))) continue;
            var cls = FileTypes.Classify(Path.GetExtension(path));
            if (cls is null) continue;

            FileInfo fi;
            try { fi = new FileInfo(path); _ = fi.Length; }
            catch (Exception) { continue; } // deleted or unreadable since it was listed

            var mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
            if (existing.TryGetValue(path, out var ex) && ex.Size == fi.Length && ex.Mtime == mtime && !NeedsDateReread(ex.TakenSource, ex.TakenAt, name))
            {
                lock (Progress) { Progress.FilesSeen++; Progress.FilesSkipped++; }
                TouchSeen(ex.Id, runId);
                continue;
            }
            work.Add(new PendingFile(path, cls.Value.Kind, cls.Value.IsRaw, existing.TryGetValue(path, out var e2) ? e2.Id : null));
            lock (Progress) { Progress.FilesSeen++; Progress.FilesQueued++; Progress.CurrentPath = path; }
        }
    }

    private void TouchSeen(long assetId, long runId)
    {
        _touched.Add(assetId);
        if (_touched.Count >= 500) FlushTouched(runId);
    }

    private void FlushTouched(long runId)
    {
        if (_touched.Count == 0) return;
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        c.Execute("UPDATE assets SET last_seen_scan=@runId, missing=0 WHERE id=@id", _touched.Select(id => new { runId, id }), tx);
        tx.Commit();
        _touched.Clear();
    }

    private Dictionary<string, (long Id, long Size, long Mtime, string? TakenSource, string? TakenAt)> LoadExisting(string root)
    {
        using var c = _db.Open();
        return c.Query<(string path, long id, long size, long mtime, string? takenSource, string? takenAt)>(
                "SELECT path, id, size, mtime, taken_source, taken_at FROM assets WHERE path LIKE @prefix", new { prefix = root.TrimEnd('/') + "/%" })
            .ToDictionary(x => x.path, x => (x.id, x.size, x.mtime, x.takenSource, x.takenAt));
    }

    /// <summary>
    /// An unchanged file is normally skipped. It's read again when:
    /// <list type="bullet">
    /// <item>it has no saved date source and its name starts with a date, so the file-name date can replace a guess
    /// from the file timestamps when there's no metadata date;</item>
    /// <item>its date is in the future, so it gets the next date it has;</item>
    /// <item>it's an AVI whose date didn't come from its metadata (<see cref="AviDate"/>). This happens once, in the
    /// first full scan while <see cref="AviDatesReadKey"/> is unset.</item>
    /// </list>
    /// A date fixed later (in an editor, or in the file's timestamps) changes the file, so it gets read again anyway.
    /// </summary>
    private bool NeedsDateReread(string? takenSource, string? takenAt, string fileName) =>
        (takenSource is null && FileNameDate.Parse(fileName) is not null)
        || (takenAt is not null && string.CompareOrdinal(takenAt, CaptureDate.Latest()) > 0)
        || (_rereadAvis && takenSource != "metadata" && AviDate.IsAvi(fileName));

    /// <summary>Counts files that <see cref="NeedsDateReread"/> applies to. If there are any, a scan runs at startup.</summary>
    public int CountFilesNeedingDateReread()
    {
        using var c = _db.Open();
        var named = c.Query<string>("""
            SELECT file_name FROM assets WHERE taken_source IS NULL AND missing = 0
              AND (file_name GLOB '[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]*' OR file_name GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]*')
            """).Count(n => FileNameDate.Parse(n) is not null);
        var avis = _settings.Get(AviDatesReadKey) == "1" ? 0
            : c.ExecuteScalar<int>("SELECT COUNT(*) FROM assets WHERE missing = 0 AND lower(ext) = 'avi' AND COALESCE(taken_source, '') <> 'metadata'");
        return named + avis + c.ExecuteScalar<int>("SELECT COUNT(*) FROM assets WHERE missing = 0 AND taken_at > @latest", new { latest = CaptureDate.Latest() });
    }

    /// <summary>
    /// Picks the capture date for a file: its own metadata (EXIF, video/audio container) first, then a date at the
    /// start of its name, then its file timestamps. An implausible date (<see cref="CaptureDate"/>, e.g. in the future)
    /// falls through to the next source. Returns the date and where it came from, or "none" when no date fits.
    /// </summary>
    public static (DateTime? Date, string Source) ChooseTakenAt(DateTime? metadata, string fileName, FileInfo file)
    {
        if (metadata is { } m && CaptureDate.IsPlausible(m)) return (m, "metadata");
        if (FileNameDate.Parse(fileName) is { } n && CaptureDate.IsPlausible(n)) return (n, "filename");
        return FallbackDate(file) is { } f ? (f, "file") : (null, "none");
    }

    private sealed record Probed(string Path, FileInfo Info, string Kind, bool IsRaw, long? ExistingId, string Hash, MediaMetadata Meta);

    private async Task ProcessBatchAsync(List<PendingFile> batch, long runId, CancellationToken ct)
    {
        // Hash and read metadata in parallel (mostly disk wait), then write everything in one transaction.
        var results = new Probed?[batch.Count];
        await Parallel.ForAsync(0, batch.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount), CancellationToken = ct }, async (i, token) =>
        {
            var f = batch[i];
            lock (Progress) Progress.CurrentPath = f.Path;
            try
            {
                var info = new FileInfo(f.Path);
                var hash = await Task.Run(() => Hasher.HashFile(f.Path), token);
                var meta = await _meta.ReadAsync(f.Path, f.Kind, token);
                results[i] = new Probed(f.Path, info, f.Kind, f.IsRaw, f.ExistingId, hash, meta);
                var (taken, source) = ChooseTakenAt(meta.TakenAt, Path.GetFileName(f.Path), info);
                _log.LogDebug("Indexed {Path} ({Kind}{Raw}, {Size:N0} bytes, hash {Hash}, taken {Taken} from {Source})",
                    f.Path, f.Kind, f.IsRaw ? " RAW" : "", info.Length, hash, taken?.ToString("yyyy-MM-dd HH:mm:ss") ?? "unknown", source);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "failed to index {Path}", f.Path);
                lock (Progress) Progress.FilesFailed++;
            }
            finally { lock (Progress) Progress.FilesProcessed++; }
        });

        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var affectedItems = new HashSet<long>();
        foreach (var r in results)
        {
            if (r is null) continue;
            var fileName = Path.GetFileName(r.Path);
            var ext = Path.GetExtension(r.Path).TrimStart('.').ToLowerInvariant();
            var (taken, takenSource) = ChooseTakenAt(r.Meta.TakenAt, fileName, r.Info);
            var takenAt = taken?.ToString("yyyy-MM-ddTHH:mm:ss");
            // Only a metadata timestamp is precise enough to pair a JPEG with its RAW across folders.
            var hasExif = takenSource == "metadata";
            var groupKey = GroupKey(r.Path, r.Hash, hasExif ? takenAt : null, c, tx);
            var itemId = EnsureItem(c, tx, groupKey, r.Path, r.Kind);
            affectedItems.Add(itemId);
            var mtime = new DateTimeOffset(r.Info.LastWriteTimeUtc).ToUnixTimeSeconds();

            if (r.ExistingId is { } existingId)
            {
                // The file can move to another item (say, its EXIF date changed), so the old item needs recomputing too.
                if (c.ExecuteScalar<long?>("SELECT item_id FROM assets WHERE id=@existingId", new { existingId }, tx) is { } oldItem) affectedItems.Add(oldItem);
                c.Execute("""
                    UPDATE assets SET item_id=@itemId, size=@size, mtime=@mtime, hash=@hash, width=@w, height=@h, duration_sec=@dur, taken_at=@takenAt,
                        taken_source=@takenSource, codec=@codec, audio_codec=@acodec, container=@container, projection=@projection, last_seen_scan=@runId, missing=0 WHERE id=@id
                    """, new { itemId, size = r.Info.Length, mtime, hash = r.Hash, w = r.Meta.Width, h = r.Meta.Height, dur = r.Meta.DurationSec, takenAt, takenSource, codec = r.Meta.VideoCodec, acodec = r.Meta.AudioCodec, container = r.Meta.Container, projection = r.Meta.Projection, runId, id = existingId }, tx);
                lock (Progress) Progress.FilesUpdated++;
            }
            else
            {
                c.Execute("""
                    INSERT INTO assets(item_id, path, file_name, ext, size, mtime, hash, kind, is_raw, width, height, duration_sec, taken_at, taken_source, codec, audio_codec, container, projection, last_seen_scan, missing)
                    VALUES(@itemId, @path, @fileName, @ext, @size, @mtime, @hash, @kind, @isRaw, @w, @h, @dur, @takenAt, @takenSource, @codec, @acodec, @container, @projection, @runId, 0)
                    """, new { itemId, path = r.Path, fileName, ext, size = r.Info.Length, mtime, hash = r.Hash, kind = r.Kind, isRaw = r.IsRaw ? 1 : 0, w = r.Meta.Width, h = r.Meta.Height, dur = r.Meta.DurationSec, takenAt, takenSource, codec = r.Meta.VideoCodec, acodec = r.Meta.AudioCodec, container = r.Meta.Container, projection = r.Meta.Projection, runId }, tx);
                lock (Progress) Progress.FilesAdded++;
            }
        }
        // Set the main file, size and date now so the new items show up while the scan continues.
        if (affectedItems.Count > 0) RebuildItemColumns(c, tx, affectedItems);
        tx.Commit();
        _db.Touch();

        // The thumbnail workers handle this batch while the scan moves on to the next one.
        if (affectedItems.Count > 0 && ThumbnailsEnabled)
            _thumbs.Enqueue(c.Query<Asset>("SELECT a.* FROM assets a JOIN items i ON i.primary_asset_id=a.id WHERE i.id IN @ids AND a.missing=0 AND i.hidden=0", new { ids = affectedItems }));
        if (affectedItems.Count > 0) _faces?.EnqueueItems(affectedItems.ToList());
        var written = results.Count(r => r is not null);
        _log.LogDebug("Committed batch of {Count} files ({Items} items updated)", written, affectedItems.Count);
        BatchCommitted?.Invoke(written);
    }

    /// <summary>Short duration for log lines: 850ms, 12.3s, 4m05s, 1h02m.</summary>
    public static string Fmt(TimeSpan t) =>
        t.TotalSeconds < 1 ? $"{t.TotalMilliseconds:0}ms"
        : t.TotalMinutes < 1 ? $"{t.TotalSeconds:0.0}s"
        : t.TotalHours < 1 ? $"{(int)t.TotalMinutes}m{t.Seconds:00}s"
        : $"{(int)t.TotalHours}h{t.Minutes:00}m";

    private static DateTime? FallbackDate(FileInfo fi)
    {
        // No EXIF: take the earlier of creation and modification time (a copy often resets one and keeps the other).
        // Times in the future or before 1980 (a file system's zero date) are ignored.
        var times = new[] { fi.CreationTime, fi.LastWriteTime }.Where(t => CaptureDate.IsPlausible(t, earliestYear: 1980)).ToList();
        return times.Count == 0 ? null : times.Min();
    }

    /// <summary>The item group key for a file. Public for tests.</summary>
    public static string ComputeGroupKey(string path, string? exifTakenAt)
    {
        var stem = NormalizeStem(Path.GetFileNameWithoutExtension(path));
        if (exifTakenAt is not null) return $"t|{stem}|{exifTakenAt}";
        var dir = Path.GetDirectoryName(path) ?? "";
        return $"d|{stem}|{dir}";
    }

    /// <summary>Strips common copy suffixes so "IMG_0001 (1).jpg", "IMG_0001-2.CR2" and "IMG_0001_edited.jpg" group together.</summary>
    public static string NormalizeStem(string stem)
    {
        stem = stem.ToLowerInvariant().Trim();
        stem = System.Text.RegularExpressions.Regex.Replace(stem, @"\s*\(\d+\)$", "");
        stem = System.Text.RegularExpressions.Regex.Replace(stem, @"[-_ ](copy|edited|edit|\d)$", "");
        stem = System.Text.RegularExpressions.Regex.Replace(stem, @"[-_ ]\d{1,2}$", "");
        return stem;
    }

    private static string GroupKey(string path, string hash, string? exifTakenAt, SqliteConnection c, IDbTransaction tx)
    {
        // The same file anywhere joins the same item, even if renamed.
        var dupKey = c.ExecuteScalar<string?>("SELECT i.group_key FROM assets a JOIN items i ON i.id=a.item_id WHERE a.hash=@hash AND a.path<>@path LIMIT 1", new { hash, path }, tx);
        return dupKey ?? ComputeGroupKey(path, exifTakenAt);
    }

    private static long EnsureItem(SqliteConnection c, IDbTransaction tx, string groupKey, string path, string kind)
    {
        var id = c.ExecuteScalar<long?>("SELECT id FROM items WHERE group_key=@groupKey", new { groupKey }, tx);
        if (id is not null) return id.Value;
        var dirName = Path.GetFileName(Path.GetDirectoryName(path) ?? "") ?? "";
        return c.ExecuteScalar<long>("INSERT INTO items(group_key, kind, title, dir_name, created_at) VALUES(@groupKey, @kind, @title, @dirName, @now); SELECT last_insert_rowid();",
            new { groupKey, kind, title = Path.GetFileNameWithoutExtension(path), dirName, now = Clock.NowIso() }, tx);
    }

    private void MarkMissing(long runId, List<Source> sources)
    {
        FlushTouched(runId);
        using var c = _db.Open();
        long missing = 0;
        foreach (var s in sources)
        {
            var root = GalleryOptions.ExpandHome(s.Path);
            if (!Directory.Exists(root)) continue; // drive not connected: leave its files alone
            // Exported edits live in the derived folder, so a scan never sees them; skip them (edit_of IS NULL) or they'd all be marked missing.
            missing += c.Execute("UPDATE assets SET missing=1 WHERE path LIKE @prefix AND last_seen_scan<>@runId AND missing=0 AND edit_of IS NULL", new { prefix = root.TrimEnd('/') + "/%", runId });
        }
        lock (Progress) Progress.FilesMissing += missing;
    }

    /// <summary>
    /// Finds items that have files but no valid main file, and recomputes them. An interrupted scan can leave items like
    /// this. They show a placeholder in the grid and get no thumbnail (thumbnails are made for main files), and later
    /// scans skip them since their files are unchanged. Returns the number repaired.
    /// </summary>
    public int RepairIncompleteItems()
    {
        List<long> ids;
        using (var c = _db.Open())
            ids = c.Query<long>("""
                SELECT i.id FROM items i
                WHERE EXISTS (SELECT 1 FROM assets a WHERE a.item_id = i.id)
                  AND (i.primary_asset_id IS NULL
                       OR NOT EXISTS (SELECT 1 FROM assets a WHERE a.id = i.primary_asset_id AND a.item_id = i.id))
                """).ToList();
        if (ids.Count == 0) return 0;
        foreach (var chunk in ids.Chunk(500))
        {
            using var c = _db.Open();
            using var tx = c.BeginTransaction();
            RebuildItemColumns(c, tx, chunk);
            tx.Commit();
        }
        _db.Touch();
        _log.LogWarning("Repaired {Count:N0} items left without a main file by an interrupted scan; their thumbnails will be generated", ids.Count);
        return ids.Count;
    }

    /// <summary>Recomputes the summary columns of every item and removes items with no files left.</summary>
    public void RebuildItems(CancellationToken ct)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        RebuildItemColumns(c, tx, null);
        c.Execute("DELETE FROM items WHERE NOT EXISTS (SELECT 1 FROM assets a WHERE a.item_id=items.id)", transaction: tx);
        tx.Commit();
        _db.Touch();
    }

    /// <summary>Recomputes each item's summary columns (main file, kind, size, date, hidden flag) for the given items, or all when null.</summary>
    internal static void RebuildItemColumns(SqliteConnection c, IDbTransaction tx, IReadOnlyCollection<long>? itemIds)
    {
        var where = itemIds is null ? "" : " WHERE id IN @ids";
        object? p = itemIds is null ? null : new { ids = itemIds };
        // Main file preference, in order: present, chosen by a user, original (not an exported edit), not RAW,
        // common format, more pixels, larger file, shortest name.
        c.Execute($"""
            UPDATE items SET primary_asset_id = (
                SELECT a.id FROM assets a WHERE a.item_id = items.id
                ORDER BY a.missing ASC, (a.id = items.preferred_asset_id) DESC, (a.edit_of IS NOT NULL) ASC, a.is_raw ASC,
                         CASE WHEN a.ext IN ('jpg','jpeg','heic','png','webp','mp4','mov','mp3','flac','wav') THEN 0 ELSE 1 END ASC,
                         COALESCE(a.width,0)*COALESCE(a.height,0) DESC, a.size DESC, length(a.file_name) ASC
                LIMIT 1){where}
            """, p, tx);
        c.Execute($"""
            UPDATE items SET
                asset_count = (SELECT COUNT(*) FROM assets a WHERE a.item_id=items.id AND a.missing=0),
                hidden      = CASE WHEN (SELECT COUNT(*) FROM assets a WHERE a.item_id=items.id AND a.missing=0)=0 THEN 1 ELSE 0 END,
                kind        = COALESCE((SELECT kind FROM assets WHERE id=items.primary_asset_id), kind),
                title       = COALESCE((SELECT substr(file_name, 1, length(file_name) - length(ext) - 1) FROM assets WHERE id=items.primary_asset_id), title),
                dir_name    = COALESCE((SELECT replace(path, '/' || file_name, '') FROM assets WHERE id=items.primary_asset_id), dir_name),
                width       = (SELECT width  FROM assets WHERE id=items.primary_asset_id),
                height      = (SELECT height FROM assets WHERE id=items.primary_asset_id),
                duration_sec= (SELECT duration_sec FROM assets WHERE id=items.primary_asset_id),
                taken_at    = (SELECT MIN(taken_at) FROM assets a WHERE a.item_id=items.id){where}
            """, p, tx);
        // Trim dir_name to the folder name.
        c.Execute($"UPDATE items SET dir_name = replace(dir_name, rtrim(dir_name, replace(dir_name, '/', '')), ''){where}", p, tx);
        c.Execute($"UPDATE items SET taken_year = CAST(substr(taken_at,1,4) AS INTEGER) WHERE taken_at IS NOT NULL{(itemIds is null ? "" : " AND id IN @ids")}", p, tx);
    }
}
