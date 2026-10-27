using Starlark.Syntax;

namespace Starlark.Eval.Futures;

/// <summary>
/// The script-visible functions of the futures extension: <c>parallel_map</c>, <c>spawn</c>, <c>all_of</c> and, under
/// the nondeterministic flag, <c>first_of</c>. They exist only when <see cref="Dialect.EnableFutures"/> is set.
/// </summary>
internal static class FutureBuiltins
{
    /// <summary>Installs the functions on an interpreter whose dialect enables futures.</summary>
    public static void Install(Dictionary<string, object?> universe, Interpreter interpreter)
    {
        universe["parallel_map"] = new Builtin("parallel_map", ParallelMap);
        universe["spawn"] = new Builtin("spawn", Spawn);
        universe["all_of"] = new Builtin("all_of", AllOf);
        if (interpreter.Dialect.EnableNondeterministicFutures)
        {
            universe["first_of"] = new Builtin("first_of", FirstOf);
        }
    }

    /// <summary>The context of the script thread calling a futures function, which must not itself be a task.</summary>
    public static ExecutionContext Owner(string function)
    {
        var ctx = ExecutionContext.Current ?? throw new InvalidOperationException("No execution context");
        if (ctx.IsTask)
        {
            throw new StarlarkException($"`{function}` cannot be called from inside a task: tasks cannot start or wait for other tasks");
        }
        return ctx;
    }

    public static void CheckSameRun(StarFuture future, ExecutionContext owner)
    {
        if (!ReferenceEquals(owner.Run, future.RunOrNull))
        {
            throw new StarlarkException("A future can only be used in the run that created it");
        }
    }

    private static FutureRun RunOf(ExecutionContext owner)
    {
        if (owner.Run is { } existing) return existing;
        var interp = owner.Interpreter;
        owner.Run = new FutureRun(interp, interp.FutureLimits, owner.Token);
        owner.OwnsRun = true;
        return owner.Run;
    }

    private static void CheckCallable(object? callee, string function)
    {
        switch (callee)
        {
            case StarFunction f when Isolation.CapturesLiveFrame(f):
                throw new StarlarkException(
                    $"`{function}`: function `{f.Name}` captures variables of a function that is still running; " +
                    "define it at module level or pass the values as arguments");
            case BoundMethod { Receiver: Mutable { Frozen: false } }:
                throw new StarlarkException($"`{function}`: a method of a list, dict or set that can still change cannot run in a task: pass the values instead");
            case StarFunction or Builtin or BoundMethod:
                return;
            case ICallable:
                return;
            default:
                throw new StarlarkException($"`{function}` needs a function, got '{Types.Name(callee)}'");
        }
    }

    // ---------------------------------------------------------------- parallel_map

    private static object? ParallelMap(Interpreter interp, IReadOnlyList<object?> args, IReadOnlyList<KeyValuePair<string, object?>> kwargs)
    {
        var reader = new ArgReader("parallel_map", args, kwargs);
        var fn = reader.Required("function");
        var items = reader.Required("iterable");
        reader.Done();
        var owner = Owner("parallel_map");
        CheckCallable(fn, "parallel_map");

        int cap = owner.Interpreter.FutureLimits.MaxLiveFutures;
        // Check the size before building anything, so a huge range or a lazy source cannot exhaust memory first.
        if (items is StarRange r && r.Count > cap)
        {
            throw new ExecutionLimitException("futures", $"parallel_map over {r.Count} items exceeds the limit of {cap}");
        }
        var list = new List<object?>();
        foreach (var item in Ops.Iterate(items))
        {
            if (list.Count >= cap)
            {
                throw new ExecutionLimitException("futures", $"parallel_map over more than {cap} items exceeds the limit");
            }
            list.Add(item);
        }
        var futures = new List<StarFuture>(list.Count);
        try
        {
            foreach (var item in list) futures.Add(Start(owner, fn!, [item], [], list.Count));
            return Collect(owner, futures);
        }
        finally
        {
            foreach (var f in futures) f.Abandon(owner);
        }
    }

    // ---------------------------------------------------------------- spawn, get, all_of

    private static object? Spawn(Interpreter interp, IReadOnlyList<object?> args, IReadOnlyList<KeyValuePair<string, object?>> kwargs)
    {
        if (args.Count == 0) throw new StarlarkException("Missing parameter `function` for call to `spawn`");
        var owner = Owner("spawn");
        var fn = args[0];
        CheckCallable(fn, "spawn");
        return Start(owner, fn!, args.Skip(1).ToList(), kwargs.ToList(), 1);
    }

