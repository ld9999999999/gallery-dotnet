namespace Gallery.Core.Services;

/// <summary>A JPEG stream found inside a file.</summary>
/// <param name="Sof">Start-of-frame marker (0xC0 baseline, 0xC1 extended, 0xC2 progressive, 0xC3 lossless, …), 0 if none.</param>
/// <param name="Decodable">True for baseline / extended / progressive frames, which normal JPEG decoders can read.</param>
public sealed record JpegStream(int Offset, int Length, byte Sof, int Width, int Height, bool Decodable)
{
    public long Pixels => (long)Width * Height;
}

/// <summary>
/// Pulls the embedded JPEG preview out of a camera RAW file. Almost every RAW format (CR2, CR3, NEF, ARW, ORF, RW2, RAF,
/// DNG, PEF, …) holds one or more JPEG versions of the photo, so searching for JPEG streams saves needing a native
/// RAW decoder. Full RAW decoding happens only for editing (see DevelopService).
///
/// Some JPEG streams in a RAW file aren't pictures you can show: Canon CR2 stores the sensor data itself as lossless
/// JPEG (SOF3), and many DNGs do the same in tiles. Those are valid and much bigger than the preview, but normal
/// decoders can't read them. So only baseline, extended and progressive frames are considered, and the one with the
/// most pixels wins.
/// </summary>
public static class RawPreviewExtractor
{
    public static byte[]? ExtractLargestJpeg(string path) => ExtractLargestJpeg(File.ReadAllBytes(path));

    /// <summary>The decodable JPEG with the most pixels (the bigger stream on a tie), or null if there is none.</summary>
    public static byte[]? ExtractLargestJpeg(ReadOnlySpan<byte> data)
    {
        var best = PickPreview(FindJpegs(data));
        return best is null ? null : data.Slice(best.Offset, best.Length).ToArray();
    }

    public static JpegStream? PickPreview(IEnumerable<JpegStream> streams)
    {
        JpegStream? best = null;
        foreach (var j in streams)
        {
            if (!j.Decodable) continue;
            if (best is null || j.Pixels > best.Pixels || (j.Pixels == best.Pixels && j.Length > best.Length)) best = j;
        }
        return best;
    }

    /// <summary>For log messages, e.g. "5184×3456 lossless, 160×120 baseline".</summary>
    public static string Describe(IEnumerable<JpegStream> streams) =>
        string.Join(", ", streams.Select(j => $"{j.Width}×{j.Height} {j.Sof switch
        {
            0xC0 => "baseline",
            0xC1 => "extended",
            0xC2 => "progressive",
            0xC3 or 0xC7 or 0xCB or 0xCF => "lossless",
            0xC5 or 0xC6 or 0xCD or 0xCE => "hierarchical",
            0xC9 or 0xCA => "arithmetic-coded",
            0 => "no frame header",
            _ => $"type 0x{j.Sof:X2}",
        }}"));

    /// <summary>Every valid top-level JPEG stream in the data, in file order.</summary>
    public static List<JpegStream> FindJpegs(ReadOnlySpan<byte> data)
    {
        var found = new List<JpegStream>();
        var i = 0;
        while (i < data.Length - 3)
        {
            // SOI followed by a marker byte: FF D8 FF xx
            if (data[i] == 0xFF && data[i + 1] == 0xD8 && data[i + 2] == 0xFF)
            {
                var (end, sof, width, height) = Walk(data, i);
                if (end > 0)
                {
                    found.Add(new JpegStream(i, end - i, sof, width, height, sof is 0xC0 or 0xC1 or 0xC2 && width > 0 && height > 0));
                    i = end; // skip its contents so thumbnails inside its EXIF aren't listed separately
                    continue;
                }
            }
            i++;
        }
        return found;
    }

    private static bool IsSof(byte m) => m is >= 0xC0 and <= 0xCF && m is not (0xC4 or 0xC8 or 0xCC); // DHT, JPG, DAC are not frames

    /// <summary>
    /// Walks the JPEG segments from SOI. Returns the index just after EOI (-1 if the structure is invalid) and the
    /// first frame header's type and size.
    /// </summary>
    private static (int End, byte Sof, int Width, int Height) Walk(ReadOnlySpan<byte> d, int start)
    {
        byte sof = 0;
        int width = 0, height = 0;
        var p = start + 2;
        while (p + 4 <= d.Length)
        {
            if (d[p] != 0xFF) return (-1, 0, 0, 0);
            var marker = d[p + 1];
            if (marker == 0xD8) return (-1, 0, 0, 0);                   // SOI inside SOI: not a real stream
            if (marker == 0xD9) return (p + 2, sof, width, height);     // EOI with no scan data (unusual)
            if (marker == 0xFF) { p++; continue; }                     // fill byte
            if (marker is >= 0xD0 and <= 0xD7 || marker == 0x01) { p += 2; continue; } // standalone markers
            var segLen = (d[p + 2] << 8) | d[p + 3];
            if (segLen < 2) return (-1, 0, 0, 0);
            if (sof == 0 && IsSof(marker) && p + 9 <= d.Length)
            {
                sof = marker;
                height = (d[p + 5] << 8) | d[p + 6];
                width = (d[p + 7] << 8) | d[p + 8];
            }
            if (marker == 0xDA)
            {
                // Start of scan: compressed data runs to EOI (FF D9); FF 00 and RSTn inside it are skipped.
                var q = p + 2 + segLen;
                while (q + 1 < d.Length)
                {
                    if (d[q] == 0xFF)
                    {
                        var m = d[q + 1];
                        if (m == 0xD9) return (q + 2, sof, width, height);
                        if (m == 0xD8) return (-1, 0, 0, 0);
                        if (m != 0x00 && !(m is >= 0xD0 and <= 0xD7) && m != 0xFF)
                        {
                            // Another segment (e.g. DHT between progressive scans); keep walking.
                            p = q;
                            goto next;
                        }
                    }
                    q++;
                }
                return (-1, 0, 0, 0);
            }
            p += 2 + segLen;
        next:;
        }
        return (-1, 0, 0, 0);
    }
}
