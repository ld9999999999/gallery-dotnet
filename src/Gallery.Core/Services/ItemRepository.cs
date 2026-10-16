using System.Text;
using Dapper;
using Gallery.Core.Data;
using Gallery.Core.Models;

namespace Gallery.Core.Services;

public sealed class ItemFilter
{
    public string? Q { get; set; }          // matched against title, file name and folder name
    public string? Kind { get; set; }       // image | video | audio
    public int? Year { get; set; }
    public string? From { get; set; }       // yyyy-MM-dd
    public string? To { get; set; }
    public List<(string Key, string? Value)> Tags { get; } = new();
    public long? AlbumId { get; set; }
    /// <summary>Items that show a face of every one of these people.</summary>
    public List<long> PersonIds { get; } = new();
    public bool DuplicatesOnly { get; set; }
    public bool VariationsOnly { get; set; }
    public bool Untagged { get; set; }
    /// <summary>For viewers (admins see everything): leaves out items hidden from viewers, directly or through a hidden album.</summary>
    public bool ForViewers { get; set; }
    public string Sort { get; set; } = "date_desc"; // date_desc | date_asc | name | added
    public int Offset { get; set; }
    public int Limit { get; set; } = 200;
}

/// <param name="Projection">"equirectangular" or "dual-fisheye" when the viewer shows it as a 360° photo (see <see cref="Services.Projection"/>), else null.</param>
/// <param name="Squeeze">How much the long side is stretched when shown, for an anamorphic lens (<see cref="Services.Squeeze"/>). 1 means no stretch.</param>
public sealed record ItemDto(long Id, string Kind, string? TakenAt, string Title, string DirName, long? PrimaryAssetId,
    int? Width, int? Height, double? DurationSec, int AssetCount, bool IsRaw, string Ext, string Hash, int Rotation, bool Edited = false,
    bool HiddenFromViewers = false, bool InHiddenAlbum = false, string? Projection = null, double Squeeze = 1);

/// <summary>A year in the Years view: how many items it has, and one to show as its cover.</summary>
/// <param name="Chosen">True when an admin picked the cover; otherwise it's the year's newest item.</param>
public sealed record YearSummary(int Year, long Count, ItemDto? Cover, bool Chosen = false);

/// <param name="Projection">How it is shown: the item's <c>projection</c> tag, else what the scan found in this file.</param>
/// <param name="DetectedProjection">What the scan found in this file, regardless of the tag.</param>
/// <param name="Lenses">For two fisheye circles: how the viewer stitches them. Read from the file when the item is opened.</param>
public sealed record AssetDto(long Id, string Path, string FileName, string Ext, long Size, long Mtime, string Hash, string Kind,
    bool IsRaw, int? Width, int? Height, double? DurationSec, string? TakenAt, string? TakenSource, string? Codec, string? AudioCodec, bool Missing, bool Identical,
    long? EditOf = null, string? Projection = null, string? DetectedProjection = null, DualFisheye.Rig? Lenses = null);

public sealed record ItemDetailDto(ItemDto Item, List<AssetDto> Assets, List<ItemTag> Tags, List<Album> Albums);

public sealed class ItemRepository
{
    private readonly Db _db;

    public ItemRepository(Db db) => _db = db;

    /// <summary>
    /// Raised with item ids after an item's main file or manual rotation changes, so work done on the picture (faces) can
    /// be redone. Scans report their own changes.
    /// </summary>
    public event Action<IReadOnlyCollection<long>>? PictureChanged;

    /// <summary>Lets code elsewhere (such as deleting an edit) notify <see cref="PictureChanged"/> listeners.</summary>
    internal void OnPictureChanged(IReadOnlyCollection<long> itemIds) => PictureChanged?.Invoke(itemIds);

    /// <summary>
    /// SQL condition on items (named <paramref name="i"/> in the query) for what a viewer may see: the item isn't hidden
    /// from viewers and isn't in an album hidden from viewers. Admins skip this filter.
    /// </summary>
    internal static string ViewerVisible(string i = "i") =>
        $"{i}.viewer_hidden=0 AND {i}.id NOT IN (SELECT hai.item_id FROM album_items hai JOIN albums hal ON hal.id=hai.album_id WHERE hal.viewer_hidden=1)";

