using System.Collections.Concurrent;
using System.Threading.Channels;
using Gallery.Core.Models;

namespace Gallery.Core.Services.Faces;

/// <summary>What the face queue is doing, for the status panel. State: off, no-models, downloading, ready or error.</summary>
public sealed record FaceActivity(bool Running, long Done, long Pending, long Faces, string State, string? Detail = null, bool Grouping = false);

/// <summary>
/// Finds faces in every photo in the background (docs/faces.md). Files wait in a queue like the thumbnail queue. Scans,
/// a sweep at startup and after each scan, and changes to an item's rotation or main file add to it. For each file:
/// render upright at 1024 px in memory -> find faces -> crop small ones from a 2048 px rendering -> fingerprint every
/// face -> store all in one transaction, so a restart carries on where it stopped.
/// </summary>
public sealed class FaceService : BackgroundService
{
    /// <summary>Long edge, in pixels, of the picture searched for faces.</summary>
    public const int PreviewSize = 1024;

    // Faces with eyes this far apart (pixels, in the 1024 preview) are cropped from a 2048 rendering, because from the 1024
    // preview their fingerprints are less exact (0.83 match with the full-size picture, 0.96 from the 2048 rendering).
    // Smaller faces are never grouped, so they are not worth the extra decode.
    private const float LargerCropFromEyePx = 12;
    private const float LargerCropBelowEyePx = 32;

    private readonly SettingsService _settings;
    private readonly ThumbnailService _thumbs;
    private readonly FaceModels _models;
    private readonly FaceRepository _repo;
    private readonly GalleryOptions _options;
    private readonly ILogger<FaceService> _log;
    private readonly BackgroundCpu? _cpu;
    private readonly int _workers;

    private readonly Channel<long> _queue = Channel.CreateUnbounded<long>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly ConcurrentDictionary<long, byte> _queued = new(); // files waiting or being processed
    private long _doneSinceIdle;
    private long _facesSinceIdle;
    private int _sweeping;
    private int _sweepRequested;
    private CancellationToken _stopping = CancellationToken.None;

    private readonly object _analyzerSync = new();
    private AnalyzerSlot? _slot;
    private DateTime _lastUsedUtc;

    /// <summary>
    /// Free the models after this long without work. Once loaded, buffalo_l holds about 430 MB of native ONNX Runtime
    /// memory, which an idle server can put to better use. Loading them again takes about a second.
    /// </summary>
    public static readonly TimeSpan IdleUnload = TimeSpan.FromMinutes(5);
    private string? _error; // why the models could not start; cleared when the settings or the models change

    /// <summary>Raised after a photo's faces were stored, so grouping knows there is new work.</summary>
    public event Action? FacesSaved;

    /// <summary>Tests: an analyzer without model files.</summary>
    internal Func<FacePack, IFaceAnalyzer>? AnalyzerFactory { get; set; }

    public FaceService(SettingsService settings, ThumbnailService thumbs, ItemRepository items, FaceModels models, FaceRepository repo,
        GalleryOptions options, ILogger<FaceService> log, BackgroundCpu? cpu = null)
    {
        _cpu = cpu;
        _settings = settings;
        _thumbs = thumbs;
        _models = models;
        _repo = repo;
        _options = options;
        _log = log;
        _workers = options.Faces.Workers > 0 ? options.Faces.Workers : Math.Max(1, Environment.ProcessorCount / 2);
        items.PictureChanged += EnqueueItems;
    }

    public bool Enabled => _settings.GetBool(SettingsService.FacesEnabled, _options.Faces.Enabled);

    /// <summary>The pack in use: the admin's choice, else Gallery:Faces:Pack, else buffalo_l.</summary>
    public FacePack Pack =>
        _models.Find(_settings.Get(SettingsService.FacesPack, _options.Faces.Pack)) ?? _models.Find(_options.Faces.Pack) ?? _models.Packs[0];

    private bool ModelsReady(FacePack pack) => AnalyzerFactory is not null || _models.IsPresent(pack);

    private bool CanRun
    {
        get
        {
            if (!Enabled) return false;
            if (!ModelsReady(Pack)) return false;
            lock (_analyzerSync) return _error is null;
        }
    }

