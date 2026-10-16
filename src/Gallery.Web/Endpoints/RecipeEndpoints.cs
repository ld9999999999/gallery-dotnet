using System.Text.Json;
using Gallery.Core.Services;
using Gallery.Core.Services.Develop;
using Gallery.Develop;

namespace Gallery.Web.Endpoints;

/// <summary>Recipes: named editor settings applied to many photos at once (admin only). See docs/raw-editor.md.</summary>
public static class RecipeEndpoints
{
    public sealed record CreateRecipe(string Name, JsonElement Settings, List<string> Sections);
    public sealed record RenameRecipe(string Name);
    public sealed record ApplyRecipe(List<long> ItemIds, bool IncludeNonRaw, ExportOptions? Export);
    public sealed record ItemsRequest(List<long> ItemIds);
    public sealed record AssetsRequest(List<long> AssetIds);

    private static object Json(Recipe r) => new
    {
        r.Id, r.Name, sections = RecipeService.SectionList(r.Sections), r.UpdatedAt,
        settings = JsonDocument.Parse(r.Settings.ToJson()).RootElement,
    };

    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/recipes").RequireAuthorization("Admin");
        g.MapGet("/", (RecipeService recipes) => Results.Ok(recipes.All().Select(Json)));
        g.MapPost("/", (CreateRecipe req, RecipeService recipes) =>
        {
            try { return Results.Ok(Json(recipes.Create(req.Name, DevelopSettings.Parse(req.Settings.GetRawText()), RecipeService.ParseSections(req.Sections)))); }
            catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
        });
        g.MapPut("/{id:long}", (long id, RenameRecipe req, RecipeService recipes) =>
        {
            try { return recipes.Rename(id, req.Name) is { } r ? Results.Ok(Json(r)) : Results.NotFound(); }
            catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
        });
        g.MapDelete("/{id:long}", (long id, RecipeService recipes) => recipes.Delete(id) ? Results.Ok() : Results.NotFound());

        // How many of the selected items a recipe would apply to: those with a RAW, and the rest (photos without one).
        g.MapPost("/targets", (ItemsRequest req, RecipeService recipes) =>
        {
            var raw = recipes.Targets(req.ItemIds, includeNonRaw: false).Targets.Count;
            var all = recipes.Targets(req.ItemIds, includeNonRaw: true).Targets.Count;
            return Results.Ok(new { raw, other = all - raw, selected = req.ItemIds.Distinct().Count() });
        });
        g.MapPost("/{id:long}/apply", (long id, ApplyRecipe req, RecipeService recipes) =>
            recipes.Get(id) is { } r ? Results.Ok(recipes.Apply(r, req.ItemIds, req.IncludeNonRaw, req.Export)) : Results.NotFound());

        // Undo the last save of each file (after applying a recipe to a selection).
        app.MapPost("/api/edits/revert", (AssetsRequest req, EditService edits) =>
            Results.Ok(new { reverted = req.AssetIds.Distinct().Count(edits.Revert) })).RequireAuthorization("Admin");
    }
}
