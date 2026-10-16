using System.Collections.Concurrent;
using Dapper;
using Gallery.Core.Data;
using Gallery.Core.Models;

namespace Gallery.Core.Services;

/// <summary>
/// How the viewer plays an asset. For a video sent as it is, <paramref name="Fallback"/> is the converted stream to switch
/// to when the browser can't decode the file, and <paramref name="Persisted"/> says whether a saved, seekable copy backs it.
/// </summary>
public sealed record PlayInfo(bool Direct, string Url, string Mime, bool Persisted, string? Fallback = null);

/// <summary>
/// State of converting videos while the server is idle (<see cref="IdleTranscoder"/>): off, waiting (the reason is in
/// Waiting), converting (File) or done. Left is how many videos still need a saved copy.
/// </summary>
public sealed record IdleTranscodeStatus(string State, int Left = 0, string? File = null, string? Waiting = null)
{
    public static readonly IdleTranscodeStatus Off = new("off");
}

/// <summary>
/// Decides whether a browser can play an asset as it is. If not, converts it with ffmpeg: live (fragmented MP4 / MP3
/// sent straight to the response) and, when "keep transcodes" is on, also as a saved copy in the derived folder.
/// </summary>
public sealed class TranscodeService
{
    private readonly Db _db;
    private readonly SettingsService _settings;
    private readonly Ffmpeg _ffmpeg;
    private readonly ILogger<TranscodeService> _log;
    private readonly ConcurrentDictionary<long, Task> _jobs = new();
    private readonly SemaphoreSlim _persistGate = new(1);
    private volatile string? _activeFile;

    /// <summary>Saved-copy conversions that are waiting or running.</summary>
    public int QueuedCount => _jobs.Count;

    /// <summary>File name of the running conversion, if any.</summary>
    public string? ActiveFile => _activeFile;

    /// <summary>What <see cref="IdleTranscoder"/> is doing.</summary>
    public IdleTranscodeStatus Idle { get; internal set; } = IdleTranscodeStatus.Off;

    private volatile CancellationTokenSource? _idleJob;

    public TranscodeService(Db db, SettingsService settings, Ffmpeg ffmpeg, ILogger<TranscodeService> log)
    {
        _db = db;
        _settings = settings;
        _ffmpeg = ffmpeg;
        _log = log;
    }

    /// <summary>Video codecs sent as they are from MP4 and QuickTime files, whatever the audio.</summary>
    private static readonly HashSet<string> StreamedVideoCodecs = new(StringComparer.OrdinalIgnoreCase) { "h264", "hevc" };
    private static readonly HashSet<string> StreamedVideoContainers = new(StringComparer.OrdinalIgnoreCase) { "mp4", "m4v", "mov" };

    /// <summary>
    /// Whether the file is sent to the browser as it is, with range requests for seeking. H.264 and H.265 in MP4 or
    /// QuickTime (what phones record) always are, whatever the audio. H.265 plays in Safari, and in Chrome and Edge when
    /// the hardware can decode it; a browser that can't switches to <see cref="PlayInfo.Fallback"/>.
    /// Other videos, and audio, go out as they are only if every browser plays them.
    /// </summary>
    public bool CanPlayDirectly(Asset a)
    {
        if (a.Kind == MediaKind.Video)
        {
            if (StreamedVideoContainers.Contains(a.Ext) && a.Codec is not null && StreamedVideoCodecs.Contains(a.Codec)) return true;
            var containerOk = FileTypes.BrowserVideoContainers.Contains(a.Ext);
            var codecOk = a.Codec is null || FileTypes.BrowserVideoCodecs.Contains(a.Codec);
            var audioOk = a.AudioCodec is null || FileTypes.BrowserAudioCodecs.Contains(a.AudioCodec);
            return containerOk && codecOk && audioOk;
        }
        if (a.Kind == MediaKind.Audio)
        {
            var containerOk = FileTypes.BrowserAudioContainers.Contains(a.Ext);
            var codecOk = a.AudioCodec is null || FileTypes.BrowserAudioCodecs.Contains(a.AudioCodec) || a.AudioCodec.StartsWith("pcm_");
            return containerOk && codecOk;
        }
        return false;
    }

    public string TargetExt(Asset a) => a.Kind == MediaKind.Audio ? "mp3" : "mp4";
    public string TargetMime(Asset a) => a.Kind == MediaKind.Audio ? "audio/mpeg" : "video/mp4";

    public string? PersistedPath(Asset a)
    {
        using var c = _db.Open();
        var p = c.ExecuteScalar<string?>("SELECT path FROM derived WHERE asset_id=@id AND kind='transcode' LIMIT 1", new { id = a.Id });
        if (p is not null && File.Exists(p)) return p;
        var expected = ExpectedPersistedPath(a);
        return File.Exists(expected) ? expected : null;
    }

