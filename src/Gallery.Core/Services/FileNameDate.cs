using System.Globalization;
using System.Text.RegularExpressions;

namespace Gallery.Core.Services;

/// <summary>
/// Reads a capture date from the start of a file name, for files with no EXIF or container date. It accepts
/// <c>yyyymmdd</c> and <c>yyyy-mm-dd</c> prefixes ("20201001 beach.jpg", "2020-10-01_party.mp4"). The date must be a
/// real calendar date with a year between 1900 and next year, and can't be followed by more digits ("202010011234.jpg"
/// is rejected). The time is midnight, since the name has no time of day.
/// </summary>
public static class FileNameDate
{
    private static readonly Regex Prefix = new(
        @"^(?:(?<y>\d{4})(?<m>\d{2})(?<d>\d{2})|(?<y>\d{4})-(?<m>\d{2})-(?<d>\d{2}))(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static DateTime? Parse(string fileName)
    {
        var m = Prefix.Match(Path.GetFileName(fileName));
        if (!m.Success) return null;
        var y = int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);
        var mo = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
        var d = int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture);
        if (y < 1900 || y > DateTime.Now.Year + 1 || mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo)) return null;
        return new DateTime(y, mo, d, 0, 0, 0, DateTimeKind.Unspecified);
    }
}
