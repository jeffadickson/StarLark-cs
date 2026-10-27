using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary><see cref="Interpreter.MaxAllocatedBytes"/> stops single huge allocations before they happen.</summary>
public class MemoryLimitTests
{
    private static Interpreter New(long limit = 1_000_000) => new()
    {
        Dialect = Dialect.Standard with { EnableTopLevelStmt = true },
        MaxAllocatedBytes = limit,
    };

    private static void Fails(string code)
    {
        var e = Assert.Throws<ExecutionLimitException>(() => New().Execute("t", code));
        Assert.Equal("memory", e.Limit);
    }

    /// <summary>Repeating a string, list, tuple or bytes past the limit fails before allocating.</summary>
    [Theory]
    [InlineData("x = 'ab' * 100000000\n")]
    [InlineData("x = [0] * 100000000\n")]
    [InlineData("x = (1, 2) * 100000000\n")]
    [InlineData("x = b'ab' * 2000000000\n")]
    public void RepetitionIsBounded(string code) => Fails(code);

    /// <summary>Concatenation that would pass the limit is refused.</summary>
    [Fact]
    public void ConcatenationIsBounded()
    {
        var it = New(5000);
        Assert.Throws<ExecutionLimitException>(() => it.Execute("t", "s = 'x' * 2000\nt = s + s + s\n"));
        Assert.Throws<ExecutionLimitException>(() => New(500).Execute("u", "a = [0] * 40\nb = a + a + a + a + a + a + a + a\n"));
    }

    /// <summary>Joining many strings into one that is too large is refused.</summary>
    [Fact]
    public void JoinIsBounded() => Assert.Throws<ExecutionLimitException>(() =>
        New(1000).Execute("t", "x = ','.join(['abcdefghij'] * 100)\n"));

    /// <summary>With no limit set nothing changes, and small objects pass under a limit.</summary>
    [Fact]
    public void NoLimitAndSmallObjectsAreFine()
    {
        New(0).Execute("t", "x = 'ab' * 1000\n");
        New().Execute("u", "x = 'ab' * 1000\ny = [1] * 100\n");
    }

    /// <summary>The limit applies per run and resets afterwards.</summary>
    [Fact]
    public void LimitDoesNotLeakBetweenRuns()
    {
        var it = New(100);
        Assert.Throws<ExecutionLimitException>(() => it.Execute("t", "x = 'a' * 1000\n"));
        it.MaxAllocatedBytes = 0;
        it.Execute("u", "x = 'a' * 1000\n");
    }

    /// <summary>The limit is not catchable by the script.</summary>
    [Fact]
    public void ScriptsCannotCatchIt() => Fails("def f():\n    return 'a' * 100000000\nf()\n");
}
