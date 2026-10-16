using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using Gallery.Core.Data;
using Gallery.Core.Services.Faces;

namespace Gallery.Core.Services;

/// <summary>A line of the memory report: a label with an amount in bytes or a text.</summary>
public sealed record MemoryLine(string Label, long? Bytes = null, string? Text = null, string? Note = null);

public sealed record MemorySection(string Title, string? Note, IReadOnlyList<MemoryLine> Lines);

/// <param name="Resident">The process's memory in RAM, which is what the system counts against it.</param>
/// <param name="DotNet">Memory the .NET heap has taken from the system (its objects, plus free room kept for more).</param>
/// <param name="Outside">The rest of the resident memory: native libraries and their buffers, code, stacks.</param>
public sealed record MemorySnapshot(string At, long Resident, long DotNet, long Outside, IReadOnlyList<MemorySection> Sections);

/// <summary>
/// What the server holds in memory, for the admin page:
/// - the process as the system sees it;
/// - the .NET heap (objects the code holds, by generation, and the free room the garbage collector keeps);
/// - everything outside the heap, with the known large parts broken out: the face models (ONNX Runtime's native memory;
///   buffalo_l's 182 MB of files took about 430 MB once used, measured on a Mac), SQLite's caches and ImageSharp's
///   pooled buffers.
/// <see cref="Free"/> gives back what it can.
/// </summary>
public sealed class MemoryReport
{
    private readonly FaceService _faces;
    private readonly ILogger<MemoryReport> _log;

    public MemoryReport(FaceService faces, ILogger<MemoryReport> log)
    {
        _faces = faces;
        _log = log;
    }

    public MemorySnapshot Snapshot()
    {
        var proc = ProcessMemory.Read();
        var gc = GC.GetGCMemoryInfo(GCKind.Any);
        var config = GC.GetConfigurationVariables();
        var dotnet = gc.TotalCommittedBytes;
        var outside = Math.Max(0, proc.Resident - dotnet);

        var process = new List<MemoryLine>
        {
            new("Resident", proc.Resident, Note: "In RAM now: what the system counts against the server"),
        };
        if (proc.Peak is > 0 and var peak) process.Add(new("Peak resident", peak, Note: "The most it has held since it started"));
        if (proc.Anonymous is { } anon) process.Add(new("Not backed by files", anon, Note: "Heaps and buffers; the system can only page it out to swap"));
        if (proc.FileBacked is { } files) process.Add(new("Program and libraries", files, Note: "Mapped files (the runtime, native libraries); shared, and dropped first when RAM is short"));
        process.Add(new("Threads", Text: proc.Threads.ToString("N0")));
        process.Add(LastRelease is { } r
            ? new("Released when idle", Math.Max(0, r.Before - r.After), Text: $"at {r.AtLocal:HH:mm}",
                Note: $"Resident went from {Bytes(r.Before)} to {Bytes(r.After)} the last time the server let go of memory by itself")
            : new("Released when idle", Text: "not yet", Note: "The server lets go of memory by itself once nothing has run for a couple of minutes after a busy spell"));

        var g = gc.GenerationInfo;
        var mode = (bool)config.GetValueOrDefault("ServerGC", false)!
            ? $"Server, {config.GetValueOrDefault("HeapCount")} heaps{(Convert.ToInt64(config.GetValueOrDefault("GCDynamicAdaptationMode", 0L)) == 1 ? ", adapting their number to the load" : "")}"
            : "Workstation";
        var heap = new List<MemoryLine>
        {
            new("Taken from the system", dotnet, Note: "Committed: the objects below, and room kept for new ones"),
            new("In use at the last collection", gc.HeapSizeBytes - gc.FragmentedBytes, Note: "Objects still referenced when the garbage collector last ran"),
            new("Young objects (generations 0 and 1)", g.Length > 1 ? g[0].SizeAfterBytes + g[1].SizeAfterBytes : null, Note: "Short-lived: a request's, a thumbnail's"),
            new("Old objects (generation 2)", g.Length > 2 ? g[2].SizeAfterBytes : null, Note: "Long-lived: caches, settings, services"),
            new("Large objects", g.Length > 3 ? g[3].SizeAfterBytes : null, Note: "85 KB or more each: pixel and file buffers"),
            new("Pinned objects", g.Length > 4 ? g[4].SizeAfterBytes : null),
            new("Free gaps", gc.FragmentedBytes, Note: "Between objects; reused for new ones"),
            new("Allocated since start", GC.GetTotalAllocatedBytes(), Note: "All objects ever made (most long gone)"),
            new("Collections", Text: $"{GC.CollectionCount(0):N0} · {GC.CollectionCount(1):N0} · {GC.CollectionCount(2):N0}", Note: "Generation 0 · 1 · 2"),
            new("Time paused", Text: $"{gc.PauseTimePercentage:0.##} %", Note: "Share of time the garbage collector held up the server"),
            new("Garbage collector", Text: mode, Note: "Server collectors keep more memory per heap, for speed"),
        };

        var models = _faces.Models;
        var sqlite = SQLitePCL.raw.sqlite3_memory_used();
        var known = new List<MemoryLine>
        {
            new("Outside the .NET heap", outside, Note: "Resident less what the .NET heap took"),
            models.Loaded
                ? new("Face models", models.FileBytes, Text: $"{models.Pack}, loaded",
                    Note: $"The size of their files; in memory about 2.4 times that once used. Unloaded after {FaceService.IdleUnload.TotalMinutes:0} minutes without work"
                        + (models.LastUsed is { } used ? $" (last used {Ago(used)})" : ""))
                : new("Face models", Text: "not loaded", Note: "Loaded when photos need searching for faces; unloaded again when idle"),
            new("SQLite", sqlite, Note: $"Its caches for the open database connections; at most {Bytes(SQLitePCL.raw.sqlite3_memory_highwater(0))} so far"),
            new("Image buffers", Text: "kept for reuse", Note: "ImageSharp keeps the buffers of the last pictures made; Free memory now releases them"),
        };

        return new MemorySnapshot(Clock.NowIso(), proc.Resident, dotnet, outside,
        [
            new MemorySection("This process", null, process),
            new MemorySection(".NET heap", "Objects the server's code holds, managed by the garbage collector, and the room it keeps.", heap),
            new MemorySection("Outside the .NET heap", "Native libraries and their buffers, code and thread stacks: memory the garbage collector doesn't manage.", known),
        ]);
    }

