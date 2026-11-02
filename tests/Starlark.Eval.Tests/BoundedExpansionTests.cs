using System.Diagnostics;
using System.Numerics;
using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>
/// Regression tests for the soak findings: a program must not be able to make the interpreter build an enormous
/// list just because it asked for an enormous range. Sizes are checked before anything is built, and the work that
/// is done counts against the step and memory limits.
/// </summary>
public class BoundedExpansionTests
{
    private static Interpreter New(Action<Interpreter>? configure = null)
    {
        var it = new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true } };
        configure?.Invoke(it);
        return it;
    }

    private static string Fails(string program, Action<Interpreter>? configure = null) =>
        Assert.Throws<StarlarkException>(() => New(configure).Execute("t", program)).RawMessage;

    /// <summary>Unpacking a range of the wrong length reports the mismatch without expanding the range.</summary>
    [Theory]
    [InlineData("a, b = range(2000000000)", "Too many values to unpack (got 2000000000, expected 2)")]
    [InlineData("a, b = range(1)", "Too few values to unpack (got 1, expected 2)")]
    [InlineData("a, b, c = range(1000000000000000000000000000000)", "Too many values to unpack")]
    [InlineData("a, b = [1, 2, 3]", "Too many values to unpack (got 3, expected 2)")]
    [InlineData("a, b = (1,)", "Too few values to unpack (got 1, expected 2)")]
    [InlineData("a, b = {1: 2, 3: 4, 5: 6}", "Too many values to unpack (got 3, expected 2)")]
    public void UnpackingChecksTheLengthFirst(string statement, string message)
    {
        var sw = Stopwatch.StartNew();
        Assert.Contains(message, Fails(statement + "\n"));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"took {sw.Elapsed.TotalSeconds:F1}s");
    }

    /// <summary>A correct-length unpack still works.</summary>
    [Fact]
    public void UnpackingTheRightLengthStillWorks()
    {
        var m = New().Execute("t", "a, b = range(2)\nc, d = [7, 8]\ne, f = {1: 2, 3: 4}\n");
        Assert.Equal(new BigInteger(1), m.Globals["b"]);
        Assert.Equal(new BigInteger(8), m.Globals["d"]);
        Assert.Equal(new BigInteger(3), m.Globals["f"]);
    }

    /// <summary>list, tuple, set, sorted, enumerate, reversed and zip refuse a range too big to expand, immediately.</summary>
    [Theory]
    [InlineData("list(range(10000000000))")]
    [InlineData("tuple(range(10000000000))")]
    [InlineData("set(range(10000000000))")]
    [InlineData("sorted(range(10000000000))")]
    [InlineData("reversed(range(10000000000))")]
    [InlineData("enumerate(range(10000000000))")]
    [InlineData("zip(range(10000000000), [1])")]
    [InlineData("dict(zip(range(10000000000), range(3)))")]
    public void BuiltinsRefuseHugeRanges(string expression)
    {
        var sw = Stopwatch.StartNew();
        string message = Fails($"x = {expression}\n");
        Assert.Contains("Cannot expand", message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"took {sw.Elapsed.TotalSeconds:F1}s");
    }

    /// <summary>Expanding a large but allowed range counts against the step limit.</summary>
    [Fact]
    public void ExpansionCountsAsSteps()
    {
        var it = New(i => i.MaxSteps = 50_000);
        var e = Assert.Throws<ExecutionLimitException>(() => it.Execute("t", "x = list(range(5000000))\n"));
        Assert.Equal("steps", e.Limit);
    }

    /// <summary>It also has to fit the memory ceiling, and is refused before it is built.</summary>
    [Fact]
    public void ExpansionRespectsTheMemoryCeiling()
    {
        var it = New(i => i.MaxAllocatedBytes = 1_000_000);
        var sw = Stopwatch.StartNew();
        var e = Assert.Throws<ExecutionLimitException>(() => it.Execute("t", "x = list(range(50000000))\n"));
        Assert.Equal("memory", e.Limit);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
    }

    /// <summary>Ordinary sizes are unaffected.</summary>
    [Fact]
    public void OrdinaryExpansionsStillWork()
    {
        var m = New().Execute("t",
            "a = list(range(1000))\nb = tuple(range(5))\nc = sorted([3, 1, 2])\nd = reversed(range(3))\ne = enumerate(['x', 'y'])\nf = zip([1, 2], [3, 4])\ng = set([1, 1, 2])\nh = dict([(1, 2)])\n");
        Assert.Equal(1000, ((StarList)m.Globals["a"]!).Count);
        Assert.Equal("[2, 1, 0]", Repr.Of(m.Globals["d"]));
        Assert.Equal("[(0, \"x\"), (1, \"y\")]", Repr.Of(m.Globals["e"]));
        Assert.Equal("[(1, 3), (2, 4)]", Repr.Of(m.Globals["f"]));
    }

    /// <summary>Extending a list or updating a dict or set from a huge range is refused too.</summary>
    [Theory]
    [InlineData("xs = []\nxs.extend(range(10000000000))\n")]
    [InlineData("s = set()\ns.update(range(10000000000))\n")]
    [InlineData("s = set()\ns.union(range(10000000000))\n")]
    public void MethodsRefuseHugeRanges(string program)
    {
        Assert.Contains("Cannot expand", Fails(program));
    }

    /// <summary>The known-length shortcut only applies to values that really know their length.</summary>
    [Fact]
    public void KnownLengthIsOnlyForSizedValues()
    {
        Assert.Equal(new BigInteger(5), Ops.KnownLength(new StarRange(0, 5, 1)));
        Assert.Equal(BigInteger.Zero, Ops.KnownLength(new StarRange(5, 0, 1)));
        Assert.Equal(new BigInteger(2), Ops.KnownLength(new StarList([null, null])));
        Assert.Null(Ops.KnownLength("abc"));
        Assert.Null(Ops.KnownLength(null));
    }
}
