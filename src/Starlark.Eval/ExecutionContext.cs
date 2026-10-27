namespace Starlark.Eval;

/// <summary>
/// Everything that changes while a script runs: the step counter, deadline, call depth, active-recursion set, string
/// element mode, memory ceiling and cancellation token. One context belongs to one thread of execution (the outermost
/// call, or a task), so frozen modules can be called from many threads at once without sharing any mutable state.
/// </summary>
/// <remarks>
/// The context is installed on the running thread by <see cref="Enter"/>; <see cref="Strings"/> and
/// <see cref="MemoryBudget"/> read the string mode and memory ceiling from the thread-static copies that
/// <see cref="Enter"/> sets and restores. Every thread that evaluates script code must enter a context first.
/// </remarks>
internal sealed class ExecutionContext
{
    [ThreadStatic] private static ExecutionContext? _current;

    /// <summary>The context installed on the calling thread, or null if no script is running on it.</summary>
    public static ExecutionContext? Current => _current;

    /// <summary>Creates a context for a run that starts now: budgets begin at zero and the deadline is measured from now.</summary>
    public ExecutionContext(Interpreter interpreter)
    {
        Interpreter = interpreter;
        MaxSteps = interpreter.MaxSteps;
        ReportedMaxSteps = interpreter.MaxSteps;
        MaxBytes = interpreter.MaxAllocatedBytes;
        Elements = interpreter.StringElements;
        Token = interpreter.Cancellation;
        DeadlineTicks = interpreter.Timeout is { } t
            ? System.Diagnostics.Stopwatch.GetTimestamp() + (long)(t.TotalSeconds * System.Diagnostics.Stopwatch.Frequency)
            : 0;
    }

    /// <summary>Creates the context of a task started by <paramref name="parent"/>, with its own slice of the step budget.</summary>
    private ExecutionContext(ExecutionContext parent, long maxSteps, CancellationToken token)
    {
        Interpreter = parent.Interpreter;
        MaxSteps = maxSteps;
        ReportedMaxSteps = parent.ReportedMaxSteps;
        MaxBytes = parent.MaxBytes;
        Elements = parent.Elements;
        Token = token;
        DeadlineTicks = parent.DeadlineTicks;
        Depth = parent.Depth;
        // The recursion ban holds across tasks: a task starts with the functions its spawner was inside.
        Active = [.. parent.Active];
        IsTask = true;
        Output = [];
        Run = parent.Run;
    }

    /// <summary>True for a task: it may not spawn, collect, or load.</summary>
    public bool IsTask { get; }

    /// <summary>The most a single task may print, in bytes, before it is stopped.</summary>
    public const long MaxTaskOutputBytes = 8 * 1024 * 1024;

    /// <summary>Bytes printed so far by this task.</summary>
    public long OutputBytes;

    /// <summary>Lines a task printed; the joiner prints them, in order, when it collects the task. Null outside a task.</summary>
    public List<string>? Output { get; }

    /// <summary>The tasks of this run (created by the first spawn), or null.</summary>
    public Futures.FutureRun? Run { get; set; }

    /// <summary>True if this context created <see cref="Run"/> and must dispose it when it ends.</summary>
    public bool OwnsRun { get; set; }

    /// <summary>Futures started by this context, in order. Only the owning thread touches this list.</summary>
    public List<Futures.StarFuture> Spawned { get; } = [];

    /// <summary>The step budget still available, or 0 if the run has no step limit.</summary>
    public long Remaining => MaxSteps > 0 ? Math.Max(0, MaxSteps - Steps) : 0;

    /// <summary>Charges <paramref name="steps"/> to this run, as if it had executed them.</summary>
    public void Charge(long steps)
    {
        if (MaxSteps <= 0) return;
        Steps += steps;
        if (Steps > MaxSteps) throw new ExecutionLimitException("steps", $"Script exceeded the limit of {ReportedMaxSteps} steps");
    }

    /// <summary>
    /// A deterministic share of the remaining step budget for one of <paramref name="tasks"/> tasks (0 means no limit,
    /// so a limited run never hands out 0).
    /// </summary>
    public long SliceFor(int tasks) => MaxSteps > 0 ? Math.Max(1, Remaining / Math.Max(1, tasks)) : 0;

    /// <summary>
    /// The slice for a single <c>spawn</c>: a fixed fraction of what is left, so that many spawns together cannot be
    /// handed more than the whole budget many times over. Each task is still charged only for what it uses, at join.
    /// </summary>
    public long SliceForSpawn(int maxLiveFutures) => SliceFor(Math.Max(2, maxLiveFutures));

    /// <summary>The context for a task with the given slice, sharing this run's deadline and the given token.</summary>
    public ExecutionContext ForTask(long slice, CancellationToken token) => new(this, slice, token);

    /// <summary>The interpreter whose configuration this run uses.</summary>
    public Interpreter Interpreter { get; }

    /// <summary>The step ceiling, or 0 for none.</summary>
    public long MaxSteps { get; }

