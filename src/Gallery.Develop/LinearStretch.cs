namespace Gallery.Develop;

/// <summary>
/// Stretches a picture along one side with linear interpolation, for an anamorphic photo's long side.
/// Shrinking is the area downscaler's job. Each output pixel samples the source at the position of its centre.
/// </summary>
public static class LinearStretch
{
    /// <summary>For each output position along an axis: the two source positions on each side, and the second one's weight.</summary>
    private static (int[] A, int[] B, float[] T) Taps(int source, int target)
    {
        var a = new int[target];
        var b = new int[target];
        var t = new float[target];
        var step = (double)source / target;
        for (var i = 0; i < target; i++)
        {
            var at = Math.Clamp((i + 0.5) * step - 0.5, 0, source - 1);
            a[i] = (int)Math.Floor(at);
            b[i] = Math.Min(a[i] + 1, source - 1);
            t[i] = (float)(at - a[i]);
        }
        return (a, b, t);
    }

    /// <summary>One channel, row by row, from width × height to targetWidth × targetHeight (one or both at least as large).</summary>
    public static float[] Plane(float[] src, int width, int height, int targetWidth, int targetHeight)
    {
        if (targetWidth == width && targetHeight == height) return src;
        var (xa, xb, xt) = Taps(width, targetWidth);
        var (ya, yb, yt) = Taps(height, targetHeight);
        var dst = new float[targetWidth * targetHeight];
        Parallel.For(0, targetHeight, y =>
        {
            int r0 = ya[y] * width, r1 = yb[y] * width, o = y * targetWidth;
            var ty = yt[y];
            for (var x = 0; x < targetWidth; x++)
            {
                var top = src[r0 + xa[x]] + (src[r0 + xb[x]] - src[r0 + xa[x]]) * xt[x];
                var bottom = src[r1 + xa[x]] + (src[r1 + xb[x]] - src[r1 + xa[x]]) * xt[x];
                dst[o + x] = top + (bottom - top) * ty;
            }
        });
        return dst;
    }

    /// <summary>Interleaved RGB half floats (an editing copy's pixels), same as <see cref="Plane"/>.</summary>
    public static ushort[] Halfs(ushort[] src, int width, int height, int targetWidth, int targetHeight)
    {
        if (targetWidth == width && targetHeight == height) return src;
        var (xa, xb, xt) = Taps(width, targetWidth);
        var (ya, yb, yt) = Taps(height, targetHeight);
        var dst = new ushort[targetWidth * targetHeight * 3];
        static float F(ushort h) => (float)BitConverter.UInt16BitsToHalf(h);
        Parallel.For(0, targetHeight, y =>
        {
            int r0 = ya[y] * width, r1 = yb[y] * width, o = y * targetWidth * 3;
            var ty = yt[y];
            for (var x = 0; x < targetWidth; x++)
                for (var c = 0; c < 3; c++)
                {
                    float a = F(src[(r0 + xa[x]) * 3 + c]), b = F(src[(r0 + xb[x]) * 3 + c]);
                    float d = F(src[(r1 + xa[x]) * 3 + c]), e = F(src[(r1 + xb[x]) * 3 + c]);
                    var top = a + (b - a) * xt[x];
                    var bottom = d + (e - d) * xt[x];
                    dst[o + x * 3 + c] = BitConverter.HalfToUInt16Bits((Half)(top + (bottom - top) * ty));
                }
        });
        return dst;
    }
}
