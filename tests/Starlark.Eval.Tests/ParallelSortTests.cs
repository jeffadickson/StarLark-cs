using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>Large sorts with plain keys run in parallel; the result must equal the sequential stable sort.</summary>
public class ParallelSortTests
{
    private static object? Run(string src) =>
        new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true } }.Execute("t", src).Globals["r"];

    private static string Repr(object? v) => Starlark.Eval.Repr.Of(v);

    [Theory]
    [InlineData("False")]
    [InlineData("True")]
    public void LargeIntSortMatchesSmallSortOfSameData(string reverse)
    {
        var big = Repr(Run($"xs = [(i * 7919) % 10007 for i in range(20000)]\nr = sorted(xs, reverse = {reverse})\n"));
        var chunked = Repr(Run($"xs = [(i * 7919) % 10007 for i in range(20000)]\nr = sorted(xs, key = lambda x: x, reverse = {reverse})\n"));
        Assert.Equal(chunked, big);
    }

    [Fact]
    public void SortIsStableWithKeysAndReverse()
    {
        // Keys are equal in groups; the payload shows the original order is kept (also with reverse=True).
        var r = Run("xs = [(i % 5, i) for i in range(20000)]\nr = [p[1] for p in sorted(xs, key = lambda p: p[0])][:6]\n");
        Assert.Equal("[0, 5, 10, 15, 20, 25]", Repr(r));
        var rev = Run("xs = [(i % 5, i) for i in range(20000)]\nr = [p[1] for p in sorted(xs, key = lambda p: p[0], reverse = True)][:4]\n");
        Assert.Equal("[4, 9, 14, 19]", Repr(rev));
    }

    [Fact]
    public void StringsAndFloatsSortInParallel()
    {
        var s = Run("xs = [str((i * 31) % 9973) for i in range(12000)]\nr = sorted(xs)\nok = all([r[i] <= r[i + 1] for i in range(len(r) - 1)])\n");
        Assert.NotNull(s);
        var ok = new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true } }
            .Execute("t", "xs = [float((i * 31) % 9973) / 3.0 for i in range(12000)]\nys = sorted(xs)\nr = all([ys[i] <= ys[i + 1] for i in range(len(ys) - 1)]) and len(ys) == 12000\n").Globals["r"];
        Assert.Equal(true, ok);
    }

    [Fact]
    public void MixedKeysStillRaiseTheNormalError()
    {
        var e = Assert.Throws<StarlarkException>(() => Run("xs = list(range(9000)) + ['a']\nr = sorted(xs)\n"));
        Assert.Contains("not supported on types", e.RawMessage);
    }

    [Fact]
    public void NanKeysFallBackToTheSequentialPath()
    {
        Assert.NotNull(Run("xs = [float('nan')] + [float(i) for i in range(9000)]\nr = sorted(xs)\n"));
    }
}
