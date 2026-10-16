using System.Collections.Concurrent;
using Dapper;
using Gallery.Core.Data;
using Gallery.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Gallery.Core.Services.Faces;

/// <summary>
/// Square face pictures for the screens: the box enlarged 1.6 times around its centre, cut from the picture the face was
/// found in (upright, at its search rotation). Made on demand and cached under {DataDir}/thumbs/faces. The cache name
/// includes the file's hash and the box, so a face found again elsewhere gets a new picture.
/// </summary>
public sealed class FaceCrops
{
    public static readonly int[] Sizes = { 96, 160, 320 };
    private const float Margin = 1.6f;

    private readonly Db _db;
    private readonly ThumbnailService _thumbs;
    private readonly SettingsService _settings;
    private readonly ILogger<FaceCrops> _log;
    private readonly SemaphoreSlim _gate = new(Math.Max(2, Environment.ProcessorCount / 2));
    private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> _inflight = new();

    public FaceCrops(Db db, ThumbnailService thumbs, SettingsService settings, ILogger<FaceCrops> log)
    {
        _db = db;
        _thumbs = thumbs;
        _settings = settings;
        _log = log;
    }

    public static int NormalizeSize(int requested)
    {
        foreach (var s in Sizes) if (requested <= s) return s;
        return Sizes[^1];
    }

    private sealed class Row
    {
        public long Id { get; set; }
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
        public int? ScannedRotation { get; set; }
        public double? ScannedSqueeze { get; set; }
    }

    /// <summary>The cached picture of a face (made when needed), or null when the face or its photo is gone.</summary>
    public async Task<string?> GetAsync(long faceId, int size, CancellationToken ct = default)
    {
        size = NormalizeSize(size);
        Row? face;
        Asset? asset;
        using (var c = _db.Open())
        {
            face = c.QuerySingleOrDefault<Row>("""
                SELECT f.id, f.x1, f.y1, f.x2, f.y2, fs.rotation AS scanned_rotation, fs.squeeze AS scanned_squeeze
                FROM faces f LEFT JOIN face_scans fs ON fs.asset_id=f.asset_id WHERE f.id=@faceId
                """, new { faceId });
            asset = face is null ? null : c.QuerySingleOrDefault<Asset>("SELECT a.* FROM assets a JOIN faces f ON f.asset_id=a.id WHERE f.id=@faceId", new { faceId });
        }
        if (face is null || asset is null) return null;
        var rotation = face.ScannedRotation ?? 0;
        var squeeze = Squeeze.Normalize(face.ScannedSqueeze ?? 1);
        var key = $"{asset.Hash}_r{rotation}{(squeeze == 1 ? "" : $"_a{Squeeze.Format(squeeze)}")}_{(int)(face.X1 * 1e4)}_{(int)(face.Y1 * 1e4)}_{(int)(face.X2 * 1e4)}_{(int)(face.Y2 * 1e4)}_{size}";
        var path = Path.Combine(_settings.ThumbsDir, "faces", asset.Hash[..2], key + ".jpg");
        if (File.Exists(path)) return path;
        var lazy = _inflight.GetOrAdd(key, k => new Lazy<Task<bool>>(async () =>
        {
            try
            {
                await _gate.WaitAsync(CancellationToken.None);
                try { return await RenderAsync(asset, face, rotation, squeeze, size, path); }
                finally { _gate.Release(); }
            }
            finally { _inflight.TryRemove(key, out _); }
        }));
        return await lazy.Value.WaitAsync(ct) && File.Exists(path) ? path : null;
    }

    private async Task<bool> RenderAsync(Asset asset, Row face, int rotation, double squeeze, int size, string path)
    {
        try
        {
            // The 1024 preview when the face is large enough in it, otherwise the 2048 rendering.
            var side1024 = (float)Math.Max(face.X2 - face.X1, face.Y2 - face.Y1) * FaceService.PreviewSize * Margin;
            var renderSize = side1024 >= size ? FaceService.PreviewSize : ThumbnailService.AllowedSizes[^1];
            using var img = await _thumbs.RenderAsync(asset, renderSize, rotation, squeeze);
            if (img is null) return false;
            float w = img.Width, h = img.Height;
            var cx = (float)(face.X1 + face.X2) / 2 * w;
            var cy = (float)(face.Y1 + face.Y2) / 2 * h;
            var side = Math.Min(Math.Min(w, h), Math.Max((float)(face.X2 - face.X1) * w, (float)(face.Y2 - face.Y1) * h) * Margin);
            var x = Math.Clamp(cx - side / 2, 0, w - side);
            var y = Math.Clamp(cy - side / 2, 0, h - side);
            var rect = new Rectangle((int)x, (int)y, Math.Max(1, (int)side), Math.Max(1, (int)side));
            img.Mutate(m => m.Crop(rect).Resize(size, size, KnownResamplers.Bicubic));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp" + Guid.NewGuid().ToString("N")[..6];
            await img.SaveAsJpegAsync(tmp, new JpegEncoder { Quality = 85 });
            File.Move(tmp, path, true);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "No face picture for face #{Id} in {Path}", face.Id, asset.Path);
            return false;
        }
    }
}
