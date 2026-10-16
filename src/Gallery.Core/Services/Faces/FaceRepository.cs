using System.Runtime.InteropServices;
using Dapper;
using Gallery.Core.Data;
using Gallery.Core.Models;

namespace Gallery.Core.Services.Faces;

/// <summary>A stored face: box and landmarks as fractions of the upright picture, and the fingerprint at full precision.</summary>
public sealed record StoredFace(long Id, long AssetId, string Model, float X1, float Y1, float X2, float Y2, float[] Landmarks,
    float Score, float EyePx, float[] Embedding, long? PersonId, string? AssignedBy);

/// <summary>A face to store, already converted to fractions of the picture.</summary>
public sealed record NewFace(float X1, float Y1, float X2, float Y2, float[] Landmarks, float Score, float EyePx, float[] Embedding);

/// <summary>For the current pack: how many photos to search, and how many are done.</summary>
public sealed record FaceCounts(long Photos, long Done, long Failed, long Faces, long PhotosWithFaces)
{
    public long Pending => Math.Max(0, Photos - Done - Failed);
}

/// <summary>
/// Reads and writes the faces tables. Each item is searched through its main file or, for an exported edit, the file it
/// was made from (the same picture). Only photos in items that still have files are searched.
/// </summary>
public sealed class FaceRepository
{
    private readonly Db _db;

    public FaceRepository(Db db) => _db = db;

    private sealed class TargetRow
    {
        public long AssetId { get; set; }
        public string? RotationTag { get; set; }
        public string? SqueezeTag { get; set; }
        public string? Model { get; set; }
        public int? ScannedRotation { get; set; }
        public double? ScannedSqueeze { get; set; }
        public string? Status { get; set; }
    }

    private const string TargetSql = """
        SELECT COALESCE(src.id, p.id) AS asset_id,
               (SELECT t.value FROM item_tags t WHERE t.item_id=i.id AND t.key='rotate-degrees' LIMIT 1) AS rotation_tag,
               (SELECT t.value FROM item_tags t WHERE t.item_id=i.id AND t.key='anamorphic' LIMIT 1) AS squeeze_tag,
               fs.model AS model, fs.rotation AS scanned_rotation, fs.squeeze AS scanned_squeeze, fs.status AS status
        FROM items i
        JOIN assets p ON p.id=i.primary_asset_id AND p.missing=0
        LEFT JOIN assets src ON src.id=p.edit_of AND src.missing=0
        LEFT JOIN face_scans fs ON fs.asset_id=COALESCE(src.id, p.id)
        WHERE i.hidden=0 AND i.kind='image'
        """;

    private List<TargetRow> TargetRows(IReadOnlyCollection<long>? itemIds)
    {
        using var c = _db.Open();
        return itemIds is null
            ? c.Query<TargetRow>(TargetSql + " ORDER BY i.taken_at DESC").ToList()
            : c.Query<TargetRow>(TargetSql + " AND i.id IN @ids", new { ids = itemIds }).ToList();
    }

    private static bool Current(TargetRow r, string model) =>
        r.Model == model && r.ScannedRotation == Rotation.Parse(r.RotationTag) && Math.Abs((r.ScannedSqueeze ?? 1) - Squeeze.Parse(r.SqueezeTag)) < 0.001;

    /// <summary>Files still to search for <paramref name="model"/> (never searched, searched with another pack, or turned or stretched after the search), newest first.</summary>
    public List<long> Targets(string model, IReadOnlyCollection<long>? itemIds = null) =>
        TargetRows(itemIds).Where(r => !Current(r, model)).Select(r => r.AssetId).Distinct().ToList();

    public FaceCounts Counts(string model)
    {
        var rows = TargetRows(null);
        var current = rows.Where(r => Current(r, model)).ToList();
        using var c = _db.Open();
        var (faces, withFaces) = c.QuerySingle<(long, long)>("""
            SELECT COUNT(*), COUNT(DISTINCT f.asset_id) FROM faces f JOIN face_scans fs ON fs.asset_id=f.asset_id AND fs.model=f.model
            WHERE f.model=@model
            """, new { model });
        return new FaceCounts(rows.Count, current.Count(r => r.Status == "done"), current.Count(r => r.Status == "failed"), faces, withFaces);
    }

