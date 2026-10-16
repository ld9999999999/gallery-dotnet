namespace Gallery.Core.Services;

/// <summary>
/// Manual rotation set by an admin, stored as the item tag <c>rotate-degrees</c> (90, 180 or 270, clockwise).
/// It applies after the camera's EXIF orientation: the photo is first turned upright as EXIF says, then by this amount.
/// Since it starts from the upright picture, one value works for every version of an item, even when a JPEG and its RAW
/// have different EXIF orientations.
/// </summary>
public static class Rotation
{
    public const string TagKey = "rotate-degrees";

    /// <summary>Returns 0, 90, 180 or 270. A value that isn't a multiple of 90 becomes 0.</summary>
    public static int Normalize(int degrees)
    {
        var d = ((degrees % 360) + 360) % 360;
        return d % 90 == 0 ? d : 0;
    }

    public static int Parse(string? value) => int.TryParse(value?.Trim(), out var d) ? Normalize(d) : 0;

    /// <summary>Raise this when earlier thumbnails are wrong, so browsers fetch them again.</summary>
    public const string ThumbGeneration = "t3";

    /// <summary>
    /// A version value for thumbnail URLs. It changes when the file, its rotation, its anamorphic squeeze
    /// (<see cref="Squeeze"/>) or <see cref="ThumbGeneration"/> changes. app.js builds the same value.
    /// </summary>
    public static string Version(string hash, int rotation, double squeeze = 1) =>
        $"{hash}{(rotation == 0 ? "" : $".r{rotation}")}{(Squeeze.Normalize(squeeze) == 1 ? "" : $".a{Squeeze.Format(squeeze)}")}.{ThumbGeneration}";
}
