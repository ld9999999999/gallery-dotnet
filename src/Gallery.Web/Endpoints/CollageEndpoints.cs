using Gallery.Core.Services;
using SixLabors.ImageSharp;

namespace Gallery.Web.Endpoints;

/// <summary>Collages of up to nine selected photos: the layout for the preview, and the finished JPEG.</summary>
public static class CollageEndpoints
{
    /// <param name="ItemIds">The selected items, in the order picked; only photos are used.</param>
    /// <param name="Width">The picture's width in pixels; its height follows from the layout.</param>
    /// <param name="Gap">Space between photos and around the edge, in pixels; 0 for none.</param>
    /// <param name="Border">The frame around each photo, in pixels; 0 for none.</param>
    /// <param name="Variant">How many times "Rearrange" was pressed: 0 is the best layout, and the layouts repeat in a cycle.</param>
    /// <param name="Background">The colour between photos (#rrggbb), when they are spaced.</param>
    /// <param name="BorderColour">The colour of the frames (#rrggbb).</param>
    public record CollageRequest(List<long> ItemIds, int Width, int Gap = 0, int Border = 0, int Variant = 0,
        string? Background = null, string? BorderColour = null);

    private const int MinWidth = 100;
    private const int MostSpace = 2000;

    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/collage").RequireAuthorization("Viewer");

        g.MapPost("/layout", async (CollageRequest req, CollageService collages, HttpContext ctx, CancellationToken ct) =>
        {
            var (photos, layout, count, variant, error) = await PlanAsync(req, collages, ctx, ct);
            if (error is not null) return Results.BadRequest(error);
            return Results.Ok(new
            {
                width = layout!.Width,
                height = layout.Height,
                variant,
                variants = count,
                leftOut = req.ItemIds.Distinct().Count() - photos.Count,
                maxPixels = Collage.MaxPixels,
                tiles = layout.Tiles.Select(t =>
                {
                    var p = photos[t.Photo];
                    return new
                    {
                        itemId = p.Item.Id, assetId = p.Asset.Id, hash = p.Asset.Hash, rotation = p.Item.Rotation, squeeze = p.Item.Squeeze,
                        x = t.X, y = t.Y, width = t.Width, height = t.Height,
                    };
                }),
            });
        });

        g.MapPost("", async (CollageRequest req, CollageService collages, HttpContext ctx, CancellationToken ct) =>
        {
            var (photos, layout, _, _, error) = await PlanAsync(req, collages, ctx, ct);
            if (error is not null) return Results.BadRequest(error);
            if ((long)layout!.Width * layout.Height > Collage.MaxPixels)
                return Results.BadRequest($"That would be {layout.Width} × {layout.Height} px, more than {Collage.MaxPixels / 1_000_000} megapixels. Make it narrower.");
            Color background = Color.White, frame = Color.White;
            if (req.Background is not null && !Color.TryParseHex(req.Background, out background)) return Results.BadRequest("The background colour should be #rrggbb");
            if (req.BorderColour is not null && !Color.TryParseHex(req.BorderColour, out frame)) return Results.BadRequest("The border colour should be #rrggbb");
            try
            {
                using var img = await collages.RenderAsync(photos, layout, Math.Max(0, req.Border), background, frame, ct);
                var jpeg = new MemoryStream();
                await CollageService.SaveJpegAsync(img, jpeg, ct);
                jpeg.Position = 0;
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.File(jpeg, "image/jpeg", $"collage-{DateTime.Now:yyyy-MM-dd-HHmmss}.jpg");
            }
            catch (CollageException ex) { return Results.BadRequest(ex.Message); }
        });
    }

    /// <summary>The photos and the layout asked for, or what is wrong with the request.</summary>
    private static async Task<(List<CollageService.Photo> Photos, Collage.Layout? Layout, int Count, int Variant, string? Error)> PlanAsync(
        CollageRequest req, CollageService collages, HttpContext ctx, CancellationToken ct)
    {
        if (req.ItemIds is not { Count: > 0 }) return ([], null, 0, 0, "Select some photos first");
        if (req.Width is < MinWidth or > Collage.MaxWidth) return ([], null, 0, 0, $"The width should be {MinWidth} to {Collage.MaxWidth} px");
        if (req.Gap is < 0 or > MostSpace || req.Border is < 0 or > MostSpace) return ([], null, 0, 0, $"Spacing and borders should be 0 to {MostSpace} px");
        // Check this before reading any photo, so a too-large selection costs nothing.
        if (req.ItemIds.Distinct().Count() > 4 * Collage.MaxPhotos) return ([], null, 0, 0, $"A collage holds up to {Collage.MaxPhotos} photos");
        var photos = await collages.PhotosAsync(req.ItemIds, ctx.IsViewer(), ct);
        if (photos.Count == 0) return (photos, null, 0, 0, "None of the selected items is a photo");
        if (photos.Count > Collage.MaxPhotos) return (photos, null, 0, 0, $"A collage holds up to {Collage.MaxPhotos} photos; {photos.Count} are selected");
        var arrangements = collages.Arrange(photos);
        var variant = ((req.Variant % arrangements.Count) + arrangements.Count) % arrangements.Count;
        if (arrangements[variant].At(new Collage.Options(req.Width, req.Gap, req.Border)) is not { } layout)
            return (photos, null, 0, 0, "The spacing and borders leave no room for the photos at this width");
        return (photos, layout, arrangements.Count, variant, null);
    }
}