    /// <summary>
    /// The file with the rotation and anamorphic squeeze to search it at (from its item's rotate-degrees and
    /// anamorphic tags). Null when the file is deleted or missing.
    /// </summary>
    public (Asset Asset, int Rotation, double Squeeze)? Target(long assetId)
    {
        using var c = _db.Open();
        var asset = c.QuerySingleOrDefault<Asset>("SELECT * FROM assets WHERE id=@assetId AND missing=0", new { assetId });
        if (asset is null) return null;
        var tags = c.Query<(string Key, string Value)>("SELECT key, value FROM item_tags WHERE item_id=@item AND key IN ('rotate-degrees', 'anamorphic')",
            new { item = asset.ItemId }).ToList();
        return (asset, Rotation.Parse(tags.FirstOrDefault(t => t.Key == Rotation.TagKey).Value), Squeeze.Parse(tags.FirstOrDefault(t => t.Key == Squeeze.TagKey).Value));
    }

    private sealed record OldFace(long Id, double X1, double Y1, double X2, double Y2, long? PersonId, string? AssignedBy);

    /// <summary>
    /// Replaces the file's faces (of any pack) and saves the search, in one transaction. Each new face takes the person,
    /// the "removed by hand" mark and the rejections of the old face it overlaps most (IoU above 0.5; turn the old boxes
    /// first if the photo was turned after the last search). This keeps names after a pack change or a rotation. A
    /// person's chosen cover moves to the new face.
    /// </summary>
    /// <param name="squeeze">The anamorphic squeeze the picture was stretched by. A stretch doesn't change boxes, which are
    /// fractions of the picture, so old faces still match.</param>
    public void Save(long assetId, string model, int rotation, IReadOnlyList<NewFace> faces, double squeeze = 1)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var oldIds = c.Query<long>("SELECT id FROM faces WHERE asset_id=@assetId", new { assetId }, tx).ToList();
        var old = c.Query<OldFace>("SELECT id, x1, y1, x2, y2, person_id, assigned_by FROM faces WHERE asset_id=@assetId AND (person_id IS NOT NULL OR assigned_by IS NOT NULL)",
            new { assetId }, tx).ToList();
        var oldRotation = c.ExecuteScalar<int?>("SELECT rotation FROM face_scans WHERE asset_id=@assetId", new { assetId }, tx) ?? 0;
        var turn = Rotation.Normalize(rotation - oldRotation);
        var oldBoxes = old.Select(o => RotateBox(((float)o.X1, (float)o.Y1, (float)o.X2, (float)o.Y2), turn)).ToList();
        var taken = new bool[old.Count];
        var now = Clock.NowIso();
        foreach (var f in faces)
        {
            var best = -1;
            var bestIoU = 0.5f;
            for (var i = 0; i < old.Count; i++)
            {
                if (taken[i]) continue;
                var iou = IoU((f.X1, f.Y1, f.X2, f.Y2), oldBoxes[i]);
                if (iou > bestIoU) { bestIoU = iou; best = i; }
            }
            var prior = best >= 0 ? old[best] : null;
            if (best >= 0) taken[best] = true;
            var id = c.ExecuteScalar<long>("""
                INSERT INTO faces(asset_id, model, x1, y1, x2, y2, landmarks, score, eye_px, embedding, person_id, assigned_by, created_at)
                VALUES(@assetId, @model, @x1, @y1, @x2, @y2, @landmarks, @score, @eyePx, @embedding, @personId, @assignedBy, @now);
                SELECT last_insert_rowid();
                """, new { assetId, model, x1 = f.X1, y1 = f.Y1, x2 = f.X2, y2 = f.Y2, landmarks = FloatBytes(f.Landmarks), score = f.Score, eyePx = f.EyePx,
                embedding = HalfBytes(f.Embedding), personId = prior?.PersonId, assignedBy = prior?.AssignedBy, now }, tx);
            if (prior is not null)
            {
                c.Execute("INSERT OR IGNORE INTO face_rejections(face_id, person_id) SELECT @id, person_id FROM face_rejections WHERE face_id=@old", new { id, old = prior.Id }, tx);
                c.Execute("UPDATE people SET cover_face_id=@id WHERE cover_face_id=@old", new { id, old = prior.Id }, tx);
            }
        }
        c.Execute("DELETE FROM faces WHERE id IN @oldIds", new { oldIds }, tx); // their rejections are deleted with them (copied above)
        Record(c, tx, assetId, model, rotation, faces.Count, "done", null, now, squeeze);
        tx.Commit();
    }

    /// <summary>Returns a box (fractions of a picture) after the picture is turned clockwise by <paramref name="degrees"/>.</summary>
    public static (float X1, float Y1, float X2, float Y2) RotateBox((float X1, float Y1, float X2, float Y2) b, int degrees) => Rotation.Normalize(degrees) switch
    {
        90 => (1 - b.Y2, b.X1, 1 - b.Y1, b.X2),
        180 => (1 - b.X2, 1 - b.Y2, 1 - b.X1, 1 - b.Y1),
        270 => (b.Y1, 1 - b.X2, b.Y2, 1 - b.X1),
        _ => b,
    };

    public static float IoU((float X1, float Y1, float X2, float Y2) a, (float X1, float Y1, float X2, float Y2) b)
    {
        var iw = Math.Max(0, Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1));
        var ih = Math.Max(0, Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1));
        var inter = iw * ih;
        var union = (a.X2 - a.X1) * (a.Y2 - a.Y1) + (b.X2 - b.X1) * (b.Y2 - b.Y1) - inter;
        return union <= 0 ? 0 : inter / union;
    }

    /// <summary>Records that the file could not be searched, so sweeps skip it until asked.</summary>
    public void SaveFailed(long assetId, string model, int rotation, string error, double squeeze = 1)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        Record(c, tx, assetId, model, rotation, 0, "failed", error, Clock.NowIso(), squeeze);
        tx.Commit();
    }

    private static void Record(Microsoft.Data.Sqlite.SqliteConnection c, System.Data.IDbTransaction tx, long assetId, string model, int rotation, int count,
        string status, string? error, string now, double squeeze) =>
        c.Execute("""
            INSERT INTO face_scans(asset_id, model, rotation, squeeze, face_count, status, error, scanned_at)
            VALUES(@assetId, @model, @rotation, @squeeze, @count, @status, @error, @now)
            ON CONFLICT(asset_id) DO UPDATE SET model=excluded.model, rotation=excluded.rotation, squeeze=excluded.squeeze,
                face_count=excluded.face_count, status=excluded.status, error=excluded.error, scanned_at=excluded.scanned_at
            """, new { assetId, model, rotation, squeeze = Squeeze.Normalize(squeeze), count, status, error, now }, tx);

    /// <summary>Clears searches so the next sweep does them again: only failed ones, or all (faces stay until searched again).</summary>
    public int ForgetScans(bool failedOnly)
    {
        using var c = _db.Open();
        return c.Execute(failedOnly ? "DELETE FROM face_scans WHERE status='failed'" : "DELETE FROM face_scans");
    }

    /// <summary>Makes the next sweep search these files again. Their faces stay until then, and their names carry over.</summary>
    public int RedoScans(IReadOnlyCollection<long> assetIds)
    {
        if (assetIds.Count == 0) return 0;
        using var c = _db.Open();
        return c.Execute("UPDATE face_scans SET model='' WHERE asset_id IN @assetIds", new { assetIds });
    }

    public List<StoredFace> ForAsset(long assetId)
    {
        using var c = _db.Open();
        return c.Query<FaceRow>("SELECT * FROM faces WHERE asset_id=@assetId ORDER BY x1", new { assetId }).Select(r => r.ToFace()).ToList();
    }

    private sealed class FaceRow
    {
        public long Id { get; set; }
        public long AssetId { get; set; }
        public string Model { get; set; } = "";
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
        public byte[] Landmarks { get; set; } = [];
        public double Score { get; set; }
        public double EyePx { get; set; }
        public byte[] Embedding { get; set; } = [];
        public long? PersonId { get; set; }
        public string? AssignedBy { get; set; }

        public StoredFace ToFace() => new(Id, AssetId, Model, (float)X1, (float)Y1, (float)X2, (float)Y2, MemoryMarshal.Cast<byte, float>(Landmarks).ToArray(),
            (float)Score, (float)EyePx, FromHalfBytes(Embedding), PersonId, AssignedBy);
    }

    private static byte[] FloatBytes(float[] v) => MemoryMarshal.AsBytes(v.AsSpan()).ToArray();

    internal static byte[] HalfBytes(float[] v)
    {
        var h = new Half[v.Length];
        for (var i = 0; i < v.Length; i++) h[i] = (Half)v[i];
        return MemoryMarshal.AsBytes(h.AsSpan()).ToArray();
    }

    internal static float[] FromHalfBytes(byte[] b)
    {
        var h = MemoryMarshal.Cast<byte, Half>(b);
        var v = new float[h.Length];
        for (var i = 0; i < h.Length; i++) v[i] = (float)h[i];
        return v;
    }
}