    /// <summary>Which backend each model runs on, once the models have been loaded.</summary>
    public string? Backends { get { lock (_analyzerSync) return _slot?.Analyzer.Backends; } }

    public FaceActivity Activity
    {
        get
        {
            var pending = _queued.Count;
            var running = pending > 0 || Volatile.Read(ref _sweeping) == 1;
            string? error;
            lock (_analyzerSync) error = _error;
            var pack = Pack;
            var state = !Enabled ? "off"
                : _models.Download is { Running: true } ? "downloading"
                : !ModelsReady(pack) ? "no-models"
                : error is not null ? "error"
                : "ready";
            return new FaceActivity(running, pending > 0 ? Interlocked.Read(ref _doneSinceIdle) : 0, pending, Interlocked.Read(ref _facesSinceIdle), state, error);
        }
    }

    /// <summary>Changes the admin's settings, then sweeps with them. Null leaves a setting as it is.</summary>
    public void Configure(bool? enabled, string? pack)
    {
        if (pack is not null)
        {
            var p = _models.Find(pack) ?? throw new ArgumentException($"unknown face pack \"{pack}\"");
            _settings.Set(SettingsService.FacesPack, p.Name);
        }
        if (enabled is not null) _settings.Set(SettingsService.FacesEnabled, enabled.Value ? "true" : "false");
        lock (_analyzerSync) _error = null;
        RequestSweep();
    }

    /// <summary>Downloads a pack's models in the background and starts finding faces when they are in place.</summary>
    public bool StartDownload(FacePack pack) => _models.StartDownload(pack, () =>
    {
        lock (_analyzerSync) _error = null;
        RequestSweep();
    });

    /// <summary>Searches files again: those that failed, or every photo. Returns how many searches were forgotten.</summary>
    public int FindAgain(bool failedOnly)
    {
        var n = _repo.ForgetScans(failedOnly);
        lock (_analyzerSync) _error = null;
        RequestSweep();
        return n;
    }

    // ---------------------------------------------------------------- queue

    /// <summary>Queues the photos of these items when they still need searching.</summary>
    public void EnqueueItems(IReadOnlyCollection<long> itemIds)
    {
        if (itemIds.Count == 0 || !CanRun) return;
        try { Enqueue(_repo.Targets(Pack.Name, itemIds)); }
        catch (Exception ex) { _log.LogWarning(ex, "Could not queue faces for {Count} items", itemIds.Count); }
    }

    private int Enqueue(IEnumerable<long> assetIds)
    {
        var n = 0;
        foreach (var id in assetIds)
            if (_queued.TryAdd(id, 0)) { _queue.Writer.TryWrite(id); n++; }
        return n;
    }

    /// <summary>Starts a sweep in the background; a request during a sweep makes it go round once more.</summary>
    public void RequestSweep()
    {
        if (!CanRun) return;
        _ = Task.Run(async () =>
        {
            try { await SweepAsync(_stopping); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogWarning(ex, "Face sweep failed"); }
        });
    }

