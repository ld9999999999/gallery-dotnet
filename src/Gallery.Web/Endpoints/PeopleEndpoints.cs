using Gallery.Core.Services.Faces;

namespace Gallery.Web.Endpoints;

/// <summary>People and their faces (docs/faces.md). Viewers browse named people they may see; only admins can make fixes.</summary>
public static class PeopleEndpoints
{
    public record PersonUpdate(string? Name, bool? Hidden, long? CoverFaceId);
    public record MergeRequest(long Into);
    public record AssignRequest(List<long> FaceIds, long? PersonId, string? Name);
    public record FacesRequest(List<long> FaceIds);

    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api").RequireAuthorization("Viewer");

        g.MapGet("/people", (HttpContext ctx, PeopleService people, bool? hidden) => Results.Ok(people.People(ctx.IsViewer(), hidden == true)));
        g.MapGet("/people/{id:long}", (long id, HttpContext ctx, PeopleService people) =>
            people.Person(id, ctx.IsViewer()) is { } p ? Results.Ok(p) : Results.NotFound());
        g.MapGet("/items/{id:long}/faces", (long id, HttpContext ctx, PeopleService people) =>
            people.ItemFaces(id, ctx.IsViewer()) is { } faces ? Results.Ok(faces) : Results.NotFound());
        // A face's picture. Like thumbnails, it returns "not found" when the viewer may not see its photo.
        g.MapGet("/faces/{id:long}/crop", async (long id, int? size, HttpContext ctx, PeopleService people, FaceCrops crops, CancellationToken ct) =>
        {
            if (people.Face(id, ctx.IsViewer()) is null) return Results.NotFound();
            var path = await crops.GetAsync(id, size ?? 160, ct);
            if (path is null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.File(path, "image/jpeg");
        });

        var admin = app.MapGroup("/api").RequireAuthorization("Admin");

        admin.MapPut("/people/{id:long}", (long id, PersonUpdate req, PeopleService people) =>
        {
            try
            {
                if (req.Name is not null) people.Rename(id, req.Name);
                if (req.Hidden is { } hidden) people.SetHidden(id, hidden);
                if (req.CoverFaceId is { } face) people.SetCover(id, face);
                return Results.Ok();
            }
            catch (PersonExistsException ex) { return Results.Conflict(new { message = ex.Message, existingId = ex.ExistingId }); }
            catch (KeyNotFoundException ex) { return Results.NotFound(ex.Message); }
        });
        admin.MapDelete("/people/{id:long}", (long id, PeopleService people) =>
        {
            try { people.Delete(id); return Results.Ok(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });
        admin.MapPost("/people/{id:long}/merge", (long id, MergeRequest req, PeopleService people) =>
        {
            try { people.Merge(id, req.Into); return Results.Ok(); }
            catch (KeyNotFoundException ex) { return Results.NotFound(ex.Message); }
        });
        admin.MapGet("/people/{id:long}/faces", (long id, int? offset, int? limit, PeopleService people) =>
        {
            var (faces, total) = people.FacesOf(id, offset ?? 0, limit ?? 200);
            return Results.Ok(new { faces, total });
        });
        admin.MapPost("/faces/assign", (AssignRequest req, PeopleService people) =>
        {
            if (req.FaceIds is not { Count: > 0 }) return Results.BadRequest("faceIds required");
            try { return Results.Ok(new { personId = people.Assign(req.FaceIds, req.PersonId, req.Name) }); }
            catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
            catch (KeyNotFoundException ex) { return Results.NotFound(ex.Message); }
        });
        admin.MapPost("/faces/unassign", (FacesRequest req, PeopleService people) =>
            req.FaceIds is not { Count: > 0 } ? Results.BadRequest("faceIds required") : Results.Ok(new { changed = people.Unassign(req.FaceIds) }));
        admin.MapGet("/faces/unassigned", (int? offset, int? limit, bool? all, PeopleService people) =>
        {
            var (faces, total) = people.Unassigned(offset ?? 0, limit ?? 100, all == true);
            return Results.Ok(new { faces, total });
        });
        admin.MapPost("/admin/faces/group", (PeopleService people) => { people.RequestPass(); return Results.Ok(); });
    }
}
