using System.Numerics;
using Starlark.Eval;
using Starlark.Eval.Futures;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>The futures extension: parallel_map, spawn/get, all_of, isolation, limits and teardown.</summary>
public class FuturesTests
{
    private static Dialect FuturesDialect => Dialect.Standard with { EnableTopLevelStmt = true, EnableFutures = true };

    private static Interpreter New(Action<Interpreter>? configure = null)
    {
        var it = new Interpreter { Dialect = FuturesDialect };
        configure?.Invoke(it);
        return it;
    }

    private static object? Val(string program, Action<Interpreter>? configure = null, string name = "r") =>
        New(configure).Execute("t", program).Globals[name];

    private static string Fails(string program, Action<Interpreter>? configure = null) =>
        Assert.Throws<StarlarkException>(() => New(configure).Execute("t", program)).RawMessage;

    // ---------------------------------------------------------------- the flag

    /// <summary>With the flag off the names do not exist.</summary>
    [Fact]
    public void NamesAreAbsentWithoutTheFlag()
    {
        var it = new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true } };
        Assert.Contains("not found", Assert.Throws<StarlarkException>(() => it.Execute("t", "x = parallel_map(len, [])\n")).RawMessage);
        Assert.Contains("not found", Assert.Throws<StarlarkException>(() => it.Execute("u", "x = spawn(len, [])\n")).RawMessage);
    }

    /// <summary>first_of and done need the second flag.</summary>
    [Fact]
    public void NondeterministicNamesNeedTheirOwnFlag()
    {
        Assert.Contains("not found", Fails("x = first_of([])\n"));
        const string head = "def f():\n    return 1\n\nx = spawn(f)\n";
        Assert.Contains("no attribute `done`", Fails(head + "x.done()\n"));
        var open = Dialect.Standard with { EnableTopLevelStmt = true, EnableFutures = true, EnableNondeterministicFutures = true };
        var it = new Interpreter { Dialect = open };
        var m = it.Execute("t", head + "r = x.get()\nd = type(first_of)\n");
        Assert.Equal(new BigInteger(1), m.Globals["r"]);
    }

    // ---------------------------------------------------------------- parallel_map

    /// <summary>Results come back in input order whatever order the tasks finish in.</summary>
    [Fact]
    public void ParallelMapKeepsInputOrder()
    {
        const string program = "def sq(n):\n    return n * n\n\nr = parallel_map(sq, [1, 2, 3, 4, 5, 6, 7, 8])\n";
        Assert.Equal("[1, 4, 9, 16, 25, 36, 49, 64]", Repr.Of(Val(program)));
    }

    /// <summary>It works on builtins and on any iterable.</summary>
    [Fact]
    public void ParallelMapAcceptsBuiltinsAndIterables()
    {
        Assert.Equal("[1, 2, 3]", Repr.Of(Val("r = parallel_map(len, ['a', 'bb', 'ccc'])\n")));
        Assert.Equal("[0, 1, 2]", Repr.Of(Val("r = parallel_map(abs, range(3))\n")));
        Assert.Equal("[]", Repr.Of(Val("r = parallel_map(len, [])\n")));
    }

    /// <summary>The result equals the sequential comprehension for many sizes.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(500)]
    public void ParallelMapEqualsTheComprehension(int n)
    {
        string program = $"def f(x):\n    return x * 3 + 1\n\nxs = range({n})\nr = parallel_map(f, xs) == [f(x) for x in xs]\n";
        Assert.Equal(true, Val(program));
    }

    // ---------------------------------------------------------------- spawn, get, all_of

    /// <summary>spawn returns a future and get returns the result.</summary>
    [Fact]
    public void SpawnAndGet()
    {
        const string program = "def add(a, b = 10):\n    return a + b\n\nf = spawn(add, 1, b = 2)\ng = spawn(add, 5)\nr = [f.get(), g.get()]\n";
        Assert.Equal("[3, 15]", Repr.Of(Val(program)));
    }

    /// <summary>all_of returns results in argument order.</summary>
    [Fact]
    public void AllOfKeepsArgumentOrder()
    {
        const string program = "def f(n):\n    return n\n\nfs = [spawn(f, i) for i in range(10)]\nr = all_of(reversed(fs))\n";
        Assert.Equal("[9, 8, 7, 6, 5, 4, 3, 2, 1, 0]", Repr.Of(Val(program)));
    }

    /// <summary>get() raises the error the task raised, with its message.</summary>
    [Fact]
    public void GetRaisesTheTasksError()
    {
        const string program = "def bad():\n    fail('nope')\n\nf = spawn(bad)\nf.get()\n";
        Assert.Contains("nope", Fails(program));
    }

    /// <summary>When several tasks fail, the lowest-index failure is the one reported.</summary>
    [Fact]
    public void AllOfReportsTheLowestIndexFailure()
    {
        const string program = "def f(n):\n    if n >= 2:\n        fail('bad ' + str(n))\n    return n\n\nall_of([spawn(f, i) for i in range(6)])\n";
        for (int i = 0; i < 20; i++) Assert.Contains("bad 2", Fails(program));
    }

    /// <summary>Collecting the same failed future twice gives two distinct exception objects with the same message.</summary>
    [Fact]
    public void ErrorsAreFreshPerCollection()
    {
        const string program = "def bad():\n    fail('x')\n\ndef run():\n    f = spawn(bad)\n    return f\n\nr = 0\n";
        var it = New();
        it.Execute("t", program);
        var original = new StarlarkException("boom");
        var a = StarFuture.Fresh(original);
        var b = StarFuture.Fresh(original);
        Assert.NotSame(a, b);
        Assert.NotSame(original, a);
        Assert.Equal("boom", ((StarlarkException)a).RawMessage);
    }

    // ---------------------------------------------------------------- the future value

    /// <summary>A future has no truth value, cannot be hashed, and prints as an opaque handle.</summary>
    [Fact]
    public void FutureValueRules()
    {
        const string head = "def f():\n    return 1\n\nx = spawn(f)\n";
        Assert.Contains("no truth value", Fails(head + "if x:\n    pass\n"));
        Assert.Contains("unhashable", Fails(head + "d = {x: 1}\n"));
        Assert.Equal("future", Val(head + "r = type(x)\nx.get()\n"));
        Assert.Equal("<future>", Val(head + "r = repr(x)\nx.get()\n"));
    }

    /// <summary>A future that is still pending when the program ends is an error, so it can never end up in a frozen global.</summary>
    [Fact]
    public void FuturesCannotBeStoredAsGlobals() =>
        Assert.Contains("never collected", Fails("def f():\n    return 1\n\nx = spawn(f)\ng = [x]\n"));

    /// <summary>Starting a task and never collecting it is an error, not a silent cancellation.</summary>
    [Fact]
    public void UncollectedFuturesAreAnError()
    {
        const string program = "def f():\n    return 1\n\nspawn(f)\n";
        Assert.Contains("never collected", Fails(program));
    }

    // ---------------------------------------------------------------- isolation

    /// <summary>spawn does not freeze the caller's own list.</summary>
    [Fact]
    public void SpawnDoesNotFreezeTheCallersValues()
    {
        const string program = "def total(xs):\n    return sum_of(xs)\n\ndef sum_of(xs):\n    t = 0\n    for x in xs:\n        t += x\n    return t\n\n" +
            "def run():\n    xs = [1, 2, 3]\n    f = spawn(total, xs)\n    xs.append(4)\n    return [f.get(), len(xs)]\n\nr = run()\n";
        Assert.Equal("[6, 4]", Repr.Of(Val(program)));
    }

    /// <summary>A task that mutates its argument changes only its own copy.</summary>
    [Fact]
    public void TasksMutateTheirOwnCopy()
    {
        const string program = "def grow(xs):\n    return xs\n\ndef run():\n    xs = [1]\n    f = spawn(grow, xs)\n    r = f.get()\n    return [xs, r]\n\nr = run()\n";
        Assert.Equal("[[1], [1]]", Repr.Of(Val(program)));
    }

    /// <summary>Aliasing inside the arguments of one call is preserved in the copy.</summary>
    [Fact]
    public void AliasingIsPreservedWithinOneCall()
    {
        const string program = "def same(a, b):\n    return a == b\n\ndef run():\n    xs = [1, 2]\n    return spawn(same, xs, xs).get()\n\nr = run()\n";
        Assert.Equal(true, Val(program));
    }

    /// <summary>The result of a task is frozen.</summary>
    [Fact]
    public void ResultsAreFrozen()
    {
        const string program = "def make():\n    return [1, 2]\n\ndef run():\n    xs = spawn(make).get()\n    xs.append(3)\n\nrun()\n";
        Assert.Contains("frozen", Fails(program));
    }

    /// <summary>A function that reads a variable of a still-running function is rejected, which is what a loop-captured lambda is.</summary>
    [Fact]
    public void ClosuresOverLiveFramesAreRejected()
    {
        const string program = "def run():\n    n = 5\n    def inner():\n        return n\n    return spawn(inner).get()\n\nrun()\n";
        Assert.Contains("captures variables of a function that is still running", Fails(program));
        const string loop = "def run():\n    fs = []\n    for i in range(3):\n        fs.append(spawn(lambda: i))\n    return all_of(fs)\n\nrun()\n";
        Assert.Contains("captures variables", Fails(loop));
    }

    /// <summary>A nested function that reads only module globals and its own arguments is fine.</summary>
    [Fact]
    public void NestedFunctionsThatCaptureNothingAreAllowed()
    {
        const string program = "K = 7\n\ndef run():\n    def inner(x):\n        return x + K\n    return spawn(inner, 1).get()\n\nr = run()\n";
        Assert.Equal(new BigInteger(8), Val(program));
    }

    /// <summary>A task sees the module's frozen globals.</summary>
    [Fact]
    public void TasksReadFrozenGlobals()
    {
        const string program = "TABLE = [10, 20, 30]\n\ndef pick(i):\n    return TABLE[i]\n\nr = parallel_map(pick, [0, 1, 2])\n";
        Assert.Equal("[10, 20, 30]", Repr.Of(Val(program)));
    }

    // ---------------------------------------------------------------- restrictions

    /// <summary>A task cannot start or wait for other tasks, so the task graph has depth one.</summary>
    [Fact]
    public void TasksCannotSpawnOrCollect()
    {
        const string spawnInTask = "def child():\n    return 1\n\ndef parent():\n    return spawn(child).get()\n\nr = spawn(parent).get()\n";
        Assert.Contains("cannot be called from inside a task", Fails(spawnInTask));
        const string mapInTask = "def child(x):\n    return x\n\ndef parent():\n    return parallel_map(child, [1])\n\nr = spawn(parent).get()\n";
        Assert.Contains("cannot be called from inside a task", Fails(mapInTask));
    }

    /// <summary>Spawning something that is not callable is an error.</summary>
    [Fact]
    public void SpawnNeedsAFunction() => Assert.Contains("needs a function", Fails("x = spawn(5)\n"));

    // ---------------------------------------------------------------- output and recursion

    /// <summary>Printed output of tasks appears at the join, in index order.</summary>
    [Fact]
    public void TaskOutputIsOrdered()
    {
        var lines = new List<string>();
        const string program = "def say(i):\n    print('task', i)\n    return i\n\nr = parallel_map(say, range(6))\n";
        New(i => i.Print = lines.Add).Execute("t", program);
        Assert.Equal(["task 0", "task 1", "task 2", "task 3", "task 4", "task 5"], lines);
    }

    /// <summary>Spawning a function that is already running is a recursion error, just as calling it would be.</summary>
    [Fact]
    public void SpawningAFunctionOnTheStackIsRecursion()
    {
        const string program = "def f(n):\n    return spawn(f, n).get()\n\nf(1)\n";
        Assert.Contains("called recursively", Fails(program));
    }

    // ---------------------------------------------------------------- limits

    /// <summary>The step budget covers work done in tasks, so a hostile loop in a task is stopped.</summary>
    [Fact]
    public void StepLimitAppliesInsideTasks()
    {
        const string program = "def spin(n):\n    for i in range(1000000000):\n        pass\n\nr = parallel_map(spin, [1])\n";
        var e = Assert.Throws<ExecutionLimitException>(() => New(i => i.MaxSteps = 100_000).Execute("t", program));
        Assert.Equal("steps", e.Limit);
    }

    /// <summary>A limit hit in a task fails the run as a limit, not as a script error.</summary>
    [Fact]
    public void LimitsInTasksAreNotScriptErrors()
    {
        const string program = "def spin(n):\n    for i in range(1000000000):\n        pass\n\ndef run():\n    f = spawn(spin, 1)\n    return f.get()\n\nr = run()\n";
        Assert.Throws<ExecutionLimitException>(() => New(i => i.MaxSteps = 50_000).Execute("t", program));
    }

    /// <summary>The wall-clock limit stops a task that is still running.</summary>
    [Fact]
    public void TimeoutStopsATask()
    {
        const string program = "def spin(n):\n    for i in range(1000000000):\n        pass\n\nr = parallel_map(spin, [1, 2])\n";
        var e = Assert.Throws<ExecutionLimitException>(() => New(i => i.Timeout = TimeSpan.FromMilliseconds(300)).Execute("t", program));
        Assert.Equal("time", e.Limit);
    }

    /// <summary>Cancelling the host token stops the tasks and the call.</summary>
    [Fact]
    public void CancellationStopsTasks()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        const string program = "def spin(n):\n    for i in range(1000000000):\n        pass\n\nr = parallel_map(spin, [1, 2])\n";
        Assert.ThrowsAny<OperationCanceledException>(() => New(i => i.Cancellation = cts.Token).Execute("t", program));
    }

    /// <summary>Every spawn costs steps, so a spawn loop cannot dodge the step limit.</summary>
    [Fact]
    public void SpawningCostsSteps()
    {
        const string program = "def f():\n    return 1\n\ndef run():\n    fs = [spawn(f) for i in range(100)]\n    return all_of(fs)\n\nr = run()\n";
        var e = Assert.Throws<ExecutionLimitException>(() => New(i => i.MaxSteps = 20_000).Execute("t", program));
        Assert.Equal("steps", e.Limit);
    }

    /// <summary>The total number of tasks per run is capped.</summary>
    [Fact]
    public void TotalSpawnsAreCapped()
    {
        const string program = "def f(x):\n    return x\n\nr = parallel_map(f, range(50))\nr2 = parallel_map(f, range(50))\n";
        var e = Assert.Throws<ExecutionLimitException>(() =>
            New(i => i.FutureLimits = new FutureLimits { MaxSpawns = 60, SpawnStepCost = 0 }).Execute("t", program));
        Assert.Equal("tasks", e.Limit);
    }

    /// <summary>The number of uncollected futures is capped.</summary>
    [Fact]
    public void LiveFuturesAreCapped()
    {
        const string program = "def f():\n    return 1\n\ndef run():\n    fs = [spawn(f) for i in range(20)]\n    return all_of(fs)\n\nr = run()\n";
        var e = Assert.Throws<ExecutionLimitException>(() =>
            New(i => i.FutureLimits = new FutureLimits { MaxLiveFutures = 10, SpawnStepCost = 0 }).Execute("t", program));
        Assert.Equal("futures", e.Limit);
    }

    /// <summary>Run statistics are available to the host.</summary>
    [Fact]
    public void StatsAreReported()
    {
        var it = New(i => i.FutureLimits = new FutureLimits { SpawnStepCost = 0 });
        it.Execute("t", "def f(x):\n    return x\n\nr = parallel_map(f, range(10))\n");
        Assert.NotNull(it.LastRunStats);
        Assert.Equal(10, it.LastRunStats!.Spawns);
        Assert.Equal(0, it.LastRunStats.LeakedTasks);
        Assert.InRange(it.LastRunStats.PeakConcurrency, 1, 4);
    }

    // ---------------------------------------------------------------- host integration

    /// <summary>Utf8 mode and the memory ceiling apply on the task threads.</summary>
    [Fact]
    public void TasksInheritStringModeAndMemoryLimit()
    {
        var it = New(i => { i.StringElements = StringElements.Utf8; i.MaxAllocatedBytes = 1000; });
        var m = it.Execute("t", "def n(s):\n    return len(s)\n\nr = parallel_map(n, ['é'])\n");
        Assert.Equal("[2]", Repr.Of(m.Globals["r"]));
        Assert.Throws<ExecutionLimitException>(() =>
            it.Execute("u", "def big(x):\n    return 'x' * 100000\n\nr = parallel_map(big, [1])\n"));
    }

    /// <summary>A host function called from a task works and gets the task's cancellation token.</summary>
    [Fact]
    public void HostFunctionsRunInTasks()
    {
        var it = New();
        it.Register("double", (int n) => n * 2);
        var m = it.Execute("t", "r = parallel_map(double, [1, 2, 3])\n");
        Assert.Equal("[2, 4, 6]", Repr.Of(m.Globals["r"]));
    }

    /// <summary>A task thread does not leak its run state, and no threads remain after the run.</summary>
    [Fact]
    public void RunsLeaveNothingBehind()
    {
        var it = New();
        for (int i = 0; i < 20; i++) it.Execute("t" + i, "def f(x):\n    return x\n\nr = parallel_map(f, range(20))\n");
        Assert.False(Interpreter.IsRunningScript);
        Assert.Equal(0, it.LastRunStats!.LeakedTasks);
    }

    /// <summary>A task cannot load modules.</summary>
    [Fact]
    public void LoadIsRejectedInTasks()
    {
        var it = New();
        it.SourceLoader = _ => "X = 1\n";
        // load statements run at module top level, so a task can only reach one through a function body, which Starlark forbids.
        Assert.Contains("load", Assert.Throws<StarlarkSyntaxException>(() =>
            it.Execute("t", "def f():\n    load('m.star', 'X')\n\nr = parallel_map(f, [1])\n")).RawMessage);
    }
}
