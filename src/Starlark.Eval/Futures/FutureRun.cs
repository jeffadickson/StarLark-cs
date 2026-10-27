using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Starlark.Syntax;

namespace Starlark.Eval.Futures;

/// <summary>Limits for the tasks of one run. The defaults are the "untrusted host" profile of the design.</summary>
public sealed record FutureLimits
{
    /// <summary>Tasks running at once. The scheduler never blocks the spawning script when this is reached; extra tasks queue.</summary>
    public int MaxConcurrency { get; init; } = 4;

    /// <summary>Total tasks one run may start.</summary>
    public int MaxSpawns { get; init; } = 10_000;

    /// <summary>Futures that have been started but not yet collected.</summary>
    public int MaxLiveFutures { get; init; } = 1_000;

    /// <summary>Steps charged to the spawner for each task started.</summary>
    public int SpawnStepCost { get; init; } = 1_000;

    /// <summary>The most worker threads one run may ever create, whatever <see cref="MaxConcurrency"/> says.</summary>
    public const int MaxWorkersCeiling = 64;

    /// <summary>How long a run waits for tasks to stop when it ends.</summary>
    public TimeSpan GracePeriod { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>What happened in a run, for the host.</summary>
public sealed record RunStats(long Spawns, int PeakLiveFutures, int PeakConcurrency, int LeakedTasks);

/// <summary>
/// The tasks of one top-level run: owns the scheduler, the live-future registry, the counters and the shutdown
/// protocol. Created lazily by the first <c>spawn</c> or <c>parallel_map</c> and disposed when the run ends.
/// </summary>
internal sealed class FutureRun : IDisposable
{
    private readonly object _gate = new();
    private readonly List<StarFuture> _live = [];
    private readonly CancellationTokenSource _cts;
    private readonly CancellationToken _token;
    private readonly List<Thread> _workers = [];
    private readonly int _maxWorkers;
    private readonly BlockingCollection<Action> _queue = new();
    private int _disposed;
    private bool _closing;
    private long _spawns;
    private int _peakLive;
    private int _running;
    private int _peakRunning;
    private int _leaked;

    public FutureRun(Interpreter interpreter, FutureLimits limits, CancellationToken parent)
    {
        Interpreter = interpreter;
        Limits = limits;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        _token = _cts.Token;   // read once: the source is disposed at teardown, but a late worker must never touch it
        // A dedicated, bounded set of threads, started on demand: script tasks never use the shared thread pool, so a
        // blocked task cannot starve the host, and tasks queue (rather than block the spawner) when every worker is busy.
        _maxWorkers = Math.Clamp(limits.MaxConcurrency, 1, FutureLimits.MaxWorkersCeiling);
    }

    private void EnsureWorker(int queued)
    {
        lock (_gate)
        {
            if (_workers.Count >= _maxWorkers || _workers.Count >= queued + _running) return;
            var thread = new Thread(WorkerLoop, 16 * 1024 * 1024) { IsBackground = true, Name = "starlark-task-" + _workers.Count };
            _workers.Add(thread);
            thread.Start();
        }
    }

    public Interpreter Interpreter { get; }
    public FutureLimits Limits { get; }
    public CancellationToken Token => _token;

    public RunStats Stats
    {
        get
        {
            lock (_gate) return new RunStats(_spawns, _peakLive, _peakRunning, _leaked);
        }
    }

    private void WorkerLoop()
    {
        // Nothing may escape this loop: an unhandled exception on a non-pool thread ends the whole process.
        try
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                try
                {
                    work();
                }
                catch (Exception)
                {
                    // RunTask completes the future itself; anything reaching here is already recorded.
                }
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Starts <paramref name="body"/> as a task. The caller has already deep-copied the arguments. The spawner is
    /// charged <see cref="FutureLimits.SpawnStepCost"/> steps.
    /// </summary>
    public StarFuture Start(Func<object?> body, ExecutionContext parent, int taskCount)
    {
        StarFuture future;
        lock (_gate)
        {
            if (_closing) throw new StarlarkException("The run is ending: no more tasks can be started");
            if (_spawns >= Limits.MaxSpawns)
            {
                throw new ExecutionLimitException("tasks", $"Script started more than {Limits.MaxSpawns} tasks");
            }
            if (_live.Count >= Limits.MaxLiveFutures)
            {
                throw new ExecutionLimitException("futures", $"Script has more than {Limits.MaxLiveFutures} uncollected futures");
            }
            _spawns++;
            future = new StarFuture(this);
            _live.Add(future);
            _peakLive = Math.Max(_peakLive, _live.Count);
        }
        parent.Charge(Limits.SpawnStepCost);

        // Each task gets a deterministic slice of what the spawner has left, and its own copy of the run state.
        long slice = taskCount > 1 ? parent.SliceFor(taskCount) : parent.SliceForSpawn(Limits.MaxLiveFutures);
        var context = parent.ForTask(slice, _token);
        future.Context = context;
        future.Slice = slice;
        _queue.Add(() => RunTask(future, context, body));
        EnsureWorker(_queue.Count);
        return future;
    }

    private void RunTask(StarFuture future, ExecutionContext context, Func<object?> body)
    {
        int now = Interlocked.Increment(ref _running);
        lock (_gate) _peakRunning = Math.Max(_peakRunning, now);
        try
        {
            if (_token.IsCancellationRequested)
            {
                future.Complete(null, new OperationCanceledException(_token));
                return;
            }
            try
            {
                using var scope = ExecutionContext.Enter(context);
                future.Complete(Isolation.FreezeResult(body()), null);
            }
            catch (Exception e)
            {
                future.Complete(null, e);
                // A limit in any task ends the whole run, whether or not the script collects this future. A task's own
                // cancellation (for example a host call timing out) only counts when it is the run being cancelled.
                if (e is ExecutionLimitException || (e is OperationCanceledException && _token.IsCancellationRequested)) Fail(e);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    private Exception? _limitFailure;

    private void Fail(Exception e)
    {
        lock (_gate) _limitFailure ??= e;
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run was torn down while this worker was still finishing: nothing is left to cancel.
        }
    }

    /// <summary>The limit or cancellation that ended the run, if a task caused one.</summary>
    public Exception? LimitFailure
    {
        get
        {
            lock (_gate) return _limitFailure;
        }
    }

    /// <summary>Marks a future as collected, so it no longer counts against the live-future cap.</summary>
    public void Collected(StarFuture future)
    {
        lock (_gate) _live.Remove(future);
    }

    /// <summary>Futures not yet collected.</summary>
    public IReadOnlyList<StarFuture> Live
    {
        get
        {
            lock (_gate) return _live.ToList();
        }
    }

    /// <summary>
    /// Ends the run: refuses new tasks, cancels the rest, waits up to the grace period for them to stop, and observes
    /// every fault so none surfaces later as an unobserved task exception.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        List<StarFuture> pending;
        Thread[] workers;
        lock (_gate)
        {
            _closing = true;
            pending = _live.ToList();
            workers = _workers.ToArray();
        }
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        _queue.CompleteAdding();
        long graceTicks = (long)(Math.Min(Limits.GracePeriod.TotalSeconds, 3600) * System.Diagnostics.Stopwatch.Frequency);
        long deadline = System.Diagnostics.Stopwatch.GetTimestamp() + graceTicks;
        int stuck = 0;
        foreach (var w in workers)
        {
            long left = deadline - System.Diagnostics.Stopwatch.GetTimestamp();
            var span = TimeSpan.FromSeconds(Math.Max(0, (double)left / System.Diagnostics.Stopwatch.Frequency));
            if (!w.Join(span) && w.IsAlive) stuck++;
        }
        lock (_gate) _leaked = stuck;
        foreach (var f in pending) f.Observe();
        // A worker that did not stop still holds the token source and the queue: leave them for it, and let the
        // finalizer reclaim them once it ends, rather than pulling them out from under it.
        if (stuck == 0)
        {
            _cts.Dispose();
            _queue.Dispose();
        }
    }
}
