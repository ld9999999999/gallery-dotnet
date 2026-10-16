using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Gallery.Core.Models;

using Gallery.Develop;

namespace Gallery.Core.Services.Develop;

/// <summary>What <c>raw-identify -v</c> reports about a RAW file.</summary>
public sealed record RawIdentity(string? Camera, int Width, int Height, int Flip, double[]? AsShot, int? Iso, string? Shutter, string? Aperture)
{
    /// <summary>Largest ÷ smallest as-shot multiplier: how much LibRaw's highlight modes 1–9 make the output darker.</summary>
    public double? HeadroomRatio => AsShot is { Length: >= 3 } m && m.Where(v => v > 0).ToArray() is { Length: >= 3 } pos ? pos.Max() / pos.Min() : null;
}

/// <summary>
/// LibRaw's command-line tools: <c>raw-identify</c> for metadata and <c>dcraw_emu</c> for pixels. They run as separate
/// processes, like ffmpeg, so a decoder crash on a damaged file cannot stop the server.
/// </summary>
public sealed partial class RawTools
{
    /// <summary>Linear 16-bit, XYZ, camera white balance, AHD demosaicing, to stdout; the highlight mode is added per call.</summary>
    internal static readonly string[] DecodeArgs = ["-4", "-o", "5", "-w", "-q", "3"];

    private readonly GalleryOptions _options;
    private readonly ILogger<RawTools> _log;
    private bool? _available;

    public RawTools(GalleryOptions options, ILogger<RawTools> log)
    {
        _options = options;
        _log = log;
    }

    public string DcrawEmuPath => _options.DcrawEmuPath;
    public string RawIdentifyPath => _options.RawIdentifyPath;

    /// <summary>Whether both tools can be started (cached).</summary>
    public bool IsAvailable => _available ??= CanRun(RawIdentifyPath) && CanRun(DcrawEmuPath);

    private static bool CanRun(string exe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return true; // both print their usage and exit when given no file
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException) { return false; }
    }

    public async Task<RawIdentity?> IdentifyAsync(string path, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(RawIdentifyPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add(path);
        using var p = Process.Start(psi)!;
        var errTask = p.StandardError.ReadToEndAsync(ct);
        var text = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var identity = ParseIdentity(text);
        if (identity is null) _log.LogDebug("raw-identify could not read {Path}: {Err}", path, (text + await errTask).Trim());
        return identity;
    }

    [GeneratedRegex(@"^Camera:\s*(?<v>.+?)(?:\s+ID:.*)?$", RegexOptions.Multiline)] private static partial Regex CameraLine();
    [GeneratedRegex(@"^Output size:\s*(?<w>\d+)\s*x\s*(?<h>\d+)", RegexOptions.Multiline)] private static partial Regex OutputSizeLine();
    [GeneratedRegex(@"^Image flip:\s*(?<v>-?\d+)", RegexOptions.Multiline)] private static partial Regex FlipLine();
    [GeneratedRegex(@"^ISO speed:\s*(?<v>[\d.]+)", RegexOptions.Multiline)] private static partial Regex IsoLine();
    [GeneratedRegex(@"^Shutter:\s*(?<v>\S+)", RegexOptions.Multiline)] private static partial Regex ShutterLine();
    [GeneratedRegex(@"^Aperture:\s*(?<v>\S+)", RegexOptions.Multiline)] private static partial Regex ApertureLine();
    [GeneratedRegex(@"^\s*As shot\s+(?<r>[\d.]+)\s+(?<g>[\d.]+)\s+(?<b>[\d.]+)\s+(?<g2>[\d.]+)", RegexOptions.Multiline)] private static partial Regex AsShotLine();

    /// <summary>Reads the fields we use from <c>raw-identify -v</c> output; null when it can't decode the file.</summary>
    internal static RawIdentity? ParseIdentity(string text)
    {
        var size = OutputSizeLine().Match(text);
        if (!size.Success || text.Contains("Cannot decode")) return null;
        static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
        var shot = AsShotLine().Match(text);
        double[]? asShot = null;
        if (shot.Success)
        {
            var g = D(shot.Groups["g"].Value);
            var g2 = D(shot.Groups["g2"].Value);
            asShot = [D(shot.Groups["r"].Value), g > 0 ? g : g2, D(shot.Groups["b"].Value)];
            if (asShot.Any(v => v <= 0)) asShot = null;
        }
        string? Value(Regex r) => r.Match(text) is { Success: true } m ? m.Groups["v"].Value.Trim() : null;
        var iso = Value(IsoLine());
        return new RawIdentity(
            Value(CameraLine()),
            int.Parse(size.Groups["w"].Value), int.Parse(size.Groups["h"].Value),
            int.TryParse(Value(FlipLine()), out var flip) ? flip : 0,
            asShot,
            iso is not null && double.TryParse(iso, CultureInfo.InvariantCulture, out var isoValue) ? (int)Math.Round(isoValue) : null,
            Value(ShutterLine()), Value(ApertureLine()));
    }

    /// <summary>
    /// Decodes to linear 16-bit XYZ. <paramref name="onSize"/> gets the (upright) size first, then <paramref name="onRow"/>
    /// every row, top to bottom, as width × 3 values. LibRaw applies the file's orientation itself.
    /// </summary>
    public async Task DecodeAsync(string path, int highlightMode, Action<int, int> onSize, Action<ReadOnlySpan<ushort>> onRow, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(DcrawEmuPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in DecodeArgs) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("-H"); psi.ArgumentList.Add(highlightMode.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-Z"); psi.ArgumentList.Add("-");
        psi.ArgumentList.Add(path);
        using var p = Process.Start(psi)!;
        using var kill = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var errTask = p.StandardError.ReadToEndAsync(CancellationToken.None);
        var stream = new BufferedStream(p.StandardOutput.BaseStream, 1 << 20);
        try
        {
            var magic = Token(stream);
            if (magic != "P6") throw new InvalidDataException($"dcraw_emu wrote no image ({(await errTask).Trim()})");
            var width = int.Parse(Token(stream));
            var height = int.Parse(Token(stream));
            var max = int.Parse(Token(stream));
            if (max != 65535) throw new InvalidDataException($"dcraw_emu wrote {max}-level output, not 16-bit");
            onSize(width, height);
            var bytes = new byte[width * 6];
            var row = new ushort[width * 3];
            for (var y = 0; y < height; y++)
            {
                await stream.ReadExactlyAsync(bytes, ct);
                var values = MemoryMarshal.Cast<byte, ushort>(bytes);
                if (BitConverter.IsLittleEndian) BinaryPrimitives.ReverseEndianness(values, row); // PPM is big-endian
                else values.CopyTo(row);
                onRow(row);
            }
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException($"dcraw_emu stopped early ({(await errTask).Trim()})");
        }
        await p.WaitForExitAsync(ct);
    }

    /// <summary>Reads one word of a PPM header (words are split by whitespace), and the one whitespace character after it.</summary>
    private static string Token(Stream s)
    {
        var sb = new System.Text.StringBuilder();
        int c;
        while ((c = s.ReadByte()) != -1 && char.IsWhiteSpace((char)c)) { }
        if (c == -1) throw new EndOfStreamException();
        do sb.Append((char)c); while ((c = s.ReadByte()) != -1 && !char.IsWhiteSpace((char)c));
        return sb.ToString();
    }
}
