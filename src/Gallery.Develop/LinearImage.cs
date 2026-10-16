using System.Buffers.Binary;

namespace Gallery.Develop;

/// <summary>An editing copy in memory: linear Rec.2020, one float array per channel (the layout the renderer's loops need).</summary>
public sealed class LinearImage
{
    private static readonly Lazy<float[]> HalfTable = new(() =>
    {
        var t = new float[65536];
        for (var i = 0; i < t.Length; i++) t[i] = (float)BitConverter.UInt16BitsToHalf((ushort)i);
        return t;
    });

    public LinearImage(DevelopHeader header, int width, int height, float[] r, float[] g, float[] b)
    {
        if (r.Length != width * height || g.Length != r.Length || b.Length != r.Length) throw new ArgumentException("channel sizes do not match");
        Header = header;
        Width = width;
        Height = height;
        R = r;
        G = g;
        B = b;
    }

    public DevelopHeader Header { get; }
    public int Width { get; }
    public int Height { get; }
    public float[] R { get; }
    public float[] G { get; }
    public float[] B { get; }
    /// <summary>RAWs get the base tone curve. Other photos are already toned, so by default they render as they are.</summary>
    public bool IsRaw => Header.SourceKind == DevelopFile.RawSource;

    /// <summary>Reads a decompressed .gdev file, interleaved or shuffled.</summary>
    public static LinearImage FromGdev(byte[] file)
    {
        var (header, offset) = DevelopFile.ReadHeader(new MemoryStream(file, writable: false));
        var n = header.Width * header.Height;
        if (file.Length < offset + (long)n * 6) throw new InvalidDataException("the .gdev file is cut short");
        float[] r = new float[n], g = new float[n], b = new float[n];
        var table = HalfTable.Value;
        var o = (int)offset;
        if (header.Layout == DevelopFile.Shuffled)
        {
            var lo = o + n * 3;
            for (var i = 0; i < n; i++)
            {
                var k = i * 3;
                r[i] = table[file[o + k] << 8 | file[lo + k]];
                g[i] = table[file[o + k + 1] << 8 | file[lo + k + 1]];
                b[i] = table[file[o + k + 2] << 8 | file[lo + k + 2]];
            }
        }
        else
        {
            for (var i = 0; i < n; i++)
            {
                var k = o + i * 6;
                r[i] = table[BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(k))];
                g[i] = table[BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(k + 2))];
                b[i] = table[BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(k + 4))];
            }
        }
        return new LinearImage(header, header.Width, header.Height, r, g, b);
    }

    /// <summary>The pixels as interleaved half-float bit patterns, as <see cref="DevelopFile.Write"/> takes them.</summary>
    public ushort[] ToHalfs()
    {
        var n = Width * Height;
        var halfs = new ushort[n * 3];
        for (var i = 0; i < n; i++)
        {
            halfs[i * 3] = BitConverter.HalfToUInt16Bits((Half)R[i]);
            halfs[i * 3 + 1] = BitConverter.HalfToUInt16Bits((Half)G[i]);
            halfs[i * 3 + 2] = BitConverter.HalfToUInt16Bits((Half)B[i]);
        }
        return halfs;
    }

    /// <summary>A copy shrunk to fit the given size (area average), or this image if it already fits.</summary>
    public LinearImage FitWithin(int maxWidth, int maxHeight)
    {
        var f = Math.Min((double)maxWidth / Width, (double)maxHeight / Height);
        if (f >= 1) return this;
        int w = Math.Max(1, (int)Math.Round(Width * f)), h = Math.Max(1, (int)Math.Round(Height * f));
        float[] r = new float[w * h], g = new float[w * h], b = new float[w * h];
        var down = new AreaDownscaler(Width, Height, w, h, (y, row) =>
        {
            for (var x = 0; x < w; x++) { r[y * w + x] = row[x * 3]; g[y * w + x] = row[x * 3 + 1]; b[y * w + x] = row[x * 3 + 2]; }
        });
        var src = new float[Width * 3];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++) { var i = y * Width + x; src[x * 3] = R[i]; src[x * 3 + 1] = G[i]; src[x * 3 + 2] = B[i]; }
            down.AddRow(src);
        }
        return new LinearImage(Header, w, h, r, g, b);
    }
}
