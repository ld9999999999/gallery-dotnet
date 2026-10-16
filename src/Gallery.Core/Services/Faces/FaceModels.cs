using System.IO.Compression;
using System.Security.Cryptography;
using Gallery.Core.Models;

namespace Gallery.Core.Services.Faces;

public sealed record FaceModelFile(string Name, long Size, string Sha256);

/// <summary>A pack: a face detector and a face recogniser, shipped together in one zip on InsightFace's release page.</summary>
public sealed record FacePack(string Name, string Description, FaceModelFile Detector, FaceModelFile Recognizer, string ZipUrl, long ZipSize, string ZipSha256)
{
    public long FilesSize => Detector.Size + Recognizer.Size;
}

/// <summary>Progress of a model download. Bytes count the zip. Error is set when it failed.</summary>
public sealed record FaceDownload(string Pack, bool Running, long Done, long Total, string? Error);

/// <summary>
/// The face model packs (docs/faces.md): which files they hold, where they are ({ModelsDir}/{pack}/), whether they are
/// there, and how to download them from InsightFace's v0.7 release. Files are checked against their SHA-256 after
/// download and once per run before they are first loaded.
/// </summary>
public sealed class FaceModels
{
    private const string Release = "https://github.com/deepinsight/insightface/releases/download/v0.7/";

    public static readonly IReadOnlyList<FacePack> DefaultPacks = new[]
    {
        new FacePack("buffalo_l", "Accurate: SCRFD-10G and ArcFace ResNet-50",
            new FaceModelFile("det_10g.onnx", 16_923_827, "5838f7fe053675b1c7a08b633df49e7af5495cee0493c7dcf6697200b85b5b91"),
            new FaceModelFile("w600k_r50.onnx", 174_383_860, "4c06341c33c2ca1f86781dab0e829f88ad5b64be9fba56e56bc9ebdefc619e43"),
            Release + "buffalo_l.zip", 288_621_354, "80ffe37d8a5940d59a7384c201a2a38d4741f2f3c51eef46ebb28218a7b0ca2f"),
        new FacePack("buffalo_s", "For low-power CPUs: SCRFD-500M and MobileFaceNet",
            new FaceModelFile("det_500m.onnx", 2_524_817, "5e4447f50245bbd7966bd6c0fa52938c61474a04ec7def48753668a9d8b4ea3a"),
            new FaceModelFile("w600k_mbf.onnx", 13_616_099, "9cc6e4a75f0e2bf0b1aed94578f144d15175f357bdc05e815e5c4a02b319eb4f"),
            Release + "buffalo_sc.zip", 14_969_382, "57d31b56b6ffa911c8a73cfc1707c73cab76efe7f13b675a05223bf42de47c72"),
    };

    private readonly ILogger<FaceModels> _log;
    private readonly HttpMessageHandler? _handler;
    private readonly object _sync = new();
    private readonly HashSet<string> _verified = new(); // "path|size|mtime" of files whose SHA-256 matched in this run
    private FaceDownload? _download;

    public FaceModels(GalleryOptions options, ILogger<FaceModels> log) : this(options, log, null, null) { }

    /// <summary>For tests: other packs, and an HTTP handler instead of the network.</summary>
    internal FaceModels(GalleryOptions options, ILogger<FaceModels> log, IReadOnlyList<FacePack>? packs, HttpMessageHandler? handler)
    {
        _log = log;
        _handler = handler;
        Packs = packs ?? DefaultPacks;
        Dir = string.IsNullOrWhiteSpace(options.Faces.ModelsDir)
            ? Path.Combine(options.ResolvedDataDir, "models", "faces")
            : GalleryOptions.ExpandHome(options.Faces.ModelsDir);
    }

    public IReadOnlyList<FacePack> Packs { get; }

    /// <summary>The folder that holds one subfolder per pack.</summary>
    public string Dir { get; }

    public FacePack? Find(string name) => Packs.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public string PathOf(FacePack pack, FaceModelFile file) => Path.Combine(Dir, pack.Name, file.Name);

