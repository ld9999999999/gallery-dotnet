using Gallery.Core.Models;

namespace Gallery.Core.Services;

/// <summary>
/// Converts videos that browsers can't play as they are (MKV, AVI, MTS, WMV…) into saved MP4 copies while the server is
/// idle, newest first and one at a time. The admin turns it on (<see cref="SettingsService.TranscodeWhenIdle"/>, with
/// saved copies kept). Idle means no scan, thumbnails, faces, exports or requested conversion, and nobody using the
/// gallery for <see cref="QuietFor"/>. ffmpeg runs at the lowest CPU and disk priority, so a conversion that has started
/// is left to finish, and the next one waits until the server is idle again. When someone plays a video that needs
/// converting, that one goes first and stops the one running here, which is retried later.
/// </summary>
public sealed class IdleTranscoder : BackgroundService
{
    private static readonly TimeSpan Check = TimeSpan.FromSeconds(30);

    private readonly TranscodeService _transcodes;
    private readonly StatusService _status;
    private readonly SettingsService _settings;
    private readonly ActivityMonitor _activity;
    private readonly Ffmpeg _ffmpeg;
    private readonly ILogger<IdleTranscoder> _log;
    private readonly HashSet<long> _failed = []; // ffmpeg couldn't convert these; skipped until a restart

    public IdleTranscoder(TranscodeService transcodes, StatusService status, SettingsService settings, ActivityMonitor activity, Ffmpeg ffmpeg, ILogger<IdleTranscoder> log)
    {
        _transcodes = transcodes;
        _status = status;
        _settings = settings;
        _activity = activity;
        _ffmpeg = ffmpeg;
        _log = log;
    }

    /// <summary>How long the gallery must be unused before it counts as idle.</summary>
    internal TimeSpan QuietFor { get; set; } = TimeSpan.FromMinutes(5);

    private bool Enabled => _settings.GetBool(SettingsService.TranscodeWhenIdle) && _settings.GetBool(SettingsService.KeepTranscodes, true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var goOn = false;
            try { goOn = await StepAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { _log.LogWarning(ex, "Converting videos while idle failed"); }
            if (goOn) continue;
            try { await Task.Delay(Check, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Converts the next video if it is time. Returns true to go straight on to the next one (after a conversion
    /// finished or failed), false after one was stopped (a played video goes first) or when there was nothing to do.
    /// </summary>
    internal async Task<bool> StepAsync(CancellationToken ct)
    {
        var (status, video) = Look();
        _transcodes.Idle = status;
        if (video is null) return false;

        using var job = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watch = StopWhenTurnedOffAsync(job);
        bool? done;
        try { done = await _transcodes.PersistWhileIdleAsync(video, job.Token); }
        finally { await job.CancelAsync(); await watch; }
        if (done == false) _failed.Add(video.Id);

        // If it was stopped, Look() usually says why (turned off, or someone played a video). Otherwise retry shortly.
        var after = Look().Status;
        _transcodes.Idle = done is null && after.State == "converting" ? new("waiting", after.Left, Waiting: "stopped; trying again shortly") : after;
        return done is not null;
    }

    /// <summary>The status to show, and the next video to convert when it is time.</summary>
    private (IdleTranscodeStatus Status, Asset? Next) Look()
    {
        if (!Enabled) return (IdleTranscodeStatus.Off, null);
        if (!_ffmpeg.IsAvailable) return (new("waiting", Waiting: "ffmpeg is not available"), null);
        var todo = _transcodes.NeedingCopies(_failed);
        if (todo.Count == 0) return (new("done"), null);
        if (WhyBusy() is { } reason) return (new("waiting", todo.Count, Waiting: reason), null);
        return (new("converting", todo.Count, todo[0].FileName), todo[0]);
    }

    /// <summary>Why the server isn't idle, or null when it is.</summary>
    private string? WhyBusy() =>
        _status.BackgroundWork() ?? (_activity.Quiet < QuietFor ? $"the gallery was used in the last {QuietFor.TotalMinutes:0} minutes" : null);

    private async Task StopWhenTurnedOffAsync(CancellationTokenSource job)
    {
        try
        {
            while (!job.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), job.Token);
                if (!Enabled) { _log.LogInformation("Converting videos while idle was turned off"); await job.CancelAsync(); }
            }
        }
        catch (OperationCanceledException) { }
    }
}