    private static (string Where, DynamicParameters P) BuildWhere(ItemFilter f)
    {
        var sb = new StringBuilder("WHERE i.hidden=0");
        var p = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(f.Q))
        {
            sb.Append(" AND (i.title LIKE @q OR i.dir_name LIKE @q OR EXISTS (SELECT 1 FROM assets a WHERE a.item_id=i.id AND (a.file_name LIKE @q OR a.path LIKE @q)) OR EXISTS (SELECT 1 FROM item_tags t WHERE t.item_id=i.id AND t.value LIKE @q))");
            p.Add("q", "%" + f.Q.Trim() + "%");
        }
        if (!string.IsNullOrWhiteSpace(f.Kind)) { sb.Append(" AND i.kind=@kind"); p.Add("kind", f.Kind); }
        if (f.Year is not null) { sb.Append(" AND i.taken_year=@year"); p.Add("year", f.Year); }
        if (!string.IsNullOrWhiteSpace(f.From)) { sb.Append(" AND i.taken_at>=@from"); p.Add("from", f.From); }
        if (!string.IsNullOrWhiteSpace(f.To)) { sb.Append(" AND i.taken_at<@to"); p.Add("to", f.To.Length == 10 ? f.To + "T23:59:59" : f.To); }
        var ti = 0;
        foreach (var (key, value) in f.Tags)
        {
            ti++;
            if (value is null) { sb.Append($" AND EXISTS (SELECT 1 FROM item_tags t WHERE t.item_id=i.id AND t.key=@tk{ti})"); }
            else { sb.Append($" AND EXISTS (SELECT 1 FROM item_tags t WHERE t.item_id=i.id AND t.key=@tk{ti} AND t.value=@tv{ti})"); p.Add($"tv{ti}", value); }
            p.Add($"tk{ti}", key);
        }
        if (f.AlbumId is not null) { sb.Append(" AND EXISTS (SELECT 1 FROM album_items ai WHERE ai.item_id=i.id AND ai.album_id=@album)"); p.Add("album", f.AlbumId); }
        var pi = 0;
        foreach (var person in f.PersonIds)
        {
            pi++;
            sb.Append($" AND EXISTS (SELECT 1 FROM live_faces lf WHERE lf.item_id=i.id AND lf.person_id=@person{pi})");
            p.Add($"person{pi}", person);
        }
        if (f.DuplicatesOnly) sb.Append(" AND EXISTS (SELECT 1 FROM assets a WHERE a.item_id=i.id AND a.missing=0 GROUP BY a.hash HAVING COUNT(*)>1)");
        if (f.VariationsOnly) sb.Append(" AND i.asset_count>1");
        if (f.Untagged) sb.Append(" AND NOT EXISTS (SELECT 1 FROM item_tags t WHERE t.item_id=i.id)");
        if (f.ForViewers) sb.Append(" AND ").Append(ViewerVisible());
        return (sb.ToString(), p);
    }

    private static string OrderBy(string sort) => sort switch
    {
        "date_asc" => "ORDER BY i.taken_at ASC, i.id ASC",
        "name" => "ORDER BY i.title COLLATE NOCASE ASC, i.id",
        "added" => "ORDER BY i.id DESC",
        _ => "ORDER BY i.taken_at DESC, i.id DESC",
    };

    private const string ItemSelect = """
        SELECT i.id, i.kind, i.taken_at, i.title, i.dir_name, i.primary_asset_id, i.width, i.height, i.duration_sec, i.asset_count,
               COALESCE(a.is_raw,0) AS is_raw, COALESCE(a.ext,'') AS ext, COALESCE(a.hash,'') AS hash,
               (SELECT t.value FROM item_tags t WHERE t.item_id=i.id AND t.key='rotate-degrees' LIMIT 1) AS rotation_tag,
               (SELECT t.value FROM item_tags t WHERE t.item_id=i.id AND t.key='anamorphic' LIMIT 1) AS squeeze_tag,
               a.projection, (SELECT t.value FROM item_tags t WHERE t.item_id=i.id AND t.key='projection' LIMIT 1) AS projection_tag,
               EXISTS (SELECT 1 FROM edits e JOIN assets ea ON ea.id=e.asset_id WHERE ea.item_id=i.id) AS edited,
               i.viewer_hidden AS hidden_from_viewers,
               EXISTS (SELECT 1 FROM album_items hai JOIN albums hal ON hal.id=hai.album_id WHERE hai.item_id=i.id AND hal.viewer_hidden=1) AS in_hidden_album
        FROM items i LEFT JOIN assets a ON a.id=i.primary_asset_id
        """;

    /// <summary>
    /// How many of the filter's items fall in each month ("2023-12"), in its date order (newest first unless sorted oldest
    /// first). These are the month headings of the gallery grid. Undated items are left out.
    /// </summary>
    public List<(string Month, long Count)> MonthCounts(ItemFilter f)
    {
        var (where, p) = BuildWhere(f);
        using var c = _db.Open();
        return c.Query<(string, long)>($"""
            SELECT substr(i.taken_at, 1, 7) AS month, COUNT(*) FROM items i {where} AND i.taken_at IS NOT NULL
            GROUP BY month ORDER BY month {(f.Sort == "date_asc" ? "ASC" : "DESC")}
            """, p).ToList();
    }

    public (List<ItemDto> Items, long Total) Query(ItemFilter f)
    {
        var (where, p) = BuildWhere(f);
        p.Add("limit", Math.Clamp(f.Limit, 1, 1000));
        p.Add("offset", Math.Max(0, f.Offset));
        using var c = _db.Open();
        var total = c.ExecuteScalar<long>($"SELECT COUNT(*) FROM items i {where}", p);
        var rows = c.Query<ItemRow>($"{ItemSelect} {where} {OrderBy(f.Sort)} LIMIT @limit OFFSET @offset", p).Select(ToDto).ToList();
        return (rows, total);
    }

    public List<long> QueryIds(ItemFilter f)
    {
        var (where, p) = BuildWhere(f);
        using var c = _db.Open();
        return c.Query<long>($"SELECT i.id FROM items i {where} {OrderBy(f.Sort)}", p).ToList();
    }

    private sealed class ItemRow
    {
        public long Id { get; set; } public string Kind { get; set; } = ""; public string? TakenAt { get; set; } public string Title { get; set; } = "";
        public string DirName { get; set; } = ""; public long? PrimaryAssetId { get; set; } public int? Width { get; set; } public int? Height { get; set; }
        public double? DurationSec { get; set; } public int AssetCount { get; set; } public int IsRaw { get; set; } public string Ext { get; set; } = ""; public string Hash { get; set; } = ""; public string? RotationTag { get; set; }
        public int Edited { get; set; }
        public int HiddenFromViewers { get; set; } public int InHiddenAlbum { get; set; }
        public string? Projection { get; set; } public string? ProjectionTag { get; set; }
        public string? SqueezeTag { get; set; }
    }

    private static ItemDto ToDto(ItemRow r) => new(r.Id, r.Kind, r.TakenAt, r.Title, r.DirName, r.PrimaryAssetId, r.Width, r.Height, r.DurationSec, r.AssetCount,
        r.IsRaw == 1, r.Ext, r.Hash, Rotation.Parse(r.RotationTag), r.Edited == 1, r.HiddenFromViewers == 1, r.InHiddenAlbum == 1,
        Services.Projection.Effective(r.Projection, r.ProjectionTag, r.Kind), Services.Squeeze.Parse(r.SqueezeTag));

    /// <summary>An item with its files, tags and albums. Null when it doesn't exist, or when a viewer asks for a hidden one.</summary>
    public ItemDetailDto? Get(long id, bool forViewers = false)
    {
        using var c = _db.Open();
        var row = c.QuerySingleOrDefault<ItemRow>($"{ItemSelect} WHERE i.id=@id", new { id });
        if (row is null || (forViewers && (row.HiddenFromViewers == 1 || row.InHiddenAlbum == 1))) return null;
        var assets = c.Query<Asset>("SELECT * FROM assets WHERE item_id=@id ORDER BY missing, is_raw, size DESC", new { id }).ToList();
        var hashCounts = assets.GroupBy(a => a.Hash).ToDictionary(g => g.Key, g => g.Count());
        var tags = c.Query<ItemTag>("SELECT item_id, key, value FROM item_tags WHERE item_id=@id ORDER BY key, value", new { id }).ToList();
        var albums = c.Query<Album>("SELECT al.* FROM albums al JOIN album_items ai ON ai.album_id=al.id WHERE ai.item_id=@id ORDER BY al.name", new { id }).ToList();
        return new ItemDetailDto(ToDto(row),
            assets.Select(a => new AssetDto(a.Id, a.Path, a.FileName, a.Ext, a.Size, a.Mtime, a.Hash, a.Kind, a.IsRaw == 1, a.Width, a.Height, a.DurationSec, a.TakenAt, a.TakenSource, a.Codec, a.AudioCodec, a.Missing == 1, hashCounts[a.Hash] > 1, a.EditOf,
                Services.Projection.Effective(a.Projection, row.ProjectionTag, a.Kind), a.Projection)).ToList(),
            tags, albums);
    }

    /// <summary>Items by id, in no particular order. Unknown ids are skipped.</summary>
    public List<ItemDto> GetItems(IEnumerable<long> ids)
    {
        using var c = _db.Open();
        var list = new List<ItemDto>();
        foreach (var chunk in ids.Distinct().Chunk(500))
            list.AddRange(c.Query<ItemRow>($"{ItemSelect} WHERE i.id IN @chunk", new { chunk }).Select(ToDto));
        return list;
    }

    public Asset? GetAsset(long id)
    {
        using var c = _db.Open();
        return c.QuerySingleOrDefault<Asset>("SELECT * FROM assets WHERE id=@id", new { id });
    }

    public List<(int Year, long Count)> Years()
    {
        using var c = _db.Open();
        return c.Query<(int year, long count)>("SELECT taken_year, COUNT(*) FROM items WHERE hidden=0 AND taken_year IS NOT NULL GROUP BY taken_year ORDER BY taken_year DESC").Select(x => (x.year, x.count)).ToList();
    }

    /// <summary>Years with their item counts, newest first, each with its latest photo or video as a cover.</summary>
    public List<YearSummary> YearSummaries(bool forViewers = false)
    {
        var visible = forViewers ? " AND " + ViewerVisible() : "";
        using var c = _db.Open();
        // The admin's chosen cover wins while it's still from that year and visible; otherwise the year's newest.
        var rows = c.Query<(int Year, long Count, long? Chosen, long? Newest)>($"""
            SELECT y.taken_year, y.n,
                   (SELECT i.id FROM year_covers yc JOIN items i ON i.id=yc.item_id
                    WHERE yc.year=y.taken_year AND i.taken_year=y.taken_year AND i.hidden=0 AND i.kind<>'audio' AND i.primary_asset_id IS NOT NULL{visible}),
                   (SELECT i.id FROM items i WHERE i.taken_year=y.taken_year AND i.hidden=0 AND i.kind<>'audio' AND i.primary_asset_id IS NOT NULL{visible}
                    ORDER BY i.taken_at DESC LIMIT 1)
            FROM (SELECT i.taken_year, COUNT(*) AS n FROM items i WHERE i.hidden=0 AND i.taken_year IS NOT NULL{visible} GROUP BY i.taken_year) y
            ORDER BY y.taken_year DESC
            """).ToList();
        var covers = GetItems(rows.Select(r => r.Chosen ?? r.Newest).OfType<long>()).ToDictionary(x => x.Id);
        return rows.Select(r => new YearSummary(r.Year, r.Count, (r.Chosen ?? r.Newest) is { } id ? covers.GetValueOrDefault(id) : null, r.Chosen is not null)).ToList();
    }

    /// <summary>
    /// Sets the cover for a year in the Years view: an item taken that year, or null for the default (its newest).
    /// Returns false when the item wasn't taken that year.
    /// </summary>
    public bool SetYearCover(int year, long? itemId)
    {
        using var c = _db.Open();
        if (itemId is null) c.Execute("DELETE FROM year_covers WHERE year=@year", new { year });
        else if (c.Execute("INSERT OR REPLACE INTO year_covers(year, item_id) SELECT @year, id FROM items WHERE id=@itemId AND taken_year=@year", new { year, itemId }) == 0) return false;
        _db.Touch();
        return true;
    }

    public List<(string Key, long Count)> TagKeys(bool forViewers = false)
    {
        using var c = _db.Open();
        return c.Query<(string key, long count)>($"SELECT t.key, COUNT(*) FROM item_tags t JOIN items i ON i.id=t.item_id WHERE i.hidden=0{(forViewers ? " AND " + ViewerVisible() : "")} GROUP BY t.key ORDER BY t.key")
            .Select(x => (x.key, x.count)).ToList();
    }

    public List<(string Value, long Count)> TagValues(string key, bool forViewers = false)
    {
        using var c = _db.Open();
        return c.Query<(string value, long count)>($"SELECT t.value, COUNT(*) AS n FROM item_tags t JOIN items i ON i.id=t.item_id WHERE t.key=@key AND i.hidden=0{(forViewers ? " AND " + ViewerVisible() : "")} GROUP BY t.value ORDER BY n DESC, t.value", new { key })
            .Select(x => (x.value, x.count)).ToList();
    }

    /// <summary>Whether a viewer may see this item.</summary>
    public bool IsVisibleToViewers(long itemId)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<long>($"SELECT COUNT(*) FROM items i WHERE i.id=@itemId AND {ViewerVisible()}", new { itemId }) > 0;
    }

    /// <summary>Whether a viewer may see this file, which depends on its item.</summary>
    public bool IsAssetVisibleToViewers(long assetId)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<long>($"SELECT COUNT(*) FROM assets a JOIN items i ON i.id=a.item_id WHERE a.id=@assetId AND {ViewerVisible()}", new { assetId }) > 0;
    }

    /// <summary>Hides items from viewers, or shows them again. Returns how many changed. Admins see them either way.</summary>
    public int SetHiddenFromViewers(IEnumerable<long> itemIds, bool hidden)
    {
        using var c = _db.Open();
        var n = c.Execute("UPDATE items SET viewer_hidden=@h WHERE id IN @ids AND viewer_hidden<>@h", new { ids = itemIds.Distinct().ToList(), h = hidden ? 1 : 0 });
        if (n > 0) _db.Touch();
        return n;
    }

    public int SetTags(IEnumerable<long> itemIds, IEnumerable<ItemTag> add, IEnumerable<ItemTag> remove)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var ids = itemIds.Distinct().ToList();
        var n = 0;
        foreach (var t in remove)
            n += c.Execute("DELETE FROM item_tags WHERE item_id=@id AND key=@key AND value=@value", ids.Select(id => new { id, key = t.Key.Trim(), value = t.Value.Trim() }), tx);
        foreach (var t in add)
        {
            if (string.IsNullOrWhiteSpace(t.Key)) continue;
            // These tags hold a single value, so setting one replaces the old one.
            if (SingleValueTags.Contains(t.Key.Trim()))
                c.Execute("DELETE FROM item_tags WHERE item_id=@id AND key=@key", ids.Select(id => new { id, key = t.Key.Trim() }), tx);
            n += c.Execute("INSERT OR IGNORE INTO item_tags(item_id, key, value) VALUES(@id,@key,@value)", ids.Select(id => new { id, key = t.Key.Trim(), value = t.Value.Trim() }), tx);
        }
        tx.Commit();
        // A turn or a squeeze changes the picture, so faces have to be found again.
        if (add.Concat(remove).Any(t => PictureTags.Contains(t.Key.Trim()))) PictureChanged?.Invoke(ids);
        return n;
    }

    private static readonly HashSet<string> SingleValueTags = new(StringComparer.OrdinalIgnoreCase) { Rotation.TagKey, Services.Projection.TagKey, Services.Squeeze.TagKey };
    private static readonly HashSet<string> PictureTags = new(StringComparer.OrdinalIgnoreCase) { Rotation.TagKey, Services.Squeeze.TagKey };

    /// <summary>Whether the viewer shows this file as a 360° photo: the item's projection tag, else what was found in the file.</summary>
    public string? GetProjectionForAsset(long assetId)
    {
        using var c = _db.Open();
        var row = c.QuerySingleOrDefault<(string Kind, string? Detected, string? Tag)>(
            "SELECT a.kind, a.projection, (SELECT t.value FROM item_tags t WHERE t.item_id=a.item_id AND t.key=@key LIMIT 1) FROM assets a WHERE a.id=@assetId",
            new { assetId, key = Services.Projection.TagKey });
        return row.Kind is null ? null : Services.Projection.Effective(row.Detected, row.Tag, row.Kind);
    }

    /// <summary>
    /// Lets an admin set whether an item is a 360° photo: true shows it as one, false as a flat photo, null goes back to
    /// what the scan found in the file. Stored as the projection tag (360 or flat). Returns false when the item doesn't exist.
    /// </summary>
    public bool SetProjection(long itemId, bool? sphere)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM items WHERE id=@itemId", new { itemId }, tx) == 0) return false;
        c.Execute("DELETE FROM item_tags WHERE item_id=@itemId AND key=@key", new { itemId, key = Services.Projection.TagKey }, tx);
        if (sphere is { } on)
            c.Execute("INSERT INTO item_tags(item_id, key, value) VALUES(@itemId, @key, @value)",
                new { itemId, key = Services.Projection.TagKey, value = on ? Services.Projection.TagSphere : Services.Projection.TagFlat }, tx);
        tx.Commit();
        _db.Touch();
        return true;
    }

    /// <summary>The manual rotation and anamorphic squeeze of the item an asset belongs to (0 and 1 when none).</summary>
    public (int Rotation, double Squeeze) GetViewForAsset(long assetId)
    {
        using var c = _db.Open();
        var tags = c.Query<(string Key, string Value)>(
            "SELECT t.key, t.value FROM item_tags t JOIN assets a ON a.item_id=t.item_id WHERE a.id=@assetId AND t.key IN (@rotation, @squeeze)",
            new { assetId, rotation = Rotation.TagKey, squeeze = Services.Squeeze.TagKey }).ToList();
        return (Rotation.Parse(tags.FirstOrDefault(t => t.Key == Rotation.TagKey).Value),
            Services.Squeeze.Parse(tags.FirstOrDefault(t => t.Key == Services.Squeeze.TagKey).Value));
    }

    /// <summary>
    /// Sets the anamorphic squeeze of these items (1 or less removes it) and returns the factor stored.
    /// Their pictures change, so their faces are found again (names carry over; see FaceRepository.Save).
    /// </summary>
    public double SetSqueeze(IReadOnlyCollection<long> itemIds, double factor)
    {
        factor = Services.Squeeze.Normalize(factor);
        var ids = itemIds.Distinct().ToList();
        using (var c = _db.Open())
        using (var tx = c.BeginTransaction())
        {
            c.Execute("DELETE FROM item_tags WHERE item_id IN @ids AND key=@key", new { ids, key = Services.Squeeze.TagKey }, tx);
            if (factor != 1)
                c.Execute("INSERT INTO item_tags(item_id, key, value) SELECT id, @key, @value FROM items WHERE id IN @ids",
                    new { ids, key = Services.Squeeze.TagKey, value = Services.Squeeze.Format(factor) }, tx);
            tx.Commit();
        }
        _db.Touch();
        PictureChanged?.Invoke(ids);
        return factor;
    }

    /// <summary>Manual rotation of the item an asset belongs to (0 when none).</summary>
    public int GetRotationForAsset(long assetId)
    {
        using var c = _db.Open();
        return Rotation.Parse(c.ExecuteScalar<string?>(
            "SELECT t.value FROM item_tags t JOIN assets a ON a.item_id=t.item_id WHERE a.id=@assetId AND t.key=@key LIMIT 1",
            new { assetId, key = Rotation.TagKey }));
    }

    /// <summary>Turns an item by <paramref name="by"/> degrees (±90, 180) relative to its current rotation and returns the new one.
    /// Turning back to 0 removes the tag. Returns null when the item doesn't exist.</summary>
    public int? RotateItem(long itemId, int by)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM items WHERE id=@itemId", new { itemId }, tx) == 0) return null;
        var current = Rotation.Parse(c.ExecuteScalar<string?>("SELECT value FROM item_tags WHERE item_id=@itemId AND key=@key LIMIT 1", new { itemId, key = Rotation.TagKey }, tx));
        var next = Rotation.Normalize(current + by);
        c.Execute("DELETE FROM item_tags WHERE item_id=@itemId AND key=@key", new { itemId, key = Rotation.TagKey }, tx);
        if (next != 0)
            c.Execute("INSERT INTO item_tags(item_id, key, value) VALUES(@itemId, @key, @value)", new { itemId, key = Rotation.TagKey, value = next.ToString() }, tx);
        tx.Commit();
        if (next != current) PictureChanged?.Invoke([itemId]);
        return next;
    }

    public void SetPrimaryAsset(long itemId, long assetId)
    {
        using var c = _db.Open();
        // preferred_asset_id preserves the choice when the item is rebuilt (scans, regrouping).
        if (c.Execute("UPDATE items SET primary_asset_id=@assetId, preferred_asset_id=@assetId, kind=(SELECT kind FROM assets WHERE id=@assetId), width=(SELECT width FROM assets WHERE id=@assetId), height=(SELECT height FROM assets WHERE id=@assetId) WHERE id=@itemId AND EXISTS (SELECT 1 FROM assets WHERE id=@assetId AND item_id=@itemId)", new { itemId, assetId }) > 0)
            PictureChanged?.Invoke([itemId]);
    }

    public sealed record Stats(
        long Items, long Images, long Videos, long Audio,
        long Assets, long Bytes, long Raw,
        long DuplicateGroups, long DuplicateAssets, long DuplicateBytes,
        long VariationItems, long Missing);

    /// <summary>
    /// Library totals. Duplicates are files with the same bytes (same hash): <c>DuplicateAssets</c> counts the extra copies
    /// after the first in each of the <c>DuplicateGroups</c> groups. <c>VariationItems</c> counts items with more than one
    /// distinct file, such as a JPEG with its RAW. Slow on large libraries, so call it through <see cref="StatusService"/>,
    /// which caches the result.
    /// </summary>
    public Stats GetStats()
    {
        using var c = _db.Open();
        var kinds = c.Query<(string kind, long n)>("SELECT kind, COUNT(*) FROM items WHERE hidden=0 GROUP BY kind").ToDictionary(x => x.kind, x => x.n);
        var (assets, bytes, raw) = c.QuerySingle<(long, long, long)>("SELECT COUNT(*), COALESCE(SUM(size),0), COALESCE(SUM(is_raw),0) FROM assets WHERE missing=0");
        var (dupGroups, dupCount, dupBytes) = c.QuerySingle<(long, long, long)>("""
            SELECT COUNT(*), COALESCE(SUM(n-1),0), COALESCE(SUM((n-1)*size),0)
            FROM (SELECT COUNT(*) AS n, MAX(size) AS size FROM assets WHERE missing=0 GROUP BY hash HAVING COUNT(*)>1)
            """);
        var variations = c.ExecuteScalar<long>("SELECT COUNT(*) FROM (SELECT item_id FROM assets WHERE missing=0 GROUP BY item_id HAVING COUNT(DISTINCT hash)>1)");
        var missing = c.ExecuteScalar<long>("SELECT COUNT(*) FROM assets WHERE missing=1");
        long K(string k) => kinds.TryGetValue(k, out var n) ? n : 0;
        return new Stats(kinds.Values.Sum(), K(MediaKind.Image), K(MediaKind.Video), K(MediaKind.Audio),
            assets, bytes, raw, dupGroups, dupCount, dupBytes, variations, missing);
    }
}
