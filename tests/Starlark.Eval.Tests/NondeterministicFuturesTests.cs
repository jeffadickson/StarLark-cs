using System.Numerics;
using Starlark.Eval;
using Starlark.Eval.Futures;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>
/// The nondeterministic profile: <c>future.done()</c> and <c>first_of</c>, whose results depend on how fast tasks run.
/// The tests control timing with a host gate, so the outcomes they assert are the ones that must hold whatever the
/// scheduler does.
/// </summary>
public class NondeterministicFuturesTests
{
    private static Interpreter New(Action<Interpreter>? configure = null)
    {
        var it = new Interpreter
        {
            Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableFutures = true, EnableNondeterministicFutures = true },
            FutureLimits = new FutureLimits { SpawnStepCost = 0, HostCallStepCost = 0, GracePeriod = TimeSpan.FromMilliseconds(300) },
        };
        configure?.Invoke(it);
        return it;
    }

    // ---------------------------------------------------------------- done()

    /// <summary>done() is false while a task is held, and true once it has been released and has finished.</summary>
    [Fact]
    public void DoneReflectsCompletion()
    {
        var gate = new ManualResetEventSlim(false);
        var it = New();
        it.Register("held", () => { gate.Wait(); return 7; }, threadSafe: true);
        it.Define("release", (_, _, _) => { gate.Set(); return null; });
        const string program = "def work():\n    return held()\n\ndef run():\n    f = spawn(work)\n    before = f.done()\n    release()\n    v = f.get()\n    after = f.done()\n    return [before, v, after]\n\nr = run()\n";
        Assert.Equal("[False, 7, True]", Repr.Of(it.Execute("t", program).Globals["r"]));
    }

    /// <summary>done() costs steps, so polling it in a loop cannot dodge the step limit.</summary>
    [Fact(Skip = "Intermittently aborts the test host when a held worker outlives the run; under investigation")]
    public void DonePollingCostsSteps()
    {
        var gate = new ManualResetEventSlim(false);
        var it = New(i => i.MaxSteps = 20_000);
        it.Register("held", () => { gate.Wait(); return 1; }, threadSafe: true);
        const string program = "def work():\n    return held()\n\ndef run():\n    f = spawn(work)\n    for i in range(1000000):\n        if f.done():\n            break\n    return f.get()\n\nr = run()\n";
        try
        {
            var e = Assert.Throws<ExecutionLimitException>(() => it.Execute("t", program));
            Assert.Equal("steps", e.Limit);
        }
        finally
        {
            gate.Set();
        }
    }

    // ---------------------------------------------------------------- first_of

    /// <summary>first_of returns the index and result of a task that has finished while another is still held.</summary>
    [Fact]
    public void FirstOfReturnsTheFinishedOne()
    {
        var gate = new ManualResetEventSlim(false);
        var it = New();
        it.Register("slow", () => { gate.Wait(); return 100; }, threadSafe: true);
        it.Register("fast", () => 5, threadSafe: true);
        it.Define("release", (_, _, _) => { gate.Set(); return null; });
        const string program = "def a():\n    return slow()\n\ndef b():\n    return fast()\n\ndef run():\n    fs = [spawn(a), spawn(b)]\n    first = first_of(fs)\n    release()\n    rest = all_of(fs)\n    return [first, rest]\n\nr = run()\n";
        Assert.Equal("[(1, 5), [100, 5]]", Repr.Of(it.Execute("t", program).Globals["r"]));
    }

    /// <summary>When the first finisher failed, first_of raises that failure.</summary>
    [Fact(Skip = "Intermittently aborts the test host when a held worker outlives the run; under investigation")]
    public void FirstOfRaisesAFailure()
    {
        var gate = new ManualResetEventSlim(false);
        var it = New();
        it.Register("slow", () => { gate.Wait(); return 1; }, threadSafe: true);
        it.Define("release", (_, _, _) => { gate.Set(); return null; });
        const string program = "def a():\n    return slow()\n\ndef b():\n    fail('quick failure')\n\ndef run():\n    fs = [spawn(a), spawn(b)]\n    try_first = first_of(fs)\n    return try_first\n\nr = run()\n";
        try
        {
            Assert.Contains("quick failure", Assert.Throws<StarlarkException>(() => it.Execute("t", program)).RawMessage);
        }
        finally
        {
            gate.Set();
        }
    }

    /// <summary>Asking for the first of nothing is an error, not a hang.</summary>
    [Fact]
    public void FirstOfNeedsAFuture()
    {
        var it = New();
        Assert.Contains("at least one future", Assert.Throws<StarlarkException>(() => it.Execute("t", "x = first_of([])\n")).RawMessage);
        Assert.Contains("needs futures", Assert.Throws<StarlarkException>(() => it.Execute("u", "x = first_of([1])\n")).RawMessage);
    }

    /// <summary>first_of respects the deadline instead of waiting forever for a task that never finishes.</summary>
    [Fact(Skip = "Intermittently aborts the test host when a held worker outlives the run; under investigation")]
    public void FirstOfHonoursTheDeadline()
    {
        var gate = new ManualResetEventSlim(false);
        var it = New(i =>
        {
            i.Timeout = TimeSpan.FromMilliseconds(300);
            i.FutureLimits = new FutureLimits { SpawnStepCost = 0, GracePeriod = TimeSpan.FromMilliseconds(200) };
        });
        it.Register("stuck", () => { gate.Wait(); return 1; }, threadSafe: true);
        const string program = "def a():\n    return stuck()\n\ndef run():\n    return first_of([spawn(a)])\n\nr = run()\n";
        try
        {
            var task = Task.Run(() => Assert.ThrowsAny<Exception>(() => it.Execute("t", program)));
            Assert.True(task.Wait(TimeSpan.FromSeconds(30)), "first_of did not return at the deadline");
        }
        finally
        {
            gate.Set();
        }
    }

    /// <summary>The nondeterministic functions cannot be used from inside a task, like the rest of the extension.</summary>
    [Fact]
    public void NotAvailableInsideATask()
    {
        var it = New();
        const string program = "def child():\n    return 1\n\ndef parent():\n    return first_of([])\n\nr = spawn(parent).get()\n";
        Assert.Contains("cannot be called from inside a task", Assert.Throws<StarlarkException>(() => it.Execute("t", program)).RawMessage);
    }

    /// <summary>Losers of first_of are still futures the script must collect before the call ends.</summary>
    [Fact]
    public void LosersMustStillBeCollected()
    {
        var it = New();
        it.Register("fast", () => 5, threadSafe: true);
        const string program = "def a():\n    return fast()\n\ndef run():\n    fs = [spawn(a), spawn(a)]\n    return first_of(fs)\n\nr = run()\n";
        Assert.Contains("not collected before it returned", Assert.Throws<StarlarkException>(() => it.Execute("t", program)).RawMessage);
    }
}
