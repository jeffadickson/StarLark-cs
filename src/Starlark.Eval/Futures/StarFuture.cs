using System.Runtime.ExceptionServices;

namespace Starlark.Eval.Futures;

/// <summary>
/// The result of work running on another thread. It is a non-escaping handle: it has no truth value, cannot be hashed,
/// cannot be frozen until it has been collected, and is only valid inside the run that created it.
/// </summary>
internal sealed class StarFuture : IHostValue
{
    private readonly FutureRun _run;
    private readonly TaskCompletionSource<bool> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private object? _result;
    private Exception? _error;

    public StarFuture(FutureRun run) => _run = run;

    /// <summary>The run that created this future.</summary>
    public FutureRun Run => _run;

    /// <summary>The run, for comparison with the caller's run.</summary>
    public FutureRun RunOrNull => _run;

    /// <summary>The result without checking for errors; only valid once the task has finished without one.</summary>
    public object? PeekResult => _result;

    /// <summary>The task's own run state, whose step count and printed output are settled when it is collected.</summary>
    public ExecutionContext? Context { get; set; }

    /// <summary>The steps reserved for the task, refunded (less what it used) when it is collected.</summary>
    public long Slice { get; set; }

    /// <summary>Order in which the owner started its futures; used to find the ones a call left uncollected.</summary>
    public long Serial { get; set; }

    /// <summary>Whether the result has been collected by the owner.</summary>
    public bool Collected { get; private set; }

    /// <summary>Whether the task has finished (successfully or not).</summary>
    public bool IsDone => _done.Task.IsCompleted;

    /// <summary>The underlying completion, for waiting on several futures.</summary>
    public Task Completion => _done.Task;

    public void Complete(object? result, Exception? error)
    {
        _result = result;
        _error = error;
        _done.TrySetResult(true);
    }

    /// <summary>Marks the future as handled without reading it, so it stops counting against the live cap.</summary>
    public void Abandon(ExecutionContext owner)
    {
        if (Collected) return;
        Collected = true;
        owner.Spawned.Remove(this);
        _run.Collected(this);
        Observe();
    }

    /// <summary>Reads the outcome so a fault is never left unobserved.</summary>
    public void Observe()
    {
        if (_done.Task.IsCompleted) _ = _error;
    }

    /// <summary>Blocks until the task finishes, the waiter's deadline passes, or the waiter is cancelled.</summary>
    public void Wait(ExecutionContext waiter)
    {
        if (_done.Task.IsCompleted) return;
        if (waiter.DeadlineTicks != 0)
        {
            long left = waiter.DeadlineTicks - System.Diagnostics.Stopwatch.GetTimestamp();
            var span = TimeSpan.FromSeconds(Math.Max(0, (double)left / System.Diagnostics.Stopwatch.Frequency));
            if (!_done.Task.Wait(span, waiter.Token))
            {
                throw new ExecutionLimitException("time", $"Script exceeded the time limit of {waiter.Interpreter.Timeout}");
            }
        }
        else
        {
            _done.Task.Wait(waiter.Token);
        }
    }

    /// <summary>
    /// Settles the task with its owner, once: refunds unused steps, prints what the task printed, and stops counting
    /// the future against the cap. Always happens in the owner's program order, so the result is deterministic.
    /// </summary>
    public void Join(ExecutionContext owner)
    {
        if (Collected) return;
        Collected = true;
        owner.Spawned.Remove(this);
        _run.Collected(this);
        if (Context is { } task)
        {
            owner.Refund(Slice - task.Steps);
            if (task.Output is { } lines)
            {
                foreach (var line in lines) owner.Interpreter.Print(line);
            }
        }
    }

    /// <summary>The value, or a fresh copy of the error the task raised (so threads never share one exception object).</summary>
    public object? Result()
    {
        if (_run.LimitFailure is { } limit) throw Fresh(limit);
        if (_error is { } e) throw Fresh(e);
        return _result;
    }

    /// <summary>The error the task raised, if it failed.</summary>
    public Exception? Error => _error;

    public static Exception Fresh(Exception e)
    {
        switch (e)
        {
            case StarlarkException s:
            {
                var copy = new StarlarkException(s.RawMessage);
                if (s.Span is { } span && s.CodeMap is { } map) copy.AttachLocation(span, map);
                return copy;
            }
            case ExecutionLimitException l:
                return new ExecutionLimitException(l.Limit, l.Message);
            case OperationCanceledException:
                return new OperationCanceledException(e.Message);
            default:
                return new StarlarkException($"Task failed with {e.GetType().Name}: {e.Message}");
        }
    }

    // ---------------------------------------------------------------- script-visible behaviour

    public string TypeName => "future";

    public string Repr() => "<future>";

    public bool Truthy() => throw new StarlarkException("A future has no truth value: call get() to read its result");

    public int HashValue() => throw new StarlarkException("unhashable type: 'future'");

    public bool EqualsValue(object? other) => ReferenceEquals(this, other);

    public IEnumerable<string> Attributes() => _run.Interpreter.Dialect.EnableNondeterministicFutures ? ["get", "done"] : ["get"];

    public bool TryGetAttribute(string name, out object? value)
    {
        switch (name)
        {
            case "get":
                value = new Builtin("get", (it, a, k) =>
                {
                    new ArgReader("get", a, k).Done();
                    return GetValue();
                });
                return true;
            case "done" when _run.Interpreter.Dialect.EnableNondeterministicFutures:
                value = new Builtin("done", (it, a, k) =>
                {
                    new ArgReader("done", a, k).Done();
                    ExecutionContext.Current?.Charge(10);
                    return IsDone;
                });
                return true;
        }
        value = null;
        return false;
    }

    public void Freeze()
    {
        if (!Collected)
        {
            throw new StarlarkException("A future that has not been collected cannot be stored: call get() or all_of() first");
        }
    }

    /// <summary>Implements <c>future.get()</c>.</summary>
    private object? GetValue()
    {
        var owner = FutureBuiltins.Owner("get");
        FutureBuiltins.CheckSameRun(this, owner);
        Wait(owner);
        Join(owner);
        return Result();
    }
}