    /// <summary>The run-level step limit that error messages quote, the same for every task.</summary>
    public long ReportedMaxSteps { get; }

    /// <summary>Steps taken so far.</summary>
    public long Steps;

    /// <summary>The absolute <see cref="System.Diagnostics.Stopwatch"/> timestamp of the deadline, or 0 for none.</summary>
    public long DeadlineTicks { get; }

    /// <summary>The largest single allocation in bytes, or 0 for none.</summary>
    public long MaxBytes { get; }

    /// <summary>The string element mode of this run.</summary>
    public StringElements Elements { get; }

    /// <summary>The cancellation token of this run.</summary>
    public CancellationToken Token { get; }

    /// <summary>Current call depth.</summary>
    public int Depth { get; set; }

    /// <summary>Functions currently on the call stack, for the recursion ban.</summary>
    public HashSet<object> Active { get; private set; } = [];

    private int _poll;
    private long _ticks;

    /// <summary>Tells the dispose protocol which context to clean up. Set by <see cref="Enter"/>.</summary>

    /// <summary>Counts one unit of work and, every 1024 units, checks the time and cancellation budgets.</summary>
    public void Step()
    {
        if (MaxSteps > 0 && ++Steps > MaxSteps)
        {
            throw new ExecutionLimitException("steps", $"Script exceeded the limit of {ReportedMaxSteps} steps");
        }
        if ((++_ticks & 1023) == 0)
        {
            PollNow();
        }
    }

    /// <summary>Throws if the run was cancelled or its deadline has passed.</summary>
    public void Poll()
    {
        if ((++_poll & 1023) != 0) return;
        PollNow();
    }

    /// <summary>Checks cancellation and the deadline now.</summary>
    public void PollNow()
    {
        Token.ThrowIfCancellationRequested();
        if (DeadlineTicks != 0 && System.Diagnostics.Stopwatch.GetTimestamp() > DeadlineTicks)
        {
            throw new ExecutionLimitException("time", $"Script exceeded the time limit of {Interpreter.Timeout}");
        }
    }

    /// <summary>
    /// Installs a context on the calling thread (and its string mode and memory ceiling) until the result is
    /// disposed, which restores whatever was installed before.
    /// </summary>
    public static Scope Enter(ExecutionContext context)
    {
        var scope = new Scope(true, _current, Strings.Enter(context.Elements), MemoryBudget.Set(context.MaxBytes));
        _current = context;
        return scope;
    }

    /// <summary>
    /// Installs <paramref name="context"/> unless this thread already runs a script for the same interpreter, in which
    /// case the running context is reused (a nested call shares the outer call's budgets). The result does nothing on
    /// disposal when nothing was installed.
    /// </summary>
    public static Scope EnterOrReuse(Interpreter interpreter)
    {
        if (_current is { } outer)
        {
            if (ReferenceEquals(outer.Interpreter, interpreter)) return default;
            if (outer.IsTask) throw new StarlarkException("A task cannot call into another interpreter");
        }
        return Enter(new ExecutionContext(interpreter));
    }

    /// <summary>
    /// Like <see cref="EnterOrReuse"/>, and for an outermost call also ends the run when it finishes: futures the
    /// call started but never collected are an error, and every task is cancelled, awaited and observed.
    /// </summary>
    public static RunScope EnterRun(Interpreter interpreter)
    {
        var scope = EnterOrReuse(interpreter);
        return new RunScope(scope, scope.IsActive ? _current : null);
    }

    /// <summary>Ends an outermost run: checks that every future was collected, then tears the run down.</summary>
    internal readonly struct RunScope(Scope scope, ExecutionContext? owned) : IDisposable
    {
        /// <summary>True if this scope started the run (as opposed to a nested call inside a running script).</summary>
        public bool IsOutermost => owned is not null;

        public void Dispose()
        {
            if (owned is null)
            {
                scope.Dispose();
                return;
            }
            try
            {
                // The normal-return path (Execute) has already rejected uncollected futures. If we are here with some
                // left, the call is failing for another reason, which must win; just release them.
                foreach (var f in owned.Spawned.ToList()) f.Abandon(owned);
            }
            finally
            {
                if (owned.Run is { } run && owned.OwnsRun)
                {
                    run.Dispose();   // counts workers that did not stop within the grace period
                    owned.Interpreter.RecordRun(run.Stats);
                    owned.Run = null;
                    owned.OwnsRun = false;
                }
                scope.Dispose();
            }
        }
    }

    /// <summary>Restores the previous context. The default value does nothing.</summary>
    internal readonly struct Scope(bool active, ExecutionContext? previous, Strings.Scope strings, long previousBytes) : IDisposable
    {
        /// <summary>True if this scope installed a context (false for a nested call that reused the outer one).</summary>
        public bool IsActive => active;

        public void Dispose()
        {
            if (!active) return;
            _current = previous;
            MemoryBudget.Set(previousBytes);
            strings.Dispose();
        }
    }
}
