using Dapper;
using Gallery.Core.Data;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Core.Services.Faces;

namespace Gallery.Web.Endpoints;

public static class AdminEndpoints
{
    public record SourceRequest(string Path);
    public record PasswordRequest(string? Password);
    public record SettingsRequest(Dictionary<string, string> Values);
    public record FaceSettingsRequest(bool? Enabled, string? Pack);
    public record FacePackRequest(string Pack);
    public record FindFacesRequest(bool FailedOnly);

    private static readonly HashSet<string> EditableSettings = new()
    {
        SettingsService.DerivedDir, SettingsService.KeepTranscodes, SettingsService.TranscodeWhenIdle, SettingsService.ViewerPasswordRequired,
        SettingsService.GalleryTitle, SettingsService.ThumbQuality, SettingsService.PregenThumbs,
    };

    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin").RequireAuthorization("Admin");

        g.MapGet("/sources", (SourceService sources) => Results.Ok(sources.All()));
        g.MapPost("/sources", (SourceRequest req, SourceService sources) => sources.Add(req.Path) is { } error ? Results.BadRequest(error) : Results.Ok());
        g.MapDelete("/sources/{id:long}", (long id, SourceService sources) => { sources.Remove(id); return Results.Ok(); });
        g.MapPut("/sources/{id:long}/enabled", (long id, bool enabled, SourceService sources) => { sources.SetEnabled(id, enabled); return Results.Ok(); });

        g.MapPost("/scan", (Scanner scanner) => Results.Ok(new { queued = scanner.RequestScan() }));
        g.MapPost("/scan/cancel", (Scanner scanner) => { scanner.CancelScan(); return Results.Ok(); });
        g.MapGet("/scan/status", (Scanner scanner, Db db) =>
        {
            using var c = db.Open();
            var runs = c.Query<ScanRun>("SELECT * FROM scan_runs ORDER BY id DESC LIMIT 10").ToList();
            lock (scanner.Progress) return Results.Ok(new { progress = scanner.Progress, runs });
        });
        g.MapPost("/rebuild", (Scanner scanner, FaceService faces) =>
        {
            scanner.RebuildItems(CancellationToken.None);
            scanner.QueueMissingThumbnails(); // a rebuild can change an item's main file
            faces.RequestSweep();
            return Results.Ok();
        });
        // Fix items without a main file, then queue every missing thumbnail (even if background thumbnails are off).
        g.MapPost("/thumbnails", async (Scanner scanner, ThumbnailService thumbs, CancellationToken ct) =>
        {
            var repaired = await Task.Run(scanner.RepairIncompleteItems, ct);
            var retried = thumbs.ForgetFailures(); // the user asked, so retry files that failed before
            var queued = await thumbs.SweepAsync(ct);
            return Results.Ok(new { repaired, retried, queued });
        });

        g.MapGet("/settings", (SettingsService s) => Results.Ok(new { values = s.All(), dataDir = s.DataDir, derivedDir = s.GetDerivedDir() }));

        // Show what the server holds in memory, and free what it can (see MemoryReport).
        g.MapGet("/memory", (MemoryReport memory) => Results.Ok(memory.Snapshot()));
        g.MapPost("/memory/free", (MemoryReport memory) =>
        {
            var (before, after, done) = memory.Free();
            return Results.Ok(new { before, after, done });
        });
        g.MapPut("/settings", (SettingsRequest req, SettingsService s) =>
        {
            foreach (var (k, v) in req.Values)
            {
                if (!EditableSettings.Contains(k)) return Results.BadRequest($"unknown setting {k}");
                if (k == SettingsService.DerivedDir && !string.IsNullOrWhiteSpace(v))
                {
                    var dir = GalleryOptions.ExpandHome(v);
                    try { Directory.CreateDirectory(dir); } catch (Exception ex) { return Results.BadRequest($"cannot create {dir}: {ex.Message}"); }
                }
                s.Set(k, v);
            }
            return Results.Ok();
        });

