using System.Collections.Concurrent;
using System.Text.Json;
using Gallery.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Gallery.Core.Services;

/// <summary>
/// How far away things are along the seam of a 360° photo saved as two fisheye circles (<see cref="DualFisheye.EstimateSeam"/>).
/// The viewer uses it to line the lenses up. It's worked out from a 4096 px wide copy of the picture on first request
/// (about a second for an X5 photo, mostly decoding), then saved next to the thumbnails as {hash}_seam{n}.json.
/// </summary>
public sealed class SeamService
{
    /// <summary>Raise this when the estimate changes, so saved ones are recomputed. app.js asks with the same value.</summary>
    public const string Version = "3";

    private const int PictureSize = 4096;

    private readonly SettingsService _settings;
    private readonly ThumbnailService _thumbs;
    private readonly ILogger<SeamService> _log;
    private readonly ConcurrentDictionary<string, Lazy<Task<double[]?>>> _inflight = new();
    private readonly SemaphoreSlim _gate = new(Math.Max(1, Environment.ProcessorCount / 4));

    public SeamService(SettingsService settings, ThumbnailService thumbs, ILogger<SeamService> log)
    {
        _settings = settings;
        _thumbs = thumbs;
        _log = log;
    }

    public string CachePath(string hash) => Path.Combine(_settings.ThumbsDir, hash[..2], $"{hash}_seam{Version}.json");

    /// <summary>The distances (1/metres, <see cref="DualFisheye.SeamPlaces"/> of them), or null if the file has no lens calibration or can't be read.</summary>
    public async Task<double[]?> GetAsync(Asset asset, CancellationToken ct = default)
    {
        var path = CachePath(asset.Hash);
        if (File.Exists(path))
        {
            try { return JsonSerializer.Deserialize<double[]>(await File.ReadAllTextAsync(path, ct)); }
            catch (JsonException) { } // broken file: recompute
        }
        // The work outlives this request, so the result is saved for the next viewer even if this one moves on.
        var lazy = _inflight.GetOrAdd(asset.Hash, key => new Lazy<Task<double[]?>>(async () =>
        {
            try { return await ComputeAsync(asset, path); }
            finally { _inflight.TryRemove(asset.Hash, out _); }
        }));
        return await lazy.Value.WaitAsync(ct);
    }

    private async Task<double[]?> ComputeAsync(Asset asset, string path)
    {
        if (asset.Width is not { } w || asset.Height is not { } h || DualFisheye.Read(asset.Path, w, h) is not { } rig) return null;
        await _gate.WaitAsync();
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            // The calibration matches the picture as the camera saved it, so render without the manual rotation.
            using var picture = await _thumbs.RenderAsync(asset, PictureSize, 0);
            if (picture is null) return null;
            var profile = await Task.Run(() =>
            {
                using var grey = picture.CloneAs<L8>();
                grey.Mutate(x => x.GaussianBlur(1.2f));
                var lum = new float[grey.Width * grey.Height];
                grey.ProcessPixelRows(rows =>
                {
                    for (var y = 0; y < rows.Height; y++)
                    {
                        var row = rows.GetRowSpan(y);
                        for (var x = 0; x < row.Length; x++) lum[y * row.Length + x] = row[x].PackedValue;
                    }
                });
                return DualFisheye.EstimateSeam(rig, lum, grey.Width, grey.Height);
            });
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp" + Guid.NewGuid().ToString("N")[..6];
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(profile));
            File.Move(tmp, path, true);
            _log.LogDebug("Seam distances for {Path} in {Ms} ms: {Profile}", asset.Path, clock.ElapsedMilliseconds,
                string.Join(' ', profile.Select(r => r.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))));
            return profile;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("Could not work out the seam of {Path}: {Error}", asset.Path, ex.Message);
            return null;
        }
        finally { _gate.Release(); }
    }
}
