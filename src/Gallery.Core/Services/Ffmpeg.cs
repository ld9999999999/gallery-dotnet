using System.Diagnostics;
using System.Text.Json;
using Gallery.Core.Models;

namespace Gallery.Core.Services;

/// <summary>ffmpeg's stdout, or a one-line summary of why it failed.</summary>
public sealed record FfmpegOutput(byte[]? Bytes, string? Error);

/// <summary>A picture stored as a grid of tiles, as phones save HEIC and AVIF photos. Holds its displayed size and turn.</summary>
public sealed record TileGrid(int Width, int Height, int Rotation);

public sealed record ProbeResult(
    double? DurationSec, int? Width, int? Height, string? VideoCodec, string? AudioCodec,
    string? Container, DateTime? CreationTime, int Rotation, bool HasVideo, bool HasAudio, bool HasCoverArt);

public sealed class Ffmpeg
{
    private readonly GalleryOptions _opt;
    private readonly ILogger<Ffmpeg> _log;
    private bool? _available;
    private Version? _version;

    public Ffmpeg(GalleryOptions opt, ILogger<Ffmpeg> log)
    {
        _opt = opt;
        _log = log;
    }

    public string FfmpegPath => _opt.FfmpegPath;
    public string FfprobePath => _opt.FfprobePath;

    public bool IsAvailable
    {
        get
        {
            if (_available is { } a) return a;
            try
            {
                using var p = Process.Start(new ProcessStartInfo(FfmpegPath, "-version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                var first = p!.StandardOutput.ReadLine() ?? "";
                p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                _available = p.ExitCode == 0;
                _version = ParseVersion(first);
                VersionText = _version?.ToString() ?? (first.Length > 0 ? first : "of unknown version");
            }
            catch (Exception ex)
            {
                _log.LogWarning("ffmpeg not available ({Message}); video/audio metadata, thumbnails and transcoding are disabled", ex.Message);
                _available = false;
            }
            return _available.Value;
        }
    }

    /// <summary>Parses the version from "ffmpeg version 7.1.1 …" (a leading n is allowed). Null for builds that don't report one (N-123-g…).</summary>
    internal static Version? ParseVersion(string firstLine)
    {
        var m = System.Text.RegularExpressions.Regex.Match(firstLine, @"version n?(\d+)\.(\d+)");
        return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : null;
    }

    /// <summary>The version for messages, e.g. "6.1".</summary>
    public string VersionText { get; private set; } = "";

    /// <summary>
    /// ffmpeg 7.1 and later join the tiles of a tiled picture (HEIC or AVIF from a phone). Older versions silently decode
    /// only one tile. Builds that report no version count as recent.
    /// </summary>
    public bool AssemblesTileGrids => IsAvailable && (_version is null || _version >= new Version(7, 1));

    /// <summary>
    /// The tile grid of a picture stored as one (needs ffprobe 7.1+), with its displayed size and turn. Null when the
    /// picture isn't tiled or ffprobe is too old to tell.
    /// </summary>
    public async Task<TileGrid?> ProbeTileGridAsync(string path, CancellationToken ct = default)
    {
        if (!AssemblesTileGrids) return null;
        var psi = new ProcessStartInfo(FfprobePath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-v", "error", "-print_format", "json", "-show_stream_groups", path }) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var stderr = p.StandardError.ReadToEndAsync(ct);
            var stdout = await p.StandardOutput.ReadToEndAsync(ct);
            await stderr;
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout)) return null;
            using var doc = JsonDocument.Parse(stdout);
            if (!doc.RootElement.TryGetProperty("stream_groups", out var groups)) return null;
            foreach (var g in groups.EnumerateArray())
            {
                if (!g.TryGetProperty("type", out var t) || t.GetString() != "Tile Grid" || !g.TryGetProperty("components", out var comps)) continue;
                foreach (var c in comps.EnumerateArray())
                    if (c.TryGetProperty("width", out var w) && c.TryGetProperty("height", out var h))
                        return new TileGrid(w.GetInt32(), h.GetInt32(), ReadRotation(c));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "ffprobe tile grid read failed for {Path}", path); }
        return null;
    }