        // Faces (docs/faces.md): settings, model packs and their download, progress, and running the search again.
        g.MapGet("/faces", (FaceService faces, FaceModels models, FaceRepository repo, PeopleService people) =>
        {
            var pack = faces.Pack;
            var counts = repo.Counts(pack.Name);
            return Results.Ok(new
            {
                enabled = faces.Enabled,
                pack = pack.Name,
                activity = faces.Activity with { Grouping = people.Grouping },
                lastPass = people.LastPass,
                backends = faces.Backends,
                modelsDir = models.Dir,
                download = models.Download,
                counts = new { counts.Photos, counts.Done, counts.Failed, counts.Pending, counts.Faces, counts.PhotosWithFaces },
                packs = models.Packs.Select(p => new
                {
                    name = p.Name,
                    description = p.Description,
                    present = models.IsPresent(p),
                    files = new[] { p.Detector.Name, p.Recognizer.Name },
                    filesBytes = p.FilesSize,
                    downloadBytes = p.ZipSize,
                    url = p.ZipUrl,
                }),
            });
        });
        g.MapPut("/faces", (FaceSettingsRequest req, FaceService faces) =>
        {
            try { faces.Configure(req.Enabled, req.Pack); }
            catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
            return Results.Ok();
        });
        g.MapPost("/faces/download", (FacePackRequest req, FaceService faces, FaceModels models) =>
            models.Find(req.Pack) is not { } pack ? Results.BadRequest($"unknown face pack \"{req.Pack}\"")
            : faces.StartDownload(pack) ? Results.Ok() : Results.Conflict("a download is already running"));
        g.MapPost("/faces/find-again", (FindFacesRequest req, FaceService faces) => Results.Ok(new { forgotten = faces.FindAgain(req.FailedOnly) }));

        g.MapGet("/users", (UserService users) => Results.Ok(users.All()));
        g.MapPut("/users/viewer-password", (PasswordRequest req, UserService users, SettingsService s) =>
        {
            users.SetPassword(UserService.ViewerUsername, req.Password, UserService.ViewerRole);
            s.Set(SettingsService.ViewerPasswordRequired, string.IsNullOrEmpty(req.Password) ? "false" : "true");
            return Results.Ok();
        });
        g.MapPut("/users/admin-password", (PasswordRequest req, HttpContext ctx, UserService users) =>
        {
            if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 6) return Results.BadRequest("password must be at least 6 characters");
            users.SetPassword(ctx.User.Identity!.Name!, req.Password, UserService.AdminRole);
            return Results.Ok();
        });

        g.MapGet("/transcodes", (TranscodeService tc) => Results.Ok(tc.ListPersisted().Select(x => new { assetId = x.AssetId, path = x.Path, size = x.Size })));
        g.MapDelete("/transcodes/{assetId:long}", (long assetId, TranscodeService tc) => { tc.DeletePersisted(assetId); return Results.Ok(); });
        g.MapPost("/transcodes/{assetId:long}", (long assetId, ItemRepository repo, TranscodeService tc) =>
        {
            var a = repo.GetAsset(assetId);
            if (a is null) return Results.NotFound();
            tc.QueuePersist(a);
            return Results.Ok();
        });

        g.MapGet("/duplicates", (Db db, int? limit, int? offset) =>
        {
            using var c = db.Open();
            var rows = c.Query("""
                SELECT a.hash, COUNT(*) AS copies, a.size, SUM(a.size) - a.size AS wasted,
                       group_concat(a.path, char(10)) AS paths, MIN(a.item_id) AS item_id, MIN(a.id) AS asset_id
                FROM assets a WHERE a.missing=0
                GROUP BY a.hash HAVING COUNT(*)>1
                ORDER BY wasted DESC LIMIT @limit OFFSET @offset
                """, new { limit = Math.Clamp(limit ?? 100, 1, 1000), offset = offset ?? 0 });
            return Results.Ok(rows);
        });
    }
}
