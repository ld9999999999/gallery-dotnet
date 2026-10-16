namespace Gallery.Desktop.ViewModels;

internal static class Format
{
    public static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static string Bytes(long n) => n < 1024 ? $"{n} B" : n < 1 << 20 ? $"{n / 1024.0:0} KB" : n < 1 << 30 ? $"{n / 1048576.0:0.0} MB" : $"{n / 1073741824.0:0.00} GB";

    public static string Count(long n, string one, string many) => $"{n:N0} {(n == 1 ? one : many)}";

    /// <summary>"2024-08-01 14:03:22" from the index's ISO timestamps.</summary>
    public static string Date(string? iso) => string.IsNullOrEmpty(iso) ? "—" : iso.Replace('T', ' ');

    public static string DateRange(string? from, string? to)
    {
        var a = from?[..Math.Min(10, from.Length)];
        var b = to?[..Math.Min(10, to.Length)];
        return a is null ? "" : b is null || b == a ? a : $"{a} – {b}";
    }
}
