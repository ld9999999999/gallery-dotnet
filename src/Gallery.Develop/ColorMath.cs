namespace Gallery.Develop;

/// <summary>Colour conversions into the editing space: linear light, Rec.2020 primaries, D65.</summary>
public static class ColorMath
{
    /// <summary>CIE XYZ (D65), as LibRaw writes it with <c>-o 5</c>, to linear Rec.2020. 3×3, row by row.</summary>
    public static readonly float[] XyzToRec2020 =
    [
        1.7166512f, -0.3556708f, -0.2533663f,
        -0.6666844f, 1.6164812f, 0.0157685f,
        0.0176399f, -0.0427706f, 0.9421031f,
    ];

    /// <summary>Linear sRGB (Rec.709 primaries) to linear Rec.2020. 3×3, row by row.</summary>
    public static readonly float[] SrgbToRec2020 =
    [
        0.6274040f, 0.3292820f, 0.0433136f,
        0.0690970f, 0.9195400f, 0.0113612f,
        0.0163916f, 0.0880132f, 0.8955950f,
    ];

    /// <summary>Linear Rec.2020 to linear sRGB (Rec.709 primaries). 3×3, row by row.</summary>
    public static readonly float[] Rec2020ToSrgb =
    [
        1.6604910f, -0.5876411f, -0.0728499f,
        -0.1245505f, 1.1328999f, -0.0083494f,
        -0.0181508f, -0.1005789f, 1.1187297f,
    ];

    /// <summary>Linear Rec.2020 to CIE XYZ (D65). 3×3, row by row.</summary>
    public static readonly float[] Rec2020ToXyz =
    [
        0.6369580f, 0.1446169f, 0.1688810f,
        0.2627002f, 0.6779981f, 0.0593017f,
        0.0000000f, 0.0280727f, 1.0609851f,
    ];

    /// <summary>Brightness (luminance) weights for linear Rec.2020 and for linear sRGB.</summary>
    public const float Rec2020R = 0.2627f, Rec2020G = 0.6780f, Rec2020B = 0.0593f;
    public const float SrgbR = 0.2126f, SrgbG = 0.7152f, SrgbB = 0.0722f;

    private static readonly double[] Bradford = [0.8951, 0.2664, -0.1614, -0.7502, 1.7135, 0.0367, 0.0389, -0.0685, 1.0296];

    /// <summary>Multiplies two 3×3 matrices: a·b (row by row).</summary>
    public static double[] Multiply(double[] a, double[] b)
    {
        var m = new double[9];
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 3; c++)
                m[r * 3 + c] = a[r * 3] * b[c] + a[r * 3 + 1] * b[3 + c] + a[r * 3 + 2] * b[6 + c];
        return m;
    }

    public static double[] Invert(double[] m)
    {
        double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
        var det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        return
        [
            (e * i - f * h) / det, (c * h - b * i) / det, (b * f - c * e) / det,
            (f * g - d * i) / det, (a * i - c * g) / det, (c * d - a * f) / det,
            (d * h - e * g) / det, (b * g - a * h) / det, (a * e - b * d) / det,
        ];
    }

    public static double[] ToDouble(float[] m) => m.Select(v => (double)v).ToArray();

    /// <summary>The CIE 1960 (u, v) colour of a black body at a temperature in kelvin (Krystek's formula, 1000–15000 K).</summary>
    public static (double U, double V) PlanckUv(double kelvin)
    {
        var t = Math.Clamp(kelvin, 1000, 15000);
        var u = (0.860117757 + 1.54118254e-4 * t + 1.28641212e-7 * t * t) / (1 + 8.42420235e-4 * t + 7.08145163e-7 * t * t);
        var v = (0.317398726 + 4.22806245e-5 * t + 4.20481691e-8 * t * t) / (1 - 2.89741816e-5 * t + 1.61456053e-7 * t * t);
        return (u, v);
    }

    private static double[] XyzOfUv(double u, double v)
    {
        var d = 2 * u - 8 * v + 4;
        double x = 3 * u / d, y = 2 * v / d;
        return [x / y, 1, (1 - x - y) / y];
    }

    /// <summary>
    /// White balance change from as-shot, as a matrix on linear Rec.2020. The picture is shown as if lit by a black body
    /// <paramref name="temperature"/> × 0.6 mired cooler (so the result is warmer) and moved <paramref name="tint"/> toward
    /// magenta, using Bradford chromatic adaptation. Zero temperature and zero tint give the identity matrix.
    /// </summary>
    public static double[] WhiteBalance(double temperature, double tint)
    {
        const double referenceKelvin = 6500;
        var mired = 1e6 / referenceKelvin - 0.6 * temperature;
        var (u, v) = PlanckUv(1e6 / mired);
        var (ru, rv) = PlanckUv(referenceKelvin);
        v += 0.00015 * tint; // above the black-body line is greener; a greener light makes the result more magenta
        var lmsSource = Apply(Bradford, XyzOfUv(u, v));
        var lmsReference = Apply(Bradford, XyzOfUv(ru, rv));
        double[] scale = [lmsReference[0] / lmsSource[0], 0, 0, 0, lmsReference[1] / lmsSource[1], 0, 0, 0, lmsReference[2] / lmsSource[2]];
        var adapt = Multiply(Invert(Bradford), Multiply(scale, Bradford));
        return Multiply(ToDouble(XyzToRec2020), Multiply(adapt, ToDouble(Rec2020ToXyz)));
    }

    private static double[] Apply(double[] m, double[] v) =>
        [m[0] * v[0] + m[1] * v[1] + m[2] * v[2], m[3] * v[0] + m[4] * v[1] + m[5] * v[2], m[6] * v[0] + m[7] * v[1] + m[8] * v[2]];

    /// <summary>The sRGB curve: an encoded value in 0..1 to linear light.</summary>
    public static float SrgbToLinear(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

    /// <summary>Linear light to the sRGB encoding, for 0..1.</summary>
    public static float LinearToSrgb(float v) => v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1 / 2.4f) - 0.055f;

    /// <summary>A table of linear light for every 16-bit sRGB value.</summary>
    public static float[] SrgbDecodeTable16()
    {
        var t = new float[65536];
        for (var i = 0; i < t.Length; i++) t[i] = SrgbToLinear(i / 65535f);
        return t;
    }

    /// <summary>Applies a 3×3 matrix to packed RGB (or XYZ) values in place, after scaling them.</summary>
    public static void Transform(Span<float> rgb, ReadOnlySpan<float> m, float scale = 1)
    {
        for (var i = 0; i + 2 < rgb.Length; i += 3)
        {
            float a = rgb[i] * scale, b = rgb[i + 1] * scale, c = rgb[i + 2] * scale;
            rgb[i] = m[0] * a + m[1] * b + m[2] * c;
            rgb[i + 1] = m[3] * a + m[4] * b + m[5] * c;
            rgb[i + 2] = m[6] * a + m[7] * b + m[8] * c;
        }
    }
}
