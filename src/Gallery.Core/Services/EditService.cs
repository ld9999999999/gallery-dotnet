using Dapper;
using Gallery.Core.Data;
using Gallery.Develop;

namespace Gallery.Core.Services;

public sealed record EditRecord(long AssetId, DevelopSettings Settings, DevelopSettings? Previous, long? RecipeId, string UpdatedAt);

/// <summary>Saved photo-editor settings, one edit per file. Originals stay as they are; see docs/raw-editor.md.</summary>
public sealed class EditService
{
    private readonly Db _db;

    public EditService(Db db) => _db = db;

    private sealed class Row
    {
        public long AssetId { get; set; }
        public string Params { get; set; } = "";
        public string? PreviousParams { get; set; }
        public long? RecipeId { get; set; }
        public string UpdatedAt { get; set; } = "";
    }

    private static EditRecord ToRecord(Row r) => new(r.AssetId, DevelopSettings.Parse(r.Params),
        r.PreviousParams is null ? null : DevelopSettings.Parse(r.PreviousParams), r.RecipeId, r.UpdatedAt);

    public EditRecord? Get(long assetId)
    {
        using var c = _db.Open();
        var row = c.QuerySingleOrDefault<Row>("SELECT * FROM edits WHERE asset_id=@assetId", new { assetId });
        return row is null ? null : ToRecord(row);
    }

    /// <summary>Saves the settings, clamped to their ranges, and keeps the ones they replace as the previous version.</summary>
    public EditRecord Save(long assetId, DevelopSettings settings, long? recipeId = null)
    {
        var json = settings.Clamped().ToJson();
        using var c = _db.Open();
        c.Execute("""
            INSERT INTO edits(asset_id, params, previous_params, recipe_id, updated_at) VALUES(@assetId, @json, NULL, @recipeId, @now)
            ON CONFLICT(asset_id) DO UPDATE SET previous_params=edits.params, params=excluded.params, recipe_id=excluded.recipe_id, updated_at=excluded.updated_at
            """, new { assetId, json, recipeId, now = Clock.NowIso() });
        _db.Touch();
        return Get(assetId)!;
    }

    /// <summary>
    /// Undoes the last save (an applied recipe, say) by restoring the previous settings, or removing the edit if there
    /// were none. Returns false if the file has no edit.
    /// </summary>
    public bool Revert(long assetId)
    {
        using var c = _db.Open();
        var n = c.Execute("UPDATE edits SET params=previous_params, previous_params=NULL, recipe_id=NULL, updated_at=@now WHERE asset_id=@assetId AND previous_params IS NOT NULL",
            new { assetId, now = Clock.NowIso() });
        if (n == 0) n = c.Execute("DELETE FROM edits WHERE asset_id=@assetId", new { assetId });
        if (n > 0) _db.Touch();
        return n > 0;
    }

    /// <summary>Removes the edit, so the file shows as shot.</summary>
    public bool Delete(long assetId)
    {
        using var c = _db.Open();
        var n = c.Execute("DELETE FROM edits WHERE asset_id=@assetId", new { assetId });
        if (n > 0) _db.Touch();
        return n > 0;
    }
}
