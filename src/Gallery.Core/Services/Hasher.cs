using System.Buffers;
using System.IO.Hashing;

namespace Gallery.Core.Services;

/// <summary>
/// Fast content hashing with XxHash3 (64-bit). Files up to <see cref="FullHashLimit"/> are hashed in full.
/// Larger files (usually video) are hashed from three 4 MiB samples (start, middle, end) plus the file length,
/// which is enough to find duplicates without reading tens of gigabytes during a scan.
/// </summary>
public static class Hasher
{
    public const long FullHashLimit = 256L * 1024 * 1024;
    private const int SampleSize = 4 * 1024 * 1024;
    private const int BufferSize = 1024 * 1024;

    public static string HashFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        return fs.Length <= FullHashLimit ? HashFull(fs) : HashSampled(fs);
    }

    public static string HashFull(Stream s)
    {
        var h = new XxHash3();
        var buf = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0) h.Append(buf.AsSpan(0, n));
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
        return h.GetCurrentHashAsUInt64().ToString("x16");
    }

    public static string HashSampled(FileStream fs)
    {
        var h = new XxHash3();
        Span<byte> len = stackalloc byte[8];
        BitConverter.TryWriteBytes(len, fs.Length);
        h.Append(len);
        var buf = ArrayPool<byte>.Shared.Rent(SampleSize);
        try
        {
            foreach (var offset in new[] { 0L, fs.Length / 2 - SampleSize / 2, fs.Length - SampleSize })
            {
                fs.Seek(offset, SeekOrigin.Begin);
                var read = fs.ReadAtLeast(buf.AsSpan(0, SampleSize), SampleSize, throwOnEndOfStream: false);
                h.Append(buf.AsSpan(0, read));
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
        // The "s" prefix keeps a sampled hash from ever matching a full hash.
        return "s" + (h.GetCurrentHashAsUInt64() >> 4).ToString("x15");
    }
}
