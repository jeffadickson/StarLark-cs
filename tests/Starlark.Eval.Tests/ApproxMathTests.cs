using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>
/// The pure Starlark package <c>std/approx_math.star</c> (sin, cos, exp, log, log2, atan) and the host's internal
/// <c>sin</c> ... <c>atan</c> built on <see cref="Math"/>. The package is checked against <see cref="Math"/> over many inputs.
/// </summary>
public class ApproxMathTests
{
    private static Interpreter New() => new()
    {
        Dialect = Dialect.Standard with { EnableTopLevelStmt = true },
        MaxSteps = 50_000_000,
    };

    private static Interpreter Typed() => new() { Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableTypes = DialectTypes.Enable } };

    private const string Load = "load('std/approx_math.star', 'sin', 'cos', 'exp', 'log', 'log2', 'atan', 'pi', 'e')\n";

    private static object? Run(string body) => New().Execute("t.star", Load + body).Globals["r"];

    /// <summary>Distance between two doubles in units in the last place; infinity when one is special and the other is not.</summary>
    private static double Ulps(double got, double want)
    {
        if (double.IsNaN(want)) return double.IsNaN(got) ? 0 : double.PositiveInfinity;
        if (double.IsInfinity(want) || want == 0) return got == want ? 0 : double.PositiveInfinity;
        return Math.Abs(got - want) / (Math.BitIncrement(Math.Abs(want)) - Math.Abs(want));
    }

    /// <summary>Calls a package function once per input in a single script run, returning the results in order.</summary>
    private static double[] Evaluate(string function, double[] inputs)
    {
        var it = New();
        var m = it.Execute("t.star", Load);
        var fn = m.Globals[function];
        return inputs.Select(x => (double)it.CallValue(fn, [x])!).ToArray();
    }

    private static double[] Spread(int seed, int n, Func<Random, double> make)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, n).Select(_ => make(rng)).ToArray();
    }

    private static void AssertClose(string name, Func<double, double> reference, double[] inputs, double maxUlps)
    {
        var got = Evaluate(name, inputs);
        for (int i = 0; i < inputs.Length; i++)
        {
            double u = Ulps(got[i], reference(inputs[i]));
            Assert.True(u <= maxUlps, $"{name}({inputs[i]:R}) = {got[i]:R}, want {reference(inputs[i]):R}: {u:F1} ulp off (limit {maxUlps})");
        }
    }

    // ---------------------------------------------------------------- accuracy

    [Fact]
    public void SinAgreesWithMath() =>
        AssertClose("sin", Math.Sin, [.. Spread(1, 600, r => (r.NextDouble() - 0.5) * 8), .. Spread(2, 600, r => (r.NextDouble() - 0.5) * 2000)], 3);

    [Fact]
    public void CosAgreesWithMath() =>
        AssertClose("cos", Math.Cos, [.. Spread(3, 600, r => (r.NextDouble() - 0.5) * 8), .. Spread(4, 600, r => (r.NextDouble() - 0.5) * 2000)], 3);

    [Fact]
    public void ExpAgreesWithMath() =>
        AssertClose("exp", Math.Exp, [.. Spread(5, 600, r => (r.NextDouble() - 0.5) * 20), .. Spread(6, 600, r => (r.NextDouble() - 0.5) * 1400)], 3);

    [Fact]
    public void LogAgreesWithMath() =>
        AssertClose("log", Math.Log, [.. Spread(7, 600, r => r.NextDouble() * 10), .. Spread(8, 600, r => Math.Exp((r.NextDouble() - 0.5) * 1400))], 3);

    [Fact]
    public void Log2AgreesWithMath() =>
        AssertClose("log2", Math.Log2, [.. Spread(9, 600, r => r.NextDouble() * 10), .. Spread(10, 600, r => Math.Exp((r.NextDouble() - 0.5) * 1400))], 4);

    [Fact]
    public void AtanAgreesWithMath() =>
        AssertClose("atan", Math.Atan, [.. Spread(11, 600, r => (r.NextDouble() - 0.5) * 20), .. Spread(12, 600, r => Math.Exp((r.NextDouble() - 0.5) * 200) * (r.Next(2) == 0 ? 1 : -1))], 4);

    [Fact]
    public void ExactResultsAreExact()
    {
        Assert.Equal(0.0, Evaluate("sin", [0.0])[0]);
        Assert.Equal(1.0, Evaluate("cos", [0.0])[0]);
        Assert.Equal(1.0, Evaluate("exp", [0.0])[0]);
        Assert.Equal(0.0, Evaluate("log", [1.0])[0]);
        Assert.Equal([3.0, 10.0, -1.0, 0.0, 52.0], Evaluate("log2", [8.0, 1024.0, 0.5, 1.0, Math.Pow(2, 52)]));
    }

    [Fact]
    public void SubnormalAndExtremeValuesWork()
    {
        Assert.True(Ulps(Evaluate("log2", [1e-310])[0], Math.Log2(1e-310)) <= 4);
        Assert.True(Ulps(Evaluate("exp", [-740.0])[0], Math.Exp(-740)) <= 4);   // a subnormal result: a few ulps are only a few hundredths of the value's precision
        Assert.True(Ulps(Evaluate("log", [double.MaxValue])[0], Math.Log(double.MaxValue)) <= 4);
        Assert.Equal(Math.Atan(1e300), Evaluate("atan", [1e300])[0]);
        Assert.Equal(1e-300, Evaluate("atan", [1e-300])[0]);
    }

    // ---------------------------------------------------------------- special values (C and Go behaviour, not Python's)

    [Fact]
    public void SpecialValuesFollowTheCLibrary()
    {
        Assert.Equal(double.NegativeInfinity, Evaluate("log", [0.0])[0]);
        Assert.True(double.IsNaN(Evaluate("log", [-1.0])[0]));
        Assert.Equal(double.NegativeInfinity, Evaluate("log2", [0.0])[0]);
        Assert.True(double.IsNaN(Evaluate("log2", [-2.0])[0]));
        Assert.Equal(double.PositiveInfinity, Evaluate("exp", [1000.0])[0]);
        Assert.Equal(0.0, Evaluate("exp", [-1000.0])[0]);
        Assert.True(double.IsNaN(Evaluate("sin", [double.PositiveInfinity])[0]));
        Assert.True(double.IsNaN(Evaluate("cos", [double.NegativeInfinity])[0]));
        Assert.Equal(Math.PI / 2, Evaluate("atan", [double.PositiveInfinity])[0]);
        Assert.Equal(-Math.PI / 2, Evaluate("atan", [double.NegativeInfinity])[0]);
        Assert.True(double.IsNaN(Evaluate("exp", [double.NaN])[0]));
        Assert.Equal(double.PositiveInfinity, Evaluate("log", [double.PositiveInfinity])[0]);
    }

    [Fact]
    public void NegativeZeroKeepsItsSign()
    {
        Assert.True(double.IsNegative(Evaluate("sin", [-0.0])[0]));
        Assert.True(double.IsNegative(Evaluate("atan", [-0.0])[0]));
    }

    [Fact]
    public void LogWithABase()
    {
        Assert.Equal("2.0", Repr.Of(Run("r = log(100, 10)\n")));
        Assert.Equal("3.0", Repr.Of(Run("r = log(8, 2)\n")));
        Assert.Equal("1.0", Repr.Of(Run("r = log(e)\n")));
        var err = Assert.Throws<StarlarkException>(() => Run("r = log(2, 1)\n"));
        Assert.Contains("division by zero", err.RawMessage);
    }

    [Fact]
    public void IntegersAreAccepted()
    {
        Assert.Equal("0.0", Repr.Of(Run("r = log(1)\n")));
        Assert.Equal("1.0", Repr.Of(Run("r = exp(0)\n")));
        Assert.Equal("10.0", Repr.Of(Run("r = log2(1024)\n")));
    }

    [Theory]
    [InlineData("sin('x')")]
    [InlineData("cos(None)")]
    [InlineData("exp([1])")]
    [InlineData("log('a')")]
    [InlineData("log2({})")]
    [InlineData("atan((1,))")]
    [InlineData("log(2, 'b')")]
    public void WrongTypesAreErrors(string call)
    {
        var e = Assert.Throws<StarlarkException>(() => Run("r = " + call + "\n"));
        Assert.Contains("want float or int", e.RawMessage);
    }

    [Fact]
    public void PiAndEAreExported()
    {
        Assert.Equal(Math.PI, Run("r = pi\n"));
        Assert.Equal(Math.E, Run("r = e\n"));
    }

    // ---------------------------------------------------------------- the leading underscore means internal

    [Theory]
    [InlineData("_ln")]
    [InlineData("_horner")]
    [InlineData("_check")]
    [InlineData("_reduce")]
    [InlineData("_frexp")]
    public void InternalHelpersCannotBeLoaded(string name)
    {
        var e = Assert.Throws<StarlarkException>(() => New().Execute("t.star", $"load('std/approx_math.star', '{name}')\n"));
        Assert.Contains("does not export", e.RawMessage);
    }

    [Fact]
    public void ThePackageCallsNoRecursionAndStaysWithinTheStepBudget()
    {
        // Starlark forbids recursion, so merely running every function proves none of them recurse. The budget shows they are cheap.
        var it = New();
        it.MaxSteps = 20_000;
        it.Execute("t.star", Load + "r = [sin(1e6), cos(-3.0), exp(700), log(1e-300), log2(1e300), atan(5), log(9, 3)]\n");
    }

    [Fact]
    public void ThePackageIsBundledAndListed()
    {
        Assert.Contains("std/approx_math.star", StandardLibrary.Packages());
        Assert.Null(StandardLibrary.Read("std/missing.star"));
        Assert.Null(StandardLibrary.Read("other/approx_math.star"));
    }

    [Fact]
    public void AHostLoaderCanReplaceThePackage()
    {
        var it = New();
        it.SourceLoader = id => id == "std/approx_math.star" ? "sin = lambda x: 42\n" : null;
        Assert.Equal("42", Repr.Of(it.Execute("t.star", "load('std/approx_math.star', 'sin')\nr = sin(1)\n").Globals["r"]));
    }

    [Fact]
    public void TheCheckerAcceptsLoadsFromThePackage()
    {
        var result = Typed().Typecheck("t.star", Load + "def f(x: float) -> float:\n    return sin(x) + log(x, 2)\n");
        Assert.Empty(result.Errors);
    }

    // ---------------------------------------------------------------- the host's own sin ... atan

    private static object? Internal(string body) => New().Execute("t.star", body).Globals["r"];

    [Fact]
    public void InternalFunctionsMatchMathExactly()
    {
        Assert.Equal(Math.Sin(0.5), Internal("r = sin(0.5)\n"));
        Assert.Equal(Math.Cos(0.5), Internal("r = cos(0.5)\n"));
        Assert.Equal(Math.Exp(1.5), Internal("r = exp(1.5)\n"));
        Assert.Equal(Math.Log(7.0), Internal("r = log(7.0)\n"));
        Assert.Equal(Math.Log2(7.0), Internal("r = log2(7.0)\n"));
        Assert.Equal(Math.Atan(0.25), Internal("r = atan(0.25)\n"));
        Assert.Equal(Math.Log(100.0) / Math.Log(10.0), Internal("r = log(100, 10)\n"));
    }

    [Fact]
    public void InternalFunctionsAcceptIntegers()
    {
        Assert.Equal(Math.Sin(2), Internal("r = sin(2)\n"));
        Assert.Equal(Math.Exp(3), Internal("r = exp(3)\n"));
    }

    [Fact]
    public void InternalFunctionsReturnSpecialValuesNotErrors()
    {
        Assert.Equal(double.NegativeInfinity, Internal("r = log(0)\n"));
        Assert.True(double.IsNaN((double)Internal("r = log(-1)\n")!));
        Assert.Equal(double.PositiveInfinity, Internal("r = exp(1000)\n"));
        Assert.True(double.IsNaN((double)Internal("r = sin(1.0e400 - 1.0e400)\n".Replace("1.0e400", "1e308 * 10"))!));
    }

    [Theory]
    [InlineData("sin('x')")]
    [InlineData("log(None)")]
    [InlineData("atan(True)")]
    [InlineData("exp([])")]
    public void InternalFunctionsRejectWrongTypes(string call)
    {
        var e = Assert.Throws<StarlarkException>(() => Internal("r = " + call + "\n"));
        Assert.Contains("int or float", e.RawMessage);
    }

    [Fact]
    public void InternalFunctionsRejectBadArguments()
    {
        Assert.Contains("division by zero", Assert.Throws<StarlarkException>(() => Internal("r = log(2, 1)\n")).RawMessage);
        Assert.Throws<StarlarkException>(() => Internal("r = sin()\n"));
        Assert.Throws<StarlarkException>(() => Internal("r = sin(1, 2)\n"));
        Assert.Throws<StarlarkException>(() => Internal("r = sin(x = 1, y = 2)\n"));
        Assert.Contains("too large", Assert.Throws<StarlarkException>(() => Internal("r = sin(1 << 2000)\n")).RawMessage);
    }

    [Fact]
    public void InternalFunctionsHaveCheckerSignatures()
    {
        Assert.Empty(Typed().Typecheck("t.star", "def f(x: float) -> float:\n    return sin(x) + log(x, 2) + atan(3)\n").Errors);
        Assert.NotEmpty(Typed().Typecheck("t.star", "def f():\n    return sin('x')\n").Errors);
    }

    [Fact]
    public void InternalAndPackageVersionsAgree()
    {
        var it = New();
        var m = it.Execute("t.star", Load + "def both(f, g, x):\n    return (f(x), g(x))\n");
        foreach (var (pure, host, reference) in new (string, string, Func<double, double>)[]
                 { ("sin", "sin", Math.Sin), ("cos", "cos", Math.Cos), ("exp", "exp", Math.Exp), ("log2", "log2", Math.Log2), ("atan", "atan", Math.Atan) })
        {
            foreach (double x in new[] { 0.1, 0.7, 1.3, 2.9, 5.5 })
            {
                var pureValue = (double)Evaluate(pure, [x])[0];
                Assert.True(Ulps(pureValue, reference(x)) <= 4, $"{pure}({x})");
                Assert.Equal(reference(x), (double)Internal($"r = {host}({x:R})\n")!);
            }
        }
    }
}
