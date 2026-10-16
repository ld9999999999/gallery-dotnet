using Dapper;
using Gallery.Core.Data;
using Gallery.Core.Models;
using Microsoft.Data.Sqlite;

namespace Gallery.Core.Services;

/// <summary>
/// Keeps the index in step with files deleted, renamed or moved outside a scan. Deleted files are marked missing
/// rather than erased: an item whose files are all missing is hidden from the gallery, and returns with its tags and
/// albums if the file comes back (a drive mounted again, a file restored). Renames and moves inside a source just
/// update the stored path. <see cref="LibraryWatcher"/> calls it live, and it also runs at startup (for files deleted
/// while the server was off) and when an item is opened.
/// </summary>
public sealed class FileChangeService
{
    private readonly Db _db;
    private readonly ILogger<FileChangeService> _log;

    public FileChangeService(Db db, ILogger<FileChangeService> log)
    {
        _db = db;
        _log = log;
    }

    /// <summary>Files found deleted and found back, and how many items were hidden from or returned to the gallery.</summary>
    public sealed record Result(int Deleted, int Restored, int ItemsHidden, int ItemsRestored)
    {
        public static readonly Result None = new(0, 0, 0, 0);
    }

    /// <summary>Enabled source folders that exist right now. A missing one is probably unmounted, so its files are left
    /// alone instead of being reported as deleted.</summary>
    public List<string> PresentSourceRoots()
    {
        using var c = _db.Open();
        return c.Query<string>("SELECT path FROM sources WHERE enabled=1")
            .Select(p => GalleryOptions.ExpandHome(p).TrimEnd('/'))
            .Where(Directory.Exists)
            .ToList();
    }

    private static bool Under(string path, IEnumerable<string> roots) => roots.Any(r => path.StartsWith(r + "/", StringComparison.Ordinal));

    /// <summary>
    /// Marks deleted files as missing. A path may be a file or a folder (a deleted folder reports only itself), so
    /// everything stored under it is marked too.
    /// </summary>
    public Result MarkDeleted(IReadOnlyCollection<string> paths)
    {
        var roots = PresentSourceRoots();
        var relevant = paths.Select(p => p.TrimEnd('/')).Where(p => Under(p, roots)).Distinct().ToList();
        if (relevant.Count == 0) return Result.None;

        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var affected = new HashSet<long>();
        var files = new List<string>();
        foreach (var p in relevant)
        {
            var hit = c.Query<(long id, long itemId, string path)>("""
                SELECT id, item_id, path FROM assets
                WHERE missing = 0 AND (path = @p OR substr(path, 1, length(@dir)) = @dir)
                """, new { p, dir = p + "/" }, tx).ToList();
            foreach (var h in hit)
            {
                if (File.Exists(h.path)) continue; // deleted and replaced right away, for example
                c.Execute("UPDATE assets SET missing = 1 WHERE id = @id", new { h.id }, tx);
                affected.Add(h.itemId);
                files.Add(h.path);
            }
        }
        var result = Finish(c, tx, affected, files.Count, 0);
        if (files.Count > 0)
            _log.LogInformation("Removed {Count:N0} deleted file(s) from the gallery ({Hidden:N0} item(s) now hidden): {Files}",
                files.Count, result.ItemsHidden, Sample(files));
        return result;
    }

    /// <summary>
    /// Applies a rename or move inside the sources. The file (or every file under a renamed folder) keeps its asset,
    /// item, tags and albums under the new path. Renaming a file to a non-media name counts as deleting it.
    /// </summary>
    public Result Renamed(string oldPath, string newPath)
    {
        oldPath = oldPath.TrimEnd('/');
        newPath = newPath.TrimEnd('/');
        var roots = PresentSourceRoots();
        if (!Under(oldPath, roots)) return Result.None;
        if (!Under(newPath, roots)) return MarkDeleted(new[] { oldPath }); // moved out of the sources

        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var affected = new HashSet<long>();
        var moved = 0;
        var deleted = 0;

        // A single file:
        var file = c.QueryFirstOrDefault<(long id, long itemId)?>("SELECT id, item_id FROM assets WHERE path = @oldPath", new { oldPath }, tx);
        if (file is { } f)
        {
            var newName = Path.GetFileName(newPath);
            var cls = FileTypes.Classify(Path.GetExtension(newPath));
            var taken = c.ExecuteScalar<long>("SELECT COUNT(*) FROM assets WHERE path = @newPath", new { newPath }, tx) > 0;
            if (cls is null || taken)
            {
                // No longer a media file, or it replaced an indexed one. Treat it as deleted and let the next scan sort out the rest.
                c.Execute("UPDATE assets SET missing = 1 WHERE id = @id", new { f.id }, tx);
                deleted++;
            }
            else
            {
                c.Execute("UPDATE assets SET path=@newPath, file_name=@newName, ext=@ext, missing=0 WHERE id=@id",
                    new { newPath, newName, ext = Path.GetExtension(newPath).TrimStart('.').ToLowerInvariant(), f.id }, tx);
                moved++;
            }
            affected.Add(f.itemId);
        }

        // A folder: rewrite the path prefix of everything under it.
        var oldDir = oldPath + "/";
        var under = c.Query<(long id, long itemId, string path)>("SELECT id, item_id, path FROM assets WHERE substr(path, 1, length(@oldDir)) = @oldDir", new { oldDir }, tx).ToList();
        foreach (var u in under)
        {
            var target = newPath + "/" + u.path[oldDir.Length..];
            if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM assets WHERE path = @target", new { target }, tx) > 0) continue;
            c.Execute("UPDATE assets SET path = @target WHERE id = @id", new { target, u.id }, tx);
            affected.Add(u.itemId);
            moved++;
        }

        var result = Finish(c, tx, affected, deleted, 0);
        if (moved > 0) _log.LogInformation("Moved {Count:N0} file(s) in the index: {Old} -> {New}", moved, oldPath, newPath);
        else if (file is not null) _log.LogInformation("Removed {Path} from the gallery (renamed to {New})", oldPath, newPath);
        return result;
    }

