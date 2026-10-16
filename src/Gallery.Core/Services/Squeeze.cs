using System.Globalization;

namespace Gallery.Core.Services;

/// <summary>
/// An anamorphic lens's squeeze: the factor to stretch a photo's long side by so it looks right (1.33, 1.5, 2…).
/// Stored per item as the tag <c>anamorphic</c>, like <see cref="Rotation"/>, and applied wherever the picture is drawn:
/// thumbnails and previews, the viewer (a video in the browser), the photo editor, exports and face finding. The stretch
/// comes after the picture is upright and rotated. Rotating keeps the long side long, and the lens squeezes along the
/// sensor's long side however the camera is held.
/// </summary>
public static class Squeeze
{
    public const string TagKey = "anamorphic";

    /// <summary>The largest factor allowed. Anamorphic lenses go up to 2×, a bit more with adapters stacked on lenses.</summary>
    public const double Max = 3;

    /// <summary>Factors offered in the menus.</summary>
    public static readonly double[] Common = [1.25, 1.33, 1.5, 1.55, 1.6, 1.65, 1.8, 2];

    /// <summary>1 (none), or a factor above 1 rounded to hundredths and at most <see cref="Max"/>.</summary>
    public static double Normalize(double factor) =>
        double.IsFinite(factor) && factor >= 1.005 ? Math.Round(Math.Min(factor, Max), 2) : 1;

    /// <summary>The tag's factor ("1.5", "1.5x" or "1.5×"); 1 when the tag is missing or isn't a number.</summary>
    public static double Parse(string? tag) =>
        double.TryParse(tag?.Trim().TrimEnd('x', 'X', '×').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Normalize(v) : 1;

    /// <summary>The factor as the tag and URLs write it: "1.5", "1.33", "2".</summary>
    public static string Format(double factor) => Normalize(factor).ToString("0.##", CultureInfo.InvariantCulture);

    public static (int W, int H) Apply((int W, int H) size, double factor) => Apply(size.W, size.H, factor);

    /// <summary>A picture's size with its long side stretched (the width of a square one).</summary>
    public static (int W, int H) Apply(int width, int height, double factor)
    {
        factor = Normalize(factor);
        if (factor == 1) return (width, height);
        return width >= height ? ((int)Math.Round(width * factor), height) : (width, (int)Math.Round(height * factor));
    }
}
