using System.IO.Compression;
using System.Text.Json;
using Gallery.Core.Services;
using Gallery.Core.Services.Develop;
using Gallery.Develop;

namespace Gallery.Web.Endpoints;

/// <summary>Editing copies for the photo editor (admin only). See docs/raw-editor.md.</summary>
public static class DevelopEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/assets").RequireAuthorization("Admin");

        // What the editor needs first: kind, upright size (after the item's rotation), camera, white balance, sizes offered.
        g.MapGet("/{id:long}/develop", async (long id, ItemRepository repo, DevelopService develop, CancellationToken ct) =>
        {
            var a = repo.GetAsset(id);
            if (a is null) return Results.NotFound();
            var (rotation, squeeze) = repo.GetViewForAsset(id);
            try { return Results.Ok(await develop.GetInfoAsync(a, rotation, squeeze, ct)); }
            catch (DevelopException ex) { return Results.Problem(ex.Message, statusCode: 422); }
        });

        // The editing copy (.gdev), long edge ?size= (1600 or 2560; larger means full size). Sent gzip-compressed, as stored.
        g.MapGet("/{id:long}/develop/source", async (long id, int? size, ItemRepository repo, DevelopService develop, HttpContext ctx, CancellationToken ct) =>
        {
            var a = repo.GetAsset(id);
            if (a is null) return Results.NotFound();
            string path;
            var (rotation, squeeze) = repo.GetViewForAsset(id);
            try { path = await develop.GetSourceAsync(a, size ?? DevelopService.Sizes[^1], rotation, squeeze, ct); }
            catch (DevelopException ex) { return Results.Problem(ex.Message, statusCode: 422); }
            ctx.Response.Headers.CacheControl = "private, no-cache";
            if (AcceptsGzip(ctx))
            {
                ctx.Response.Headers.ContentEncoding = "gzip";
                return Results.File(path, DevelopFile.MediaType, lastModified: File.GetLastWriteTimeUtc(path));
            }
            return Results.Stream(new GZipStream(File.OpenRead(path), CompressionMode.Decompress), DevelopFile.MediaType);
        });

        // Get, save or forget the saved editor settings for a file (defaults when it has none).
        g.MapGet("/{id:long}/edit", (long id, ItemRepository repo, EditService edits) =>
            repo.GetAsset(id) is { } a ? Results.Ok(EditResponse(edits.Get(id), a.IsRaw == 1)) : Results.NotFound());
        g.MapPut("/{id:long}/edit", (long id, JsonElement settings, ItemRepository repo, EditService edits) =>
        {
            if (repo.GetAsset(id) is not { } a) return Results.NotFound();
            if (a.Kind != Gallery.Core.Models.MediaKind.Image) return Results.BadRequest("only photos can be edited");
            DevelopSettings parsed;
            try { parsed = DevelopSettings.Parse(settings.GetRawText()); }
            catch (JsonException ex) { return Results.BadRequest($"settings are not valid: {ex.Message}"); }
            return Results.Ok(EditResponse(edits.Save(id, parsed), a.IsRaw == 1));
        });
        g.MapDelete("/{id:long}/edit", (long id, ItemRepository repo, EditService edits) =>
        {
            if (repo.GetAsset(id) is not { } a) return Results.NotFound();
            edits.Delete(id);
            return Results.Ok(EditResponse(null, a.IsRaw == 1));
        });

        // Delete an edit exported to the gallery, so it can be exported again. Only the editor's own files, never originals.
        g.MapDelete("/{id:long}/file", (long id, ItemRepository repo, ExportService exports) =>
            exports.DeleteEdit(id) is { } itemId ? Results.Ok(new { itemId, primaryAssetId = repo.Get(itemId)?.Item.PrimaryAssetId })
            : repo.GetAsset(id) is null ? Results.NotFound()
            : Results.BadRequest("only a copy exported from the photo editor can be deleted here"));

        // Export to JPEG (queued; one at a time). Settings in the body take the place of the saved edit.
        g.MapPost("/{id:long}/export", (long id, ExportRequest req, ItemRepository repo, EditService edits, ExportService exports, DevelopService develop) =>
        {
            if (repo.GetAsset(id) is not { } a) return Results.NotFound();
            if (develop.WhyNot(a) is { } why) return Results.Problem(why, statusCode: 422);
            var settings = Settings(req.Settings) ?? edits.Get(id)?.Settings ?? DevelopSettings.DefaultFor(a.IsRaw == 1);
            if (!req.Options.SaveToGallery && !req.Options.Download) return Results.BadRequest("choose to save in the gallery, download, or both");
            return Results.Ok(JobResponse(exports.Queue(id, settings, req.Options)));
        });
        g.MapPost("/{id:long}/export/estimate", async (long id, ExportRequest req, ItemRepository repo, EditService edits, ExportService exports, CancellationToken ct) =>
        {
            if (repo.GetAsset(id) is not { } a) return Results.NotFound();
            try
            {
                var (w, h, bytes) = await exports.EstimateAsync(a, Settings(req.Settings) ?? edits.Get(id)?.Settings ?? DevelopSettings.DefaultFor(a.IsRaw == 1), req.Options, ct);
                return Results.Ok(new { width = w, height = h, bytes });
            }
            catch (DevelopException ex) { return Results.Problem(ex.Message, statusCode: 422); }
        });

        // A full-resolution part of the upright picture, for the 100% view. With angle, left, top, right and bottom (the
        // editor's crop, saved or not), it is a part of the cropped and straightened picture, in its own pixels.
        g.MapGet("/{id:long}/develop/crop", async (long id, int x, int y, int w, int h, double? angle, double? left, double? top, double? right, double? bottom,
            ItemRepository repo, DevelopService develop, HttpContext ctx, CancellationToken ct) =>
        {
            var a = repo.GetAsset(id);
            if (a is null) return Results.NotFound();
            if (w < 1 || h < 1 || (long)w * h > 4096L * 4096) return Results.BadRequest("crop size must be between 1×1 and 4096×4096");
            byte[] bytes;
            var (rotation, squeeze) = repo.GetViewForAsset(id);
            var crop = new CropSettings { Angle = angle ?? 0, Left = left ?? 0, Top = top ?? 0, Right = right ?? 1, Bottom = bottom ?? 1 };
            try { bytes = await develop.GetCropAsync(a, rotation, x, y, w, h, squeeze, crop, ct); }
            catch (DevelopException ex) { return Results.Problem(ex.Message, statusCode: 422); }
            ctx.Response.Headers.CacheControl = "private, no-cache";
            if (!AcceptsGzip(ctx)) return Results.Bytes(bytes, DevelopFile.MediaType);
            using var ms = new MemoryStream();
            await using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) await gz.WriteAsync(bytes, ct);
            ctx.Response.Headers.ContentEncoding = "gzip";
            return Results.Bytes(ms.ToArray(), DevelopFile.MediaType);
        });
    }

    public sealed record ExportRequest(JsonElement? Settings, ExportOptions Options);

    private static DevelopSettings? Settings(JsonElement? json) =>
        json is { ValueKind: JsonValueKind.Object } j ? DevelopSettings.Parse(j.GetRawText()) : null;

    private static object JobResponse(ExportJob j) => new
    {
        id = j.Id, assetId = j.AssetId, state = j.State, message = j.Message, width = j.Width, height = j.Height, bytes = j.Bytes,
        resultAssetId = j.ResultAssetId, fileName = j.FileName, download = j.Options.Download && j.State == "done" ? $"/api/exports/{j.Id}/file" : null,
    };

    /// <summary>Export progress and downloads.</summary>
    public static void MapExports(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/exports").RequireAuthorization("Admin");
        g.MapGet("/{id}", (string id, ExportService exports) => exports.Get(id) is { } j ? Results.Ok(JobResponse(j)) : Results.NotFound());
        g.MapGet("/{id}/file", (string id, ExportService exports) =>
            exports.Get(id) is { State: "done", FilePath: { } path } j && File.Exists(path)
                ? Results.File(path, "image/jpeg", j.FileName)
                : Results.NotFound());
    }

    /// <summary>The saved settings (or where this photo starts), and the starting settings for Reset and Before.</summary>
    private static object EditResponse(EditRecord? edit, bool raw) => new
    {
        settings = JsonDocument.Parse((edit?.Settings ?? DevelopSettings.DefaultFor(raw)).ToJson()).RootElement,
        defaults = JsonDocument.Parse(DevelopSettings.DefaultFor(raw).ToJson()).RootElement,
        saved = edit is not null,
        updatedAt = edit?.UpdatedAt,
    };

    private static bool AcceptsGzip(HttpContext ctx) => ctx.Request.Headers.AcceptEncoding.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase);
}
