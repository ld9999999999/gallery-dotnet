using System.Threading.Channels;

namespace Gallery.Core.Services;

/// <summary>
/// Watches the source folders so files deleted, renamed or moved while the server runs show up in the gallery within a
/// few seconds, without a scan. Events are collected for a moment and applied in batches, so deleting a whole folder
/// is one update. New files still need a scan.
///
/// Watching is best effort: it starts after the web server, and each folder gets a time limit to start (some platforms
/// and sandboxes hang here). If watching fails, deletions are still found by the startup check, by every scan, and
/// when an item is opened.
/// On Linux the number of watched folders is capped by fs.inotify.max_user_watches; the log says how to raise it when
/// the cap is hit. Watchers are re-checked every minute and whenever the admin changes sources, which picks up added,
/// enabled or remounted sources.
/// </summary>
public sealed class LibraryWatcher : BackgroundService
{
    private abstract record Change;
    private sealed record Deleted(string Path) : Change;
    private sealed record Moved(string From, string To) : Change;
    private sealed record Lost(string Root, string Message) : Change;

    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);

    private readonly FileChangeService _changes;
    private readonly ILogger<LibraryWatcher> _log;
    private readonly Channel<Change> _queue = Channel.CreateUnbounded<Change>();
    private readonly Channel<bool> _refreshRequests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new();
    private readonly HashSet<string> _starting = new(); // roots whose watcher is still starting (possibly stuck)
    private readonly object _lock = new();

    public LibraryWatcher(FileChangeService changes, ILogger<LibraryWatcher> log)
    {
        _changes = changes;
        _log = log;
    }

    public IReadOnlyCollection<string> Watched { get { lock (_lock) return _watchers.Keys.ToList(); } }

    /// <summary>Asks for the watchers to be matched to the current sources soon. Returns immediately.</summary>
    public void RequestRefresh() => _refreshRequests.Writer.TryWrite(true);

    // Entry points for file events. The OS watchers call these, and tests can too.
    internal void ReportDeleted(string path) => _queue.Writer.TryWrite(new Deleted(path));
    internal void ReportMoved(string from, string to) => _queue.Writer.TryWrite(new Moved(from, to));

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Yield(); // keep the host's startup (and so the web server) from waiting on us
        var refreshLoop = Task.Run(() => RefreshLoopAsync(ct), ct);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(ct))
            {
                // Let a burst (a folder deleted, a batch moved) arrive, then apply it in order.
                await Task.Delay(Settle, ct);
                var batch = new List<Change>();
                while (_queue.Reader.TryRead(out var c)) batch.Add(c);
                try { await ApplyAsync(batch, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _log.LogError(ex, "Applying {Count} file change(s) failed", batch.Count); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            List<FileSystemWatcher> all;
            lock (_lock) { all = _watchers.Values.ToList(); _watchers.Clear(); }
            foreach (var w in all) _ = Task.Run(w.Dispose); // disposing can hang, just like starting
            try { await refreshLoop; } catch (OperationCanceledException) { }
        }
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        RequestRefresh();
        using var timer = new PeriodicTimer(RefreshInterval);
        var tick = timer.WaitForNextTickAsync(ct).AsTask();
        while (!ct.IsCancellationRequested)
        {
            var request = _refreshRequests.Reader.WaitToReadAsync(ct).AsTask();
            var done = await Task.WhenAny(tick, request);
            if (done == tick) tick = timer.WaitForNextTickAsync(ct).AsTask();
            while (_refreshRequests.Reader.TryRead(out _)) { }
            if (ct.IsCancellationRequested) break;
            try { await RefreshAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Updating folder watchers failed"); }
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var roots = _changes.PresentSourceRoots();
        List<string> toStart;
        lock (_lock)
        {
            foreach (var gone in _watchers.Keys.Except(roots).ToList())
            {
                var w = _watchers[gone];
                _watchers.Remove(gone);
                _ = Task.Run(w.Dispose);
                _log.LogInformation("Stopped watching {Root}", gone);
            }
            toStart = roots.Except(_watchers.Keys).Except(_starting).ToList();
            foreach (var r in toStart) _starting.Add(r);
        }
        foreach (var root in toStart)
        {
            var start = Task.Run(() => CreateWatcher(root));
            try
            {
                var w = await start.WaitAsync(StartTimeout, ct);
                lock (_lock) { _watchers[root] = w; _starting.Remove(root); }
                _log.LogInformation("Watching {Root} for deleted and moved files", root);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                lock (_lock) _starting.Remove(root);
                _ = start.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
                throw;
            }
            catch (TimeoutException)
            {
                _log.LogWarning("Watching {Root} did not start within {Seconds}s; deleted files there will be found by the startup check, scans and when items are opened",
                    root, StartTimeout.TotalSeconds);
                // Stay marked as starting: if it comes up later we keep it, and if it's stuck we don't pile on more attempts.
                _ = start.ContinueWith(t =>
                {
                    lock (_lock)
                    {
                        _starting.Remove(root);
                        if (t.IsCompletedSuccessfully && !_watchers.ContainsKey(root)) { _watchers[root] = t.Result; _log.LogInformation("Watching {Root} (started late)", root); }
                    }
                }, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                lock (_lock) _starting.Remove(root);
                _log.LogWarning("Cannot watch {Root} for deleted files ({Error}); they will be found by the startup check, scans and when items are opened", root, Inotify(ex.Message));
            }
        }
    }

    private FileSystemWatcher CreateWatcher(string root)
    {
        var w = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            InternalBufferSize = 64 * 1024,
        };
        w.Deleted += (_, e) => ReportDeleted(e.FullPath);
        w.Renamed += (_, e) => ReportMoved(e.OldFullPath, e.FullPath);
        w.Error += (_, e) => _queue.Writer.TryWrite(new Lost(root, e.GetException().Message));
        w.EnableRaisingEvents = true;
        return w;
    }

    private static string Inotify(string message) => message.Contains("inotify", StringComparison.OrdinalIgnoreCase)
        ? $"{message}. Raise the limit with: sudo sysctl fs.inotify.max_user_watches=524288 (add it to /etc/sysctl.conf to keep it)"
        : message;

    private async Task ApplyAsync(List<Change> batch, CancellationToken ct)
    {
        var deletes = new List<string>();
        void FlushDeletes() { if (deletes.Count > 0) { _changes.MarkDeleted(deletes); deletes.Clear(); } }
        var lost = false;
        foreach (var change in batch)
        {
            switch (change)
            {
                case Deleted d: deletes.Add(d.Path); break;
                case Moved m: FlushDeletes(); _changes.Renamed(m.From, m.To); break;
                case Lost l:
                    lost = true;
                    _log.LogWarning("Watching {Root} missed file events ({Error}); checking all files instead", l.Root, Inotify(l.Message));
                    break;
            }
        }
        FlushDeletes();
        if (lost) await _changes.CheckAllAsync(ct); // events were lost, so compare the whole index with the disk
    }
}
