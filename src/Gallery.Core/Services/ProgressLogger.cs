using Gallery.Core.Models;

namespace Gallery.Core.Services;

/// <summary>
/// Logs a progress line every few seconds while the server is busy (scan, thumbnails, transcodes, faces), then a summary
/// line once it goes idle. Gallery:ProgressLogSeconds sets the interval (0 turns it off).
/// With --verbose it also logs the file being indexed.
/// </summary>
public sealed class ProgressLogger : BackgroundService
{
    private readonly StatusService _status;
    private readonly ILogger<ProgressLogger> _log;
    private readonly TimeSpan _interval;

    public ProgressLogger(StatusService status, GalleryOptions options, ILogger<ProgressLogger> log)
    {
        _status = status;
        _log = log;
        _interval = TimeSpan.FromSeconds(Math.Max(0, options.ProgressLogSeconds));
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_interval == TimeSpan.Zero) return;
        using var timer = new PeriodicTimer(_interval);
        var wasBusy = false;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                LibraryStatus s;
                try { s = _status.Snapshot(includePaths: true); }
                catch (Exception ex) { _log.LogDebug(ex, "status snapshot failed"); continue; }

                if (s.Busy)
                {
                    _log.LogInformation("Progress: {Line}", Describe(s));
                    if (s.Scan.CurrentPath is { } path) _log.LogDebug("Current file: {Path}", path);
                }
                else if (wasBusy)
                {
                    var t = s.Stats;
                    _log.LogInformation("Idle. Library: {Items:N0} items, {Assets:N0} files, {Dups:N0} duplicate copies, {Variations:N0} items with variations, {Missing:N0} missing files",
                        t.Items, t.Assets, t.DuplicateAssets, t.VariationItems, t.Missing);
                }
                wasBusy = s.Busy;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public static string Describe(LibraryStatus s)
    {
        var parts = new List<string>();
        var sc = s.Scan;
        if (sc.Running)
        {
            parts.Add(sc.Phase switch
            {
                "checking" => "scan checking for deleted files",
                "discovering" => $"scan discovering: {sc.Discovered:N0} files found, {sc.Queued:N0} new/changed, {sc.Unchanged:N0} unchanged",
                "indexing" => $"scan indexing {sc.Processed:N0}/{sc.Queued:N0} ({(sc.Queued == 0 ? 100 : sc.Processed * 100 / sc.Queued)}%), {sc.Pending:N0} pending"
                    + (sc.FilesPerSecond is { } r ? $", {r:0.0} files/s" : "")
                    + (sc.EtaSeconds is { } e ? $", ETA {Scanner.Fmt(TimeSpan.FromSeconds(e))}" : "")
                    + (sc.Failed > 0 ? $", {sc.Failed:N0} failed" : ""),
                _ => "scan finalizing (updating items, checking for missing files)",
            });
        }
        var th = s.Thumbnails;
        if (th.Running) parts.Add(th.Pending > 0 ? $"thumbnails {th.Done:N0} done, {th.Pending:N0} pending" : "thumbnails: checking which are missing");
        var tc = s.Transcodes;
        if (tc.Queued > 0) parts.Add($"transcodes {tc.Queued} queued" + (tc.Active is { } a ? $", converting {a}" : ""));
        if (s.Faces is { } f)
        {
            if (f.Running) parts.Add(f.Pending > 0 ? $"faces {f.Done:N0} photos done, {f.Pending:N0} pending, {f.Faces:N0} faces found" : "faces: checking which photos need searching");
            else if (f.State == "downloading") parts.Add("faces: downloading models");
        }
        return string.Join(" | ", parts);
    }
}