    /// <summary>True when both files are there with the expected sizes (fast; the SHA-256 is checked before first use).</summary>
    public bool IsPresent(FacePack pack) =>
        new[] { pack.Detector, pack.Recognizer }.All(f => new FileInfo(PathOf(pack, f)) is { Exists: true } fi && fi.Length == f.Size);

    /// <summary>Null when both files match their SHA-256, otherwise what is wrong. Each file is hashed only once per run.</summary>
    public string? Verify(FacePack pack)
    {
        foreach (var f in new[] { pack.Detector, pack.Recognizer })
        {
            var fi = new FileInfo(PathOf(pack, f));
            if (!fi.Exists) return $"{fi.FullName} is missing";
            if (fi.Length != f.Size) return $"{fi.FullName} has {fi.Length:N0} bytes; expected {f.Size:N0}";
            var key = $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
            lock (_sync) if (_verified.Contains(key)) continue;
            using (var s = fi.OpenRead())
                if (!Convert.ToHexStringLower(SHA256.HashData(s)).Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
                    return $"{fi.FullName} is not the expected file (SHA-256 differs); download the pack again";
            lock (_sync) _verified.Add(key);
        }
        return null;
    }

    public FaceDownload? Download { get { lock (_sync) return _download; } }

    /// <summary>
    /// Starts downloading a pack in the background. Returns false if a download is already running.
    /// <paramref name="done"/> runs when it finishes successfully.
    /// </summary>
    public bool StartDownload(FacePack pack, Action? done = null)
    {
        lock (_sync)
        {
            if (_download is { Running: true }) return false;
            _download = new FaceDownload(pack.Name, true, 0, pack.ZipSize, null);
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await DownloadAsync(pack, CancellationToken.None);
                lock (_sync) _download = _download! with { Running = false, Done = _download.Total };
                _log.LogInformation("Face models {Pack} installed in {Dir}", pack.Name, Path.Combine(Dir, pack.Name));
                done?.Invoke();
            }
            catch (Exception ex)
            {
                lock (_sync) _download = _download! with { Running = false, Error = ex.Message };
                _log.LogWarning("Downloading face models {Pack} failed: {Error}", pack.Name, ex.Message);
            }
        });
        return true;
    }

    /// <summary>Downloads the pack's zip, checks it, then puts the two model files in place (and checks them too).</summary>
    internal async Task DownloadAsync(FacePack pack, CancellationToken ct)
    {
        var dir = Path.Combine(Dir, pack.Name);
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, Path.GetFileName(new Uri(pack.ZipUrl).AbsolutePath) + ".part");
        try
        {
            using (var http = _handler is null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false))
            {
                http.Timeout = TimeSpan.FromHours(1);
                using var response = await http.GetAsync(pack.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                await using var body = await response.Content.ReadAsStreamAsync(ct);
                await using var file = File.Create(zipPath);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 16];
                long done = 0;
                int n;
                while ((n = await body.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                    sha.AppendData(buffer, 0, n);
                    done += n;
                    lock (_sync) if (_download is { } d && d.Pack == pack.Name) _download = d with { Done = done };
                }
                var hash = Convert.ToHexStringLower(sha.GetHashAndReset());
                if (!hash.Equals(pack.ZipSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"the downloaded {Path.GetFileName(zipPath[..^5])} is not the expected file (SHA-256 {hash})");
            }
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var f in new[] { pack.Detector, pack.Recognizer })
            {
                var entry = zip.Entries.FirstOrDefault(e => Path.GetFileName(e.FullName) == f.Name)
                    ?? throw new InvalidDataException($"{f.Name} is not in the zip");
                var target = Path.Combine(dir, f.Name);
                var tmp = target + ".part";
                entry.ExtractToFile(tmp, overwrite: true);
                string hash;
                await using (var s = File.OpenRead(tmp)) hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(s, ct));
                if (!hash.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(tmp);
                    throw new InvalidDataException($"{f.Name} in the zip is not the expected file (SHA-256 {hash})");
                }
                File.Move(tmp, target, overwrite: true);
            }
        }
        finally
        {
            try { File.Delete(zipPath); } catch (IOException) { }
        }
    }
}
