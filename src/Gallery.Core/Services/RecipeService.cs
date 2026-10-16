using Dapper;
using Gallery.Core.Data;
using Gallery.Core.Models;
using Gallery.Core.Services.Develop;
using Gallery.Develop;
using Microsoft.Data.Sqlite;

namespace Gallery.Core.Services;

public sealed record Recipe(long Id, string Name, DevelopSettings Settings, DevelopSections Sections, string UpdatedAt);

/// <summary>The result of applying a recipe to a selection.</summary>
public sealed record RecipeResult(int Applied, int Skipped, List<long> AssetIds, List<string> ExportJobs);

/// <summary>
/// Recipes: named settings, saved with the sections (white balance, tone, colour, curves, detail) they cover. Applying
/// one replaces just those sections of each photo's edit and keeps the previous edit so the change can be undone.
/// </summary>
public sealed class RecipeService
{
    private readonly Db _db;
    private readonly EditService _edits;
    private readonly ExportService _exports;

    public RecipeService(Db db, EditService edits, ExportService exports)
    {
        _db = db;
        _edits = edits;
        _exports = exports;
    }

    private sealed class Row
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string Params { get; set; } = "";
        public string Sections { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
    }

    private static Recipe ToRecipe(Row r) => new(r.Id, r.Name, DevelopSettings.Parse(r.Params), ParseSections(r.Sections), r.UpdatedAt);

    /// <summary>Section names as used in the API and storage: whiteBalance, tone, color, curves, detail.</summary>
    public static readonly IReadOnlyDictionary<string, DevelopSections> SectionNames = new Dictionary<string, DevelopSections>(StringComparer.OrdinalIgnoreCase)
    {
        ["whiteBalance"] = DevelopSections.WhiteBalance, ["tone"] = DevelopSections.Tone, ["color"] = DevelopSections.Color,
        ["curves"] = DevelopSections.Curves, ["detail"] = DevelopSections.Detail,
    };

    public static DevelopSections ParseSections(IEnumerable<string>? names) =>
        (names ?? []).Aggregate(DevelopSections.None, (all, n) => all | (SectionNames.TryGetValue(n.Trim(), out var s) ? s : DevelopSections.None));

    private static DevelopSections ParseSections(string stored) => ParseSections(stored.Split(',', StringSplitOptions.RemoveEmptyEntries));

    public static List<string> SectionList(DevelopSections sections) => SectionNames.Where(kv => sections.HasFlag(kv.Value)).Select(kv => kv.Key).ToList();

    public List<Recipe> All()
    {
        using var c = _db.Open();
        return c.Query<Row>("SELECT * FROM recipes ORDER BY name COLLATE NOCASE").Select(ToRecipe).ToList();
    }

    public Recipe? Get(long id)
    {
        using var c = _db.Open();
        return c.QuerySingleOrDefault<Row>("SELECT * FROM recipes WHERE id=@id", new { id }) is { } r ? ToRecipe(r) : null;
    }

    /// <summary>Saves a new recipe. Throws <see cref="ArgumentException"/> for an empty or taken name, or when no sections are chosen.</summary>
    public Recipe Create(string name, DevelopSettings settings, DevelopSections sections)
    {
        name = Check(name, sections);
        using var c = _db.Open();
        try
        {
            var id = c.ExecuteScalar<long>("""
                INSERT INTO recipes(name, params, sections, created_at, updated_at) VALUES(@name, @json, @sections, @now, @now);
                SELECT last_insert_rowid();
                """, new { name, json = settings.Clamped().ToJson(), sections = string.Join(',', SectionList(sections)), now = Clock.NowIso() });
            return Get(id)!;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new ArgumentException($"There is already a recipe called \"{name}\""); }
    }

    public Recipe? Rename(long id, string name)
    {
        name = Check(name, DevelopSections.All);
        using var c = _db.Open();
        try { c.Execute("UPDATE recipes SET name=@name, updated_at=@now WHERE id=@id", new { id, name, now = Clock.NowIso() }); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new ArgumentException($"There is already a recipe called \"{name}\""); }
        return Get(id);
    }

    public bool Delete(long id)
    {
        using var c = _db.Open();
        return c.Execute("DELETE FROM recipes WHERE id=@id", new { id }) > 0;
    }

    private static string Check(string name, DevelopSections sections)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new ArgumentException("Give the recipe a name");
        if (name.Length > 100) throw new ArgumentException("The name is too long");
        if (sections == DevelopSections.None) throw new ArgumentException("Choose at least one part of the edit to keep in the recipe");
        return name;
    }

    /// <summary>
    /// The file of each item a recipe applies to: its RAW if it has one, otherwise its main photo (only with
    /// <paramref name="includeNonRaw"/>). Items with neither (videos, audio, missing files) are skipped.
    /// </summary>
    public (List<Asset> Targets, int Skipped) Targets(IEnumerable<long> itemIds, bool includeNonRaw)
    {
        using var c = _db.Open();
        var targets = new List<Asset>();
        var skipped = 0;
        foreach (var itemId in itemIds.Distinct())
        {
            var assets = c.Query<Asset>("SELECT * FROM assets WHERE item_id=@itemId AND missing=0 AND kind='image' AND edit_of IS NULL ORDER BY size DESC", new { itemId }).ToList();
            var raw = assets.FirstOrDefault(a => a.IsRaw == 1);
            var primaryId = c.ExecuteScalar<long?>("SELECT primary_asset_id FROM items WHERE id=@itemId", new { itemId });
            var pick = raw ?? (includeNonRaw ? assets.FirstOrDefault(a => a.Id == primaryId) ?? assets.FirstOrDefault() : null);
            if (pick is null) skipped++; else targets.Add(pick);
        }
        return (targets, skipped);
    }

    /// <summary>Applies a recipe to the items' files (see <see cref="Targets"/>) and queues exports if <paramref name="export"/> is given.</summary>
    public RecipeResult Apply(Recipe recipe, IEnumerable<long> itemIds, bool includeNonRaw, ExportOptions? export)
    {
        var (targets, skipped) = Targets(itemIds, includeNonRaw);
        var jobs = new List<string>();
        foreach (var a in targets)
        {
            var current = _edits.Get(a.Id)?.Settings ?? DevelopSettings.DefaultFor(a.IsRaw == 1);
            var edited = _edits.Save(a.Id, current.With(recipe.Settings, recipe.Sections), recipe.Id);
            if (export is not null) jobs.Add(_exports.Queue(a.Id, edited.Settings, export).Id);
        }
        return new RecipeResult(targets.Count, skipped, targets.Select(a => a.Id).ToList(), jobs);
    }
}
