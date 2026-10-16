using Dapper;
using Gallery.Core.Data;
using Gallery.Core.Models;
using Gallery.Core.Services.Develop;
using Gallery.Core.Services.Faces;

namespace Gallery.Core.Services;

/// <summary>
/// Fixes HEIC photos that were indexed without libheif, the first time the gallery starts with libheif installed. For
/// some of them ffmpeg reported the size of a single 512 px tile and made thumbnails (and found faces) from that tile;
/// others it couldn't open at all. The repair reads each size again, then redoes thumbnails, editing copies and face
/// search. Faces found again in the same place keep their names. Runs once per library.
/// </summary>
public sealed class HeicRepair : BackgroundService
{
    public const string DoneKey = "heic_repaired";

    private readonly Db _db;
    private readonly SettingsService _settings;
    private readonly HeifDecoder _heif;
    private readonly ThumbnailService _thumbs;
    private readonly DevelopService _develop;
    private readonly FaceRepository _faces;
    private readonly FaceService? _faceService;
    private readonly ILogger<HeicRepair> _log;

    public HeicRepair(Db db, SettingsService settings, HeifDecoder heif, ThumbnailService thumbs, DevelopService develop, FaceRepository faces,
        ILogger<HeicRepair> log, FaceService? faceService = null)
    {
        _db = db;
        _settings = settings;
        _heif = heif;
        _thumbs = thumbs;
        _develop = develop;
        _faces = faces;
        _faceService = faceService;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // let startup carry on
        try { await RunAsync(stoppingToken); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogError(ex, "Repairing HEIC photos failed"); }
    }

    /// <summary>Does the repair if it is due and libheif is installed. Returns how many files were redone.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        if (_settings.Get(DoneKey) == "1") return 0;
        List<Asset> assets;
        using (var c = _db.Open())
            assets = c.Query<Asset>("SELECT * FROM assets WHERE ext IN ('heic','heif') AND missing=0").ToList();
        if (!_heif.IsAvailable)
        {
            // Leave it unmarked so the repair runs once libheif is installed.
            if (assets.Count > 0) _log.LogInformation("{Count:N0} HEIC photos are decoded with ffmpeg, which can't open some recent iPhone files: {Hint}", assets.Count, HeifDecoder.NotInstalled);
            return 0;
        }
        var resized = 0;
        await Parallel.ForEachAsync(assets, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2), CancellationToken = ct }, async (a, token) =>
        {
            if (await _heif.ProbeSizeAsync(a.Path, token) is { } size && (size.Width, size.Height) != (a.Width, a.Height))
            {
                using var c = _db.Open();
                c.Execute("UPDATE assets SET width=@Width, height=@Height WHERE id=@id", new { size.Width, size.Height, id = a.Id });
                c.Execute("UPDATE items SET width=@Width, height=@Height WHERE primary_asset_id=@id", new { size.Width, size.Height, id = a.Id });
                Interlocked.Increment(ref resized);
            }
            DeleteCached(a.Hash);
        });
        _faces.RedoScans(assets.Select(a => a.Id).ToList());
        _settings.Set(DoneKey, "1");
        if (resized > 0) _db.Touch();
        if (assets.Count == 0) return 0;
        _log.LogInformation("HEIC photos are decoded with libheif now: {Count:N0} redone ({Resized:N0} with a corrected size); their thumbnails and faces are being made again",
            assets.Count, resized);
        _thumbs.ForgetFailures();
        if (_settings.GetBool(SettingsService.PregenThumbs, true)) await _thumbs.SweepAsync(ct);
        _faceService?.RequestSweep();
        return assets.Count;
    }

    /// <summary>Thumbnails (every size and rotation), face crops and editing copies of one file.</summary>
    private void DeleteCached(string hash)
    {
        var dirs = new[]
        {
            Path.Combine(_settings.ThumbsDir, hash[..2]),
            Path.Combine(_settings.ThumbsDir, "faces", hash[..2]),
            _develop.CacheDir,
        };
        foreach (var dir in dirs.Where(Directory.Exists))
            foreach (var f in Directory.EnumerateFiles(dir, hash + "_*"))
                try { File.Delete(f); }
                catch (IOException ex) { _log.LogDebug(ex, "Could not delete {Path}", f); }
    }
}
