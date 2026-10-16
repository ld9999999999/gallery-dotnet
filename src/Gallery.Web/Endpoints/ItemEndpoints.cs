using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Web.Endpoints;

public static class ItemEndpoints
{
    public record TagOp(string Key, string Value);
    public record BulkTagRequest(List<long> ItemIds, List<TagOp>? Add, List<TagOp>? Remove);
    public record PrimaryRequest(long AssetId);
    public record RotateRequest(int By);
    /// <summary>true: show as a 360° photo; false: as a flat photo; null: as detected.</summary>
    public record ProjectionRequest(bool? Sphere);
    public record HideRequest(List<long> ItemIds, bool Hidden);
    public record SqueezeRequest(List<long> ItemIds, double Factor);
    public record YearCoverRequest(long? ItemId);

    public static ItemFilter ParseFilter(HttpRequest r)
    {
        var q = r.Query;
        var f = new ItemFilter
        {
            Q = q["q"],
            Kind = q["kind"],
            Year = int.TryParse(q["year"], out var y) ? y : null,
            From = q["from"],
            To = q["to"],
            AlbumId = long.TryParse(q["album"], out var a) ? a : null,
            DuplicatesOnly = q["dups"] == "1",
            VariationsOnly = q["variations"] == "1",
            Untagged = q["untagged"] == "1",
            ForViewers = r.HttpContext.IsViewer(),
            Sort = q["sort"].FirstOrDefault() ?? "date_desc",
            Offset = int.TryParse(q["offset"], out var o) ? o : 0,
            Limit = int.TryParse(q["limit"], out var l) ? l : 200,
        };
        foreach (var p in q["person"]) if (long.TryParse(p, out var person)) f.PersonIds.Add(person);
        foreach (var t in q["tag"])
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            var idx = t.IndexOf(':');
            if (idx < 0) f.Tags.Add((t, null)); else f.Tags.Add((t[..idx], t[(idx + 1)..]));
        }
        return f;
    }

    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api").RequireAuthorization("Viewer");

        g.MapGet("/items", (HttpRequest req, ItemRepository repo) =>
        {
            var f = ParseFilter(req);
            var (items, total) = repo.Query(f);
            return Results.Ok(new { items, total, offset = f.Offset, limit = f.Limit });
        });

        g.MapGet("/items/ids", (HttpRequest req, ItemRepository repo) => Results.Ok(repo.QueryIds(ParseFilter(req))));
        // Items per month, in the result's date order: the grid's month headings.
        g.MapGet("/items/months", (HttpRequest req, ItemRepository repo) =>
            Results.Ok(repo.MonthCounts(ParseFilter(req)).Select(m => new { month = m.Month, count = m.Count })));

        g.MapGet("/items/years", (HttpContext ctx, ItemRepository repo) => Results.Ok(repo.YearSummaries(ctx.IsViewer()).Select(y => new
        {
            year = y.Year,
            count = y.Count,
            cover = y.Cover is { PrimaryAssetId: { } assetId } c ? new { assetId, itemId = c.Id, hash = c.Hash, rotation = c.Rotation, squeeze = c.Squeeze, chosen = y.Chosen } : null,
        })));

        // Opening an item re-checks its files, so a deleted one is noticed (and the item hidden) right away. A 360° photo
        // saved as two fisheye circles comes with their lens calibration, read from the file, for the viewer to stitch.
        g.MapGet("/items/{id:long}", (long id, HttpContext ctx, ItemRepository repo, FileChangeService changes) =>
        {
            if (ctx.IsViewer() && !repo.IsVisibleToViewers(id)) return Results.NotFound();
            changes.CheckItem(id);
            var d = repo.Get(id, ctx.IsViewer());
            if (d is null) return Results.NotFound();
            // An exported edit covers its original's whole frame, maybe smaller, so the lenses sit where they do in the original.
            (int W, int H)? Frame(AssetDto a) =>
                (a.EditOf is { } of ? d.Assets.FirstOrDefault(x => x.Id == of) ?? a : a) is { Width: { } w, Height: { } h } ? (w, h) : null;
            var assets = d.Assets.Select(a => a.Projection == Projection.DualFisheye && !a.Missing && Frame(a) is var (w, h)
                ? a with { Lenses = DualFisheye.Read(a.Path, w, h) } : a).ToList();
            return Results.Ok(d with { Assets = assets });
        });

        // Library totals plus scan / thumbnail / transcode activity. The UI asks for it often; only admins get file paths.
        g.MapGet("/status", (HttpContext ctx, StatusService status) => Results.Ok(status.Snapshot(includePaths: ctx.User.IsInRole(UserService.AdminRole))));

        g.MapGet("/tags/keys", (HttpContext ctx, ItemRepository repo) => Results.Ok(repo.TagKeys(ctx.IsViewer()).Select(x => new { key = x.Key, count = x.Count })));
        g.MapGet("/tags/values", (string key, HttpContext ctx, ItemRepository repo) =>
            Results.Ok(repo.TagValues(key, ctx.IsViewer()).Select(x => new { value = x.Value, count = x.Count })));

        var admin = app.MapGroup("/api").RequireAuthorization("Admin");

        admin.MapPost("/items/tags", (BulkTagRequest req, ItemRepository repo) =>
        {
            if (req.ItemIds is null || req.ItemIds.Count == 0) return Results.BadRequest("itemIds required");
            var n = repo.SetTags(req.ItemIds,
                (req.Add ?? new()).Select(t => new ItemTag { Key = t.Key, Value = t.Value }),
                (req.Remove ?? new()).Select(t => new ItemTag { Key = t.Key, Value = t.Value }));
            return Results.Ok(new { changed = n });
        });

        // Turn an item by ±90 or 180 degrees on top of its EXIF orientation; stored as the rotate-degrees tag.
        admin.MapPost("/items/{id:long}/rotate", (long id, RotateRequest req, ItemRepository repo) =>
        {
            if (req.By % 90 != 0) return Results.BadRequest("by must be a multiple of 90");
            return repo.RotateItem(id, req.By) is { } rotation ? Results.Ok(new { rotation }) : Results.NotFound();
        });

        // Whether the viewer shows an item as a 360° photo, whatever was detected; stored as the projection tag.
        admin.MapPost("/items/{id:long}/projection", (long id, ProjectionRequest req, ItemRepository repo) =>
            repo.SetProjection(id, req.Sphere) && repo.Get(id) is { } d ? Results.Ok(new { projection = d.Item.Projection }) : Results.NotFound());

        // Hide items from viewers, or show them again. Admins always see them.
        // An anamorphic lens's squeeze for these items: their long side is stretched by it wherever they are shown, edited
        // and exported. 1 removes it.
        admin.MapPost("/items/anamorphic", (SqueezeRequest req, ItemRepository repo) =>
            req.ItemIds is not { Count: > 0 } ? Results.BadRequest("no items") : Results.Ok(new { squeeze = repo.SetSqueeze(req.ItemIds, req.Factor) }));
        // The photo shown for a year in the Years view; itemId null goes back to the year's newest.
        admin.MapPut("/items/years/{year:int}/cover", (int year, YearCoverRequest req, ItemRepository repo) =>
            repo.SetYearCover(year, req.ItemId) ? Results.Ok() : Results.BadRequest($"the cover must be a photo taken in {year}"));
        admin.MapPost("/items/hidden", (HideRequest req, ItemRepository repo) =>
        {
            if (req.ItemIds is null || req.ItemIds.Count == 0) return Results.BadRequest("itemIds required");
            return Results.Ok(new { changed = repo.SetHiddenFromViewers(req.ItemIds, req.Hidden) });
        });

        admin.MapPut("/items/{id:long}/primary", (long id, PrimaryRequest req, ItemRepository repo) =>
        {
            repo.SetPrimaryAsset(id, req.AssetId);
            return Results.Ok();
        });
    }
}
