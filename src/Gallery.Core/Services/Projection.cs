using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Xmp;
using Directory = MetadataExtractor.Directory;

namespace Gallery.Core.Services;

/// <summary>What a photo's metadata says about it being a 360° photo, before its size is checked.</summary>
public enum PanoramaHint
{
    None,
    /// <summary>Taken with a camera that makes 360° photos; counts as 360° when the frame is exactly 2:1.</summary>
    Camera360,
    /// <summary>XMP (GPano) says it's an equirectangular picture of the whole sphere.</summary>
    Sphere,
    /// <summary>Two fisheye circles side by side, with a lens calibration the viewer can stitch them with (<see cref="Services.DualFisheye"/>).</summary>
    DualFisheye,
    /// <summary>Unstitched, with a lens calibration in a layout we can't read, so it's shown flat.</summary>
    Unstitched,
}

/// <summary>
/// 360° photos, which the web viewer shows as a view you can look around in. There are two kinds, both exactly twice as
/// wide as high: an equirectangular picture of the whole sphere around the camera (360° across, 180° up and down), and
/// the two fisheye circles a camera such as an Insta360 saves before stitching (<see cref="Services.DualFisheye"/>).
/// Each file is checked when it's indexed (<c>assets.projection</c>). An admin can override the result for an item with
/// the item tag <c>projection</c> set to <c>360</c> or <c>flat</c>; otherwise the main file's detection applies.
/// </summary>
public static class Projection
{
    /// <summary>The stored value for a stitched 360° photo. Flat photos store null.</summary>
    public const string Equirectangular = "equirectangular";

    /// <summary>The stored value for an unstitched photo: two fisheye circles side by side, with their lens calibration.</summary>
    public const string DualFisheye = "dual-fisheye";

    public static bool IsSphere(string? projection) => projection is Equirectangular or DualFisheye;

    public const string TagKey = "projection";
    public const string TagSphere = "360";
    public const string TagFlat = "flat";

    private const string GPanoNs = "http://ns.google.com/photos/1.0/panorama/";
    private const string TiffNs = "http://ns.adobe.com/tiff/1.0/";

    /// <summary>Equirectangular, dual fisheye, or null for a flat photo.</summary>
    public static string? Decide(PanoramaHint hint, int? width, int? height)
    {
        var twoToOne = width is > 0 && height is > 0 && width == 2 * height;
        return hint switch
        {
            PanoramaHint.Sphere => Equirectangular,
            PanoramaHint.DualFisheye when twoToOne => DualFisheye,
            PanoramaHint.Camera360 when twoToOne => Equirectangular,
            _ => null,
        };
    }

    /// <summary>
    /// Reads the hint from a photo's metadata. Many 360° cameras skip the GPano tags (the Insta360 X5 writes only
    /// tiff:Make and tiff:Model in its XMP), so the camera model counts too. Checked in order: GPano (a stitched picture),
    /// then a lens calibration (only an unstitched picture has one), then the camera model.
    /// </summary>
    public static PanoramaHint ReadHint(IReadOnlyList<Directory> dirs)
    {
        string? make = null, model = null;
        var sphere = false;
        foreach (var d in dirs)
        {
            if (d is ExifIfd0Directory ifd0)
            {
                make ??= ifd0.GetString(ExifDirectoryBase.TagMake);
                model ??= ifd0.GetString(ExifDirectoryBase.TagModel);
            }
            else if (d is XmpDirectory { XmpMeta: { } xmp })
            {
                try
                {
                    sphere |= IsWholeSphere(
                        xmp.GetPropertyString(GPanoNs, "ProjectionType"),
                        Int(xmp.GetPropertyString(GPanoNs, "CroppedAreaImageWidthPixels")), Int(xmp.GetPropertyString(GPanoNs, "CroppedAreaImageHeightPixels")),
                        Int(xmp.GetPropertyString(GPanoNs, "FullPanoWidthPixels")), Int(xmp.GetPropertyString(GPanoNs, "FullPanoHeightPixels")));
                    make ??= xmp.GetPropertyString(TiffNs, "Make");
                    model ??= xmp.GetPropertyString(TiffNs, "Model");
                }
                catch (Exception) { } // broken XMP gives no hint
            }
        }
        if (sphere) return PanoramaHint.Sphere;
        if (Services.DualFisheye.FindCalibration(dirs) is { } calibration)
            return Services.DualFisheye.IsReadable(calibration) ? PanoramaHint.DualFisheye : PanoramaHint.Unstitched;
        return IsCamera360(make, model) ? PanoramaHint.Camera360 : PanoramaHint.None;
    }

    private static int? Int(string? s) => int.TryParse(s?.Trim(), out var v) && v > 0 ? v : null;

    /// <summary>
    /// True when GPano says equirectangular and the picture covers the whole panorama. A phone's partial panorama is also
    /// tagged equirectangular, but its cropped area is smaller than the full sphere and would look stretched as a sphere,
    /// so it stays flat.
    /// </summary>
    public static bool IsWholeSphere(string? projectionType, int? croppedWidth, int? croppedHeight, int? fullWidth, int? fullHeight)
    {
        if (!string.Equals(projectionType?.Trim(), Equirectangular, StringComparison.OrdinalIgnoreCase)) return false;
        static bool Covers(int? part, int? full) => part is null || full is null || part >= full * 0.99;
        return Covers(croppedWidth, fullWidth) && Covers(croppedHeight, fullHeight);
    }

    /// <summary>Cameras that make 360° photos. Their flat shots (single-lens mode, other frame shapes) are found by size.</summary>
    public static bool IsCamera360(string? make, string? model)
    {
        var name = $"{make} {model}".Trim();
        if (name.Length == 0) return false;
        bool Has(string s) => name.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("Arashi Vision") || Has("Insta360")) return true;                  // Insta360
        if (Has("THETA")) return true;                                            // RICOH THETA
        if (Has("GoPro") && (Has("MAX") || Has("Fusion"))) return true;           // GoPro MAX, Fusion
        if (Has("Gear 360") || Has("SM-C200") || Has("SM-R210")) return true;     // Samsung Gear 360 (2016, 2017)
        if (Has("Kandao") || Has("QooCam")) return true;
        if (Has("Mi Sphere")) return true;                                        // Xiaomi
        return false;
    }

    /// <summary>
    /// What the viewer shows: the admin's choice (the <see cref="TagKey"/> tag) if there is one, otherwise the detected
    /// value. Only photos can be 360°. When the tag says 360°, a photo detected as two fisheye circles keeps that kind and
    /// any other photo is treated as equirectangular.
    /// </summary>
    public static string? Effective(string? detected, string? tag, string kind)
    {
        if (kind != Models.MediaKind.Image) return null;
        return ParseTag(tag) switch
        {
            true => detected == DualFisheye ? DualFisheye : Equirectangular,
            false => null,
            null => IsSphere(detected) ? detected : null,
        };
    }

    /// <summary>True for 360, false for flat, null when the tag is missing or unrecognised.</summary>
    public static bool? ParseTag(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        TagSphere or "360°" or Equirectangular or "sphere" => true,
        TagFlat or "none" or "normal" => false,
        _ => null,
    };
}
