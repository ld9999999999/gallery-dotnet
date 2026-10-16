using System.Diagnostics;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Web.Endpoints;

public static class AssetEndpoints
{
    /// <summary>A file a viewer asks for whose item is hidden from viewers: answered as if it did not exist.</summary>
    private static bool Hidden(HttpContext ctx, ItemRepository repo, long assetId) => ctx.IsViewer() && !repo.IsAssetVisibleToViewers(assetId);

    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/assets").RequireAuthorization("Viewer");

        // Thumbnail / preview. ?size=256|512|1024|2048 (rounded up to the nearest allowed size); 4096 and 8192 for photos
        // only, for the viewer's zoom and 360° view, which show a part at a time (capped at the photo's own size).
        // ?v= carries the file's content hash: the URL changes whenever the file does, so the browser may cache it forever.
        // Without a matching v (an old page, or a rebuilt index reusing ids) the response is cached only briefly.
        g.MapGet("/{id:long}/thumb", async (long id, int? size, string? v, ItemRepository repo, ThumbnailService thumbs, HttpContext ctx, CancellationToken ct) =>
        {
            var a = repo.GetAsset(id);
            if (a is null || Hidden(ctx, repo, id)) return Results.NotFound();
            var requested = size ?? 256;
            var large = requested > ThumbnailService.AllowedSizes[^1];
            if (large && a.Kind != MediaKind.Image)
                return Results.BadRequest($"Previews larger than {ThumbnailService.AllowedSizes[^1]} px are only made for photos");
            // The admin's manual rotation of the item (after EXIF), and an anamorphic lens's squeeze.
            var (rotation, squeeze) = repo.GetViewForAsset(id);
            var path = await thumbs.GetAsync(a, requested, rotation, squeeze, large, ct);
            if (path is null)
            {
                ctx.Response.Headers.CacheControl = "no-store"; // may succeed later (file came back, ffmpeg installed)
                return Results.Redirect(a.Kind == MediaKind.Audio ? "/img/audio.svg" : "/img/broken.svg");
            }
            ctx.Response.Headers.CacheControl = v == Rotation.Version(a.Hash, rotation, squeeze) ? "private, max-age=31536000, immutable" : "private, no-cache";
            return Results.File(path, "image/jpeg", enableRangeProcessing: false, lastModified: File.GetLastWriteTimeUtc(path));
        });

        // For a 360° photo saved as two fisheye circles: how far away things are along the seam, which the viewer uses to
        // line the two lenses up. Worked out the first time it is asked for. ?v= is the file's hash and the estimate's
        // version, as app.js builds it: with a match the answer may be cached for good.
        g.MapGet("/{id:long}/seam", async (long id, string? v, ItemRepository repo, SeamService seams, HttpContext ctx, CancellationToken ct) =>
        {
            var a = repo.GetAsset(id);
            if (a is null || Hidden(ctx, repo, id) || repo.GetProjectionForAsset(id) != Projection.DualFisheye) return Results.NotFound();
            if (await seams.GetAsync(a, ct) is not { } profile) return Results.NotFound();
            ctx.Response.Headers.CacheControl = v == $"{a.Hash}.s{SeamService.Version}" ? "private, max-age=31536000, immutable" : "private, no-cache";
            return Results.Ok(new { inverseDistance = profile });
        });

        // Original file (range requests enabled so <video>/<audio> can seek).
        g.MapGet("/{id:long}/file", (long id, bool? download, ItemRepository repo, FileChangeService changes, HttpContext ctx) =>
        {
            var a = repo.GetAsset(id);
            if (a is null || Hidden(ctx, repo, id)) return Results.NotFound();
            if (!File.Exists(a.Path)) { changes.CheckItem(a.ItemId); return Results.NotFound(); }
            ctx.Response.Headers.CacheControl = "private, max-age=3600";
            return Results.File(a.Path, FileTypes.MimeFor(a.Ext), download == true ? a.FileName : null, enableRangeProcessing: true);
        });

        // EXIF / container metadata for the info panel, read from the file on demand.
        g.MapGet("/{id:long}/metadata", async (long id, ItemRepository repo, MetadataService meta, HttpContext ctx, CancellationToken ct) =>
        {
            var a = repo.GetAsset(id);
            if (a is null || Hidden(ctx, repo, id) || !File.Exists(a.Path)) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "private, max-age=300";
            return Results.Ok(await meta.ReadDetailsAsync(a, ct));
        });

        g.MapGet("/{id:long}/playinfo", (long id, HttpContext ctx, ItemRepository repo, TranscodeService tc) =>
        {
            var a = repo.GetAsset(id);
            if (a is null || Hidden(ctx, repo, id)) return Results.NotFound();
            if (a.Kind == MediaKind.Image) return Results.BadRequest("not playable");
            return Results.Ok(tc.GetPlayInfo(a));
        });

        // Playable stream: the saved transcode if there is one (seekable), else live ffmpeg output. A file normally sent as it
        // is comes here with convert=true when the browser could not decode it.
        g.MapGet("/{id:long}/stream", async (long id, double? t, bool? convert, ItemRepository repo, TranscodeService tc, Ffmpeg ffmpeg, HttpContext ctx, ILoggerFactory lf) =>
        {
            var a = repo.GetAsset(id);
            if (a is null || Hidden(ctx, repo, id) || !File.Exists(a.Path)) return Results.NotFound();
            var direct = tc.CanPlayDirectly(a);
            if (direct && convert != true) return Results.File(a.Path, FileTypes.MimeFor(a.Ext), enableRangeProcessing: true);

            var persisted = tc.PersistedPath(a);
            if (persisted is not null) return Results.File(persisted, tc.TargetMime(a), enableRangeProcessing: true);
            if (!ffmpeg.IsAvailable) return Results.Problem("ffmpeg is not available for transcoding", statusCode: 503);
            if (direct) tc.QueueFallbackCopy(a);

            var log = lf.CreateLogger("LiveTranscode");
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = tc.TargetMime(a);
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["X-Gallery-Live-Transcode"] = "1";
            using var proc = ffmpeg.StartStreaming(tc.LiveArgs(a, t ?? 0));
            try
            {
                await proc.StandardOutput.BaseStream.CopyToAsync(ctx.Response.Body, 64 * 1024, ctx.RequestAborted);
                await proc.WaitForExitAsync(ctx.RequestAborted);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            finally
            {
                if (!proc.HasExited) { try { proc.Kill(true); } catch { } }
                log.LogDebug("live transcode of {Path} ended", a.Path);
            }
            return Results.Empty;
        });
    }
}
