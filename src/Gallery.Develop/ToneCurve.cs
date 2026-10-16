namespace Gallery.Develop;

/// <summary>Tone curves: the RAW base curve, contrast, brightness, and the user's point curves.</summary>
public static class ToneCurve
{
    /// <summary>Scene value used as middle grey in a RAW (white, where the sensor clips, is 1.0).</summary>
    public const double SceneGrey = 0.10;
    /// <summary>Linear display value that middle grey is shown at (about 48 % in sRGB).</summary>
    public const double DisplayGrey = 0.20;
    /// <summary>Slope of the base curve in log–log terms. Contrast scales it.</summary>
    public const double BaseSlope = 1.2;

    /// <summary>
    /// The RAW base curve, from scene linear to display linear: x^p / (x^p + k^p). In log terms it is an S-shape that
    /// softens highlights smoothly (1.0 shows at about 91 % in sRGB, so rebuilt highlights above it stay distinct). It
    /// keeps <see cref="SceneGrey"/> at <see cref="DisplayGrey"/> for any slope.
    /// </summary>
    public static double Filmic(double x, double slope)
    {
        if (x <= 0) return 0;
        var xp = Math.Pow(x / SceneGrey, slope);
        var kp = 1 / DisplayGrey - 1; // (k / grey)^p, set so grey stays in place
        return xp / (xp + kp);
    }

    /// <summary>The slope for a contrast setting (-100..100): from about 0.5 to 1.75 times the base.</summary>
    public static double SlopeFor(double contrast) => BaseSlope * Math.Pow(2, 0.8 * contrast / 100);

    /// <summary>Contrast for photos that are already toned (JPEG…): an S-curve on display values that keeps black and white fixed.</summary>
    public static double SCurve(double y, double contrast)
    {
        var a = 0.85 * contrast / 100;
        var s = y * y * (3 - 2 * y);
        return y + a * (s - y);
    }

    /// <summary>Brightness (-100..100) on display values: a power curve that moves midtones and keeps 0 and 1 fixed.</summary>
    public static double Brighten(double y, double brightness) => y <= 0 ? 0 : Math.Pow(y, Math.Pow(2, -0.8 * brightness / 100));

    /// <summary>
    /// A smooth, monotone curve through the points (cubic Hermite, Fritsch–Carlson), sampled into a
    /// table of <paramref name="size"/> values over 0..1. Before the first and after the last point it stays flat.
    /// </summary>
    public static float[] Spline(double[][] points, int size = 4096)
    {
        var n = points.Length;
        var x = points.Select(p => p[0]).ToArray();
        var y = points.Select(p => p[1]).ToArray();
        var table = new float[size];
        if (n < 2) { for (var i = 0; i < size; i++) table[i] = i / (float)(size - 1); return table; }
        var d = new double[n - 1];
        for (var i = 0; i < n - 1; i++) d[i] = (y[i + 1] - y[i]) / Math.Max(1e-9, x[i + 1] - x[i]);
        var m = new double[n];
        m[0] = d[0];
        m[n - 1] = d[n - 2];
        for (var i = 1; i < n - 1; i++) m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) / 2;
        for (var i = 0; i < n - 1; i++)
        {
            if (d[i] == 0) { m[i] = m[i + 1] = 0; continue; }
            var a = m[i] / d[i];
            var b = m[i + 1] / d[i];
            var h = a * a + b * b;
            if (h > 9) { var t = 3 / Math.Sqrt(h); m[i] = t * a * d[i]; m[i + 1] = t * b * d[i]; }
        }
        var k = 0;
        for (var i = 0; i < size; i++)
        {
            var t = i / (double)(size - 1);
            double v;
            if (t <= x[0]) v = y[0];
            else if (t >= x[n - 1]) v = y[n - 1];
            else
            {
                while (k < n - 2 && t > x[k + 1]) k++;
                var w = x[k + 1] - x[k];
                var s = (t - x[k]) / w;
                double s2 = s * s, s3 = s2 * s;
                v = (2 * s3 - 3 * s2 + 1) * y[k] + (s3 - 2 * s2 + s) * w * m[k] + (-2 * s3 + 3 * s2) * y[k + 1] + (s3 - s2) * w * m[k + 1];
            }
            table[i] = (float)Math.Clamp(v, 0, 1);
        }
        return table;
    }

    /// <summary>Looks up a display value in a <see cref="Spline"/> table, interpolating between entries.</summary>
    public static double Lookup(float[] table, double y)
    {
        var f = Math.Clamp(y, 0, 1) * (table.Length - 1);
        var i = (int)f;
        if (i >= table.Length - 1) return table[^1];
        var t = f - i;
        return table[i] * (1 - t) + table[i + 1] * t;
    }
}
