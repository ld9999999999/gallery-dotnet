using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Gallery.Develop;

/// <summary>Filters on one-channel float images: box means, guided filters, Gaussian blur, and fast log2/exp2.</summary>
internal static class Filters
{
    /// <summary>Mean over a (2r+1)² window, with edge pixels repeated. <paramref name="tmp"/> and <paramref name="col"/> are work space.</summary>
    public static unsafe void Box(float[] src, float[] dst, float[] tmp, float[] col, int w, int h, int r)
    {
        if (r < 1) { Array.Copy(src, dst, w * h); return; }
        var inv = 1f / (2 * r + 1);
        // Inner columns need no edge checks: x - r >= 0 and x + r + 1 <= w - 1.
        int left = Math.Min(r, w), right = Math.Max(left, w - r - 1);
        fixed (float* ps = src, pt = tmp)
        {
            for (var y = 0; y < h; y++)
            {
                float* row = ps + y * w, o = pt + y * w;
                float sum = 0;
                for (var k = -r; k <= r; k++) sum += row[Math.Clamp(k, 0, w - 1)];
                var x = 0;
                for (; x < left; x++) { o[x] = sum * inv; sum += row[Math.Min(x + r + 1, w - 1)] - row[Math.Max(x - r, 0)]; }
                for (; x < right; x++) { o[x] = sum * inv; sum += row[x + r + 1] - row[x - r]; }
                for (; x < w; x++) { o[x] = sum * inv; sum += row[Math.Min(x + r + 1, w - 1)] - row[Math.Max(x - r, 0)]; }
            }
        }
        Array.Clear(col, 0, w);
        for (var k = -r; k <= r; k++) AddRow(col, tmp, Math.Clamp(k, 0, h - 1) * w, w, 1);
        var vinv = Vector128.Create(inv);
        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            var x = 0;
            for (; x <= w - 4; x += 4) (Vector128.LoadUnsafe(ref col[0], (nuint)x) * vinv).StoreUnsafe(ref dst[0], (nuint)(row + x));
            for (; x < w; x++) dst[row + x] = col[x] * inv;
            AddRow(col, tmp, Math.Min(y + r + 1, h - 1) * w, w, 1);
            AddRow(col, tmp, Math.Max(y - r, 0) * w, w, -1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddRow(float[] col, float[] src, int offset, int w, float sign)
    {
        var s = Vector128.Create(sign);
        var x = 0;
        for (; x <= w - 4; x += 4)
            (Vector128.LoadUnsafe(ref col[0], (nuint)x) + Vector128.LoadUnsafe(ref src[0], (nuint)(offset + x)) * s).StoreUnsafe(ref col[0], (nuint)x);
        for (; x < w; x++) col[x] += src[offset + x] * sign;
    }

    /// <summary>
    /// Guided filter of <paramref name="guide"/> onto each of <paramref name="inputs"/> (He et al.): smooths while keeping
    /// the guide's edges. When <paramref name="coefficients"/> is set, returns the box-filtered (a, b) for each input
    /// instead of the filtered image (q = a·guide + b). Work space: <paramref name="s"/> needs 6 arrays of w×h, plus col.
    /// </summary>
    public static void Guided(float[] guide, float[][] inputs, float[][] outputs, int w, int h, int r, float eps, Scratch s, bool coefficients = false)
    {
        var n = w * h;
        float[] meanI = s.F[0], varI = s.F[1], meanP = s.F[2], tmp = s.F[3], a = s.F[4], b = s.F[5];
        Box(guide, meanI, tmp, s.Col, w, h, r);
        for (var i = 0; i < n; i++) a[i] = guide[i] * guide[i];
        Box(a, varI, tmp, s.Col, w, h, r);
        for (var i = 0; i < n; i++) varI[i] -= meanI[i] * meanI[i];
        for (var k = 0; k < inputs.Length; k++)
        {
            var p = inputs[k];
            Box(p, meanP, tmp, s.Col, w, h, r);
            for (var i = 0; i < n; i++) b[i] = guide[i] * p[i];
            Box(b, a, tmp, s.Col, w, h, r); // a = mean(I·p)
            for (var i = 0; i < n; i++)
            {
                var cov = a[i] - meanI[i] * meanP[i];
                var ai = cov / (varI[i] + eps);
                a[i] = ai;
                b[i] = meanP[i] - ai * meanI[i];
            }
            if (coefficients)
            {
                Box(a, outputs[2 * k], tmp, s.Col, w, h, r);
                Box(b, outputs[2 * k + 1], tmp, s.Col, w, h, r);
            }
            else
            {
                Box(a, meanP, tmp, s.Col, w, h, r);
                var q = outputs[k];
                Box(b, q, tmp, s.Col, w, h, r);
                for (var i = 0; i < n; i++) q[i] += meanP[i] * guide[i];
            }
        }
    }

    /// <summary>Gaussian blur, rows then columns, with standard deviation sigma (pixels) and edge pixels repeated.</summary>
    public static unsafe void Gaussian(float[] src, float[] dst, float[] tmp, int w, int h, float sigma)
    {
        var k = Math.Max(1, (int)Math.Ceiling(3 * sigma));
        var weights = new float[2 * k + 1];
        float total = 0;
        for (var i = -k; i <= k; i++) total += weights[i + k] = MathF.Exp(-i * i / (2 * sigma * sigma));
        for (var i = 0; i < weights.Length; i++) weights[i] /= total;
        int left = Math.Min(k, w), right = Math.Max(left, w - k);
        fixed (float* ps = src, pt = tmp, pd = dst, pw = weights)
        {
            var c = pw + k;
            for (var y = 0; y < h; y++)
            {
                float* row = ps + y * w, o = pt + y * w;
                var x = 0;
                for (; x < left; x++) { float v = 0; for (var i = -k; i <= k; i++) v += c[i] * row[Math.Clamp(x + i, 0, w - 1)]; o[x] = v; }
                for (; x < right; x++) { float v = 0; var at = row + x; for (var i = -k; i <= k; i++) v += c[i] * at[i]; o[x] = v; }
                for (; x < w; x++) { float v = 0; for (var i = -k; i <= k; i++) v += c[i] * row[Math.Clamp(x + i, 0, w - 1)]; o[x] = v; }
            }
            for (var y = 0; y < h; y++)
            {
                var o = pd + y * w;
                for (var x = 0; x < w; x++) o[x] = 0;
                for (var i = -k; i <= k; i++)
                {
                    var from = pt + Math.Clamp(y + i, 0, h - 1) * w;
                    var wt = c[i];
                    for (var x = 0; x < w; x++) o[x] += wt * from[x];
                }
            }
        }
    }

    /// <summary>Halves an image (means of 2×2 pixels; for an odd size, the last row or column is repeated).</summary>
    public static void Halve(float[] src, int w, int h, float[] dst, out int hw, out int hh)
    {
        hw = (w + 1) / 2; hh = (h + 1) / 2;
        for (var y = 0; y < hh; y++)
        {
            int y0 = 2 * y, y1 = Math.Min(2 * y + 1, h - 1);
            for (var x = 0; x < hw; x++)
            {
                int x0 = 2 * x, x1 = Math.Min(2 * x + 1, w - 1);
                dst[y * hw + x] = 0.25f * (src[y0 * w + x0] + src[y0 * w + x1] + src[y1 * w + x0] + src[y1 * w + x1]);
            }
        }
    }

    /// <summary>Enlarges a half-size image back to w×h, bilinear (the opposite of <see cref="Halve"/>).</summary>
    public static void Double(float[] half, int hw, int hh, float[] dst, int w, int h)
    {
        var x0s = new int[w]; var x1s = new int[w]; var fxs = new float[w];
        for (var x = 0; x < w; x++)
        {
            var gx = Math.Clamp((x + 0.5f) * 0.5f - 0.5f, 0, hw - 1);
            x0s[x] = (int)gx; x1s[x] = Math.Min(x0s[x] + 1, hw - 1); fxs[x] = gx - x0s[x];
        }
        for (var y = 0; y < h; y++)
        {
            var gy = Math.Clamp((y + 0.5f) * 0.5f - 0.5f, 0, hh - 1);
            int r0 = (int)gy * hw, r1 = Math.Min((int)gy + 1, hh - 1) * hw;
            var fy = gy - (int)gy;
            var o = y * w;
            for (var x = 0; x < w; x++)
            {
                float a = half[r0 + x0s[x]], b = half[r0 + x1s[x]], c = half[r1 + x0s[x]], d = half[r1 + x1s[x]];
                var top = a + (b - a) * fxs[x];
                dst[o + x] = top + (c + (d - c) * fxs[x] - top) * fy;
            }
        }
    }

    /// <summary>2^x for small x (|x| ≤ 1), a short polynomial.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Exp2Small(float x)
    {
        x = Math.Clamp(x, -1f, 1f);
        return 1f + x * (0.6931472f + x * (0.2402265f + x * (0.0555041f + x * 0.0096181f)));
    }

    /// <summary>Bilinear value of a half-size image at full-size pixel (x, y).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float FromHalf(float[] half, int hw, int hh, int x, int y)
    {
        var gx = Math.Clamp((x + 0.5f) * 0.5f - 0.5f, 0, hw - 1);
        var gy = Math.Clamp((y + 0.5f) * 0.5f - 0.5f, 0, hh - 1);
        int x0 = (int)gx, y0 = (int)gy, x1 = Math.Min(x0 + 1, hw - 1), y1 = Math.Min(y0 + 1, hh - 1);
        float fx = gx - x0, fy = gy - y0;
        return (half[y0 * hw + x0] * (1 - fx) + half[y0 * hw + x1] * fx) * (1 - fy) + (half[y1 * hw + x0] * (1 - fx) + half[y1 * hw + x1] * fx) * fy;
    }

    /// <summary>log2 to about 1e-4, without a library call (those are slow in WebAssembly). x must be positive.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float FastLog2(float x)
    {
        var bits = BitConverter.SingleToInt32Bits(x);
        var e = ((bits >> 23) & 255) - 127;
        var m = BitConverter.Int32BitsToSingle((bits & 0x7FFFFF) | 0x3F800000); // 1 ≤ m < 2
        var p = -1.7417939f + (2.8212026f + (-1.4699568f + (0.44717955f - 0.056570851f * m) * m) * m) * m;
        return e + p;
    }

    /// <summary>2^x to about 1e-4 relative, for |x| up to about 100.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float FastExp2(float x)
    {
        x = Math.Clamp(x, -100f, 100f);
        var i = (int)MathF.Floor(x);
        var f = x - i;
        var p = 1f + f * (0.6931472f + f * (0.2402265f + f * (0.0555041f + f * (0.0096181f + f * 0.0013334f))));
        return p * BitConverter.Int32BitsToSingle((i + 127) << 23);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SmoothStep(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3 - 2 * t);
    }
}

/// <summary>Work arrays for a tile, kept for reuse and made larger when needed.</summary>
internal sealed class Scratch
{
    public float[][] F = [];
    public float[] Col = [];
    public byte[] Bytes = [];
    public int Size;

    public void Ensure(int pixels, int width, int count = 16)
    {
        if (pixels > Size || F.Length < count)
        {
            Size = Math.Max(pixels, Size);
            F = Enumerable.Range(0, count).Select(_ => new float[Size]).ToArray();
        }
        if (Col.Length < width) Col = new float[width];
        if (Bytes.Length < pixels * 4) Bytes = new byte[Size * 4];
    }
}
