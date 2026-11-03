using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>
/// The no-crash policy (QA-ROB-001) for recursive walkers over script values. A stack overflow cannot be caught, so a script
/// that builds a value containing itself, or nested hundreds of thousands deep, must end in an ordinary error, never in a
/// dead process. Found by the soak run: a generated program printed a dict that contained itself.
/// </summary>
public class StackDepthTests
{
    private static Interpreter New() => new()
    {
        Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableFStrings = true },
        Print = _ => { },
        MaxSteps = 50_000_000,
        MaxAllocatedBytes = 256L * 1024 * 1024,
    };

    private static object? Run(string source, string name = "r") => New().Execute("t.star", source).Globals[name];

    private const string Cycles = "d = {'a': 1}\nd['me'] = d\nl = [1]\nl.append(l)\nk = [2]\ns = [{'k': k}]\nk.append(s)\n";

    /// <summary>Builds a list nested 200,000 deep, far more than the stack can recurse through.</summary>
    private const string Deep = "def build():\n    a = []\n    for i in range(200000):\n        a = [a]\n    return a\n";

    // ---------------------------------------------------------------- cycles print as placeholders

    [Fact]
    public void ADictContainingItselfPrintsWithAPlaceholder()
    {
        Assert.Equal("{\"a\": 1, \"me\": {...}}", Run(Cycles + "r = repr(d)\n"));
    }

    [Fact]
    public void AListContainingItselfPrintsWithAPlaceholder()
    {
        Assert.Equal("[1, [...]]", Run(Cycles + "r = str(l)\n"));
    }

    [Fact]
    public void ACycleThroughSeveralContainersPrints()
    {
        // s holds a dict whose list k holds s again: the inner reference to s is the cycle.
        Assert.Equal("[{\"k\": [2, [...]]}]", Run(Cycles + "r = repr(s)\n"));
    }

    [Theory]
    [InlineData("r = '%s' % l\n")]
    [InlineData("r = '%r' % d\n")]
    [InlineData("r = '{}'.format(d)\n")]
    [InlineData("r = f'{l}'\n")]
    [InlineData("r = repr(struct(x = d))\n")]
    [InlineData("r = repr((l, 1))\n")]
    public void EveryWayOfFormattingACycleWorks(string tail)
    {
        Assert.Contains("...", (string)Run(Cycles + tail)!);
    }

    [Fact]
    public void PrintAndFailAcceptACycle()
    {
        var printed = new List<string>();
        var it = New();
        it.Print = printed.Add;
        it.Execute("t.star", Cycles + "print(d)\n");
        Assert.Contains("{...}", printed.Single());
        var e = Assert.Throws<StarlarkException>(() => New().Execute("t.star", Cycles + "fail(l)\n"));
        Assert.Contains("[...]", e.RawMessage);
    }

    [Fact]
    public void ASharedChildIsNotACycle()
    {
        // The same list twice side by side is not a cycle, so it prints in full both times.
        Assert.Equal("[[1], [1]]", Run("x = [1]\nr = repr([x, x])\n"));
    }

    // ---------------------------------------------------------------- absurd depth is an error

    [Theory]
    [InlineData("r = repr(build())\n")]
    [InlineData("a = build()\nr = a == a\n")]
    [InlineData("r = build() == build()\n")]
    [InlineData("r = build() < build()\n")]
    [InlineData("r = sorted([build(), build()])\n")]
    public void VeryDeepValuesEndInAnErrorNotAProcessCrash(string tail)
    {
        // Reaching the assertion at all means the process survived; the error is ordinary and catchable.
        var e = Assert.Throws<StarlarkException>(() => New().Execute("t.star", Deep + tail));
        Assert.Contains("nested too deeply", e.RawMessage);
    }

    [Fact]
    public void HashingADeepTupleEndsInAnError()
    {
        // A tuple of a list is not hashable, but the check walks the nesting first and must not overflow on the way.
        var e = Assert.Throws<StarlarkException>(() => New().Execute("t.star", Deep + "r = {(build(),): 1}\n"));
        Assert.True(e.RawMessage.Contains("nested too deeply") || e.RawMessage.Contains("unhashable"), e.RawMessage);
    }

    [Fact]
    public void ADeepValueThatIsNotKeptIsNeverWalked()
    {
        // Only an int is stored in the module, so nothing recurses and the run simply works.
        Assert.Equal(new System.Numerics.BigInteger(1), Run(Deep + "r = len([build()])\n"));
    }

    [Fact]
    public void ModuleThatEndsHoldingAVeryDeepValueFailsCleanly()
    {
        // Freezing the module at the end walks the value too, so this errors even though the script printed nothing.
        var e = Assert.Throws<StarlarkException>(() => New().Execute("t.star", Deep + "keep = build()\n"));
        Assert.Contains("nested too deeply", e.RawMessage);
    }

    [Fact]
    public void ReasonableDepthStillWorks()
    {
        // Depth 500 is well past anything a real program needs and must keep working.
        var s = (string)Run("def build():\n    a = []\n    for i in range(500):\n        a = [a]\n    return a\nr = repr(build())\n")!;
        Assert.Equal(1002, s.Length);
    }

    [Fact]
    public void TheSameHoldsInsideATask()
    {
        var it = new Interpreter
        {
            Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableFutures = true },
            Print = _ => { },
            MaxSteps = 100_000_000,
        };
        var e = Assert.Throws<StarlarkException>(() =>
            it.Execute("t.star", Deep + "def work(x):\n    return repr(build())\nr = parallel_map(work, [1, 2])\n"));
        Assert.Contains("nested too deeply", e.RawMessage);
    }
}
