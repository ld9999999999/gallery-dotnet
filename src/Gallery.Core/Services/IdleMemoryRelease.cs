namespace Gallery.Core.Services;

/// <summary>
/// Gives memory back when the server goes quiet. The garbage collector only runs while objects are being made, ImageSharp
/// keeps its pooled image buffers, and glibc holds on to memory freed by native code. So after busy work (a scan,
/// thumbnails, someone browsing) an idle server stays at 400–600 MB resident, where "Free memory now" brings it down to
/// 200 MB. Once nobody has used the gallery and nothing has run in the background for <see cref="QuietFor"/>, and at
/// least <see cref="MinAllocated"/> was allocated since the last release (so a truly idle server isn't woken for
/// nothing), it does what that button does (<see cref="MemoryReport.Free"/>). The face models have their own idle timer.
/// </summary>
public sealed class IdleMemoryRelease : BackgroundService
{
    private readonly MemoryReport _memory;
    private readonly StatusService _status;
    private readonly ActivityMonitor _activity;
    private long _allocatedAtLastRelease;

    public IdleMemoryRelease(MemoryReport memory, StatusService status, ActivityMonitor activity)
    {
        _memory = memory;
        _status = status;
        _activity = activity;
    }

    internal TimeSpan QuietFor { get; set; } = TimeSpan.FromMinutes(2);
    internal long MinAllocated { get; set; } = 64L << 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(stoppingToken)) Tick();
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Frees memory if the server is idle and has allocated enough since last time. Returns what it did, or why it didn't.</summary>
    internal string Tick()
    {
        if (_activity.Quiet < QuietFor) return "the gallery is in use";
        if (_status.BackgroundWork() is { } work) return work;
        var allocated = GC.GetTotalAllocatedBytes();
        if (allocated - _allocatedAtLastRelease < MinAllocated) return "nothing new to let go of";
        _memory.Free(whenIdle: true);
        _allocatedAtLastRelease = GC.GetTotalAllocatedBytes();
        return "released";
    }
}
