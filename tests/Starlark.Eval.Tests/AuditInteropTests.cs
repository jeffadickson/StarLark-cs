using System.Diagnostics;
using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>
/// Regressions from the crash and railway audit of the host interop layer: endless host sequences are stopped by the
/// run's limits, every host failure reaches the host as a documented type with the same shape in every mode, and
/// members that cannot be exposed are rejected when <see cref="Interpreter.Expose"/> is called.
/// </summary>
public class AuditInteropTests
{
    private static Dialect Open => Dialect.Standard with { EnableTopLevelStmt = true, EnableFutures = true };

    private static Interpreter New(Action<Interpreter>? configure = null)
    {
        var it = new Interpreter { Dialect = Open };
        configure?.Invoke(it);
        return it;
    }

    private static T WithTimeout<T>(Func<T> work, int seconds = 30)
    {
        var task = Task.Run(work);
        if (!Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds))).GetAwaiter().GetResult().Equals(task))
        {
            throw new TimeoutException("test hung");
        }
        return task.GetAwaiter().GetResult();
    }

    /// <summary>An endless sequence that counts what it yields and stops on its own after a hard cap, so a regression cannot exhaust memory.</summary>
    private sealed class Endless
    {
        public long Yielded;
        public const long HardCap = 20_000_000;

        public IEnumerable<int> Items()
        {
            while (Interlocked.Increment(ref Yielded) < HardCap) yield return 0;
            throw new InvalidOperationException("the interpreter did not stop the endless sequence");
        }
    }

    // C2
    [Fact]
    public void EndlessHostSequenceIsStoppedByTheStepBudget()
    {
        var src = new Endless();
        var it = New(i => i.MaxSteps = 10_000);
        it.Register("inf", () => src.Items());
        var e = WithTimeout(() => Assert.Throws<ExecutionLimitException>(() => it.Execute("t", "inf()\n")));
        Assert.Equal("steps", e.Limit);
        Assert.True(src.Yielded < 1_000_000, $"{src.Yielded} items were enumerated");
    }

    // C2
    [Fact]
    public void EndlessHostSequenceIsStoppedByTheTimeout()
    {
        var src = new Endless();
        var it = New(i => i.Timeout = TimeSpan.FromMilliseconds(200));
        it.Register("inf", () => src.Items());
        var e = WithTimeout(() => Assert.Throws<ExecutionLimitException>(() => it.Execute("t", "inf()\n")));
        Assert.Equal("time", e.Limit);
    }

    // C2
    [Fact]
    public void EndlessHostSequenceIsStoppedByCancellation()
    {
        var src = new Endless();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var it = New(i => i.Cancellation = cts.Token);
        it.Register("inf", () => src.Items());
        WithTimeout(() => Assert.ThrowsAny<OperationCanceledException>(() => it.Execute("t", "inf()\n")));
    }

    // C2
    [Fact]
    public void EndlessHostDictionaryIsStoppedByTheStepBudget()
    {
        var it = New(i => i.MaxSteps = 10_000);
        it.Register("big", () => Enumerable.Range(0, 100_000).ToDictionary(i => i, i => i));
        var e = Assert.Throws<ExecutionLimitException>(() => it.Execute("t", "big()\n"));
        Assert.Equal("steps", e.Limit);
    }

    // C1
    [Fact]
    public void EndlessAsFutureSequenceStopsWhenTheRunEnds()
    {
        var src = new Endless();
        var it = New(i => i.Timeout = TimeSpan.FromMilliseconds(300));
        it.Register("inf", () => Task.FromResult(src.Items()), asFuture: true);
        var e = WithTimeout(() => Assert.Throws<ExecutionLimitException>(() => it.Execute("t", "f = inf()\nf.get()\n")));
        Assert.Equal("time", e.Limit);
        long after = Interlocked.Read(ref src.Yielded);
        Thread.Sleep(300);
        long later = Interlocked.Read(ref src.Yielded);
        Assert.True(later - after < 2048, $"conversion kept running after the run ended: {after} then {later}");
    }

    // C1
    [Fact]
    public void EndlessAsFutureSequenceIsStoppedByTheStepBudget()
    {
        var src = new Endless();
        var it = New(i => i.MaxSteps = 10_000);
        it.Register("inf", () => Task.FromResult(src.Items()), asFuture: true);
        var e = WithTimeout(() => Assert.Throws<ExecutionLimitException>(() => it.Execute("t", "f = inf()\nf.get()\n")));
        Assert.Equal("steps", e.Limit);
        Assert.True(src.Yielded < 1_000_000, $"{src.Yielded} items were enumerated");
    }

    // C19
    [Fact]
    public void LazyHostResultThatThrowsIsAStarlarkErrorInEveryMode()
    {
        var it = New();
        it.Register("parse", (string[] xs) => xs.Select(int.Parse));
        it.Register("parse_async", (string[] xs) => Task.FromResult(xs.Select(int.Parse)), asFuture: true);
        var direct = Assert.Throws<StarlarkException>(() => it.Execute("a", "parse(['x'])\n"));
        Assert.Contains("Host function `parse` failed", direct.RawMessage);
        var future = Assert.Throws<StarlarkException>(() => it.Execute("b", "parse_async(['x']).get()\n"));
        Assert.Contains("Host function `parse_async` failed", future.RawMessage);
    }

    public sealed class Odd
    {
        public T Get<T>() => default!;
        public ReadOnlySpan<char> Span() => "ab".AsSpan();
    }

    // C20
    [Fact]
    public void ExposingGenericOrRefStructMethodsIsRejectedAtExpose()
    {
        var it = New();
        var e = Assert.Throws<ArgumentException>(() => it.Expose("h", new Odd()));
        Assert.Contains("Get", e.Message);
    }

    public sealed class SpanOnly
    {
        public ReadOnlySpan<char> Span() => "ab".AsSpan();
    }

    // C20
    [Fact]
    public void ExposingARefStructReturnIsRejectedAtExpose()
    {
        var e = Assert.Throws<ArgumentException>(() => New().Expose("h", new SpanOnly()));
        Assert.Contains("Span", e.Message);
    }

    // C30
    [Fact]
    public void HostTaskCancelledOnItsOwnIsAStarlarkErrorInEveryMode()
    {
        var it = New();
        it.Register("c", () => Task.FromCanceled<int>(new CancellationToken(true)));
        it.Register("cf", () => Task.FromCanceled<int>(new CancellationToken(true)), asFuture: true);
        it.Register("cx", () => Task.FromException<int>(new OperationCanceledException("own")), asFuture: true);
        Assert.Contains("Host function `c` was cancelled", Assert.Throws<StarlarkException>(() => it.Execute("a", "c()\n")).RawMessage);
        Assert.Contains("Host function `cf` was cancelled", Assert.Throws<StarlarkException>(() => it.Execute("b", "cf().get()\n")).RawMessage);
        Assert.Contains("Host function `cx` was cancelled", Assert.Throws<StarlarkException>(() => it.Execute("c", "cx().get()\n")).RawMessage);
    }

    // C31
    [Fact]
    public void FaultedHostTaskNamesTheFunctionInEveryMode()
    {
        var it = New();
        it.Register("bad", () => Task.FromException<int>(new IOException("disk")));
        it.Register("badf", () => Task.FromException<int>(new IOException("disk")), asFuture: true);
        Assert.Contains("Host function `bad` failed: disk", Assert.Throws<StarlarkException>(() => it.Execute("a", "bad()\n")).RawMessage);
        Assert.Contains("Host function `badf` failed: disk", Assert.Throws<StarlarkException>(() => it.Execute("b", "badf().get()\n")).RawMessage);
    }

    // C32: fields cannot carry [Expose]/[Hide] any more (CS0592); the attribute usage proves it.
    [Fact]
    public void ExposeAndHideCannotMarkFields()
    {
        foreach (var t in new[] { typeof(ExposeAttribute), typeof(HideAttribute) })
        {
            var usage = (AttributeUsageAttribute)Attribute.GetCustomAttribute(t, typeof(AttributeUsageAttribute))!;
            Assert.False(usage.ValidOn.HasFlag(AttributeTargets.Field), $"{t.Name} is allowed on fields");
        }
    }

    public sealed class Over
    {
        public int F(int x) => x;
        public string F(string s) => s;
    }

    public sealed class RenamedOver
    {
        public int F(int x) => x;
        [Expose("f_str")] public string F(string s) => s;
        [Expose] public int G() => 1;
    }

    // C33
    [Fact]
    public void OverloadedMethodsAreRejectedAtExpose()
    {
        var it = New();
        var e = Assert.Throws<ArgumentException>(() => it.Expose("o", new Over()));
        Assert.Contains("overloads", e.Message);
    }

    // C33: rename one overload and the class exposes fine (opt-in: only marked members).
    [Fact]
    public void RenamedOverloadIsReachable()
    {
        var it = New();
        it.Expose("o", new RenamedOver());
        var m = it.Execute("t", "r = [o.f_str('s'), o.g()]\n");
        Assert.Equal("[\"s\", 1]", Repr.Of(m.Globals["r"]));
    }
}
