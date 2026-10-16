using Gallery.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Gallery.Web.Endpoints;

public static class AlbumEndpoints
{
    public record CreateAlbumRequest(string Name, List<long>? ItemIds);
    public record RenameRequest(string Name);
    public record ItemsRequest(List<long> ItemIds);
    /// <summary>The item to show for the album, or null for the automatic one.</summary>
    public record CoverRequest(long? ItemId);
    public record AutoRequest(int? GapDays);
    public record HideRequest(bool Hidden);

    public static void Map(IEndpointRouteBuilder app)
    {
        var view = app.MapGroup("/api/albums").RequireAuthorization("Viewer");
        view.MapGet("/", (HttpContext ctx, AlbumService albums) => Results.Ok(albums.All(ctx.IsViewer())));
        view.MapGet("/{id:long}", (long id, HttpContext ctx, AlbumService albums) => albums.Get(id, ctx.IsViewer()) is { } al ? Results.Ok(al) : Results.NotFound());
        view.MapGet("/{id:long}/cover", (long id, int? size, HttpContext ctx, AlbumService albums) =>
        {
            var al = albums.All(ctx.IsViewer()).FirstOrDefault(a => a.Id == id);
            return al is { CoverAssetId: { } aid, CoverHash: { } hash }
                ? Results.Redirect($"/api/assets/{aid}/thumb?size={size ?? 256}&v={Rotation.Version(hash, al.CoverRotation, al.CoverSqueeze)}")
                : Results.NotFound();
        });

        var admin = app.MapGroup("/api/albums").RequireAuthorization("Admin");
        admin.MapPost("/", (CreateAlbumRequest req, AlbumService albums) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest("name required");
            var id = albums.Create(req.Name.Trim());
            if (req.ItemIds is { Count: > 0 }) albums.AddItems(id, req.ItemIds);
            return Results.Ok(albums.Get(id));
        });
        admin.MapPut("/{id:long}", (long id, RenameRequest req, AlbumService albums) => { albums.Rename(id, req.Name.Trim()); return Results.Ok(); });
        admin.MapPut("/{id:long}/cover", (long id, CoverRequest req, AlbumService albums) =>
            albums.SetCover(id, req.ItemId) ? Results.Ok() : Results.BadRequest("the cover must be a photo in the album"));
        admin.MapDelete("/{id:long}", (long id, AlbumService albums) => { albums.Delete(id); return Results.Ok(); });
        // Hide an album, and everything in it, from viewers, or show it again. Admins always see it.
        admin.MapPut("/{id:long}/hidden", (long id, HideRequest req, AlbumService albums) => albums.SetHiddenFromViewers(id, req.Hidden) ? Results.Ok() : Results.NotFound());
        admin.MapPost("/{id:long}/items", (long id, ItemsRequest req, AlbumService albums) => Results.Ok(new { added = albums.AddItems(id, req.ItemIds) }));
        admin.MapDelete("/{id:long}/items", (long id, [FromBody] ItemsRequest req, AlbumService albums) => Results.Ok(new { removed = albums.RemoveItems(id, req.ItemIds) }));
        admin.MapPost("/auto", (AutoRequest? req, AlbumService albums) => Results.Ok(albums.BuildAutoAlbums(req?.GapDays ?? 3)));
        // Delete every auto-album and make them again from the folders; manual albums stay as they are.
        admin.MapPost("/auto/regenerate", (AutoRequest? req, AlbumService albums) => Results.Ok(albums.RegenerateAutoAlbums(req?.GapDays ?? 3)));
        // Remove the albums with 0 items (never filled, or all their files deleted).
        admin.MapPost("/prune", (AlbumService albums) =>
        {
            var names = albums.PruneEmpty();
            return Results.Ok(new { removed = names.Count, names });
        });
    }
}
