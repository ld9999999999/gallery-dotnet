using Dapper;
using Gallery.Core.Data;
using Gallery.Core.Models;

namespace Gallery.Core.Services;

public sealed record SourceInfo(long Id, string Path, bool Enabled, string AddedAt, bool Exists);

/// <summary>The source folders the gallery indexes. Changes reach the file watcher at once and the files at the next scan.</summary>
public sealed class SourceService
{
    private readonly Db _db;
    private readonly LibraryWatcher _watcher;

    public SourceService(Db db, LibraryWatcher watcher)
    {
        _db = db;
        _watcher = watcher;
    }

    public List<SourceInfo> All()
    {
        using var c = _db.Open();
        return c.Query<Source>("SELECT * FROM sources ORDER BY path")
            .Select(s => new SourceInfo(s.Id, s.Path, s.Enabled == 1, s.AddedAt, Directory.Exists(GalleryOptions.ExpandHome(s.Path))))
            .ToList();
    }

    /// <summary>Adds a folder (~ is expanded). Returns null on success, otherwise the reason it wasn't added.</summary>
    public string? Add(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Enter a folder";
        var full = GalleryOptions.ExpandHome(path.Trim());
        if (!Directory.Exists(full)) return $"Directory does not exist: {full}";
        using (var c = _db.Open())
            c.Execute("INSERT OR IGNORE INTO sources(path, enabled, added_at) VALUES(@full,1,@now)", new { full, now = Clock.NowIso() });
        _watcher.RequestRefresh();
        return null;
    }

    /// <summary>Stops indexing a folder. Its items stay until the next scan marks them missing.</summary>
    public void Remove(long id)
    {
        using (var c = _db.Open()) c.Execute("DELETE FROM sources WHERE id=@id", new { id });
        _watcher.RequestRefresh();
    }

    public void SetEnabled(long id, bool enabled)
    {
        using (var c = _db.Open()) c.Execute("UPDATE sources SET enabled=@e WHERE id=@id", new { id, e = enabled ? 1 : 0 });
        _watcher.RequestRefresh();
    }
}
