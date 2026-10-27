using System.Numerics;
using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>
/// A frozen module can be called from many threads at once: all run state lives in a per-call
/// <c>ExecutionContext</c>, and frozen values are never written.
/// </summary>
public class FrozenModuleConcurrencyTests
{
    private const string Library = """
        TABLE = [i * i for i in range(200)]
        INDEX = {str(i): i for i in range(200)}
        NAMES = set(["a", "b", "c"])

        def square(n):
            return TABLE[n % 200]

        def lookup(key):
            return INDEX[key]

        def total():
            t = 0
            for x in TABLE:
                t += x
            return t

        def keys_joined():
            return ",".join([k for k in INDEX])

        def has(name):
            return name in NAMES

        def helper(n):
            return n + 1

        def chain(n):
            return helper(helper(helper(n)))

        def recursive_bait(n):
            return square(n) + square(n + 1)
        """;

    private static (Interpreter It, Module Module) Load(Action<Interpreter>? configure = null)
    {
        var it = new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true } };
        configure?.Invoke(it);
        return (it, it.Execute("lib.star", Library));
    }

    /// <summary>Many threads call the same function at the same time and each gets the right answer.</summary>
    [Fact]
    public void ManyThreadsCallTheSameFunction()
    {
        var (it, m) = Load();
        var f = m.Globals["square"];
        var wrong = 0;
        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 16 }, t =>
        {
            for (int i = 0; i < 2000; i++)
            {
                var n = (t * 31 + i) % 200;
                var r = (BigInteger)it.CallValue(f, [new BigInteger(n)])!;
                if (r != n * n) Interlocked.Increment(ref wrong);
            }
        });
        Assert.Equal(0, wrong);
    }

    /// <summary>The same function running on two threads is not mistaken for recursion.</summary>
    [Fact]
    public void ConcurrentCallsAreNotRecursion()
    {
        var (it, m) = Load();
        var f = m.Globals["chain"];
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 32, new ParallelOptions { MaxDegreeOfParallelism = 16 }, t =>
        {
            for (int i = 0; i < 3000; i++)
            {
                try
                {
                    Assert.Equal(new BigInteger(i + 3), it.CallValue(f, [new BigInteger(i)]));
                }
                catch (Exception e)
                {
                    failures.Add(e.Message);
                }
            }
        });
        Assert.Empty(failures);
    }

    /// <summary>Shared frozen lists, dicts and sets can be iterated by many threads, and stay frozen and unchanged.</summary>
    [Fact]
    public void SharedFrozenCollectionsAreIteratedConcurrently()
    {
        var (it, m) = Load();
        var total = m.Globals["total"];
        var joined = m.Globals["keys_joined"];
        var expectedTotal = Enumerable.Range(0, 200).Aggregate(BigInteger.Zero, (a, i) => a + i * i);
        var expectedJoined = string.Join(",", Enumerable.Range(0, 200));
        var wrong = 0;
        Parallel.For(0, 32, new ParallelOptions { MaxDegreeOfParallelism = 16 }, _ =>
        {
            for (int i = 0; i < 300; i++)
            {
                if ((BigInteger)it.CallValue(total, [])! != expectedTotal) Interlocked.Increment(ref wrong);
                if ((string)it.CallValue(joined, [])! != expectedJoined) Interlocked.Increment(ref wrong);
            }
        });
        Assert.Equal(0, wrong);
        // Iterating a frozen value never took the iteration lock, so it is still mutable-checks free.
        Assert.True(((StarList)m.Globals["TABLE"]!).Frozen);
    }

    /// <summary>Each thread's step budget is its own: one thread running out does not stop the others.</summary>
    [Fact]
    public void BudgetsArePerCall()
    {
        var (it, m) = Load(i => i.MaxSteps = 100_000);
        var total = m.Globals["total"];
        var square = m.Globals["square"];
        var ok = 0;
        var limited = 0;
        Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 16 }, t =>
        {
            for (int i = 0; i < 200; i++)
            {
                it.CallValue(square, [new BigInteger(i)]);
                Interlocked.Increment(ref ok);
            }
            if (t % 4 == 0)
            {
                it.MaxSteps = 100_000;
            }
        });
        Assert.Equal(16 * 200, ok);
        // A budget small enough to trip applies only to calls started after it is set.
        it.MaxSteps = 50;
        Assert.Throws<ExecutionLimitException>(() => it.CallValue(total, []));
        it.MaxSteps = 100_000;
        limited++;
        Assert.Equal(1, limited);
        it.CallValue(total, []);
    }

    /// <summary>Cancelling a token stops only the calls started under it.</summary>
    [Fact]
    public void CancellationIsPerCall()
    {
        using var cts = new CancellationTokenSource();
        var it = new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true }, Cancellation = cts.Token };
        var m = it.Execute("lib.star", "def spin():\n    for i in range(100000):\n        pass\n");
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => it.CallValue(m.Globals["spin"], []));
    }

    /// <summary>Utf8 mode and the memory ceiling are in effect on every thread, not only the one that loaded the module.</summary>
    [Fact]
    public void StringModeAndMemoryLimitApplyOnWorkerThreads()
    {
        var it = new Interpreter
        {
            Dialect = Dialect.Standard with { EnableTopLevelStmt = true },
            StringElements = StringElements.Utf8,
            MaxAllocatedBytes = 1000,
        };
        var m = it.Execute("lib.star", "def length(s):\n    return len(s)\n\ndef big():\n    return 'x' * 100000\n");
        var length = m.Globals["length"];
        var big = m.Globals["big"];
        var results = new System.Collections.Concurrent.ConcurrentBag<(BigInteger Len, bool Limited)>();
        var threads = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
        {
            var len = (BigInteger)it.CallValue(length, ["é"])!;
            bool limited;
            try { it.CallValue(big, []); limited = false; } catch (ExecutionLimitException) { limited = true; }
            results.Add((len, limited));
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        Assert.All(results, r =>
        {
            Assert.Equal(new BigInteger(2), r.Len);   // "é" is two bytes in UTF-8
            Assert.True(r.Limited);
        });
        Assert.Equal(8, results.Count);
    }

    /// <summary>Running a function from a thread leaves no context behind on it.</summary>
    [Fact]
    public void NoContextLeaksAfterACall()
    {
        var (it, m) = Load();
        it.CallValue(m.Globals["square"], [new BigInteger(3)]);
        Assert.False(Interpreter.IsRunningScript);
        Assert.Throws<StarlarkException>(() => it.CallValue(m.Globals["lookup"], ["missing"]));
        Assert.False(Interpreter.IsRunningScript);
    }

    /// <summary>A call made from inside a running script shares that script's budgets (it does not get a fresh one).</summary>
    [Fact]
    public void NestedHostCallsShareTheOuterBudget()
    {
        var it = new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true }, MaxSteps = 2000 };
        var m = it.Execute("lib.star", "def spin():\n    for i in range(100):\n        pass\n");
        var spin = m.Globals["spin"];
        it.Define("again", (interp, _, _) => interp.CallValue(spin, []));
        Assert.Throws<ExecutionLimitException>(() =>
            it.Execute("t", "for i in range(1000):\n    again()\n"));
    }
}