    /// <summary>The last idle release (<see cref="IdleMemoryRelease"/>): when it happened, and resident memory before and after.</summary>
    public (DateTime AtLocal, long Before, long After)? LastRelease { get; private set; }

    /// <summary>
    /// Gives back what memory it can, in this order: the face models (when no photo is being searched), ImageSharp's
    /// pooled buffers, SQLite's idle connections and their caches, a full compacting garbage collection, and on Linux the
    /// freed native memory. Returns the report before and after, and what was done.
    /// <paramref name="whenIdle"/> means the server is running it by itself, so the face models are left to their own
    /// idle timer (<see cref="FaceService.IdleUnload"/>).
    /// </summary>
    public (MemorySnapshot Before, MemorySnapshot After, List<string> Done) Free(bool whenIdle = false)
    {
        var before = Snapshot();
        var done = new List<string>();
        if (_faces.UnloadIfIdle(whenIdle ? FaceService.IdleUnload : TimeSpan.Zero)) done.Add("face models unloaded");
        SixLabors.ImageSharp.Configuration.Default.MemoryAllocator.ReleaseRetainedResources();
        done.Add("image buffers released");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        done.Add("idle database connections closed");
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        done.Add(".NET heap collected and compacted");
        if (ProcessMemory.ReturnFreed()) done.Add("freed native memory returned to the system");
        if (whenIdle) LastRelease = (DateTime.Now, before.Resident, ProcessMemory.Read().Resident);
        var after = Snapshot();
        _log.LogInformation("Memory {How}: resident {Before:N0} MB → {After:N0} MB ({Done})", whenIdle ? "released while idle" : "freed on request",
            before.Resident / 1048576, after.Resident / 1048576, string.Join(", ", done));
        return (before, after, done);
    }

    private static string Ago(DateTime utc)
    {
        var t = DateTime.UtcNow - utc;
        return t.TotalMinutes < 1 ? "just now" : t.TotalHours < 1 ? $"{t.TotalMinutes:0} min ago" : $"{t.TotalHours:0.#} h ago";
    }

    private static string Bytes(long n) => n >= 1048576 ? $"{n / 1048576.0:0.#} MB" : $"{n / 1024.0:0} KB";
}

/// <summary>The process's memory as the system sees it: /proc/self/status on Linux, the process's counters elsewhere.</summary>
public static class ProcessMemory
{
    public sealed record Figures(long Resident, long? Peak, long? Anonymous, long? FileBacked, int Threads);

    public static Figures Read()
    {
        if (OperatingSystem.IsLinux())
        {
            try { return ParseStatus(File.ReadLines("/proc/self/status")); }
            catch (IOException) { }
        }
        using var p = Process.GetCurrentProcess();
        return new Figures(p.WorkingSet64, p.PeakWorkingSet64, null, null, p.Threads.Count);
    }

    /// <summary>The figures in Linux's /proc/[pid]/status ("VmRSS:	  972340 kB" and so on).</summary>
    public static Figures ParseStatus(IEnumerable<string> lines)
    {
        var v = lines.Select(l => l.Split(':', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1].Trim());
        long? Kb(string key) => v.TryGetValue(key, out var s) && long.TryParse(s.Split(' ')[0], out var kb) ? kb * 1024 : null;
        return new Figures(Kb("VmRSS") ?? 0, Kb("VmHWM"), Kb("RssAnon"), Kb("RssFile") + Kb("RssShmem"),
            v.TryGetValue("Threads", out var t) && int.TryParse(t, out var n) ? n : 0);
    }

    /// <summary>
    /// glibc holds on to memory that native code freed, for reuse; malloc_trim gives it back to the system. Works on Linux
    /// with glibc only (musl and macOS lack it). Returns whether it ran.
    /// </summary>
    public static bool ReturnFreed()
    {
        if (!OperatingSystem.IsLinux()) return false;
        try { MallocTrim(0); return true; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    [DllImport("libc", EntryPoint = "malloc_trim")]
    private static extern int MallocTrim(nuint pad);
}
