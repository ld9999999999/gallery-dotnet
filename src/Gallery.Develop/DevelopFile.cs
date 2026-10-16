using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Gallery.Develop;

/// <summary>The JSON header of a <c>.gdev</c> file: what the pixels are and where they came from.</summary>
public sealed record DevelopHeader
{
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>Always <see cref="DevelopFile.ColorSpace"/>: linear light, Rec.2020 primaries, D65 white; 1.0 is sensor (or file) white.</summary>
    public string ColorSpace { get; init; } = DevelopFile.ColorSpace;
    /// <summary>"raw" (decoded by LibRaw) or "image" (a JPEG, PNG, HEIC… converted to linear light).</summary>
    public string SourceKind { get; init; } = DevelopFile.RawSource;
    public long AssetId { get; init; }
    public string Hash { get; init; } = "";
    /// <summary>Size of the whole picture at full resolution, upright. Width and Height are smaller for a smaller copy or a crop.</summary>
    public int FullWidth { get; init; }
    public int FullHeight { get; init; }
    /// <summary>The item's manual rotation (rotate-degrees) already applied, on top of the file's own orientation.</summary>
    public int Rotation { get; init; }
    /// <summary>For a crop: its top-left corner in the upright full-resolution picture.</summary>
    public int? CropX { get; init; }
    public int? CropY { get; init; }
    /// <summary>
    /// For a cropped picture (<see cref="Geometry"/>; FullWidth and FullHeight are then the crop's size): the whole
    /// picture's full size. Filter sizes are measured against it, so cropping doesn't change how strong sharpening looks.
    /// </summary>
    public int? UncroppedWidth { get; init; }
    public int? UncroppedHeight { get; init; }
    public string? Camera { get; init; }
    /// <summary>As-shot white-balance multipliers (R, G, B), for RAWs that record them.</summary>
    public double[]? AsShotMultipliers { get; init; }
    /// <summary>True when values above 1.0 hold rebuilt highlights; false when the decoder clipped at white.</summary>
    public bool HighlightHeadroom { get; init; }
    /// <summary>Exposure the camera maker recommends adding by default (DNG BaselineExposure), in stops.</summary>
    public double BaselineExposure { get; init; }
    /// <summary>
    /// How the pixel bytes are laid out: <see cref="DevelopFile.Interleaved"/> (each half float's two bytes together) or
    /// <see cref="DevelopFile.Shuffled"/> (every high byte, then every low byte), which compresses about a quarter better.
    /// </summary>
    public string Layout { get; init; } = DevelopFile.Interleaved;
}

/// <summary>
/// The editing file format, in order: <c>"GDEV"</c> -> format version (uint16) -> reserved (uint16) -> header length
/// (uint32) -> the UTF-8 JSON <see cref="DevelopHeader"/>, padded with spaces so the pixels start on an 8-byte boundary
/// -> Width × Height × 3 IEEE half floats (R, G, B interleaved), little-endian or byte-shuffled (see
/// <see cref="DevelopHeader.Layout"/>). Copies sent to the editor are shuffled and gzip-compressed. The low bytes are
/// mostly sensor noise and barely compress, so keeping them apart lets the high bytes compress well.
/// </summary>
public static class DevelopFile
{
    public const string ColorSpace = "rec2020-linear";
    public const string RawSource = "raw";
    public const string ImageSource = "image";
    public const ushort FormatVersion = 1;
    public const int PreambleBytes = 12;
    public const string MediaType = "application/x-gallery-develop";
    public const string Interleaved = "interleaved";
    public const string Shuffled = "shuffled";

    private static ReadOnlySpan<byte> Magic => "GDEV"u8;

    /// <summary>Writes a header and its pixels (half-float bit patterns, Width × Height × 3), shuffled or not.</summary>
    public static void Write(Stream output, DevelopHeader header, ReadOnlySpan<ushort> halfs, bool shuffle = false)
    {
        if (halfs.Length != (long)header.Width * header.Height * 3)
            throw new ArgumentException($"{halfs.Length} values do not fit a {header.Width}×{header.Height} RGB image");
        header = header with { Layout = shuffle ? Shuffled : Interleaved };
        var json = JsonSerializer.SerializeToUtf8Bytes(header, DevelopJson.Default.DevelopHeader);
        var pad = (8 - (PreambleBytes + json.Length) % 8) % 8;
        Span<byte> pre = stackalloc byte[PreambleBytes];
        Magic.CopyTo(pre);
        BinaryPrimitives.WriteUInt16LittleEndian(pre[4..], FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(pre[6..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(pre[8..], (uint)(json.Length + pad));
        output.Write(pre);
        output.Write(json);
        for (var i = 0; i < pad; i++) output.WriteByte((byte)' ');
        if (shuffle)
        {
            var plane = new byte[halfs.Length];
            for (var i = 0; i < halfs.Length; i++) plane[i] = (byte)(halfs[i] >> 8);
            output.Write(plane);
            for (var i = 0; i < halfs.Length; i++) plane[i] = (byte)halfs[i];
            output.Write(plane);
        }
        else if (BitConverter.IsLittleEndian) output.Write(MemoryMarshal.AsBytes(halfs));
        else
        {
            Span<byte> b = stackalloc byte[2];
            foreach (var h in halfs) { BinaryPrimitives.WriteUInt16LittleEndian(b, h); output.Write(b); }
        }
    }

    /// <summary>Reads the header and leaves the stream at the first pixel. Returns the pixel data's offset from the start.</summary>
    public static (DevelopHeader Header, long PixelOffset) ReadHeader(Stream input)
    {
        Span<byte> pre = stackalloc byte[PreambleBytes];
        input.ReadExactly(pre);
        if (!pre[..4].SequenceEqual(Magic)) throw new InvalidDataException("not a .gdev file");
        var version = BinaryPrimitives.ReadUInt16LittleEndian(pre[4..]);
        if (version != FormatVersion) throw new InvalidDataException($".gdev format version {version} is not supported");
        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(pre[8..]);
        var json = new byte[length];
        input.ReadExactly(json);
        var header = JsonSerializer.Deserialize(json, DevelopJson.Default.DevelopHeader) ?? throw new InvalidDataException("empty .gdev header");
        return (header, PreambleBytes + length);
    }

    public static (DevelopHeader Header, ushort[] Halfs) Read(Stream input)
    {
        var (header, _) = ReadHeader(input);
        var halfs = new ushort[(long)header.Width * header.Height * 3];
        if (header.Layout == Shuffled)
        {
            var hi = new byte[halfs.Length];
            var lo = new byte[halfs.Length];
            input.ReadExactly(hi);
            input.ReadExactly(lo);
            for (var i = 0; i < halfs.Length; i++) halfs[i] = (ushort)(hi[i] << 8 | lo[i]);
            return (header, halfs);
        }
        input.ReadExactly(MemoryMarshal.AsBytes(halfs.AsSpan()));
        if (!BitConverter.IsLittleEndian) for (var i = 0; i < halfs.Length; i++) halfs[i] = BinaryPrimitives.ReverseEndianness(halfs[i]);
        return (header, halfs);
    }
}
