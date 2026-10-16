using System.Collections.Concurrent;
using System.Globalization;
using Gallery.Core.Models;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Gallery.Core.Services;

/// <summary>Thrown when a collage can't be made. The message is meant for the person making it.</summary>
public sealed class CollageException(string message) : Exception(message);

/// <summary>
/// Collages of photos in the library (<see cref="Collage"/> places them). Each photo appears as the gallery shows it:
/// its item's main file, upright, turned and stretched as set for the item. The picture is drawn from the originals, so
/// it stays sharp at any width.
/// </summary>
public sealed class CollageService
{
    private readonly ItemRepository _items;
    private readonly ThumbnailService _thumbs;
    private readonly ILogger<CollageService> _log;

    /// <summary>Limits drawing to one collage at a time, since a large one takes a few hundred megabytes.</summary>
    private readonly SemaphoreSlim _gate = new(1);

    public CollageService(ItemRepository items, ThumbnailService thumbs, ILogger<CollageService> log)
    {
        _items = items;
        _thumbs = thumbs;
        _log = log;
    }

    /// <param name="Aspect">Width over height, as shown.</param>
    public sealed record Photo(ItemDto Item, Asset Asset, double Aspect);

    /// <summary>
    /// Recent arrangements, keyed by the photos' shapes. A new width, spacing or border, or Rearrange, asks for the same
    /// ones again, and nine photos of different shapes take about a tenth of a second to arrange.
    /// </summary>
    private readonly ConcurrentDictionary<string, IReadOnlyList<Collage.Arrangement>> _arranged = new();

    /// <summary>The arrangements of these photos, in the order Rearrange offers them (<see cref="Collage.Arrange"/>).</summary>
    public IReadOnlyList<Collage.Arrangement> Arrange(IReadOnlyList<Photo> photos)
    {
        var key = string.Join(',', photos.Select(p => p.Aspect.ToString("R", CultureInfo.InvariantCulture)));
        if (_arranged.Count > 32) _arranged.Clear();
        return _arranged.GetOrAdd(key, _ => Collage.Arrange(photos.Select(p => p.Aspect).ToList()));
    }

    /// <summary>
    /// The photos among these items, in the order given, with their shapes. Videos, audio, items a viewer may not see
    /// and unreadable photos are left out.
    /// </summary>
    public async Task<List<Photo>> PhotosAsync(IEnumerable<long> itemIds, bool forViewers, CancellationToken ct = default)
    {
        var ids = itemIds.Distinct().ToList();
        var items = _items.GetItems(ids).ToDictionary(i => i.Id);
        var photos = new List<Photo>();
        foreach (var id in ids)
        {
            if (!items.TryGetValue(id, out var item) || item.Kind != MediaKind.Image || item.PrimaryAssetId is not { } assetId) continue;
            if (forViewers && (item.HiddenFromViewers || item.InHiddenAlbum)) continue;
            if (_items.GetAsset(assetId) is not { } asset) continue;
            if (await AspectAsync(item, asset, ct) is { } aspect) photos.Add(new Photo(item, asset, aspect));
        }
        return photos;
    }

    /// <summary>
    /// The photo's shape as the grid shows it, measured from its thumbnail so EXIF orientation, the item's turn and any
    /// anamorphic stretch are included. The thumbnail's sides are whole pixels, so the file's own size is used instead
    /// when the two agree.
    /// </summary>
    private async Task<double?> AspectAsync(ItemDto item, Asset asset, CancellationToken ct)
    {
        var path = await _thumbs.GetAsync(asset, ThumbnailService.GridSizes[0], item.Rotation, item.Squeeze, ct: ct);
        if (path is null) return null;
        var info = await Image.IdentifyAsync(path, ct);
        var shown = (double)info.Width / info.Height;
        if (asset.Width is > 0 and var w && asset.Height is > 0 and var h)
            foreach (var (x, y) in new[] { (w, h), (h, w) })
            {
                var (sw, sh) = Squeeze.Apply(x, y, item.Squeeze);
                var exact = (double)sw / sh;
                if (Math.Abs(Math.Log(exact / shown)) < 0.01) return exact;
            }
        return shown;
    }

    /// <summary>
    /// Draws the collage: the background, then each photo with its border. Throws <see cref="CollageException"/> if a
    /// photo can't be read.
    /// </summary>
    public async Task<Image<Rgb24>> RenderAsync(IReadOnlyList<Photo> photos, Collage.Layout layout, int border, Color background, Color borderColour, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var canvas = new Image<Rgb24>(layout.Width, layout.Height, background.ToPixel<Rgb24>());
            try
            {
                var frame = borderColour.ToPixel<Rgb24>();
                var drawing = new object();
                // Read a few photos at once, but draw them onto the picture one at a time.
                await Parallel.ForEachAsync(layout.Tiles, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct }, async (tile, token) =>
                {
                    var photo = photos[tile.Photo];
                    using var img = await LoadAsync(photo, Math.Max(tile.Width, tile.Height), token)
                        ?? throw new CollageException($"Could not read {photo.Asset.FileName}");
                    if (img.Width != tile.Width || img.Height != tile.Height)
                        img.Mutate(x => x.Resize(new ResizeOptions { Mode = ResizeMode.Stretch, Size = new Size(tile.Width, tile.Height), Sampler = KnownResamplers.Bicubic }));
                    lock (drawing)
                    {
                        if (border > 0) Fill(canvas, new Rectangle(tile.X - border, tile.Y - border, tile.Width + 2 * border, tile.Height + 2 * border), frame);
                        canvas.Mutate(x => x.DrawImage(img, new Point(tile.X, tile.Y), 1f));
                    }
                });
                return canvas;
            }
            catch
            {
                canvas.Dispose();
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// The photo at about <paramref name="size"/> on its long side. If the original can't be read (gone, or unreadable
    /// at full size), the largest preview stands in, which beats a hole in the picture.
    /// </summary>
    private async Task<Image<Rgb24>?> LoadAsync(Photo photo, int size, CancellationToken ct)
    {
        var img = await _thumbs.RenderAsync(photo.Asset, size, photo.Item.Rotation, photo.Item.Squeeze, ct);
        var largest = ThumbnailService.AllowedSizes[^1];
        if (img is null && size > largest)
        {
            _log.LogInformation("Collage: {Path} could not be read at {Size} px; using its {Largest} px preview", photo.Asset.Path, size, largest);
            img = await _thumbs.RenderAsync(photo.Asset, largest, photo.Item.Rotation, photo.Item.Squeeze, ct);
        }
        return img;
    }

    private static void Fill(Image<Rgb24> canvas, Rectangle r, Rgb24 colour)
    {
        r = Rectangle.Intersect(r, canvas.Bounds);
        if (r.Width <= 0 || r.Height <= 0) return;
        canvas.ProcessPixelRows(rows =>
        {
            for (var y = r.Top; y < r.Bottom; y++) rows.GetRowSpan(y).Slice(r.Left, r.Width).Fill(colour);
        });
    }

    /// <summary>Saves the picture as a high-quality JPEG with full colour detail (4:4:4).</summary>
    public static Task SaveJpegAsync(Image<Rgb24> img, Stream stream, CancellationToken ct = default) =>
        img.SaveAsJpegAsync(stream, new JpegEncoder { Quality = 92, ColorType = JpegEncodingColor.YCbCrRatio444 }, ct);
}
