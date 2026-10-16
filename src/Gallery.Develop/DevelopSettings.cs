using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gallery.Develop;

/// <summary>Groups of settings that recipes save and apply.</summary>
[Flags]
public enum DevelopSections
{
    None = 0,
    WhiteBalance = 1,
    /// <summary>Exposure, contrast, brightness, highlights, shadows.</summary>
    Tone = 2,
    /// <summary>Saturation, vibrance.</summary>
    Color = 4,
    Curves = 8,
    /// <summary>Noise reduction, sharpening.</summary>
    Detail = 16,
    All = WhiteBalance | Tone | Color | Curves | Detail,
}

public sealed record WhiteBalanceSettings
{
    /// <summary>-100..100, relative to as-shot: positive is warmer.</summary>
    public double Temperature { get; init; }
    /// <summary>-100..100, relative to as-shot: positive is more magenta, negative more green.</summary>
    public double Tint { get; init; }
}

/// <summary>A tone curve: points (x, y) in 0..1 on display values. A smooth, non-decreasing curve is drawn through them.</summary>
public sealed record CurveSettings
{
    public static readonly double[][] Identity = [[0, 0], [1, 1]];

    public double[][] Rgb { get; init; } = Identity;
    public double[][] Red { get; init; } = Identity;
    public double[][] Green { get; init; } = Identity;
    public double[][] Blue { get; init; } = Identity;

    [JsonIgnore]
    public bool IsIdentity => IsLine(Rgb) && IsLine(Red) && IsLine(Green) && IsLine(Blue);

    private static bool IsLine(double[][] points) => points.All(p => p.Length == 2 && Math.Abs(p[0] - p[1]) < 1e-9);

    public bool Equals(CurveSettings? other) =>
        other is not null && Same(Rgb, other.Rgb) && Same(Red, other.Red) && Same(Green, other.Green) && Same(Blue, other.Blue);

    public override int GetHashCode() => HashCode.Combine(Rgb.Length, Red.Length, Green.Length, Blue.Length);

    private static bool Same(double[][] a, double[][] b) => a.Length == b.Length && a.Zip(b).All(p => p.First.SequenceEqual(p.Second));
}

public sealed record NoiseReductionSettings
{
    /// <summary>0..100.</summary>
    public double Luminance { get; init; }
    /// <summary>0..100.</summary>
    public double Color { get; init; }
}

public sealed record SharpeningSettings
{
    /// <summary>0..150.</summary>
    public double Amount { get; init; }
    /// <summary>0.5..3, in pixels of a 24-megapixel picture (scaled with the size rendered).</summary>
    public double Radius { get; init; } = 1.0;
    /// <summary>0..100: how much of the flat areas (sky, skin) are left unsharpened.</summary>
    public double Masking { get; init; }
}

/// <summary>
/// Crop and straighten (see <see cref="Geometry"/>). The picture is rotated around its centre by <see cref="Angle"/>, and
/// a level rectangle is cut from it, which straightens the photo. The edges are fractions of the upright picture's width
/// and height, measured in the rotated picture, so the same crop fits an editing copy, a 100% crop and an export of any
/// size. Left out of every recipe section, since a crop belongs to its own photo.
/// </summary>
public sealed record CropSettings
{
    /// <summary>Degrees, -45..45: positive rotates the picture clockwise.</summary>
    public double Angle { get; init; }
    public double Left { get; init; }
    public double Top { get; init; }
    public double Right { get; init; } = 1;
    public double Bottom { get; init; } = 1;
    /// <summary>
    /// The editor's ratio lock: "free", "original" or "w:h" (e.g. "3:2"), in either order. The crop's own shape tells
    /// whether it is portrait or landscape. Only the editor reads it.
    /// </summary>
    public string? Aspect { get; init; }

    [JsonIgnore]
    public bool IsIdentity => Angle == 0 && Left <= 0 && Top <= 0 && Right >= 1 && Bottom >= 1;

    /// <summary>The smallest crop, as a fraction of a side.</summary>
    public const double MinSide = 0.01;

    public CropSettings Clamped()
    {
        double l = F(Left, 0), t = F(Top, 0), r = F(Right, 1), b = F(Bottom, 1);
        if (r - l < MinSide) (l, r) = (Math.Min(l, 1 - MinSide), Math.Min(l, 1 - MinSide) + MinSide);
        if (b - t < MinSide) (t, b) = (Math.Min(t, 1 - MinSide), Math.Min(t, 1 - MinSide) + MinSide);
        var angle = double.IsFinite(Angle) ? Math.Clamp(Math.Round(Angle, 2), -45, 45) : 0;
        var aspect = Aspect is "free" or "original" || Ratio(Aspect) is not null ? Aspect : null;
        return new CropSettings { Angle = angle, Left = l, Top = t, Right = r, Bottom = b, Aspect = aspect };
    }

    /// <summary>"3:2" → 1.5 (whole numbers 1–9999 each side); null when it is not such a ratio.</summary>
    public static double? Ratio(string? aspect)
    {
        var parts = aspect?.Split(':');
        return parts is [var w, var h] && Whole(w) is { } a && Whole(h) is { } b ? (double)a / b : null;
        static int? Whole(string s) => s.Length is >= 1 and <= 4 && s[0] != '0' && s.All(char.IsAsciiDigit) ? int.Parse(s) : null;
    }

    private static double F(double v, double fallback) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : fallback;
}

