using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Gallery.Core.Services;

/// <summary>
/// Reads the capture date a camera writes into an AVI's header, which ffprobe doesn't pick up. Cameras store it in one
/// of three places (the same ones ExifTool reads):
/// <list type="bullet">
/// <item>an EXIF block in the video stream's <c>strd</c> chunk, starting "AVIF" (Fujifilm and others): IFD0 with Make,
/// Model and DateTime, and an Exif IFD with DateTimeOriginal. Little-endian, with offsets counted from the IFD, 8 bytes in;</item>
/// <item>an <c>IDIT</c> chunk (Canon, Nikon, Pentax and others): "MON SEP 28 12:51:39 2010" or "2010:09:28 12:51:39";</item>
/// <item>the <c>INFO</c> list's <c>ICRD</c> (creation date), sometimes only a day.</item>
/// </list>
/// The first valid date wins, in this order: DateTimeOriginal, DateTimeDigitized, IDIT, the EXIF DateTime (when the
/// file was last changed), ICRD. All are camera-local time, like photos' EXIF dates. Only the header lists are read;
/// the movie data is skipped.
/// </summary>
public static partial class AviDate
{
    private const int MaxChunk = 1 << 20; // header chunks bigger than this are skipped (1 MiB)

    public static bool IsAvi(string path) => path.EndsWith(".avi", StringComparison.OrdinalIgnoreCase);