    /// <summary>Raw ffprobe JSON (format and streams), or null if ffprobe is missing or fails.</summary>
    public async Task<string?> ProbeJsonAsync(string path, CancellationToken ct = default)
    {
        if (!IsAvailable) return null;
        var psi = new ProcessStartInfo(FfprobePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync(ct);
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        await stderr;
        await p.WaitForExitAsync(ct);
        return p.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout) ? null : stdout;
    }

    public async Task<ProbeResult?> ProbeAsync(string path, CancellationToken ct = default)
    {
        var stdout = await ProbeJsonAsync(path, ct);
        if (stdout is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            double? duration = null;
            string? container = null;
            DateTime? created = null;
            if (root.TryGetProperty("format", out var fmt))
            {
                if (fmt.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dv)) duration = dv;
                if (fmt.TryGetProperty("format_name", out var fn)) container = fn.GetString()?.Split(',')[0];
                created = ReadCreationTime(fmt);
            }
            int? w = null, h = null; int rotation = 0;
            string? vcodec = null, acodec = null;
            bool hasVideo = false, hasAudio = false, cover = false;
            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var s in streams.EnumerateArray())
                {
                    var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                    var isAttached = s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
                    if (type == "video")
                    {
                        if (isAttached) { cover = true; continue; }
                        if (hasVideo) continue;
                        hasVideo = true;
                        vcodec = s.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
                        if (s.TryGetProperty("width", out var sw)) w = sw.GetInt32();
                        if (s.TryGetProperty("height", out var sh)) h = sh.GetInt32();
                        rotation = ReadRotation(s);
                        created ??= s.TryGetProperty("tags", out var tg) ? ReadCreationTime(tg, isTags: true) : null;
                    }
                    else if (type == "audio" && !hasAudio)
                    {
                        hasAudio = true;
                        acodec = s.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
                    }
                }
            }
            if (rotation is 90 or 270 or -90) (w, h) = (h, w);
            return new ProbeResult(duration, w, h, vcodec, acodec, container, created, rotation, hasVideo, hasAudio, cover);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "ffprobe parse failed for {Path}", path);
            return null;
        }
    }

    private static int ReadRotation(JsonElement stream)
    {
        if (stream.TryGetProperty("side_data_list", out var sdl))
            foreach (var sd in sdl.EnumerateArray())
                if (sd.TryGetProperty("rotation", out var r) && r.TryGetInt32(out var rv)) return ((rv % 360) + 360) % 360;
        if (stream.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var rt) && int.TryParse(rt.GetString(), out var rot)) return ((rot % 360) + 360) % 360;
        return 0;
    }

    private static DateTime? ReadCreationTime(JsonElement fmtOrTags, bool isTags = false)
    {
        JsonElement tags = fmtOrTags;
        if (!isTags && !fmtOrTags.TryGetProperty("tags", out tags)) return null;
        foreach (var key in new[] { "com.apple.quicktime.creationdate", "creation_time", "date", "DATE" })
        {
            if (tags.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
            {
                // QuickTime stores UTC, but many cameras write local time marked Z. Converting to local time is a best guess.
                if (dt.Year < 1980) continue;
                return key.StartsWith("com.apple") ? DateTime.Parse(v.GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime() : dt.ToLocalTime();
            }
        }
        return null;
    }

    /// <summary>Runs ffmpeg and returns its stdout as bytes, for frame grabs piped to stdout.</summary>
    public async Task<FfmpegOutput> CaptureAsync(IEnumerable<string> args, CancellationToken ct = default)
    {
        if (!IsAvailable) return new FfmpegOutput(null, "ffmpeg is not installed");
        var list = args.ToList();
        var psi = new ProcessStartInfo(FfmpegPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-nostdin");
        foreach (var a in list) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var errTask = p.StandardError.ReadToEndAsync(ct);
        using var ms = new MemoryStream();
        await p.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        await p.WaitForExitAsync(ct);
        var err = await errTask;
        if (p.ExitCode != 0 || ms.Length == 0)
        {
            var summary = Summarize(err);
            if (summary.Length == 0) summary = $"exit code {p.ExitCode}, no output";
            _log.LogDebug("ffmpeg failed ({Code}) for {Input}: {Err}", p.ExitCode, InputOf(list), summary);
            return new FfmpegOutput(null, summary);
        }
        return new FfmpegOutput(ms.ToArray(), null);
    }

    /// <summary>The file given to ffmpeg with -i, for log messages.</summary>
    private static string InputOf(IReadOnlyList<string> args)
    {
        var i = args.ToList().IndexOf("-i");
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : "(no input)";
    }

    private static readonly System.Text.RegularExpressions.Regex Prefix = new(@"^(\[[^\]]*\]\s*)+", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Condenses ffmpeg's multi-line error output into one short line: the first few distinct messages, each labelled
    /// with the component that reported it ("[tiff @ 0x5636…] rps 0 invalid" becomes "tiff: rps 0 invalid").
    /// </summary>
    public static string Summarize(string stderr)
    {
        var seen = new List<string>();
        foreach (var raw in stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = Prefix.Match(raw);
            var text = raw[m.Length..].Trim();
            if (text.Length == 0) continue;
            var label = "";
            if (m.Success)
            {
                var groups = m.Value.Split('[', StringSplitOptions.RemoveEmptyEntries);
                var last = groups[^1].Trim().TrimEnd(']');
                label = last.Split(" @ ")[0].Split(':', '/')[^1];
            }
            var line = label.Length > 0 ? $"{label}: {text}" : text;
            if (!seen.Contains(line)) seen.Add(line);
            if (seen.Count == 3) break;
        }
        var joined = string.Join("; ", seen);
        return joined.Length <= 300 ? joined : joined[..300] + "…";
    }

    /// <summary>
    /// How to start ffmpeg. At low priority it runs under nice 19 and, on Linux, ionice's idle class, which gets the
    /// disk only when nothing else wants it. Both are set before ffmpeg starts, so every thread it creates inherits them.
    /// Where there's no nice (Windows), the process's priority class is lowered after it starts instead.
    /// </summary>
    private ProcessStartInfo StartInfo(bool lowPriority)
    {
        static string? Tool(string name) => new[] { "/usr/bin/" + name, "/bin/" + name }.FirstOrDefault(File.Exists);
        ProcessStartInfo Make(string file) => new(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (!lowPriority || OperatingSystem.IsWindows() || Tool("nice") is not { } nice) return Make(FfmpegPath);
        ProcessStartInfo psi;
        if (OperatingSystem.IsLinux() && Tool("ionice") is { } ionice)
        {
            psi = Make(ionice);
            foreach (var a in new[] { "-c", "3", "-t", nice }) psi.ArgumentList.Add(a); // -t: run anyway if the class can't be set
        }
        else psi = Make(nice);
        foreach (var a in new[] { "-n", "19", FfmpegPath }) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>Runs ffmpeg to completion. See <see cref="StartInfo"/> for <paramref name="lowPriority"/>.</summary>
    public async Task<bool> RunAsync(IEnumerable<string> args, CancellationToken ct = default, bool lowPriority = false)
    {
        if (!IsAvailable) return false;
        var psi = StartInfo(lowPriority);
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-y");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        if (lowPriority && psi.FileName == FfmpegPath)
            try { p.PriorityClass = ProcessPriorityClass.Idle; } catch (Exception ex) { _log.LogDebug("could not lower ffmpeg's priority: {Message}", ex.Message); }
        var err = p.StandardError.ReadToEndAsync(ct);
        _ = p.StandardOutput.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        if (p.ExitCode != 0) _log.LogWarning("ffmpeg failed ({Code}) for {Input}: {Err}", p.ExitCode, InputOf(args.ToList()), Summarize(await err));
        return p.ExitCode == 0;
    }

    /// <summary>Starts ffmpeg writing to stdout for the caller to read, as live transcoding does.</summary>
    public Process StartStreaming(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(FfmpegPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-nostdin");
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        // Drain stderr so ffmpeg can't block on a full pipe.
        _ = p.StandardError.ReadToEndAsync();
        return p;
    }
}