    /// <summary>
    /// Checks every indexed file under the present sources, marking the missing ones and bringing back the ones that
    /// returned. It only tests that files exist. Runs at startup and when the folder watcher lost events.
    /// </summary>
    public async Task<Result> CheckAllAsync(CancellationToken ct)
    {
        var roots = PresentSourceRoots();
        List<(long id, long itemId, string path, long missing)> assets;
        using (var c = _db.Open())
            assets = c.Query<(long, long, string, long)>("SELECT id, item_id, path, missing FROM assets").ToList();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var gone = new List<(long id, long itemId, string path)>();
        var back = new List<(long id, long itemId)>();
        await Task.Run(() =>
        {
            foreach (var a in assets)
            {
                ct.ThrowIfCancellationRequested();
                if (!Under(a.path, roots)) continue;
                var exists = File.Exists(a.path);
                if (!exists && a.missing == 0) gone.Add((a.id, a.itemId, a.path));
                else if (exists && a.missing == 1) back.Add((a.id, a.itemId));
            }
        }, ct);
        if (gone.Count == 0 && back.Count == 0)
        {
            _log.LogInformation("Checked {Count:N0} indexed files in {Elapsed}: none deleted", assets.Count, Scanner.Fmt(clock.Elapsed));
            return Result.None;
        }

        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        conn.Execute("UPDATE assets SET missing = 1 WHERE id = @id", gone.Select(g => new { g.id }), tx);
        conn.Execute("UPDATE assets SET missing = 0 WHERE id = @id", back.Select(b => new { b.id }), tx);
        var result = Finish(conn, tx, gone.Select(g => g.itemId).Concat(back.Select(b => b.itemId)).ToHashSet(), gone.Count, back.Count);
        _log.LogInformation("Checked {Count:N0} indexed files in {Elapsed}: {Gone:N0} deleted ({Hidden:N0} item(s) hidden), {Back:N0} back ({Restored:N0} item(s) restored){Sample}",
            assets.Count, Scanner.Fmt(clock.Elapsed), result.Deleted, result.ItemsHidden, result.Restored, result.ItemsRestored,
            gone.Count > 0 ? ": " + Sample(gone.Select(g => g.path).ToList()) : "");
        return result;
    }

    /// <summary>Rechecks one item's files, for example when it is opened. Returns true if something changed.</summary>
    public bool CheckItem(long itemId)
    {
        using var c = _db.Open();
        var assets = c.Query<(long id, string path, long missing)>("SELECT id, path, missing FROM assets WHERE item_id = @itemId", new { itemId }).ToList();
        var roots = PresentSourceRoots();
        var changes = assets
            .Where(a => Under(a.path, roots))
            .Select(a => (a.id, a.path, wasMissing: a.missing == 1, nowMissing: !File.Exists(a.path)))
            .Where(a => a.wasMissing != a.nowMissing)
            .ToList();
        if (changes.Count == 0) return false;
        using var tx = c.BeginTransaction();
        foreach (var ch in changes)
            c.Execute("UPDATE assets SET missing = @m WHERE id = @id", new { m = ch.nowMissing ? 1 : 0, ch.id }, tx);
        var result = Finish(c, tx, new HashSet<long> { itemId }, changes.Count(x => x.nowMissing), changes.Count(x => !x.nowMissing));
        _log.LogInformation("Item {ItemId}: {Gone} file(s) found deleted, {Back} back{Hidden}", itemId,
            changes.Count(x => x.nowMissing), changes.Count(x => !x.nowMissing), result.ItemsHidden > 0 ? "; removed from the gallery" : "");
        return true;
    }

    /// <summary>Rebuilds the affected items, commits, and reports how many were hidden (no files left) or restored.</summary>
    private Result Finish(SqliteConnection c, System.Data.IDbTransaction tx, HashSet<long> itemIds, int deleted, int restored)
    {
        if (itemIds.Count == 0) { tx.Commit(); return new Result(deleted, restored, 0, 0); }
        var before = c.Query<(long id, long hidden)>("SELECT id, hidden FROM items WHERE id IN @ids", new { ids = itemIds }, tx).ToDictionary(x => x.id, x => x.hidden);
        foreach (var chunk in itemIds.Chunk(500)) Scanner.RebuildItemColumns(c, tx, chunk);
        var after = c.Query<(long id, long hidden)>("SELECT id, hidden FROM items WHERE id IN @ids", new { ids = itemIds }, tx).ToDictionary(x => x.id, x => x.hidden);
        tx.Commit();
        _db.Touch();
        var hidden = after.Count(kv => kv.Value == 1 && before.GetValueOrDefault(kv.Key) == 0);
        var itemsRestored = after.Count(kv => kv.Value == 0 && before.GetValueOrDefault(kv.Key) == 1);
        return new Result(deleted, restored, hidden, itemsRestored);
    }

    private static string Sample(IReadOnlyList<string> paths) =>
        string.Join(", ", paths.Take(3)) + (paths.Count > 3 ? $" and {paths.Count - 3:N0} more" : "");
}