    private static object? AllOf(Interpreter interp, IReadOnlyList<object?> args, IReadOnlyList<KeyValuePair<string, object?>> kwargs)
    {
        var reader = new ArgReader("all_of", args, kwargs);
        var list = reader.Required("futures");
        reader.Done();
        var owner = Owner("all_of");
        var futures = new List<StarFuture>();
        foreach (var x in Ops.Iterate(list))
        {
            futures.Add(x as StarFuture ?? throw new StarlarkException($"`all_of` needs futures, got '{Types.Name(x)}'"));
        }
        if (futures.Count > owner.Interpreter.FutureLimits.MaxLiveFutures)
        {
            throw new ExecutionLimitException("futures", $"all_of over {futures.Count} futures exceeds the limit of {owner.Interpreter.FutureLimits.MaxLiveFutures}");
        }
        foreach (var f in futures) CheckSameRun(f, owner);
        return Collect(owner, futures);
    }

    private static object? FirstOf(Interpreter interp, IReadOnlyList<object?> args, IReadOnlyList<KeyValuePair<string, object?>> kwargs)
    {
        var reader = new ArgReader("first_of", args, kwargs);
        var list = reader.Required("futures");
        reader.Done();
        var owner = Owner("first_of");
        owner.Charge(100);
        var futures = new List<StarFuture>();
        foreach (var x in Ops.Iterate(list))
        {
            futures.Add(x as StarFuture ?? throw new StarlarkException($"`first_of` needs futures, got '{Types.Name(x)}'"));
        }
        if (futures.Count == 0) throw new StarlarkException("`first_of` needs at least one future");
        foreach (var f in futures) CheckSameRun(f, owner);

        var any = Task.WhenAny(futures.Select(f => f.Completion));
        if (!any.Wait(Remaining(owner), owner.Token))
        {
            throw new ExecutionLimitException("time", $"Script exceeded the time limit of {owner.Interpreter.Timeout}");
        }
        int index = futures.FindIndex(f => f.IsDone);
        var winner = futures[index];
        winner.Join(owner);
        return new StarTuple([new System.Numerics.BigInteger(index), winner.Result()]);
    }

    private static TimeSpan Remaining(ExecutionContext owner)
    {
        if (owner.DeadlineTicks == 0) return Timeout.InfiniteTimeSpan;
        long left = owner.DeadlineTicks - System.Diagnostics.Stopwatch.GetTimestamp();
        return TimeSpan.FromSeconds(Math.Max(0, (double)left / System.Diagnostics.Stopwatch.Frequency));
    }

    // ---------------------------------------------------------------- shared machinery

    private static StarFuture Start(ExecutionContext owner, object fn, IReadOnlyList<object?> args,
        IReadOnlyList<KeyValuePair<string, object?>> kwargs, int taskCount)
    {
        var run = RunOf(owner);
        // Copy the arguments on the spawning thread, before the task can run, so the caller keeps its values.
        var seen = new Dictionary<object, object?>(ReferenceEqualityComparer.Instance);
        var copiedArgs = args.Select(a => Isolation.Copy(a, seen)).ToList();
        var copiedKwargs = kwargs.Select(kv => new KeyValuePair<string, object?>(kv.Key, Isolation.Copy(kv.Value, seen))).ToList();

        var interp = owner.Interpreter;
        // A module-level function runs against a private frozen snapshot of the globals it reads.
        object callee = fn is StarFunction sf ? Isolation.Snapshot(sf, seen) : fn;
        var future = run.Start(() => interp.CallValue(callee, copiedArgs, copiedKwargs), owner, taskCount);
        future.Serial = owner.Spawned.Count;
        owner.Spawned.Add(future);
        return future;
    }

    /// <summary>
    /// Waits for every future and settles them in order. If any failed, the error of the lowest-index failure is raised
    /// after all of them have finished, so the outcome does not depend on which task happened to fail first.
    /// </summary>
    private static List<object?> CollectAll(ExecutionContext owner, IReadOnlyList<StarFuture> futures, out Exception? firstError)
    {
        foreach (var f in futures) f.Wait(owner);
        var results = new List<object?>(futures.Count);
        firstError = null;
        foreach (var f in futures)
        {
            f.Join(owner);
            if (f.Error is { } e && firstError is null) firstError = e;
            results.Add(f.Error is null ? f.PeekResult : null);
        }
        return results;
    }

    private static object? Collect(ExecutionContext owner, IReadOnlyList<StarFuture> futures)
    {
        var results = CollectAll(owner, futures, out var error);
        var limit = futures.Select(f => f.Run.LimitFailure).FirstOrDefault(x => x is not null);
        if (limit is not null) throw StarFuture.Fresh(limit);
        if (error is not null) throw StarFuture.Fresh(error);
        return new StarList(results);
    }

    /// <summary>Called when a call or module ends: any future it started but did not collect is an error.</summary>
    public static void CheckAllCollected(ExecutionContext owner)
    {
        if (owner.Spawned.Count == 0) return;
        int n = owner.Spawned.Count;
        var limit = owner.Run?.LimitFailure;
        foreach (var f in owner.Spawned.ToList()) f.Abandon(owner);
        // A limit hit by a task nobody collected is still a resource failure, not a mistake in the script.
        if (limit is not null) throw StarFuture.Fresh(limit);
        throw new StarlarkException($"{n} future(s) were started but never collected: call get() or all_of() before the call ends");
    }
}
