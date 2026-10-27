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
        MaxBytes = interpreter.MaxAllocatedBytes;
        Elements = interpreter.StringElements;
        Token = interpreter.Cancellation;
        DeadlineTicks = interpreter.Timeout is { } t
            ? System.Diagnostics.Stopwatch.GetTimestamp() + (long)(t.TotalSeconds * System.Diagnostics.Stopwatch.Frequency)
            : 0;
    }

    /// <summary>The interpreter whose configuration this run uses.</summary>
    public Interpreter Interpreter { get; }

    /// <summary>The step ceiling, or 0 for none.</summary>
    public long MaxSteps { get; }

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
    public int Depth;

    /// <summary>Functions currently on the call stack, for the recursion ban.</summary>
    public HashSet<object> Active { get; } = [];

    private int _poll;

    /// <summary>Counts one unit of work and, every 1024 units, checks the time and cancellation budgets.</summary>
    public void Step()
    {
        if (MaxSteps > 0 && ++Steps > MaxSteps)
        {
            throw new ExecutionLimitException("steps", $"Script exceeded the limit of {MaxSteps} steps");
        }
        if ((Steps & 1023) == 0 || MaxSteps == 0)
        {
            Poll();
        }
    }

    /// <summary>Throws if the run was cancelled or its deadline has passed.</summary>
    public void Poll()
    {
        if (MaxSteps == 0 && (++_poll & 1023) != 0) return;
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
    public static Scope EnterOrReuse(Interpreter interpreter) =>
        _current is { } outer && ReferenceEquals(outer.Interpreter, interpreter)
            ? default
            : Enter(new ExecutionContext(interpreter));

    /// <summary>Restores the previous context. The default value does nothing.</summary>
    internal readonly struct Scope(bool active, ExecutionContext? previous, Strings.Scope strings, long previousBytes) : IDisposable
    {
        public void Dispose()
        {
            if (!active) return;
            _current = previous;
            MemoryBudget.Set(previousBytes);
            strings.Dispose();
        }
    }
}
