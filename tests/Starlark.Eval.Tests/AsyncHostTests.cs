using System.Numerics;
using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>Asynchronous host functions are callable from scripts as if synchronous.</summary>
public class AsyncHostTests
{
    private static Interpreter New() => new() { Dialect = Dialect.Standard with { EnableTopLevelStmt = true } };

    /// <summary>A function returning <see cref="Task{T}"/> yields its result.</summary>
    [Fact]
    public void TaskResultsAreReturned()
    {
        var it = New();
        it.Register("fetch", async (string key) => { await Task.Delay(5); return key.ToUpperInvariant(); });
        Assert.Equal("ABC", it.Execute("t", "x = fetch('abc')\n").Globals["x"]);
    }

    /// <summary>A function returning a non-generic <see cref="Task"/> returns None.</summary>
    [Fact]
    public void PlainTasksReturnNone()
    {
        var it = New();
        var done = false;
        it.Register("work", async () => { await Task.Delay(5); done = true; });
        Assert.Null(it.Execute("t", "x = work()\n").Globals["x"]);
        Assert.True(done);
    }

    /// <summary><see cref="ValueTask{T}"/> works too.</summary>
    [Fact]
    public void ValueTasksAreSupported()
    {
        var it = New();
        it.Register("answer", () => new ValueTask<int>(42));
        Assert.Equal(new BigInteger(42), it.Execute("t", "x = answer()\n").Globals["x"]);
    }

    /// <summary>An exception inside the task becomes a script error naming the failure.</summary>
    [Fact]
    public void TaskExceptionsBecomeScriptErrors()
    {
        var it = New();
        it.Register("bad", async () => { await Task.Delay(1); throw new InvalidOperationException("down"); });
        Assert.Contains("down", Assert.Throws<StarlarkException>(() => it.Execute("t", "bad()\n")).RawMessage);
    }

    /// <summary>Cancelling while the host task runs ends the call with an <see cref="OperationCanceledException"/>.</summary>
    [Fact]
    public void CancellingEndsAPendingCall()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var it = New();
        it.Cancellation = cts.Token;
        it.Register("slow", async (CancellationToken ct) => { await Task.Delay(Timeout.Infinite, ct); });
        Assert.ThrowsAny<OperationCanceledException>(() => it.Execute("t", "slow()\n"));
    }

    /// <summary>The time limit applies to a pending host task.</summary>
    [Fact]
    public void TimeoutEndsAPendingCall()
    {
        var it = New();
        it.Timeout = TimeSpan.FromMilliseconds(100);
        it.Register("slow", async () => { await Task.Delay(TimeSpan.FromSeconds(30)); });
        var e = Assert.Throws<ExecutionLimitException>(() => it.Execute("t", "slow()\n"));
        Assert.Equal("time", e.Limit);
    }

    /// <summary>An <see cref="Interpreter"/> parameter is injected and hidden from the script's view of the signature.</summary>
    [Fact]
    public void InterpreterIsInjected()
    {
        var it = New();
        it.Register("twice", (Interpreter i, int n) => ReferenceEquals(i, it) ? n * 2 : -1);
        Assert.Equal(new BigInteger(8), it.Execute("t", "x = twice(4)\ny = twice(n = 4)\n").Globals["y"]);
    }
}
