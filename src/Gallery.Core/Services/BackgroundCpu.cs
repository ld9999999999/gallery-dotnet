using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Gallery.Core.Models;

namespace Gallery.Core.Services;

/// <summary>
/// A small set of lower-priority threads for CPU-heavy background work such as finding and grouping faces. That work
/// doesn't belong on the thread pool: a grouping pass is one big Parallel.For that grabs every pool thread, and web
/// requests, which need pool threads too, end up waiting seconds behind it. Here the work is confined to these threads,
/// half the cores unless Gallery:Faces:Workers says otherwise. On Linux each thread gets a nice value of 10 so requests
/// also win the CPU; on Windows the threads run below normal priority.
/// </summary>
public sealed class BackgroundCpu : TaskScheduler, IDisposable
{
    public const int Nice = 10;

    private readonly BlockingCollection<Task> _tasks = new();
    private readonly Thread[] _threads;
    [ThreadStatic] private static BackgroundCpu? _current;

    public BackgroundCpu(GalleryOptions options) : this(options.Faces.Workers > 0 ? options.Faces.Workers : Math.Max(1, Environment.ProcessorCount / 2)) { }

    public BackgroundCpu(int threads)
    {
        _threads = new Thread[Math.Max(1, threads)];
        for (var i = 0; i < _threads.Length; i++)
        {
            _threads[i] = new Thread(Work) { IsBackground = true, Name = $"Gallery background {i + 1}" };
            _threads[i].Start();
        }
    }

    public override int MaximumConcurrencyLevel => _threads.Length;

    /// <summary>Starts async work on these threads, including the code after each await.</summary>
    public Task Run(Func<Task> work, CancellationToken ct = default) =>
        Task.Factory.StartNew(work, ct, TaskCreationOptions.DenyChildAttach, this).Unwrap();

    public Task<T> Run<T>(Func<T> work, CancellationToken ct = default) =>
        Task.Factory.StartNew(work, ct, TaskCreationOptions.DenyChildAttach, this);

    /// <summary>Options that keep a Parallel.For on these threads.</summary>
    public ParallelOptions Parallel(CancellationToken ct = default) =>
        new() { TaskScheduler = this, MaxDegreeOfParallelism = _threads.Length, CancellationToken = ct };

    /// <summary>Whether the calling thread is one of ours. Used by tests.</summary>
    internal bool IsCurrent => _current == this;

    private void Work()
    {
        _current = this;
        Lower();
        try { foreach (var task in _tasks.GetConsumingEnumerable()) TryExecuteTask(task); }
        catch (ObjectDisposedException) { }
    }

    protected override void QueueTask(Task task)
    {
        try { _tasks.Add(task); }
        catch (InvalidOperationException) { ThreadPool.UnsafeQueueUserWorkItem(_ => TryExecuteTask(task), null); } // queue is closed (shutting down)
    }

    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => _current == this && TryExecuteTask(task);

    protected override IEnumerable<Task> GetScheduledTasks() => _tasks.ToArray();

    public void Dispose() => _tasks.CompleteAdding();

    // ---------------------------------------------------------------- priority

    private delegate int GetTid();
    private delegate int SetPriority(int which, int who, int prio);
    private static readonly (GetTid, SetPriority)? Libc = LoadLibc();

    private static (GetTid, SetPriority)? LoadLibc()
    {
        if (!OperatingSystem.IsLinux()) return null;
        foreach (var name in new[] { "libc.so.6", "libc" })
            if (NativeLibrary.TryLoad(name, out var lib)
                && NativeLibrary.TryGetExport(lib, "gettid", out var gettid) && NativeLibrary.TryGetExport(lib, "setpriority", out var setpriority))
                return (Marshal.GetDelegateForFunctionPointer<GetTid>(gettid), Marshal.GetDelegateForFunctionPointer<SetPriority>(setpriority));
        return null;
    }

    /// <summary>Lowers the calling thread's priority, on systems where a thread can have its own.</summary>
    private static void Lower()
    {
        try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { } // Windows; ignored elsewhere
        // On Linux nice is per thread: setpriority(PRIO_PROCESS, thread id) sets just this one.
        try { if (Libc is { } libc) libc.Item2(0, libc.Item1(), Nice); } catch { }
    }

    /// <summary>The calling thread's nice value on Linux, or null elsewhere. Used by tests.</summary>
    internal static int? CurrentNice()
    {
        if (Libc is not { } libc) return null;
        var stat = File.ReadAllText($"/proc/self/task/{libc.Item1()}/stat");
        return int.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[16]); // field 19, counted after "pid (comm) "
    }
}
