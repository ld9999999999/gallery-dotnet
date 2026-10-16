namespace Gallery.Core.Services;

/// <summary>
/// Checks whether a date could be when a photo or video was taken. A camera clock set years ahead, or a file copied
/// with a broken timestamp, produces dates in the future. Those get skipped in favor of the next source (see
/// <see cref="Scanner.ChooseTakenAt"/>). Dates are local time, like EXIF dates.
/// </summary>
public static class CaptureDate
{
    /// <summary>Slack for time zones and slightly fast clocks.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromDays(2);

    /// <summary>True for dates from <paramref name="earliestYear"/> up to now plus <see cref="Grace"/>.</summary>
    public static bool IsPlausible(DateTime date, int earliestYear = 1900) => date.Year >= earliestYear && date <= DateTime.Now + Grace;

    /// <summary>The latest plausible date, formatted like the database's dates, for comparing against stored asset dates.</summary>
    public static string Latest() => (DateTime.Now + Grace).ToString("yyyy-MM-ddTHH:mm:ss");
}
