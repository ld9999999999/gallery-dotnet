using System.Diagnostics;
using Gallery.Core.Models;

namespace Gallery.Core.Services;

/// <summary>
/// Decodes HEIC and HEIF photos with libheif's command-line tools, the reference decoder. heif-dec joins a phone's tile
/// grid, applies the stored turn and mirror, and reads files that ffmpeg can't (ffmpeg 7.1 fails on some recent iPhone
/// files with "error reading header", and before 7.1 it decodes a single tile). Like ffmpeg, it runs as a separate
/// process, so a decoder crash can't take the gallery down. Install with libheif-examples (plus
/// libheif-plugin-libde265 for HEVC) on Debian/Ubuntu, or brew install libheif on macOS.
/// </summary>
public sealed class HeifDecoder
{
    /// <summary>Extensions libheif reads here. AVIF stays with ffmpeg, which reads it with dav1d.</summary>
    public static readonly HashSet<string> Formats = new(StringComparer.OrdinalIgnoreCase) { "heic", "heif" };

    /// <summary>The reason reported when the tools are missing, including how to install them.</summary>
    public const string NotInstalled = "libheif's heif-dec is not installed (Debian/Ubuntu: apt install libheif-examples libheif-plugin-libde265; macOS: brew install libheif)";

    private readonly GalleryOptions _opt;
    private readonly ILogger<HeifDecoder> _log;
    private string? _decoder;   // the tool found: heif-dec, or heif-convert from libheif before 1.17
    private bool _checked;
    private readonly object _sync = new();

    public HeifDecoder(GalleryOptions opt, ILogger<HeifDecoder> log)
    {
        _opt = opt;
        _log = log;
    }

    public bool IsAvailable => Decoder is not null;

    public string? Version { get; private set; }

    private string? Decoder
    {
        get
        {
            lock (_sync)
            {
                if (_checked) return _decoder;
                _checked = true;
                // heif-convert is heif-dec's older name and accepts the same arguments used here.
                var candidates = _opt.HeifDecPath == "heif-dec" ? new[] { "heif-dec", "heif-convert" } : new[] { _opt.HeifDecPath };
                foreach (var c in candidates)
                {
                    try
                    {
                        using var p = Process.Start(new ProcessStartInfo(c, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                        var output = p!.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                        p.WaitForExit(5000);
                        if (p.ExitCode != 0) continue;
                        _decoder = c;
                        Version = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                        _log.LogInformation("HEIC photos are decoded with libheif ({Tool} {Version})", c, Version);
                        return _decoder;
                    }
                    catch (Exception) { }
                }
                return null;
            }
        }
    }

    /// <summary>
    /// Decodes the picture upright, as JPEG at quality 92 (thumbnails, previews, faces) or as uncompressed PNG for the
    /// editor (16-bit when the photo has more than 8 bits). Returns null bytes and a reason when libheif can't decode it.
    /// </summary>
    public async Task<(byte[]? Bytes, string? Error)> DecodeAsync(string path, bool png = false, CancellationToken ct = default)
    {
        if (Decoder is not { } tool) return (null, NotInstalled);
        var dir = Directory.CreateTempSubdirectory("gallery-heif-");
        try
        {
            var output = Path.Combine(dir.FullName, png ? "out.png" : "out.jpg");
            var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            if (!png) { psi.ArgumentList.Add("-q"); psi.ArgumentList.Add("92"); }
            if (Path.GetFileName(tool).StartsWith("heif-dec", StringComparison.Ordinal))
            {
                // heif-convert lacks these: skip progress lines, and skip compressing a PNG that is read back right away.
                psi.ArgumentList.Add("--quiet");
                if (png) { psi.ArgumentList.Add("--png-compression-level"); psi.ArgumentList.Add("0"); }
            }
            psi.ArgumentList.Add(path);
            psi.ArgumentList.Add(output);
            using var p = Process.Start(psi)!;
            var stderr = p.StandardError.ReadToEndAsync(ct);
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            try { await p.WaitForExitAsync(ct); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
            var err = (await stderr + " " + await stdout).Trim();
            // A file with several images gets numbered names (out-1.jpg…); the first is the primary.
            var written = File.Exists(output) ? output : dir.EnumerateFiles(png ? "out*.png" : "out*.jpg").Select(f => f.FullName).Order().FirstOrDefault();
            if (p.ExitCode != 0 || written is null)
            {
                var reason = err.Length > 0 ? Ffmpeg.Summarize(err) : $"exit code {p.ExitCode}, no output";
                _log.LogDebug("heif-dec failed ({Code}) for {Path}: {Err}", p.ExitCode, path, reason);
                return (null, reason);
            }
            return (await File.ReadAllBytesAsync(written, ct), null);
        }
        finally
        {
            try { dir.Delete(true); } catch (IOException) { }
        }
    }

    /// <summary>The primary image's size as displayed (turn applied), read from heif-info. Null when that fails.</summary>
    public async Task<(int Width, int Height)?> ProbeSizeAsync(string path, CancellationToken ct = default)
    {
        if (!IsAvailable) return null;
        try
        {
            var psi = new ProcessStartInfo(_opt.HeifInfoPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add(path);
            using var p = Process.Start(psi)!;
            var stderr = p.StandardError.ReadToEndAsync(ct);
            var stdout = await p.StandardOutput.ReadToEndAsync(ct);
            await stderr;
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0 ? ParseSize(stdout) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "heif-info failed for {Path}", path);
            return null;
        }
    }

    /// <summary>"image: 3024x4032 (id=49), primary" in heif-info's output.</summary>
    internal static (int Width, int Height)? ParseSize(string heifInfo)
    {
        var m = System.Text.RegularExpressions.Regex.Match(heifInfo, @"^image:\s*(\d+)x(\d+).*primary", System.Text.RegularExpressions.RegexOptions.Multiline);
        return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : null;
    }
}
