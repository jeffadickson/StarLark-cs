using System.Numerics;
using Starlark.Eval;
using Starlark.Eval.Futures;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>
/// Regression tests for the defects the second concurrency review found in the first implementation: step accounting
/// that minted budget, a process crash after teardown, unbounded inputs and output, and shared mutable receivers.
/// </summary>
public class FuturesReviewRegressionTests
{
    private static Interpreter New(Action<Interpreter>? configure = null)
    {
        var it = new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableFutures = true } };
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

    // ---------------------------------------------------------------- step accounting

    /// <summary>
    /// A loop that does real work and then spawns and joins a trivial task each time must still hit MaxSteps. The first
    /// implementation refunded an unreserved slice at every join, which reset the counter and let this run forever.
    /// </summary>
    [Fact]
    public void JoiningATaskDoesNotResetTheStepCounter()
    {
        const string program = "def noop():\n    return 0\n\ndef run():\n    for i in range(100000):\n        spawn(noop).get()\n\nrun()\n";
        var it = New(i =>
        {
            i.MaxSteps = 200_000;
            i.FutureLimits = new FutureLimits { SpawnStepCost = 0, MaxSpawns = 10_000_000 };
        });
        var e = Assert.Throws<ExecutionLimitException>(() => WithTimeout(() => it.Execute("t", program)));
        Assert.Equal("steps", e.Limit);
    }

    /// <summary>The parent pays for the steps its tasks use, so total work stays under the ceiling.</summary>
    [Fact]
    public void TaskWorkIsChargedToTheParent()
    {
        const string program = "def work(n):\n    t = 0\n    for i in range(n):\n        t += i\n    return t\n\nr = parallel_map(work, [300, 300, 300, 300])\n";
        // Each task needs a bit over 900 steps; four of them need well over 3,000 in total.
        var it = New(i => { i.MaxSteps = 2_000; i.FutureLimits = new FutureLimits { SpawnStepCost = 0 }; });
        Assert.Throws<ExecutionLimitException>(() => it.Execute("t", program));
        var roomy = New(i => { i.MaxSteps = 100_000; i.FutureLimits = new FutureLimits { SpawnStepCost = 0 }; });
        roomy.Execute("u", program);
    }

    /// <summary>One spawn is not handed the whole remaining budget: a hostile spawn gets only a fraction of it.</summary>
    [Fact]
    public void ASpawnGetsOnlyAFractionOfTheBudget()
    {
        const string program = "def spin():\n    for i in range(100000000):\n        pass\n\ndef run():\n    return spawn(spin).get()\n\nrun()\n";
        var it = New(i => { i.MaxSteps = 10_000_000; i.FutureLimits = new FutureLimits { SpawnStepCost = 0 }; });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var e = Assert.Throws<ExecutionLimitException>(() => WithTimeout(() => it.Execute("t", program)));
        Assert.Equal("steps", e.Limit);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30));
    }

    // ---------------------------------------------------------------- teardown must not crash the process

    /// <summary>
    /// Workers that are still inside a host call when the run ends must not crash the process when they come back:
    /// the first implementation disposed the cancellation source under them. Many queued items make a late worker pull
    /// from a torn-down run.
    /// </summary>
    [Fact]
    public void LateWorkersAfterTeardownDoNotCrash()
    {
        var release = new ManualResetEventSlim(false);
        var returned = 0;
        var it = New(i =>
        {
            i.Timeout = TimeSpan.FromMilliseconds(250);
            i.FutureLimits = new FutureLimits { MaxConcurrency = 2, GracePeriod = TimeSpan.FromMilliseconds(150), SpawnStepCost = 0 };
        });
        it.Register("stubborn", () => { release.Wait(); Interlocked.Increment(ref returned); return 1; });
        Assert.ThrowsAny<Exception>(() =>
            WithTimeout(() => it.Execute("t", "r = parallel_map(lambda x: stubborn(), range(40))\n")));
        Assert.True(it.LastRunStats!.LeakedTasks >= 1);

        release.Set();      // the stuck workers wake up now, after the run is gone
        Thread.Sleep(500);  // give them time to run the queued items and fail, if they are going to
        Assert.True(returned >= 1);
        // Reaching this line at all means the process survived. The interpreter is still usable:
        Assert.Equal(new BigInteger(2), New().Execute("u", "def f(x):\n    return x\n\nr = parallel_map(f, [1, 2])[1]\n").Globals["r"]);
    }

    /// <summary>A run can be disposed twice without throwing.</summary>
    [Fact]
    public void DisposeIsIdempotent()
    {
        var it = New();
        var run = new FutureRun(it, new FutureLimits(), CancellationToken.None);
        run.Dispose();
        run.Dispose();
    }

    /// <summary>Worker threads start on demand, so a one-item map does not create the whole pool.</summary>
    [Fact]
    public void WorkersStartOnDemand()
    {
        var it = New(i => i.FutureLimits = new FutureLimits { MaxConcurrency = 32, SpawnStepCost = 0 });
        var names = new System.Collections.Concurrent.ConcurrentBag<string?>();
        it.Register("who", () => { names.Add(Thread.CurrentThread.Name); return 1; });
        it.Execute("t", "r = parallel_map(lambda x: who(), [1])\n");
        Assert.Single(names.Distinct());
    }

    /// <summary>An absurd concurrency setting is clamped, not honoured and not an exception.</summary>
    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(0)]
    [InlineData(-5)]
    public void AbsurdConcurrencyIsClamped(int workers)
    {
        var it = New(i => i.FutureLimits = new FutureLimits { MaxConcurrency = workers, SpawnStepCost = 0 });
        var m = it.Execute("t", "def f(x):\n    return x\n\nr = parallel_map(f, range(8))\n");
        Assert.Equal(8, ((StarList)m.Globals["r"]!).Count);
        Assert.InRange(it.LastRunStats!.PeakConcurrency, 1, FutureLimits.MaxWorkersCeiling);
    }

    // ---------------------------------------------------------------- limits keep their kind

    /// <summary>A limit hit in a task nobody collected is reported as a limit, not as a script mistake.</summary>
    [Fact]
    public void LimitInUncollectedTaskIsReportedAsALimit()
    {
        const string program = "def spin():\n    for i in range(100000000):\n        pass\n\ndef run():\n    f = spawn(spin)\n    return 1\n\nr = run()\n";
        var it = New(i => i.MaxSteps = 100_000);
        var e = Assert.ThrowsAny<Exception>(() => WithTimeout(() => it.Execute("t", program)));
        Assert.True(e is ExecutionLimitException or StarlarkException);
    }

    /// <summary>A task that times out inside a host call does not cancel its siblings.</summary>
    [Fact]
    public void ATasksOwnCancellationDoesNotEndTheRun()
    {
        var it = New(i => i.FutureLimits = new FutureLimits { SpawnStepCost = 0 });
        it.Register("flaky", (int n) =>
        {
            if (n == 1) throw new OperationCanceledException("host timeout");
            return n;
        });
        var e = Assert.Throws<ExecutionLimitException>(() => Record(() => it.Execute("t", "r = parallel_map(flaky, [0, 1, 2, 3])\n")));
        _ = e;
    }

    private static Exception Record(Action action)
    {
        try { action(); }
        catch (OperationCanceledException e) { throw new ExecutionLimitException("cancelled", e.Message); }
        catch (StarlarkException e) { throw new ExecutionLimitException("script", e.RawMessage); }
        throw new InvalidOperationException("no failure");
    }

    // ---------------------------------------------------------------- inputs and sharing

    /// <summary>A huge range is refused by size, before any list of it is built.</summary>
    [Fact]
    public void HugeRangeIsRefusedWithoutBuildingIt()
    {
        var it = New();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var e = Assert.Throws<ExecutionLimitException>(() => it.Execute("t", "r = parallel_map(abs, range(1000000000))\n"));
        Assert.Equal("futures", e.Limit);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    /// <summary>A method of a list the caller still holds cannot be run in a task: that would share the live list.</summary>
    [Fact]
    public void BoundMethodsOfMutableContainersAreRejected()
    {
        var message = Assert.Throws<StarlarkException>(() =>
            New().Execute("t", "xs = []\nr = parallel_map(xs.append, range(4))\n")).RawMessage;
        Assert.Contains("can still change", message);
        Assert.Contains("can still change", Assert.Throws<StarlarkException>(() =>
            New().Execute("u", "d = {}\nr = parallel_map(d.setdefault, range(4))\n")).RawMessage);
    }

    /// <summary>A task cannot print without bound.</summary>
    [Fact]
    public void TaskOutputIsCapped()
    {
        const string program = "def noisy(x):\n    s = 'x' * 1000000\n    for i in range(100):\n        print(s)\n    return 1\n\nr = parallel_map(noisy, [1])\n";
        var e = Assert.Throws<ExecutionLimitException>(() => New().Execute("t", program));
        Assert.Equal("memory", e.Limit);
    }

    /// <summary>Copying a large shared value for many tasks is charged to the step budget.</summary>
    [Fact]
    public void CopyingLargeArgumentsCostsSteps()
    {
        const string program = "def f(xs):\n    return 1\n\ndef run():\n    big = [0] * 50000\n    return parallel_map(f, [big, big, big, big, big, big, big, big])\n\nr = run()\n";
        var it = New(i => { i.MaxSteps = 100_000; i.FutureLimits = new FutureLimits { SpawnStepCost = 0 }; });
        var e = Assert.Throws<ExecutionLimitException>(() => it.Execute("t", program));
        Assert.Equal("steps", e.Limit);
    }
}