    /// <summary>Queues every photo that still needs searching. Returns how many were added.</summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        Interlocked.Exchange(ref _sweepRequested, 1);
        if (Interlocked.CompareExchange(ref _sweeping, 1, 0) != 0) return 0; // the running sweep goes round once more
        var total = 0;
        try
        {
            while (Interlocked.Exchange(ref _sweepRequested, 0) == 1 && CanRun)
            {
                var targets = await Task.Run(() => _repo.Targets(Pack.Name), ct);
                var added = Enqueue(targets);
                if (added > 0) _log.LogInformation("Faces: {Count:N0} photos queued", added);
                total += added;
            }
        }
        finally
        {
            Volatile.Write(ref _sweeping, 0);
            if (Volatile.Read(ref _sweepRequested) == 1) RequestSweep();
        }
        return total;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        RequestSweep();
        _ = UnloadWhenIdleAsync(stoppingToken);
        // On BackgroundCpu's threads: the models block while they run, and on the thread pool they hold threads web requests need.
        var workers = Enumerable.Range(0, _workers)
            .Select(_ => _cpu?.Run(() => WorkerAsync(stoppingToken), stoppingToken) ?? Task.Run(() => WorkerAsync(stoppingToken), stoppingToken)).ToArray();
        try { await Task.WhenAll(workers); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task WorkerAsync(CancellationToken ct)
    {
        await foreach (var assetId in _queue.Reader.ReadAllAsync(ct))
        {
            try { await ProcessAsync(assetId, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { _log.LogWarning(ex, "Finding faces failed for file #{AssetId}", assetId); }
            finally
            {
                _queued.TryRemove(assetId, out _);
                Interlocked.Increment(ref _doneSinceIdle);
                if (_queued.IsEmpty && Interlocked.Exchange(ref _doneSinceIdle, 0) is var n and > 0)
                    _log.LogInformation("Faces: queue empty, {Count:N0} photos searched, {Faces:N0} faces found", n, Interlocked.Exchange(ref _facesSinceIdle, 0));
            }
        }
    }

    /// <summary>Tests: searches the photos that need it (up to <paramref name="limit"/>) on the calling task. Returns how many.</summary>
    internal async Task<int> RunOnceAsync(CancellationToken ct, int limit = int.MaxValue)
    {
        if (!CanRun) return 0;
        var n = 0;
        foreach (var id in _repo.Targets(Pack.Name).Take(limit))
        {
            await ProcessAsync(id, ct);
            n++;
        }
        return n;
    }

    // ---------------------------------------------------------------- one file

    private async Task ProcessAsync(long assetId, CancellationToken ct)
    {
        if (!CanRun) return; // dropped; a later sweep finds it again
        if (_repo.Target(assetId) is not { } target) return;
        var (asset, rotation, squeeze) = target;
        var pack = Pack;
        var slot = Acquire(pack);
        if (slot is null) return;
        try
        {
            using var preview = await _thumbs.RenderAsync(asset, PreviewSize, rotation, squeeze, ct);
            if (preview is null)
            {
                _repo.SaveFailed(assetId, pack.Name, rotation, "the picture could not be decoded", squeeze);
                return;
            }
            var picture = FacePixels.From(preview);
            var found = slot.Analyzer.Detect(picture);
            var toPreview = (float)PreviewSize / Math.Max(picture.Width, picture.Height); // when the picture is smaller than 1024
            bool NeedsLarger(DetectedFace f) => f.EyeDistance * toPreview is >= LargerCropFromEyePx and < LargerCropBelowEyePx;

            FacePixels? large = null;
            if (toPreview <= 1 && found.Any(NeedsLarger))
            {
                using var big = await _thumbs.RenderAsync(asset, ThumbnailService.AllowedSizes[^1], rotation, squeeze, ct);
                if (big is not null && big.Width > picture.Width) large = FacePixels.From(big);
            }

            var faces = new List<NewFace>(found.Count);
            foreach (var f in found)
            {
                var source = large is not null && NeedsLarger(f) ? large : picture;
                var s = (float)source.Width / picture.Width;
                var crop = FaceAligner.Crop(source, s == 1 ? f.Landmarks : f.Landmarks.Select(v => v * s).ToArray());
                var embedding = slot.Analyzer.Embed(crop);
                float w = picture.Width, h = picture.Height;
                var landmarks = new float[10];
                for (var i = 0; i < 5; i++) { landmarks[2 * i] = f.Landmarks[2 * i] / w; landmarks[2 * i + 1] = f.Landmarks[2 * i + 1] / h; }
                faces.Add(new NewFace(Math.Clamp(f.X1 / w, 0, 1), Math.Clamp(f.Y1 / h, 0, 1), Math.Clamp(f.X2 / w, 0, 1), Math.Clamp(f.Y2 / h, 0, 1),
                    landmarks, f.Score, f.EyeDistance * toPreview, embedding));
            }
            _repo.Save(assetId, pack.Name, rotation, faces, squeeze);
            Interlocked.Add(ref _facesSinceIdle, faces.Count);
            FacesSaved?.Invoke();
            _log.LogDebug("Faces: {Count} in {Path}", faces.Count, asset.Path);
        }
        finally { Release(slot); }
    }

    // ---------------------------------------------------------------- the models

    private sealed class AnalyzerSlot(IFaceAnalyzer analyzer)
    {
        public IFaceAnalyzer Analyzer { get; } = analyzer;
        public int Users;
        public bool Retired;
    }

    /// <summary>The analyzer for the pack, loading it on first use or when the pack changed. Null when it can't start.</summary>
    private AnalyzerSlot? Acquire(FacePack pack)
    {
        lock (_analyzerSync)
        {
            if (_slot is { } current && current.Analyzer.Pack == pack.Name) { current.Users++; return current; }
            if (_error is not null) return null;
            if (_slot is { } old)
            {
                old.Retired = true; // disposed when the last worker using it lets go
                if (old.Users == 0) old.Analyzer.Dispose();
                _slot = null;
            }
            try
            {
                _slot = new AnalyzerSlot(CreateAnalyzer(pack));
                _log.LogInformation("Faces: {Pack} loaded, {Backends}, {Workers} workers", pack.Name, _slot.Analyzer.Backends, _workers);
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                _log.LogWarning("Faces: could not load {Pack}: {Error}", pack.Name, ex.Message);
                return null;
            }
            _slot.Users++;
            return _slot;
        }
    }

    private void Release(AnalyzerSlot slot)
    {
        lock (_analyzerSync)
        {
            slot.Users--;
            _lastUsedUtc = DateTime.UtcNow;
            if (slot.Retired && slot.Users == 0) slot.Analyzer.Dispose();
        }
    }

    /// <summary>The models in memory: which pack, whether loaded, the size of its files and when they were last used.</summary>
    public sealed record ModelsInMemory(string Pack, bool Loaded, long FileBytes, DateTime? LastUsed);

    public ModelsInMemory Models
    {
        get
        {
            var pack = Pack;
            long files = 0;
            if (AnalyzerFactory is null)
                foreach (var file in new[] { pack.Detector, pack.Recognizer })
                    try { files += new FileInfo(_models.PathOf(pack, file)).Length; } catch (IOException) { }
            lock (_analyzerSync)
                return _slot is { } s ? new(s.Analyzer.Pack, true, files, _lastUsedUtc == default ? null : _lastUsedUtc) : new(pack.Name, false, files, null);
        }
    }

    /// <summary>
    /// Lets go of the models when no worker is using them, no photo is waiting, and they have not been used for
    /// <paramref name="idleFor"/>; the next photo loads them again. Returns whether they were released.
    /// </summary>
    public bool UnloadIfIdle(TimeSpan idleFor)
    {
        AnalyzerSlot slot;
        lock (_analyzerSync)
        {
            if (_slot is not { Users: 0 } s || !_queued.IsEmpty || DateTime.UtcNow - _lastUsedUtc < idleFor) return false;
            slot = s;
            _slot = null;
            slot.Analyzer.Dispose();
        }
        ProcessMemory.ReturnFreed(); // on Linux, the native memory they held goes back to the system
        _log.LogInformation("Faces: {Pack} unloaded{Why}, to free its memory", slot.Analyzer.Pack,
            idleFor > TimeSpan.Zero ? $" after {idleFor.TotalMinutes:0} minutes without work" : "");
        return true;
    }

    private async Task UnloadWhenIdleAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(ct)) UnloadIfIdle(IdleUnload);
        }
        catch (OperationCanceledException) { }
    }

    private IFaceAnalyzer CreateAnalyzer(FacePack pack)
    {
        if (AnalyzerFactory is not null) return AnalyzerFactory(pack);
        if (_models.Verify(pack) is { } problem) throw new InvalidDataException(problem);
        return new OnnxFaceAnalyzer(pack.Name, _models.PathOf(pack, pack.Detector), _models.PathOf(pack, pack.Recognizer),
            _options.Faces.Provider, _options.Faces.DetectorSize, _log);
    }

    public override void Dispose()
    {
        lock (_analyzerSync)
        {
            _slot?.Analyzer.Dispose();
            _slot = null;
        }
        base.Dispose();
    }
}
