using Dapper;
using Gallery.Core.Data;
using Gallery.Core.Models;

namespace Gallery.Core.Services;

public sealed class AlbumService
{
    private readonly Db _db;

    public AlbumService(Db db) => _db = db;

    /// <summary>
    /// Every album with its item count and cover (the chosen one, else its earliest item), newest first. For viewers, albums
    /// hidden from them or holding nothing they may see are left out, and counts and covers only use items they may see.
    /// </summary>
    public List<Album> All(bool forViewers = false)
    {
        var visible = forViewers ? " AND " + ItemRepository.ViewerVisible() : "";
        using var c = _db.Open();
        return c.Query<Album>($"""
            SELECT x.*, ci.primary_asset_id AS cover_asset_id, ca.hash AS cover_hash,
                   (SELECT t.value FROM item_tags t WHERE t.item_id=x.cover_item_id AND t.key=@rotation LIMIT 1) AS cover_rotation_tag,
                   (SELECT t.value FROM item_tags t WHERE t.item_id=x.cover_item_id AND t.key=@squeeze LIMIT 1) AS cover_squeeze_tag
            FROM (
                SELECT al.id, al.name, al.kind, al.auto_dir_name, al.date_from, al.date_to, al.created_at, al.viewer_hidden,
                       (SELECT COUNT(*) FROM album_items ai JOIN items i ON i.id=ai.item_id WHERE ai.album_id=al.id AND i.hidden=0{visible}) AS item_count,
                       (SELECT i.id FROM items i JOIN album_items cm ON cm.item_id=i.id AND cm.album_id=al.id WHERE i.id=al.cover_item_id AND i.hidden=0{visible}) AS chosen_cover_id,
                       COALESCE(
                           (SELECT i.id FROM items i JOIN album_items cm ON cm.item_id=i.id AND cm.album_id=al.id WHERE i.id=al.cover_item_id AND i.hidden=0{visible}),
                           (SELECT ai.item_id FROM album_items ai JOIN items i ON i.id=ai.item_id WHERE ai.album_id=al.id AND i.hidden=0{visible} ORDER BY i.taken_at LIMIT 1)
                       ) AS cover_item_id
                FROM albums al {(forViewers ? "WHERE al.viewer_hidden=0" : "")}
            ) x
            LEFT JOIN items ci ON ci.id=x.cover_item_id
            LEFT JOIN assets ca ON ca.id=ci.primary_asset_id
            {(forViewers ? "WHERE x.item_count > 0" : "")}
            ORDER BY COALESCE(x.date_from, x.created_at) DESC, x.name
            """, new { rotation = Rotation.TagKey, squeeze = Squeeze.TagKey }).ToList();
    }

    /// <summary>Null when the album doesn't exist, or when a viewer asks for a hidden one.</summary>
    public Album? Get(long id, bool forViewers = false)
    {
        using var c = _db.Open();
        return c.QuerySingleOrDefault<Album>($"SELECT *, (SELECT COUNT(*) FROM album_items WHERE album_id=albums.id) AS item_count FROM albums WHERE id=@id{(forViewers ? " AND viewer_hidden=0" : "")}", new { id });
    }

    /// <summary>Hides an album, and everything in it, from viewers, or shows it again. Admins always see it.</summary>
    public bool SetHiddenFromViewers(long id, bool hidden)
    {
        using var c = _db.Open();
        var n = c.Execute("UPDATE albums SET viewer_hidden=@h WHERE id=@id", new { id, h = hidden ? 1 : 0 });
        if (n > 0) _db.Touch();
        return n > 0;
    }