/// <summary>
/// An edit: the slider values, saved as JSON (see docs/raw-editor.md, "Settings format"). Missing fields mean defaults,
/// so settings saved by older versions still load.
/// </summary>
public sealed record DevelopSettings
{
    public const int CurrentVersion = 1;
    /// <summary>Nothing applied: a JPEG (or other toned photo) renders as it is.</summary>
    public static readonly DevelopSettings Default = new();
    /// <summary>Starting settings for a RAW: some colour noise reduction and output sharpening, as raw converters use.</summary>
    public static readonly DevelopSettings RawDefault = new() { NoiseReduction = new() { Color = 25 }, Sharpening = new() { Amount = 40 } };

    /// <summary>The starting settings for a photo with no saved edit.</summary>
    public static DevelopSettings DefaultFor(bool raw) => raw ? RawDefault : Default;

    public int Version { get; init; } = CurrentVersion;
    public WhiteBalanceSettings WhiteBalance { get; init; } = new();
    /// <summary>Stops, -5..5.</summary>
    public double Exposure { get; init; }
    /// <summary>-100..100.</summary>
    public double Contrast { get; init; }
    /// <summary>-100..100: moves midtones up or down; pure black and white stay the same.</summary>
    public double Brightness { get; init; }
    /// <summary>-100..100: negative restores highlight detail.</summary>
    public double Highlights { get; init; }
    /// <summary>-100..100: positive lifts shadows.</summary>
    public double Shadows { get; init; }
    /// <summary>-100..100.</summary>
    public double Saturation { get; init; }
    /// <summary>-100..100: like saturation, but holds back colours that are already strong, and skin tones.</summary>
    public double Vibrance { get; init; }
    public CurveSettings Curves { get; init; } = new();
    public NoiseReductionSettings NoiseReduction { get; init; } = new();
    public SharpeningSettings Sharpening { get; init; } = new();
    public CropSettings Crop { get; init; } = new();

    public static DevelopSettings Parse(string json) => (JsonSerializer.Deserialize(json, DevelopJson.Default.DevelopSettings) ?? new DevelopSettings()).Clamped();

    public string ToJson() => JsonSerializer.Serialize(this, DevelopJson.Default.DevelopSettings);

    /// <summary>A copy of these settings with every value inside its range and valid curves.</summary>
    public DevelopSettings Clamped() => this with
    {
        Version = CurrentVersion,
        WhiteBalance = new WhiteBalanceSettings { Temperature = C(WhiteBalance?.Temperature, 100), Tint = C(WhiteBalance?.Tint, 100) },
        Exposure = C(Exposure, 5),
        Contrast = C(Contrast, 100), Brightness = C(Brightness, 100), Highlights = C(Highlights, 100), Shadows = C(Shadows, 100),
        Saturation = C(Saturation, 100), Vibrance = C(Vibrance, 100),
        Curves = new CurveSettings { Rgb = Points(Curves?.Rgb), Red = Points(Curves?.Red), Green = Points(Curves?.Green), Blue = Points(Curves?.Blue) },
        NoiseReduction = new NoiseReductionSettings
        {
            Luminance = Math.Clamp(NoiseReduction?.Luminance ?? 0, 0, 100),
            Color = Math.Clamp(NoiseReduction?.Color ?? 0, 0, 100),
        },
        Sharpening = new SharpeningSettings
        {
            Amount = Math.Clamp(Sharpening?.Amount ?? 0, 0, 150),
            Radius = Math.Clamp(Sharpening?.Radius ?? 1, 0.5, 3),
            Masking = Math.Clamp(Sharpening?.Masking ?? 0, 0, 100),
        },
        Crop = (Crop ?? new CropSettings()).Clamped(),
    };

    private static double C(double? v, double limit) => v is { } x && double.IsFinite(x) ? Math.Clamp(x, -limit, limit) : 0;

    /// <summary>Curve points inside 0..1, sorted by x, one per x; fewer than two points means a straight line.</summary>
    private static double[][] Points(double[][]? points)
    {
        var list = (points ?? []).Where(p => p is { Length: 2 } && double.IsFinite(p[0]) && double.IsFinite(p[1]))
            .Select(p => new[] { Math.Clamp(p[0], 0, 1), Math.Clamp(p[1], 0, 1) })
            .GroupBy(p => Math.Round(p[0], 4)).Select(g => g.Last()).OrderBy(p => p[0]).ToArray();
        return list.Length >= 2 ? list : CurveSettings.Identity;
    }

    /// <summary>These settings with the given sections taken from <paramref name="other"/> (a recipe being applied).</summary>
    public DevelopSettings With(DevelopSettings other, DevelopSections sections) => this with
    {
        WhiteBalance = sections.HasFlag(DevelopSections.WhiteBalance) ? other.WhiteBalance : WhiteBalance,
        Exposure = sections.HasFlag(DevelopSections.Tone) ? other.Exposure : Exposure,
        Contrast = sections.HasFlag(DevelopSections.Tone) ? other.Contrast : Contrast,
        Brightness = sections.HasFlag(DevelopSections.Tone) ? other.Brightness : Brightness,
        Highlights = sections.HasFlag(DevelopSections.Tone) ? other.Highlights : Highlights,
        Shadows = sections.HasFlag(DevelopSections.Tone) ? other.Shadows : Shadows,
        Saturation = sections.HasFlag(DevelopSections.Color) ? other.Saturation : Saturation,
        Vibrance = sections.HasFlag(DevelopSections.Color) ? other.Vibrance : Vibrance,
        Curves = sections.HasFlag(DevelopSections.Curves) ? other.Curves : Curves,
        NoiseReduction = sections.HasFlag(DevelopSections.Detail) ? other.NoiseReduction : NoiseReduction,
        Sharpening = sections.HasFlag(DevelopSections.Detail) ? other.Sharpening : Sharpening,
    };
}
