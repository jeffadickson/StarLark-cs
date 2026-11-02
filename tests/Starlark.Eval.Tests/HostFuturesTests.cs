using System.Diagnostics;
using System.Numerics;
using Starlark.Eval;
using Starlark.Eval.Futures;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>
/// Host functions that return tasks: registered <c>asFuture</c> they hand the script a future so several host
/// operations overlap; otherwise they wait, as before. Covers the in-flight cap, step cost, statistics and failures.
/// </summary>
public class HostFuturesTests
{
    private static Dialect Open => Dialect.Standard with { EnableTopLevelStmt = true, EnableFutures = true };

    private static Interpreter New(Action<Interpreter>? configure = null)
    {
        var it = new Interpreter { Dialect = Open };
        configure?.Invoke(it);
        return it;
    }

    private static T WithTimeout<T>(Func<T> work, int seconds = 60)
    {
        var task = Task.Run(work);
        if (!Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds))).GetAwaiter().GetResult().Equals(task))
        {
            throw new TimeoutException("test hung");
        }
        return task.GetAwaiter().GetResult();
    }

    // ---------------------------------------------------------------- overlap

    /// <summary>Several operations started together overlap, so the total time is far less than running them one by one.</summary>
    [Fact]
    public void AsFutureOperationsOverlap()
    {
        var it = New();
        it.Register("slow", async (int n) => { await Task.Delay(300); return n * 2; }, asFuture: true);
        const string program = "def run():\n    fs = [slow(i) for i in range(6)]\n    return all_of(fs)\n\nr = run()\n";
        var sw = Stopwatch.StartNew();
        var m = it.Execute("t", program);
        Assert.Equal("[0, 2, 4, 6, 8, 10]", Repr.Of(m.Globals["r"]));
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(1200), $"took {sw.Elapsed.TotalMilliseconds:F0} ms; six 300 ms calls did not overlap");
        Assert.True(it.LastRunStats!.PeakHostOperations > 1);
        Assert.Equal(6, it.LastRunStats.HostCalls);
    }

    /// <summary>Without asFuture the same function waits, one at a time, exactly as before.</summary>
    [Fact]
    public void WithoutAsFutureTheCallWaits()
    {
        var it = New();
        it.Register("slow", async (int n) => { await Task.Delay(50); return n * 2; });
        var m = it.Execute("t", "r = [slow(i) for i in range(3)]\n");
        Assert.Equal("[0, 2, 4]", Repr.Of(m.Globals["r"]));
    }

    /// <summary>The result is a future: get() collects it, and it has the future type.</summary>
    [Fact]
    public void TheCallReturnsAFuture()
    {
        var it = New();
        it.Register("fetch", async (string key) => { await Task.Delay(5); return key.ToUpperInvariant(); }, asFuture: true);
        var m = it.Execute("t", "f = fetch('abc')\nt = type(f)\nr = f.get()\n");
        Assert.Equal("future", m.Globals["t"]);
        Assert.Equal("ABC", m.Globals["r"]);
    }

    /// <summary>Called from inside a task, an asFuture function just waits for its value.</summary>
    [Fact]
    public void InsideATaskTheCallWaits()
    {
        var it = New();
        it.Register("fetch", async (int n) => { await Task.Delay(5); return n + 1; }, asFuture: true);
        var m = it.Execute("t", "r = parallel_map(lambda x: fetch(x), [1, 2, 3])\n");
        Assert.Equal("[2, 3, 4]", Repr.Of(m.Globals["r"]));
    }

    /// <summary>Without the futures dialect the flag changes nothing.</summary>
    [Fact]
    public void WithoutTheDialectItStillWaits()
    {
        var it = new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true } };
        it.Register("fetch", async (int n) => { await Task.Delay(5); return n + 1; }, asFuture: true);
        Assert.Equal(new BigInteger(3), it.Execute("t", "r = fetch(2)\n").Globals["r"]);
    }

    // ---------------------------------------------------------------- caps and cost

    /// <summary>Starting more operations than the cap fails with a limit, and the ones already running are not affected.</summary>
    [Fact]
    public void InFlightOperationsAreCapped()
    {
        var gate = new TaskCompletionSource();
        var it = New(i => i.FutureLimits = new FutureLimits { MaxHostOperations = 3, HostCallStepCost = 0, SpawnStepCost = 0 });
        it.Register("wait", async () => { await gate.Task; return 1; }, asFuture: true);
        var e = Assert.Throws<ExecutionLimitException>(() => WithTimeout(() =>
            it.Execute("t", "def run():\n    fs = [wait() for i in range(5)]\n    return all_of(fs)\n\nr = run()\n")));
        gate.SetResult();
        Assert.Equal("host-operations", e.Limit);
    }

    /// <summary>A finished operation frees its slot, so a script can run many operations one after another.</summary>
    [Fact]
    public void SlotsAreReleasedWhenOperationsFinish()
    {
        var it = New(i => i.FutureLimits = new FutureLimits { MaxHostOperations = 2, HostCallStepCost = 0, SpawnStepCost = 0 });
        it.Register("quick", async (int n) => { await Task.Yield(); return n; }, asFuture: true);
        var m = it.Execute("t", "def run():\n    t = 0\n    for i in range(50):\n        t += quick(i).get()\n    return t\n\nr = run()\n");
        Assert.Equal(new BigInteger(1225), m.Globals["r"]);
    }

    /// <summary>Each task-returning host call costs steps, so a loop of them cannot dodge MaxSteps.</summary>
    [Fact]
    public void HostCallsCostSteps()
    {
        var it = New(i =>
        {
            i.MaxSteps = 5_000;
            i.FutureLimits = new FutureLimits { HostCallStepCost = 1_000, SpawnStepCost = 0 };
        });
        it.Register("quick", async (int n) => { await Task.Yield(); return n; }, asFuture: true);
        var e = Assert.Throws<ExecutionLimitException>(() =>
            it.Execute("t", "def run():\n    for i in range(100):\n        quick(i).get()\n\nrun()\n"));
        Assert.Equal("steps", e.Limit);
    }

    /// <summary>The cost applies to waiting calls too, because they also hold the script up on host work.</summary>
    [Fact]
    public void WaitingHostCallsCostStepsToo()
    {
        var it = New(i => { i.MaxSteps = 5_000; i.FutureLimits = new FutureLimits { HostCallStepCost = 1_000, SpawnStepCost = 0 }; });
        it.Register("quick", async (int n) => { await Task.Yield(); return n; });
        Assert.Throws<ExecutionLimitException>(() =>
            it.Execute("t", "def run():\n    for i in range(100):\n        quick(i)\n\nrun()\n"));
    }

    // ---------------------------------------------------------------- failures and lifetime

    /// <summary>An exception inside the host task surfaces from get() as a script error naming the function.</summary>
    [Fact]
    public void HostFailureSurfacesFromGet()
    {
        var it = New();
        it.Register("boom", async () => { await Task.Delay(1); throw new InvalidOperationException("disk full"); }, asFuture: true);
        var message = Assert.Throws<StarlarkException>(() => it.Execute("t", "def run():\n    return boom().get()\n\nrun()\n")).RawMessage;
        Assert.Contains("boom", message);
        Assert.Contains("disk full", message);
    }

    /// <summary>A host operation nobody collects is an error, like any other uncollected future.</summary>
    [Fact]
    public void UncollectedHostFutureIsAnError()
    {
        var it = New();
        it.Register("fetch", async () => { await Task.Delay(1); return 1; }, asFuture: true);
        Assert.Contains("never collected", Assert.Throws<StarlarkException>(() => it.Execute("t", "fetch()\n")).RawMessage);
    }

    /// <summary>The slot is given back even if the script abandons the future, so the next run is not starved.</summary>
    [Fact]
    public void AbandonedOperationsDoNotLeakSlots()
    {
        var it = New(i => i.FutureLimits = new FutureLimits { MaxHostOperations = 2, HostCallStepCost = 0, SpawnStepCost = 0 });
        it.Register("quick", async () => { await Task.Yield(); return 1; }, asFuture: true);
        for (int round = 0; round < 5; round++)
        {
            Assert.Throws<StarlarkException>(() => it.Execute("t" + round, "quick()\nquick()\n"));
        }
        Assert.Equal(new BigInteger(1), it.Execute("ok", "r = quick().get()\n").Globals["r"]);
    }

    /// <summary>Ending the run cancels the token handed to a host operation that is still running.</summary>
    [Fact]
    public void EndingTheRunCancelsPendingHostOperations()
    {
        var cancelled = new TaskCompletionSource<bool>();
        var started = new ManualResetEventSlim(false);
        var it = New(i => { i.Timeout = TimeSpan.FromMilliseconds(250); i.FutureLimits = new FutureLimits { HostCallStepCost = 0, SpawnStepCost = 0 }; });
        it.Register("hang", async (CancellationToken ct) =>
        {
            started.Set();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(true); throw; }
            return 0;
        }, asFuture: true);
        Assert.ThrowsAny<Exception>(() => WithTimeout(() => it.Execute("t", "def run():\n    return hang().get()\n\nrun()\n")));
        Assert.True(started.Wait(2000));
        Assert.True(cancelled.Task.Wait(5000), "the host operation was never cancelled");
    }

    /// <summary>The checker types an asFuture function as returning a future, and a plain one as returning its value.</summary>
    [Fact]
    public void CheckerKnowsTheReturnType()
    {
        var it = new Interpreter
        {
            Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableTypes = DialectTypes.Enable, EnableFutures = true },
        };
        it.Register("later", async (int n) => { await Task.Yield(); return n; }, asFuture: true);
        it.Register("now", async (int n) => { await Task.Yield(); return n; });
        var result = it.Typecheck("t", "def f():\n    a = later(1)\n    b = now(1)\n");
        Assert.Equal("future", result.Types["a"].ToString());
        Assert.Equal("int", result.Types["b"].ToString());
    }

    /// <summary>The run statistics report host calls and the steps the run used.</summary>
    [Fact]
    public void StatisticsIncludeHostCallsAndSteps()
    {
        var it = New(i => { i.MaxSteps = 1_000_000; i.FutureLimits = new FutureLimits { HostCallStepCost = 10, SpawnStepCost = 0 }; });
        it.Register("quick", async (int n) => { await Task.Yield(); return n; }, asFuture: true);
        it.Execute("t", "def run():\n    return all_of([quick(i) for i in range(5)])\n\nr = run()\n");
        Assert.Equal(5, it.LastRunStats!.HostCalls);
        Assert.True(it.LastRunStats.Steps > 50);
    }
}
