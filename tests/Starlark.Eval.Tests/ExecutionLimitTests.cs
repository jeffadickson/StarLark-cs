using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>The step, time and cancellation budgets that make it safe to run untrusted scripts.</summary>
public class ExecutionLimitTests
{
    private static Interpreter New() => new() { Dialect = Dialect.Standard with { EnableTopLevelStmt = true } };

    private const string Spin = "def spin():\n    for i in range(1000000000):\n        pass\n\nspin()\n";

    /// <summary>A runaway loop is stopped by the step limit.</summary>
    [Fact]
    public void StepLimitStopsALongLoop()
    {
        var it = New();
        it.MaxSteps = 10_000;
        var e = Assert.Throws<ExecutionLimitException>(() => it.Execute("t", Spin));
        Assert.Equal("steps", e.Limit);
    }

    /// <summary>A program under the limit is unaffected.</summary>
    [Fact]
    public void StepLimitLeavesSmallProgramsAlone()
    {
        var it = New();
        it.MaxSteps = 10_000;
        it.Execute("t", "x = len([1, 2, 3])\n");
    }

    /// <summary>The budget is per top-level call, so the same interpreter can be reused.</summary>
    [Fact]
    public void StepBudgetResetsOnEachExecute()
    {
        var it = New();
        it.MaxSteps = 500;
        for (int i = 0; i < 5; i++) it.Execute("t" + i, "y" + i + " = [x for x in range(20)]\n");
    }

    /// <summary>Scripts cannot catch the limit: it is not a <see cref="StarlarkException"/>.</summary>
    [Fact]
    public void LimitIsNotAScriptError()
    {
        var it = New();
        it.MaxSteps = 100;
        Assert.Throws<ExecutionLimitException>(() => it.Execute("t", Spin));
        Assert.False(typeof(StarlarkException).IsAssignableFrom(typeof(ExecutionLimitException)));
    }

    /// <summary>A wall-clock timeout stops a loop even with no step limit.</summary>
    [Fact]
    public void TimeoutStopsALongLoop()
    {
        var it = New();
        it.Timeout = TimeSpan.FromMilliseconds(100);
        var e = Assert.Throws<ExecutionLimitException>(() => it.Execute("t", Spin));
        Assert.Equal("time", e.Limit);
    }

    /// <summary>Cancelling the token stops a running script.</summary>
    [Fact]
    public void CancellationStopsAScript()
    {
        using var cts = new CancellationTokenSource();
        var it = New();
        it.Cancellation = cts.Token;
        it.Define("stop", (_, _, _) => { cts.Cancel(); return null; });
        Assert.Throws<OperationCanceledException>(() => it.Execute("t", "stop()\n" + Spin));
    }

    /// <summary>A loaded module shares the budget of the program that loaded it.</summary>
    [Fact]
    public void LoadedModulesShareTheBudget()
    {
        var it = New();
        it.MaxSteps = 10_000;
        it.SourceLoader = _ => Spin;
        Assert.Throws<ExecutionLimitException>(() => it.Execute("t", "load('m.star', 'spin')\n"));
    }
}