    private string ExpectedPersistedPath(Asset a)
    {
        var dir = Path.Combine(_settings.GetDerivedDir(), "transcodes");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{a.Hash}.{TargetExt(a)}");
    }

    public PlayInfo GetPlayInfo(Asset a)
    {
        if (CanPlayDirectly(a))
        {
            var fallback = a.Kind == MediaKind.Video && _ffmpeg.IsAvailable ? $"/api/assets/{a.Id}/stream?convert=true" : null;
            return new PlayInfo(true, $"/api/assets/{a.Id}/file", FileTypes.MimeFor(a.Ext), fallback is not null && PersistedPath(a) is not null, fallback);
        }
        var persisted = PersistedPath(a);
        if (persisted is not null) return new PlayInfo(false, $"/api/assets/{a.Id}/stream", TargetMime(a), true);
        if (_settings.GetBool(SettingsService.KeepTranscodes, true)) QueuePersist(a);
        return new PlayInfo(false, $"/api/assets/{a.Id}/stream", TargetMime(a), false);
    }

    /// <summary>
    /// Called when a browser couldn't decode a file sent as it is (<see cref="CanPlayDirectly"/>) and plays the converted
    /// stream instead. Saves a converted copy, if copies are kept, so seeking works next time.
    /// </summary>
    public void QueueFallbackCopy(Asset a)
    {
        if (_settings.GetBool(SettingsService.KeepTranscodes, true)) QueuePersist(a);
    }

    /// <summary>
    /// Whether the video stream can be copied into the converted file instead of re-encoded. H.264 can, unless the file
    /// is one sent as it is: such a file is only converted when the browser couldn't decode it.
    /// </summary>
    private bool CopiesVideo(Asset a) => a.Codec is "h264" && !(a.Ext is "mts" or "m2ts" or "ts") && !CanPlayDirectly(a);

