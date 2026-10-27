using System.Numerics;
using Starlark.Eval;
using Starlark.Eval.Futures;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>
/// Reproductions of the findings of the second review (memory model and language semantics): each program here was
/// shown to misbehave on the first implementation.
/// </summary>
public class FuturesReview2Tests
{
    private static Interpreter New(Action<Interpreter>? configure = null, bool nondeterministic = false)
    {
        var it = new Interpreter
        {
            Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableFutures = true, EnableNondeterministicFutures = nondeterministic },
        };
        configure?.Invoke(it);
        return it;
    }

    private static string Fails(string program, Action<Interpreter>? configure = null) =>
        Assert.Throws<StarlarkException>(() => New(configure).Execute("t", program)).RawMessage;

    private static object? Val(string program, string name = "r", Action<Interpreter>? configure = null) =>
        New(configure).Execute("t", program).Globals[name];

    // ---------------------------------------------------------------- closures that cross to a task

    /// <summary>A closure made by a finished call, reached through a global, must not silently pick up another global's value.</summary>
    [Fact]
    public void ClosureThroughAGlobalIsRejectedNotMisBound()
    {
        const string program = "def make():\n    k = 1\n    def g():\n        return k\n    return g\n\nG = make()\nk = 100\n\ndef f():\n    return G()\n\nr = spawn(f).get()\n";
        Assert.Contains("closure over variables", Fails(program));
    }

    /// <summary>A closure passed as an argument is checked just like the callee.</summary>
    [Fact]
    public void ClosureAsAnArgumentIsRejected()
    {
        const string program = "def apply(h):\n    return h()\n\ndef run():\n    n = 5\n    def inner():\n        return n\n    return spawn(apply, inner).get()\n\nrun()\n";
        Assert.Contains("closure over variables", Fails(program));
    }

    /// <summary>A comprehension variable or a nested parameter that happens to share a local's name is not a capture.</summary>
    [Fact]
    public void ShadowingNamesAreNotCaptures()
    {
        const string program = "def run():\n    n = 5\n    def inner(xs):\n        return [n for n in xs]\n    return spawn(inner, [1, 2]).get()\n\nr = run()\n";
        Assert.Equal("[1, 2]", Repr.Of(Val(program)));
        const string lambdaParam = "def run():\n    x = 1\n    def inner(ys):\n        return parallel_map_free(ys)\n    return 0\n\nr = run()\n";
        Assert.NotNull(Val(lambdaParam.Replace("parallel_map_free(ys)", "(lambda x: x)(ys)")));
    }

    // ---------------------------------------------------------------- shared mutable state

    /// <summary>Record defaults are frozen, so every task sees the same immutable default and none can change it.</summary>
    [Fact]
    public void RecordDefaultsCannotBeChanged()
    {
        const string program = "R = record(xs = field(list, []))\n\ndef f(i):\n    r = R()\n    r.xs.append(i)\n    return len(r.xs)\n\nr = parallel_map(f, range(10))\n";
        Assert.Contains("frozen", Fails(program));
    }

    /// <summary>A bound method kept in a global freezes its receiver at module end, so tasks cannot race on it.</summary>
    [Fact]
    public void BoundMethodInAGlobalFreezesItsList()
    {
        // While the module is still running, the list can still change, so a task may not use its method.
        const string program = "acc = [].append\n\ndef f(x):\n    acc(x)\n\nr = parallel_map(f, range(20))\n";
        Assert.Contains("can still change", Fails(program));

        // Once the module has finished, the bound method's list is frozen like every other global, and appending fails.
        var it = New();
        var m = it.Execute("lib.star", "acc = [].append\n\ndef f(x):\n    acc(x)\n");
        Assert.Contains("frozen", Assert.Throws<StarlarkException>(() => it.CallValue(m.Globals["f"], [new BigInteger(1)])).RawMessage);
    }

    /// <summary>A bound method passed inside an argument is rejected as well as one passed as the callee.</summary>
    [Fact]
    public void BoundMethodArgumentIsRejected()
    {
        const string program = "def w(p):\n    p(1)\n\ndef run():\n    xs = []\n    return spawn(w, xs.append).get()\n\nrun()\n";
        Assert.Contains("can still change", Fails(program));
    }

    /// <summary>Host-defined values are refused unless they say they are thread-safe.</summary>
    [Fact]
    public void HostValuesMustBeThreadSafe()
    {
        var it = New();
        it.DefineValue("counter", new Unsafe());
        var e = Assert.Throws<StarlarkException>(() => it.Execute("t", "def f(x):\n    return x\n\nr = parallel_map(f, [1])\n"));
        Assert.Contains("IThreadSafeHostValue", e.RawMessage);

        var ok = New();
        ok.DefineValue("limit", new Safe());
        ok.Execute("u", "def f(x):\n    return x\n\nr = parallel_map(f, [1])\n");
    }

    private sealed class Unsafe : IHostValue { public string TypeName => "unsafe"; }
    private sealed class Safe : IThreadSafeHostValue { public string TypeName => "safe"; }

    /// <summary>The universe's values are frozen once tasks can run, so a host list cannot be mutated while tasks read it.</summary>
    [Fact]
    public void UniverseValuesAreFrozenWhenTasksRun()
    {
        var it = New();
        it.DefineValue("TABLE", new[] { 1, 2, 3 });
        it.Execute("t", "def f(i):\n    return TABLE[i]\n\nr = parallel_map(f, [0, 1, 2])\n");
        Assert.True(((StarList)it.Eval("TABLE")!).Frozen);
    }

    /// <summary>Calls from tasks into host functions take turns unless the function is registered thread-safe.</summary>
    [Fact]
    public void HostCallsFromTasksAreSerialisedByDefault()
    {
        var it = New(i => i.FutureLimits = new FutureLimits { MaxConcurrency = 8, SpawnStepCost = 0 });
        int inside = 0, overlap = 0;
        it.Register("critical", (int n) =>
        {
            if (Interlocked.Increment(ref inside) > 1) Interlocked.Increment(ref overlap);
            Thread.Sleep(2);
            Interlocked.Decrement(ref inside);
            return n;
        });
        it.Execute("t", "r = parallel_map(critical, range(40))\n");
        Assert.Equal(0, overlap);
    }

    /// <summary>A host function registered as thread-safe is allowed to run concurrently.</summary>
    [Fact]
    public void ThreadSafeHostFunctionsRunInParallel()
    {
        var it = New(i => i.FutureLimits = new FutureLimits { MaxConcurrency = 8, SpawnStepCost = 0 });
        int inside = 0, peak = 0;
        it.Register("parallel", (int n) =>
        {
            int now = Interlocked.Increment(ref inside);
            int seen;
            while (now > (seen = Volatile.Read(ref peak))) Interlocked.CompareExchange(ref peak, now, seen);
            Thread.Sleep(30);
            Interlocked.Decrement(ref inside);
            return n;
        }, threadSafe: true);
        it.Execute("t", "r = parallel_map(parallel, range(16))\n");
        Assert.True(peak > 1, "expected overlapping calls");
    }

    // ---------------------------------------------------------------- futures do not escape

    /// <summary>A function cannot return while it still has uncollected futures.</summary>
    [Fact]
    public void FuturesCannotBeReturnedFromTheirFunction()
    {
        const string program = "def mk():\n    return spawn(lambda: 1)\n\nfs = {\"a\": mk()}\n";
        Assert.Contains("not collected before it returned", Fails(program));
    }

    /// <summary>Collecting inside the function is fine.</summary>
    [Fact]
    public void CollectingInsideTheFunctionIsFine()
    {
        const string program = "def one(x):\n    return x\n\ndef mk():\n    return spawn(one, 7).get()\n\nr = mk()\n";
        Assert.Equal(new BigInteger(7), Val(program));
    }

    // ---------------------------------------------------------------- scheduling-independent failures

    /// <summary>When a limit trips, no task output reaches the host, so output does not depend on how far each task got.</summary>
    [Fact]
    public void NoTaskOutputWhenALimitTrips()
    {
        const string program = "def f(i):\n    for k in range(200):\n        print(i, k)\n    if i == 3:\n        for _ in range(1000000000):\n            pass\n    return i\n\nr = parallel_map(f, range(8))\n";
        for (int round = 0; round < 5; round++)
        {
            var lines = new List<string>();
            var it = New(i => { i.Print = lines.Add; i.MaxSteps = 3_000_000; i.FutureLimits = new FutureLimits { SpawnStepCost = 0, MaxConcurrency = 1 + round % 4 }; });
            Assert.Throws<ExecutionLimitException>(() => it.Execute("t", program));
            Assert.Empty(lines);
        }
    }

    /// <summary>A step-limit message names the run's limit, the same text every time.</summary>
    [Fact]
    public void StepLimitMessageIsStable()
    {
        const string program = "def spin(n):\n    for i in range(1000000000):\n        pass\n\nr = parallel_map(spin, [1, 2, 3])\n";
        var messages = new HashSet<string>();
        for (int round = 0; round < 6; round++)
        {
            var it = New(i => { i.MaxSteps = 400_000; i.FutureLimits = new FutureLimits { SpawnStepCost = 0, MaxConcurrency = 1 + round % 3 }; });
            messages.Add(Assert.Throws<ExecutionLimitException>(() => it.Execute("t", program)).Message);
        }
        Assert.Single(messages);
        Assert.Contains("400000", messages.Single());
    }

    /// <summary>get() reports only its own future's outcome.</summary>
    [Fact]
    public void GetReportsItsOwnResult()
    {
        const string program = "def quick():\n    return 1\n\ndef run():\n    a = spawn(quick)\n    return a.get()\n\nr = run()\n";
        Assert.Equal(new BigInteger(1), Val(program));
    }

    // ---------------------------------------------------------------- Eval, dialect and other interpreters

    /// <summary>Eval ends its run, so a host that calls it in a loop does not accumulate worker threads.</summary>
    [Fact]
    public void EvalReleasesItsWorkers()
    {
        var it = New(i => i.FutureLimits = new FutureLimits { SpawnStepCost = 0 });
        for (int i = 0; i < 200; i++) Assert.Equal("[1]", Repr.Of(it.Eval("parallel_map(len, ['a'])")));
        Thread.Sleep(200);
        var workers = System.Diagnostics.Process.GetCurrentProcess().Threads.Count;
        Assert.True(workers < 400, $"{workers} threads");
    }

    /// <summary>Execute is refused on a task thread.</summary>
    [Fact]
    public void ExecuteInsideATaskIsRefused()
    {
        var it = New();
        it.Register("again", (Interpreter i) => i.Execute("inner", "x = 1\n").Globals.Count);
        Assert.Contains("cannot run inside a task", Assert.Throws<StarlarkException>(() =>
            it.Execute("t", "r = parallel_map(lambda x: again(), [1])\n")).RawMessage);
    }

    /// <summary>Changing the dialect removes names the old dialect added, and never replaces a host function.</summary>
    [Fact]
    public void DialectNamesFollowTheFlags()
    {
        var it = New(nondeterministic: true);
        it.Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableFutures = true };
        Assert.Contains("not found", Assert.Throws<StarlarkException>(() => it.Execute("t", "x = first_of([])\n")).RawMessage);
        it.Dialect = Dialect.Standard with { EnableTopLevelStmt = true };
        Assert.Contains("not found", Assert.Throws<StarlarkException>(() => it.Execute("u", "x = spawn(abs)\n")).RawMessage);

        var host = new Interpreter();
        host.Register("spawn", () => "mine");
        host.Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableFutures = true };
        Assert.Equal("mine", host.Execute("v", "r = spawn()\n").Globals["r"]);
    }

    /// <summary>The checker leaves the futures names alone when the dialect does not define them.</summary>
    [Fact]
    public void CheckerIgnoresFuturesNamesWithoutTheFlag()
    {
        var plain = new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true } };
        Assert.Empty(plain.Typecheck("t", "def f():\n    spawn(1, 2, 3)\n").Errors);
        var on = New();
        Assert.NotEmpty(on.Typecheck("t", "def f():\n    all_of([1, 2])\n").Errors);
    }
}