    public long Create(string name)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<long>("INSERT INTO albums(name, kind, created_at) VALUES(@name,'manual',@now); SELECT last_insert_rowid();", new { name, now = Clock.NowIso() });
    }

    public void Rename(long id, string name)
    {
        using var c = _db.Open();
        c.Execute("UPDATE albums SET name=@name WHERE id=@id", new { id, name });
    }

    /// <summary>
    /// Sets the album's cover to one of its items, or to null for the default (its earliest). If the chosen photo later
    /// leaves the album, or a viewer may not see it, the default shows instead. Returns false when the album doesn't
    /// exist or the item isn't in it.
    /// </summary>
    public bool SetCover(long id, long? itemId)
    {
        using var c = _db.Open();
        if (itemId is not null && c.ExecuteScalar<long>("SELECT COUNT(*) FROM album_items WHERE album_id=@id AND item_id=@itemId", new { id, itemId }) == 0) return false;
        var n = c.Execute("UPDATE albums SET cover_item_id=@itemId WHERE id=@id", new { id, itemId });
        if (n > 0) _db.Touch();
        return n > 0;
    }

    public void Delete(long id)
    {
        using var c = _db.Open();
        c.Execute("DELETE FROM albums WHERE id=@id", new { id });
    }

    /// <summary>
    /// Deletes albums with no items, either never filled or left empty after all their files were deleted (counted the
    /// way the album list counts). Photos and files are untouched. Returns the names of the albums removed.
    /// </summary>
    public List<string> PruneEmpty()
    {
        const string empty = "NOT EXISTS (SELECT 1 FROM album_items ai JOIN items i ON i.id=ai.item_id WHERE ai.album_id=albums.id AND i.hidden=0)";
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var names = c.Query<string>($"SELECT name FROM albums WHERE {empty} ORDER BY name COLLATE NOCASE", transaction: tx).ToList();
        c.Execute($"DELETE FROM albums WHERE {empty}", transaction: tx);
        tx.Commit();
        if (names.Count > 0) _db.Touch();
        return names;
    }

    public int AddItems(long albumId, IEnumerable<long> itemIds)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var pos = c.ExecuteScalar<long>("SELECT COALESCE(MAX(position),0) FROM album_items WHERE album_id=@albumId", new { albumId }, tx);
        var n = 0;
        foreach (var itemId in itemIds)
            n += c.Execute("INSERT OR IGNORE INTO album_items(album_id, item_id, position) VALUES(@albumId,@itemId,@pos)", new { albumId, itemId, pos = ++pos }, tx);
        UpdateDateRange(c, tx, albumId);
        tx.Commit();
        return n;
    }

    public int RemoveItems(long albumId, IEnumerable<long> itemIds)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var n = c.Execute("DELETE FROM album_items WHERE album_id=@albumId AND item_id=@itemId", itemIds.Select(itemId => new { albumId, itemId }), tx);
        UpdateDateRange(c, tx, albumId);
        tx.Commit();
        return n;
    }

    private static void UpdateDateRange(Microsoft.Data.Sqlite.SqliteConnection c, System.Data.IDbTransaction tx, long albumId)
    {
        c.Execute("""
            UPDATE albums SET date_from=(SELECT MIN(i.taken_at) FROM album_items ai JOIN items i ON i.id=ai.item_id WHERE ai.album_id=@albumId),
                              date_to  =(SELECT MAX(i.taken_at) FROM album_items ai JOIN items i ON i.id=ai.item_id WHERE ai.album_id=@albumId)
            WHERE id=@albumId
            """, new { albumId }, tx);
    }

    /// <summary>A folder whose items got no folder album (its name is generic and no named folder sits above it), with the item count.</summary>
    public sealed record UnplacedFolder(string Folder, int Items);

    /// <param name="Unplaced">Items in no folder album, and (up to 20 of) their folders.</param>
    public sealed record AutoAlbumResult(int Created, int Updated, int ItemsAssigned, int Unplaced = 0, IReadOnlyList<UnplacedFolder>? UnplacedFolders = null);

    /// <summary>
    /// Builds albums from folders. Each folder becomes an album named after it, however many days its photos cover.
    /// Same-named folders whose dates overlap (or fall within <paramref name="gapDays"/>) are treated as copies and share
    /// one album; otherwise each gets its own, named "Name (first date – last date)". Running it again updates the
    /// albums it made and merges albums of one folder that were split by date.
    /// </summary>
    public AutoAlbumResult BuildAutoAlbums(int gapDays = 3)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var result = Build(c, tx, gapDays);
        tx.Commit();
        _db.Touch();
        return result;
    }

    public sealed record RegenerateResult(int Removed, int Created, int ItemsAssigned, int KeptHidden, int Unplaced = 0,
        IReadOnlyList<UnplacedFolder>? UnplacedFolders = null);

    /// <summary>
    /// Deletes every auto-album and rebuilds them from the folders, as <see cref="BuildAutoAlbums"/> would for a new
    /// library. Manual albums are left alone. A new album is hidden from viewers when any of its items was in a hidden
    /// auto-album, so viewers never see what an admin hid. A cover chosen for an old album carries over when that photo
    /// is in the new one. Items added to an auto-album by hand are dropped.
    /// </summary>
    public RegenerateResult RegenerateAutoAlbums(int gapDays = 3)
    {
        const string old = "auto-replaced";
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        // Park the old albums under another kind until the new ones have copied their hiding and covers.
        var removed = c.Execute("UPDATE albums SET kind=@old WHERE kind='auto'", new { old }, tx);
        var built = Build(c, tx, gapDays);
        var keptHidden = c.Execute("""
            UPDATE albums SET viewer_hidden=1
            WHERE kind='auto' AND id IN (SELECT n.album_id FROM album_items n
                                         JOIN album_items o ON o.item_id=n.item_id JOIN albums ol ON ol.id=o.album_id
                                         WHERE ol.kind=@old AND ol.viewer_hidden=1)
            """, new { old }, tx);
        c.Execute("""
            UPDATE albums SET cover_item_id=(SELECT ol.cover_item_id FROM albums ol JOIN album_items n ON n.item_id=ol.cover_item_id
                                             WHERE ol.kind=@old AND n.album_id=albums.id ORDER BY ol.id LIMIT 1)
            WHERE kind='auto' AND cover_item_id IS NULL
            """, new { old }, tx);
        c.Execute("DELETE FROM albums WHERE kind=@old", new { old }, tx);
        tx.Commit();
        _db.Touch();
        return new RegenerateResult(removed, built.Created, built.ItemsAssigned, keptHidden, built.Unplaced, built.UnplacedFolders);
    }

    private static AutoAlbumResult Build(Microsoft.Data.Sqlite.SqliteConnection c, System.Data.IDbTransaction tx, int gapDays)
    {
        // Each file counts toward its own folder, so a photo in two folders (a copy, or a JPEG and its RAW in different
        // folders) lands in both albums. Editor copies (edit_of) are skipped; the original already counts.
        var rows = c.Query<(long ItemId, string Path, string FileName, string? TakenAt)>("""
            SELECT a.item_id, a.path, a.file_name, i.taken_at
            FROM assets a JOIN items i ON i.id=a.item_id
            WHERE i.hidden=0 AND a.missing=0 AND a.edit_of IS NULL
            """, transaction: tx).ToList();
        var roots = c.Query<string>("SELECT path FROM sources", transaction: tx).Select(r => r.TrimEnd('/', '\\')).ToList();
        var placed = new List<FolderItem>();
        var unplaced = new Dictionary<long, string>(); // item → one of its folders, for the report
        foreach (var r in rows)
        {
            if (DirectoryOf(r.Path, r.FileName) is not { } dir) continue;
            if (AlbumFolder(dir, roots) is { } f) placed.Add(new FolderItem(r.ItemId, f.Name, f.Directory, ParseDate(r.TakenAt)));
            else unplaced.TryAdd(r.ItemId, Shown(dir, roots));
        }
        var placedItems = placed.Select(p => p.Id).ToHashSet();
        var left = unplaced.Where(u => !placedItems.Contains(u.Key)).ToList();
        var report = left.GroupBy(u => u.Value).Select(g => new UnplacedFolder(g.Key, g.Count()))
            .OrderByDescending(f => f.Items).ThenBy(f => f.Folder, StringComparer.OrdinalIgnoreCase).Take(20).ToList();
        var plan = PlanAutoAlbums(placed, gapDays);

        int created = 0, updated = 0, assigned = 0;
        var existing = c.Query<Album>("SELECT * FROM albums WHERE kind='auto' ORDER BY id", transaction: tx).ToList();
        foreach (var p in plan)
        {
            var from = p.From?.ToString("yyyy-MM-ddTHH:mm:ss");
            var to = p.To?.ToString("yyyy-MM-ddTHH:mm:ss");
            var matches = existing.Where(a => string.Equals(a.AutoDirName, p.DirName, StringComparison.OrdinalIgnoreCase) && Overlaps(a.DateFrom, a.DateTo, from, to, gapDays)).ToList();
            long albumId;
            if (matches.Count == 0)
            {
                albumId = c.ExecuteScalar<long>("INSERT INTO albums(name, kind, auto_dir_name, date_from, date_to, created_at) VALUES(@name,'auto',@dir,@from,@to,@now); SELECT last_insert_rowid();",
                    new { name = p.Name, dir = p.DirName, from, to, now = Clock.NowIso() }, tx);
                created++;
            }
            else
            {
                // Keep the oldest album. The others are pieces of the same folder that were split by date, so fold
                // their items, cover and hiding into it.
                albumId = matches[0].Id;
                foreach (var m in matches) existing.Remove(m);
                foreach (var piece in matches.Skip(1))
                {
                    c.Execute("INSERT OR IGNORE INTO album_items(album_id, item_id, position) SELECT @albumId, item_id, position FROM album_items WHERE album_id=@piece",
                        new { albumId, piece = piece.Id }, tx);
                    c.Execute("""
                        UPDATE albums SET cover_item_id=COALESCE(cover_item_id, (SELECT cover_item_id FROM albums WHERE id=@piece)),
                                          viewer_hidden=MAX(viewer_hidden, (SELECT viewer_hidden FROM albums WHERE id=@piece))
                        WHERE id=@albumId
                        """, new { albumId, piece = piece.Id }, tx);
                    c.Execute("DELETE FROM albums WHERE id=@piece", new { piece = piece.Id }, tx);
                }
                c.Execute("UPDATE albums SET name=@name, date_from=@from, date_to=@to WHERE id=@id", new { name = p.Name, from, to, id = albumId }, tx);
                updated++;
            }
            var pos = 0;
            foreach (var itemId in p.Items)
                assigned += c.Execute("INSERT OR IGNORE INTO album_items(album_id, item_id, position) VALUES(@albumId,@itemId,@pos)", new { albumId, itemId, pos = pos++ }, tx);
        }
        return new AutoAlbumResult(created, updated, assigned, left.Count, report);
    }

    /// <summary>
    /// The folder a file's album is named after, tried in this order:
    /// 1. its own folder, unless the name is generic (DCIM, 100CANON, RAW, a year…);
    /// 2. the nearest folder above it with a real name, up to its source folder ("Japan/DCIM/100CANON" and
    ///    "Japan/RAW" go in Japan);
    /// 3. a year folder on the way ("Photos/2019/DCIM" goes in 2019);
    /// 4. the source folder itself ("Photos/DCIM/Camera" goes in Photos), so every photo in a source has an album.
    /// Null only for a file in a generic folder outside every source.
    /// </summary>
    internal static (string Name, string Directory)? AlbumFolder(string directory, IReadOnlyList<string> sourceRoots)
    {
        var d = directory.TrimEnd('/', '\\');
        var root = sourceRoots.Where(r => d == r || d.StartsWith(r + '/') || d.StartsWith(r + '\\')).MaxBy(r => r.Length);
        (string Name, string Directory)? year = null;
        while (true)
        {
            var name = Path.GetFileName(d);
            if (!string.IsNullOrWhiteSpace(name))
            {
                if (!IsGenericDirName(name)) return (name, d);
                if (year is null && IsYear(name)) year = (name, d);
            }
            if (root is null || d.Length <= root.Length) break; // reached the source folder; don't look above it
            var parent = Path.GetDirectoryName(d);
            if (string.IsNullOrEmpty(parent) || parent == d) break;
            d = parent;
        }
        if (year is { } y) return y;
        return root is not null && Path.GetFileName(root) is { Length: > 0 } rootName ? (rootName, root) : null;
    }

    /// <summary>A folder as the report shows it, starting at its source folder's name ("Photos/DCIM/Camera").</summary>
    private static string Shown(string directory, IReadOnlyList<string> sourceRoots)
    {
        var root = sourceRoots.Where(r => directory == r || directory.StartsWith(r + '/') || directory.StartsWith(r + '\\')).MaxBy(r => r.Length);
        return root is null ? directory : Path.GetFileName(root) + directory[root.Length..];
    }

    /// <summary>
    /// One file of an item, as <see cref="PlanAutoAlbums"/> takes it: the folder its album is named after
    /// (<see cref="AlbumFolder"/>) and when the item was taken. An item with files in several folders appears once per folder.
    /// </summary>
    public sealed record FolderItem(long Id, string DirName, string Directory, DateTime? Date);

    public sealed record PlannedAlbum(string Name, string DirName, DateTime? From, DateTime? To, List<long> Items);

    /// <summary>
    /// The albums <see cref="BuildAutoAlbums"/> makes: one per folder name. When same-named folders are more than
    /// <paramref name="gapDays"/> apart, each group of close ones gets its own album, named "Name (first date – last
    /// date)". A folder always stays whole. Undated folders join the name's first album. <see cref="AlbumFolder"/> picks
    /// which folder names an album.
    /// </summary>
    public static List<PlannedAlbum> PlanAutoAlbums(IEnumerable<FolderItem> items, int gapDays)
    {
        var plan = new List<PlannedAlbum>();
        foreach (var name in items.GroupBy(i => i.DirName, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(name.Key)) continue; // AlbumFolder falls back to a generic name only as a last resort
            var folders = name.GroupBy(i => i.Directory, StringComparer.Ordinal)
                .Select(f => (From: f.Min(i => i.Date), To: f.Max(i => i.Date), Items: f.ToList()))
                .ToList();

            var runs = new List<(DateTime? From, DateTime? To, List<FolderItem> Items)>();
            foreach (var f in folders.Where(f => f.From is not null).OrderBy(f => f.From))
            {
                if (runs.Count > 0 && (f.From!.Value - runs[^1].To!.Value).TotalDays <= gapDays)
                    runs[^1] = (runs[^1].From, f.To > runs[^1].To ? f.To : runs[^1].To, [.. runs[^1].Items, .. f.Items]);
                else
                    runs.Add((f.From, f.To, f.Items));
            }
            var undated = folders.Where(f => f.From is null).SelectMany(f => f.Items).ToList();
            if (runs.Count == 0) runs.Add((null, null, []));
            runs[0] = (runs[0].From, runs[0].To, [.. runs[0].Items, .. undated]);

            foreach (var run in runs)
            {
                var label = runs.Count > 1 && run.From is not null ? $"{name.Key} ({DateRange(run.From.Value, run.To!.Value)})" : name.Key;
                var ordered = run.Items.OrderBy(i => i.Date is null).ThenBy(i => i.Date).Select(i => i.Id).Distinct().ToList();
                plan.Add(new PlannedAlbum(label, name.Key, run.From, run.To, ordered));
            }
        }
        return plan;
    }

    /// <summary>"2022-06-10", or "2022-06-10 – 2022-06-14" when the days differ.</summary>
    public static string DateRange(DateTime from, DateTime to) =>
        from.Date == to.Date ? $"{from:yyyy-MM-dd}" : $"{from:yyyy-MM-dd} – {to:yyyy-MM-dd}";

    private static string? DirectoryOf(string? path, string? fileName) =>
        path is null || fileName is null || !path.EndsWith(fileName, StringComparison.Ordinal) ? null : path[..^fileName.Length].TrimEnd('/', '\\');

    public static bool IsGenericDirName(string name)
    {
        var n = name.Trim().ToLowerInvariant();
        if (n is "dcim" or "camera" or "photos" or "pictures" or "images" or "raw" or "jpg" or "jpeg" or "video" or "videos" or "music" or "audio" or "misc" or "100canon" or "100apple" or "100msdcf" or "100nikon" or "100olymp") return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(n, @"^\d{3}[a-z_]+\d*$")) return true; // 100CANON, 101_PANA (not "360")
        if (IsYear(n)) return true;                                                               // folders named only by a year
        return false;
    }

    private static bool IsYear(string name) => System.Text.RegularExpressions.Regex.IsMatch(name.Trim(), @"^\d{4}$");

    public static DateTime? ParseDate(string? iso) => iso is null ? null : DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.None, out var d) ? d : null;

    private static bool Overlaps(string? aFrom, string? aTo, string? bFrom, string? bTo, int gapDays)
    {
        var af = ParseDate(aFrom); var at = ParseDate(aTo); var bf = ParseDate(bFrom); var bt = ParseDate(bTo);
        if (af is null || bf is null) return af is null && bf is null;
        var gap = TimeSpan.FromDays(gapDays);
        return af.Value - gap <= (bt ?? bf.Value) && bf.Value - gap <= (at ?? af.Value);
    }
}