    /// <summary>ffmpeg args for a live stream to stdout. Fragmented MP4 lets the browser start before the file is complete.</summary>
    public IEnumerable<string> LiveArgs(Asset a, double startSec = 0)
    {
        var args = new List<string>();
        if (startSec > 0) { args.Add("-ss"); args.Add(startSec.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)); }
        args.AddRange(new[] { "-i", a.Path });
        if (a.Kind == MediaKind.Audio)
        {
            args.AddRange(new[] { "-vn", "-c:a", "libmp3lame", "-b:a", "192k", "-f", "mp3", "-" });
            return args;
        }
        var copyVideo = CopiesVideo(a);
        args.AddRange(new[] { "-map", "0:v:0", "-map", "0:a:0?", "-sn", "-dn" });
        if (copyVideo) args.AddRange(new[] { "-c:v", "copy" });
        else args.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p", "-vf", "scale='min(1920,iw)':-2", "-g", "48" });
        args.AddRange(new[] { "-c:a", "aac", "-b:a", "160k", "-ac", "2" });
        args.AddRange(new[] { "-movflags", "frag_keyframe+empty_moov+default_base_moof", "-f", "mp4", "-" });
        return args;
    }

    private IEnumerable<string> PersistArgs(Asset a, string outPath)
    {
        var args = new List<string> { "-i", a.Path };
        if (a.Kind == MediaKind.Audio)
        {
            args.AddRange(new[] { "-vn", "-c:a", "libmp3lame", "-b:a", "192k", "-f", "mp3", outPath });
            return args;
        }
        var copyVideo = CopiesVideo(a);
        args.AddRange(new[] { "-map", "0:v:0", "-map", "0:a:0?", "-sn", "-dn" });
        if (copyVideo) args.AddRange(new[] { "-c:v", "copy" });
        else args.AddRange(new[] { "-c:v", "libx264", "-preset", "medium", "-crf", "21", "-pix_fmt", "yuv420p", "-vf", "scale='min(1920,iw)':-2" });
        args.AddRange(new[] { "-c:a", "aac", "-b:a", "160k", "-ac", "2", "-movflags", "+faststart", "-f", "mp4", outPath });
        return args;
    }

    /// <summary>Queues a saved-copy conversion because someone played the video (or an admin asked). Runs one at a time.</summary>
    public void QueuePersist(Asset a)
    {
        if (!_ffmpeg.IsAvailable) return;
        _jobs.GetOrAdd(a.Id, key => Task.Run(async () =>
        {
            try { _idleJob?.Cancel(); } catch (ObjectDisposedException) { } // a copy someone asked for takes priority over an idle one
            await _persistGate.WaitAsync();
            _activeFile = a.FileName;
            try { await PersistAsync(a, lowPriority: false, CancellationToken.None); }
            catch (Exception ex) { _log.LogWarning(ex, "persistent transcode failed for {Path}", a.Path); }
            finally { _activeFile = null; _persistGate.Release(); _jobs.TryRemove(a.Id, out _); }
        }));
    }

    /// <summary>
    /// Converts a video into a saved copy at the lowest priority, for <see cref="IdleTranscoder"/>. Returns true when done,
    /// false when ffmpeg failed, null when stopped (by <paramref name="ct"/>, or because someone asked for a copy).
    /// </summary>
    public async Task<bool?> PersistWhileIdleAsync(Asset a, CancellationToken ct)
    {
        using var job = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await _persistGate.WaitAsync(ct);
        _idleJob = job;
        try
        {
            if (_jobs.Count > 0) return null; // someone asked for a copy while this waited
            return await PersistAsync(a, lowPriority: true, job.Token);
        }
        catch (OperationCanceledException) { return null; }
        finally { _idleJob = null; _persistGate.Release(); }
    }

    private async Task<bool> PersistAsync(Asset a, bool lowPriority, CancellationToken ct)
    {
        var outPath = ExpectedPersistedPath(a);
        if (File.Exists(outPath)) { Record(a, outPath); return true; }
        var tmp = outPath + ".part";
        _log.LogInformation("Transcoding {Path} -> {Out} ({Queued} queued{Idle})", a.Path, outPath, Math.Max(0, _jobs.Count - 1), lowPriority ? ", while idle" : "");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool ok;
        try { ok = await _ffmpeg.RunAsync(PersistArgs(a, tmp), ct, lowPriority); }
        catch (OperationCanceledException)
        {
            try { File.Delete(tmp); } catch { }
            _log.LogInformation("Stopped transcoding {File} after {Elapsed}; it is tried again later", a.FileName, Scanner.Fmt(clock.Elapsed));
            throw;
        }
        if (ok && File.Exists(tmp))
        {
            File.Move(tmp, outPath, true);
            Record(a, outPath);
            _log.LogInformation("Transcoded {File} in {Elapsed} ({Size:N0} bytes)", a.FileName, Scanner.Fmt(clock.Elapsed), new FileInfo(outPath).Length);
            return true;
        }
        try { File.Delete(tmp); } catch { }
        _log.LogWarning("Transcode of {Path} failed after {Elapsed}", a.Path, Scanner.Fmt(clock.Elapsed));
        return false;
    }

    /// <summary>
    /// Videos that browsers can't play as they are and that have no saved copy yet, newest first, excluding <paramref name="skip"/>.
    /// </summary>
    public List<Asset> NeedingCopies(IReadOnlySet<long>? skip = null)
    {
        using var c = _db.Open();
        var videos = c.Query<Asset>("""
            SELECT a.* FROM assets a JOIN items i ON i.id=a.item_id
            WHERE a.kind='video' AND a.missing=0 AND i.hidden=0
              AND NOT EXISTS (SELECT 1 FROM derived d WHERE d.asset_id=a.id AND d.kind='transcode')
            ORDER BY a.taken_at DESC, a.id DESC
            """);
        var dir = Path.Combine(_settings.GetDerivedDir(), "transcodes");
        return videos.Where(a => skip?.Contains(a.Id) != true && !CanPlayDirectly(a) && !File.Exists(Path.Combine(dir, $"{a.Hash}.{TargetExt(a)}"))).ToList();
    }

    private void Record(Asset a, string path)
    {
        using var c = _db.Open();
        c.Execute("INSERT OR IGNORE INTO derived(asset_id, kind, path, created_at) VALUES(@id,'transcode',@path,@now)", new { id = a.Id, path, now = Clock.NowIso() });
    }

    public IEnumerable<(long AssetId, string Path, long Size)> ListPersisted()
    {
        using var c = _db.Open();
        foreach (var r in c.Query<(long asset_id, string path)>("SELECT asset_id, path FROM derived WHERE kind='transcode'"))
        {
            var size = File.Exists(r.path) ? new FileInfo(r.path).Length : 0;
            yield return (r.asset_id, r.path, size);
        }
    }

    public void DeletePersisted(long assetId)
    {
        using var c = _db.Open();
        foreach (var p in c.Query<string>("SELECT path FROM derived WHERE kind='transcode' AND asset_id=@assetId", new { assetId }))
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        c.Execute("DELETE FROM derived WHERE kind='transcode' AND asset_id=@assetId", new { assetId });
    }
}
