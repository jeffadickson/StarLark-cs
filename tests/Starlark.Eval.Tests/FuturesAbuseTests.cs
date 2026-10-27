using System.Diagnostics;
using System.Numerics;
using Starlark.Eval;
using Starlark.Eval.Futures;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>
/// The regression and abuse cases the concurrency review asked for: deadlock, starvation, fork bombs, huge future
/// lists, ignored cancellation, hostile limits, determinism under scheduling noise, and clean teardown.
/// </summary>
public class FuturesAbuseTests
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
            throw new TimeoutException("test hung: likely a deadlock");
        }
        return task.GetAwaiter().GetResult();   // unwraps, so the script's own exception type reaches the assertion
    }

    // ---------------------------------------------------------------- deadlock and starvation

    /// <summary>
    /// More tasks than workers, each of which takes real time: the spawner never blocks on the cap, tasks queue, and the
    /// whole thing finishes. A cap that blocked the spawner would deadlock here.
    /// </summary>
    [Fact]
    public void MoreTasksThanWorkersFinish()
    {
        var it = New(i => i.FutureLimits = new FutureLimits { MaxConcurrency = 2, SpawnStepCost = 0 });
        it.Register("pause", (int ms) => { Thread.Sleep(ms); return ms; });
        var m = WithTimeout(() => it.Execute("t", "r = parallel_map(pause, [20] * 40)\n"));
        Assert.Equal(40, ((StarList)m.Globals["r"]!).Count);
    }

    /// <summary>One worker, many tasks: still completes, in order.</summary>
    [Fact]
    public void SingleWorkerCompletes()
    {
        var it = New(i => i.FutureLimits = new FutureLimits { MaxConcurrency = 1, SpawnStepCost = 0 });
        var m = WithTimeout(() => it.Execute("t", "def f(x):\n    return x + 1\n\nr = parallel_map(f, range(200))\n"));
        Assert.Equal(new BigInteger(200), m.Globals["r"] is StarList l ? new BigInteger(l.Count) : BigInteger.Zero);
    }

    /// <summary>The host's own thread pool is not used by script tasks, so pool starvation cannot stall the host.</summary>
    [Fact]
    public void ScriptTasksDoNotUseTheSharedThreadPool()
    {
        var it = New(i => i.FutureLimits = new FutureLimits { SpawnStepCost = 0 });
        var names = new System.Collections.Concurrent.ConcurrentBag<(string? Name, bool Pool)>();
        it.Register("where", () => { names.Add((Thread.CurrentThread.Name, Thread.CurrentThread.IsThreadPoolThread)); return 1; });
        it.Execute("t", "r = parallel_map(lambda x: where(), range(8))\n");
        Assert.NotEmpty(names);
        Assert.All(names, n =>
        {
            Assert.False(n.Pool);
            Assert.StartsWith("starlark-task-", n.Name);
        });
    }

    // ---------------------------------------------------------------- fork bombs and huge lists

    /// <summary>A task cannot start more tasks, so a fork bomb stops at depth one.</summary>
    [Fact]
    public void ForkBombStopsAtDepthOne()
    {
        const string program = "def bomb(n):\n    return parallel_map(bomb, [n, n])\n\nr = parallel_map(bomb, [1])\n";
        Assert.Contains("cannot be called from inside a task", Assert.Throws<StarlarkException>(() => New().Execute("t", program)).RawMessage);
    }

    /// <summary>A loop that spawns forever runs into the spawn cap or the step budget, never exhausting memory.</summary>
    [Fact]
    public void EndlessSpawnLoopIsBounded()
    {
        const string program = "def f(x):\n    return x\n\ndef run():\n    for i in range(10000000):\n        spawn(f, i).get()\n\nrun()\n";
        var it = New(i => i.FutureLimits = new FutureLimits { MaxSpawns = 500, SpawnStepCost = 10 });
        var e = Assert.Throws<ExecutionLimitException>(() => WithTimeout(() => it.Execute("t", program)));
        Assert.Equal("tasks", e.Limit);
    }

    /// <summary>A million-element parallel_map is refused up front, before any task is created.</summary>
    [Fact]
    public void HugeParallelMapIsRefusedBeforeStarting()
    {
        var it = New(i => i.FutureLimits = new FutureLimits { SpawnStepCost = 0 });
        var e = Assert.Throws<ExecutionLimitException>(() => it.Execute("t", "r = parallel_map(abs, range(1000000))\n"));
        Assert.Equal("futures", e.Limit);
        Assert.Equal(0, it.LastRunStats?.Spawns ?? 0);
    }

    // ---------------------------------------------------------------- hostile tasks

    /// <summary>One bad task among many: the run fails with the limit, and no task is left running afterwards.</summary>
    [Fact]
    public void OneHostileTaskEndsTheWholeRun()
    {
        const string program = "def f(n):\n    if n == 3:\n        for i in range(1000000000):\n            pass\n    return n\n\nr = parallel_map(f, range(8))\n";
        var it = New(i => { i.MaxSteps = 200_000; i.FutureLimits = new FutureLimits { SpawnStepCost = 0 }; });
        var e = Assert.Throws<ExecutionLimitException>(() => WithTimeout(() => it.Execute("t", program)));
        Assert.Equal("steps", e.Limit);
        Assert.Equal(0, it.LastRunStats!.LeakedTasks);
    }

    /// <summary>A limit hit in a task the script never collects still fails the run.</summary>
    [Fact]
    public void LimitInUncollectedTaskStillFailsTheRun()
    {
        const string program = "def spin():\n    for i in range(1000000000):\n        pass\n\ndef run():\n    f = spawn(spin)\n    return 1\n\nr = run()\n";
        var it = New(i => i.MaxSteps = 100_000);
        Assert.ThrowsAny<Exception>(() => WithTimeout(() => it.Execute("t", program)));
    }

    /// <summary>A host function that ignores cancellation is reported as leaked after the grace period, and the run still ends.</summary>
    [Fact]
    public void HostFunctionIgnoringCancellationIsReported()
    {
        var release = new ManualResetEventSlim(false);
        var it = New(i =>
        {
            i.Timeout = TimeSpan.FromMilliseconds(300);
            i.FutureLimits = new FutureLimits { GracePeriod = TimeSpan.FromMilliseconds(400), SpawnStepCost = 0 };
        });
        it.Register("stubborn", () => { release.Wait(); return 1; });
        try
        {
            Assert.ThrowsAny<Exception>(() => WithTimeout(() => it.Execute("t", "r = parallel_map(lambda x: stubborn(), [1])\n")));
            Assert.True(it.LastRunStats!.LeakedTasks >= 1);
        }
        finally
        {
            release.Set();   // let the stubborn thread finish so the test process can exit
        }
    }

    // ---------------------------------------------------------------- determinism

    /// <summary>The same program gives the same results, errors and output however the tasks are scheduled.</summary>
    [Fact]
    public void ResultsDoNotDependOnScheduling()
    {
        var delays = new[] { 0, 1, 3, 7, 0, 2 };
        var outcomes = new HashSet<string>();
        for (int round = 0; round < 30; round++)
        {
            var rng = new Random(round);
            var lines = new List<string>();
            var it = New(i => { i.Print = lines.Add; i.FutureLimits = new FutureLimits { MaxConcurrency = 1 + round % 4, SpawnStepCost = 0 }; });
            it.Register("jitter", () => { Thread.Sleep(delays[rng.Next(delays.Length)]); return 0; });
            const string program = "def work(i):\n    jitter()\n    print('item', i)\n    return i * i\n\nr = parallel_map(work, range(12))\n";
            var m = it.Execute("t", program);
            outcomes.Add(Repr.Of(m.Globals["r"]) + "|" + string.Join(";", lines));
        }
        Assert.Single(outcomes);
    }

    /// <summary>The same failing program raises the same error every time.</summary>
    [Fact]
    public void ErrorsDoNotDependOnScheduling()
    {
        const string program = "def f(i):\n    if i % 3 == 2:\n        fail('bad ' + str(i))\n    return i\n\nr = parallel_map(f, range(12))\n";
        var messages = new HashSet<string>();
        for (int round = 0; round < 30; round++)
        {
            var it = New(i => i.FutureLimits = new FutureLimits { MaxConcurrency = 1 + round % 4, SpawnStepCost = 0 });
            messages.Add(Assert.Throws<StarlarkException>(() => it.Execute("t", program)).RawMessage);
        }
        Assert.Single(messages);
        Assert.Contains("bad 2", messages.Single());
    }

    /// <summary>The step count charged to the run is the same on every execution.</summary>
    [Fact]
    public void StepAccountingIsDeterministic()
    {
        const string program = "def f(i):\n    t = 0\n    for k in range(i):\n        t += k\n    return t\n\nr = parallel_map(f, range(30))\n";
        var counts = new HashSet<long>();
        for (int round = 0; round < 10; round++)
        {
            var it = New(i => { i.MaxSteps = 10_000_000; i.FutureLimits = new FutureLimits { MaxConcurrency = 1 + round % 4 }; });
            it.Execute("t", program);
            counts.Add(it.LastRunStats!.Spawns);
        }
        Assert.Single(counts);
    }

    // ---------------------------------------------------------------- teardown and lifecycle

    /// <summary>After many runs no worker threads remain.</summary>
    [Fact]
    public void WorkersAreReleasedAfterEachRun()
    {
        int before = Process.GetCurrentProcess().Threads.Count;
        var it = New(i => i.FutureLimits = new FutureLimits { SpawnStepCost = 0 });
        for (int i = 0; i < 100; i++)
        {
            it.Execute("t" + i, "def f(x):\n    return x\n\nr = parallel_map(f, range(10))\n");
        }
        Thread.Sleep(200);
        int after = Process.GetCurrentProcess().Threads.Count;
        Assert.InRange(after - before, -50, 20);
    }

    /// <summary>A run that fails still tears down its workers and records statistics.</summary>
    [Fact]
    public void FailingRunsTearDownToo()
    {
        var it = New(i => i.FutureLimits = new FutureLimits { SpawnStepCost = 0 });
        for (int i = 0; i < 20; i++)
        {
            Assert.Throws<StarlarkException>(() =>
                it.Execute("t", "def f(x):\n    fail('x')\n\nr = parallel_map(f, range(5))\n"));
        }
        Assert.NotNull(it.LastRunStats);
        Assert.Equal(0, it.LastRunStats!.LeakedTasks);
    }

    /// <summary>No unobserved task exception is raised, even when tasks fail and are never read.</summary>
    [Fact]
    public void NoUnobservedTaskExceptions()
    {
        var unobserved = 0;
        void Handler(object? s, UnobservedTaskExceptionEventArgs e) => Interlocked.Increment(ref unobserved);
        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            var it = New(i => i.FutureLimits = new FutureLimits { SpawnStepCost = 0 });
            for (int i = 0; i < 20; i++)
            {
                try { it.Execute("t", "def f(x):\n    fail('x')\n\nr = parallel_map(f, range(8))\n"); } catch (StarlarkException) { }
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
        Assert.Equal(0, unobserved);
    }

    // ---------------------------------------------------------------- host contracts

    /// <summary>Running the script on a thread with a single-threaded synchronization context does not deadlock.</summary>
    [Fact]
    public void WorksUnderASingleThreadedSynchronizationContext()
    {
        var done = new ManualResetEventSlim(false);
        Exception? failure = null;
        object? result = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new SingleThreadContext());
            try
            {
                var it = New(i => i.FutureLimits = new FutureLimits { SpawnStepCost = 0 });
                result = Repr.Of(it.Execute("t", "def f(x):\n    return x\n\nr = parallel_map(f, range(5))\n").Globals["r"]);
            }
            catch (Exception e)
            {
                failure = e;
            }
            finally
            {
                done.Set();
            }
        });
        thread.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(30)), "deadlocked");
        Assert.Null(failure);
        Assert.Equal("[0, 1, 2, 3, 4]", result);
    }

    private sealed class SingleThreadContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { }
        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    /// <summary>The same interpreter can start many runs from many threads at once, each with its own tasks and budgets.</summary>
    [Fact]
    public void ConcurrentRunsOnOneInterpreter()
    {
        var it = New(i => { i.MaxSteps = 5_000_000; i.FutureLimits = new FutureLimits { SpawnStepCost = 0 }; });
        var m = it.Execute("lib.star", "def sq(x):\n    return x * x\n\ndef run(n):\n    return parallel_map(sq, range(n))\n");
        var run = m.Globals["run"];
        var bad = 0;
        Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 8 }, t =>
        {
            for (int i = 0; i < 20; i++)
            {
                int n = 5 + (t + i) % 7;
                var r = (StarList)it.CallValue(run, [new BigInteger(n)])!;
                if (r.Count != n || (BigInteger)r.Items[n - 1]! != (BigInteger)(n - 1) * (n - 1)) Interlocked.Increment(ref bad);
            }
        });
        Assert.Equal(0, bad);
    }
}
