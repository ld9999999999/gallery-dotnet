namespace Gallery.Core.Services;

/// <summary>
/// Tracks when someone last used the gallery, so background work like <see cref="IdleTranscoder"/> can wait for quiet.
/// The web server reports every request except the polls an open page makes on its own. Until the first request,
/// the clock starts at server start.
/// </summary>
public sealed class ActivityMonitor
{
    private long _last = Environment.TickCount64;

    public void Seen() => Volatile.Write(ref _last, Environment.TickCount64);

    public TimeSpan Quiet => TimeSpan.FromMilliseconds(Environment.TickCount64 - Volatile.Read(ref _last));

    /// <summary>The status polls an open page makes on its own, including the admin Memory tab. They don't count as use.</summary>
    public static bool IsPoll(string? path) => path is "/api/status" or "/api/admin/scan/status" or "/api/health" or "/api/admin/memory";
}