    /// <summary>The capture date from the AVI's header, or null if there is none or the file isn't an AVI.</summary>
    public static DateTime? Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
            Span<byte> head = stackalloc byte[12];
            if (fs.Read(head) < 12 || !head[..4].SequenceEqual("RIFF"u8) || !head[8..12].SequenceEqual("AVI "u8)) return null;
            var end = Math.Min(fs.Length, 8L + BinaryPrimitives.ReadUInt32LittleEndian(head[4..8]));
            var found = new Found();
            Walk(fs, 12, end, found, 0);
            return new[] { found.Original, found.Digitized, found.Idit, found.Modified, found.Created }.FirstOrDefault(d => d is { } x && CaptureDate.IsPlausible(x));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or EndOfStreamException) { return null; }
    }

    private sealed class Found
    {
        public DateTime? Original, Digitized, Modified, Idit, Created;
    }

    /// <summary>Walks the chunks from <paramref name="pos"/> to <paramref name="end"/>, descending into lists but skipping the movie data.</summary>
    private static void Walk(FileStream fs, long pos, long end, Found found, int depth)
    {
        Span<byte> h = stackalloc byte[12];
        while (pos + 8 <= end)
        {
            fs.Position = pos;
            if (fs.Read(h[..8]) < 8) return;
            var id = Encoding.ASCII.GetString(h[..4]);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(h[4..8]), data = pos + 8;
            if (id == "LIST" && size >= 4 && depth < 4)
            {
                if (fs.Read(h[8..12]) < 4) return;
                var type = Encoding.ASCII.GetString(h[8..12]);
                if (type != "movi") Walk(fs, data + 4, Math.Min(end, data + size), found, depth + 1);
            }
            else if (id is "strd" or "IDIT" or "ICRD" && size is > 0 and <= MaxChunk)
            {
                var bytes = new byte[size];
                fs.ReadExactly(bytes);
                if (id == "strd") ReadExif(bytes, found);
                else if (id == "IDIT") found.Idit ??= ParseText(bytes);
                else found.Created ??= ParseText(bytes);
            }
            pos = data + size + (size & 1); // chunks are padded to even length
        }
    }

    /// <summary>The "AVIF" EXIF block. IFD0 starts 8 bytes in, and offsets count from there.</summary>
    private static void ReadExif(byte[] b, Found found)
    {
        if (b.Length < 14 || !b.AsSpan(0, 4).SequenceEqual("AVIF"u8)) return;
        var tiff = b.AsSpan(8);
        foreach (var (tag, value) in Entries(tiff, 0))
        {
            if (tag == 0x0132) found.Modified ??= ParseExif(tiff, value);
            else if (tag == 0x8769 && value.Type is 4 or 13 && value.Count == 1)
                foreach (var (sub, subValue) in Entries(tiff, (int)value.Offset))
                {
                    if (sub == 0x9003) found.Original ??= ParseExif(tiff, subValue);
                    else if (sub == 0x9004) found.Digitized ??= ParseExif(tiff, subValue);
                }
        }
    }

    private readonly record struct Value(ushort Type, uint Count, uint Offset, int At);

    /// <summary>The entries of the IFD at <paramref name="at"/>, each with its tag, type, count and value (or the offset to it).</summary>
    private static List<(ushort Tag, Value Value)> Entries(ReadOnlySpan<byte> tiff, int at)
    {
        var list = new List<(ushort, Value)>();
        if (at < 0 || at + 2 > tiff.Length) return list;
        int n = BinaryPrimitives.ReadUInt16LittleEndian(tiff[at..]);
        if (n > 500) return list;
        for (var i = 0; i < n; i++)
        {
            var e = at + 2 + i * 12;
            if (e + 12 > tiff.Length) break;
            list.Add((BinaryPrimitives.ReadUInt16LittleEndian(tiff[e..]), new Value(BinaryPrimitives.ReadUInt16LittleEndian(tiff[(e + 2)..]),
                BinaryPrimitives.ReadUInt32LittleEndian(tiff[(e + 4)..]), BinaryPrimitives.ReadUInt32LittleEndian(tiff[(e + 8)..]), e + 8)));
        }
        return list;
    }

    /// <summary>Reads an ASCII EXIF date ("2010:09:28 12:51:39") from its offset in the block.</summary>
    private static DateTime? ParseExif(ReadOnlySpan<byte> tiff, Value v)
    {
        if (v.Type != 2 || v.Count < 19) return null;
        var start = v.Count <= 4 ? v.At : (int)v.Offset;
        if (start < 0 || start + 19 > tiff.Length) return null;
        var text = Encoding.ASCII.GetString(tiff.Slice(start, 19));
        return DateTime.TryParseExact(text, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    [GeneratedRegex(@"(\d{4})[:/.\-](\d{1,2})[:/.\-](\d{1,2})(?:\D+(\d{1,2}):(\d{2})(?::(\d{2}))?)?")]
    private static partial Regex Numeric();

    [GeneratedRegex(@"[A-Za-z]{3}\s+([A-Za-z]{3})\s+(\d{1,2})\s+(\d{1,2}):(\d{2}):(\d{2})\s+(\d{4})")]
    private static partial Regex Ctime();

    /// <summary>
    /// Parses a date as IDIT and ICRD write it: "MON SEP 28 12:51:39 2010" (C's ctime) or numeric forms like
    /// "2010:09:28 12:51:39", "2010/09/28/ 12:51" and "2010-09-28". A bare day becomes its midnight.
    /// </summary>
    internal static DateTime? ParseText(ReadOnlySpan<byte> bytes)
    {
        var text = Encoding.ASCII.GetString(bytes).TrimEnd('\0', '\n', '\r', ' ');
        try
        {
            if (Ctime().Match(text) is { Success: true } c)
            {
                var month = Array.FindIndex(CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames,
                    m => m.Length > 0 && m.Equals(c.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) + 1;
                if (month > 0)
                    return new DateTime(int.Parse(c.Groups[6].Value), month, int.Parse(c.Groups[2].Value),
                        int.Parse(c.Groups[3].Value), int.Parse(c.Groups[4].Value), int.Parse(c.Groups[5].Value));
            }
            if (Numeric().Match(text) is { Success: true } n)
            {
                int G(int i) => n.Groups[i].Success ? int.Parse(n.Groups[i].Value) : 0;
                return new DateTime(G(1), G(2), G(3), G(4), G(5), G(6));
            }
        }
        catch (ArgumentOutOfRangeException) { } // out-of-range values (month 13, day 32) mean no date
        return null;
    }
}
