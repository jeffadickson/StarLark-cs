using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>
/// The second group of elementary functions (tan, asin, acos, atan2, sqrt, pow, hypot, floor, ceil, trunc, degrees, radians,
/// isnan, isinf), in both forms: the host's native versions and the pure Starlark package <c>std/approx_math.star</c>.
/// Each is checked against <see cref="Math"/>, for special values, for wrong arguments, and for the two forms agreeing.
/// </summary>
public class MoreMathTests
{
    private static readonly string[] Names =
        ["tan", "asin", "acos", "atan2", "sqrt", "pow", "hypot", "floor", "ceil", "trunc", "round", "degrees", "radians", "isnan", "isinf"];

    private static readonly string Load = "load('std/approx_math.star', " + string.Join(", ", Names.Select(n => $"'{n}'")) + ")\n";

    private static Interpreter New() => new()
    {
        Dialect = Dialect.Standard with { EnableTopLevelStmt = true },
        MaxSteps = 50_000_000,
    };

    /// <summary>The native function of that name, and the package function of that name.</summary>
    private static (Func<object?[], object?> Native, Func<object?[], object?> Package) Both(string name)
    {
        var native = New();
        var nativeFn = native.Execute("n.star", $"f = {name}\n").Globals["f"];
        var package = New();
        var packageFn = package.Execute("p.star", Load + $"f = {name}\n").Globals["f"];
        return (a => native.CallValue(nativeFn, a), a => package.CallValue(packageFn, a));
    }

    private static double Ulps(double got, double want)
    {
        if (double.IsNaN(want)) return double.IsNaN(got) ? 0 : double.PositiveInfinity;
        if (double.IsInfinity(want) || want == 0) return got == want ? 0 : double.PositiveInfinity;
        return Math.Abs(got - want) / (Math.BitIncrement(Math.Abs(want)) - Math.Abs(want));
    }

    private static IEnumerable<double> Spread(int seed, int n, double lo, double hi)
    {
        var rng = new Random(seed);
        for (int i = 0; i < n; i++) yield return lo + (hi - lo) * rng.NextDouble();
    }

    private static void Close(string name, Func<double, double> reference, IEnumerable<double> inputs, double maxUlps)
    {
        var (native, package) = Both(name);
        foreach (var x in inputs)
        {
            double want = reference(x);
            double n = (double)native([x])!, p = (double)package([x])!;
            Assert.True(Ulps(n, want) <= 1, $"native {name}({x:R}) = {n:R}, want {want:R}");
            Assert.True(Ulps(p, want) <= maxUlps, $"package {name}({x:R}) = {p:R}, want {want:R}: {Ulps(p, want):F1} ulp (limit {maxUlps})");
        }
    }

    // ---------------------------------------------------------------- accuracy against System.Math

    [Fact] public void TanAgrees() => Close("tan", Math.Tan, Spread(1, 400, -20, 20).Concat(Spread(2, 100, -1e5, 1e5)), 6);
    [Fact] public void AsinAgrees() => Close("asin", Math.Asin, Spread(3, 400, -1, 1), 6);
    [Fact] public void AcosAgrees() => Close("acos", Math.Acos, Spread(4, 400, -1, 1), 6);
    [Fact] public void SqrtAgrees() => Close("sqrt", Math.Sqrt, Spread(5, 300, 0, 1e6).Concat(Spread(6, 200, 0, 1e-300)).Concat(Spread(7, 200, 0, 1e300)), 1);
    [Fact] public void DegreesAgrees() => Close("degrees", x => x * 57.29577951308232, Spread(8, 100, -1000, 1000), 1);
    [Fact] public void RadiansAgrees() => Close("radians", x => x * 0.017453292519943295, Spread(9, 100, -1000, 1000), 1);

    [Fact]
    public void Atan2Agrees()
    {
        var (native, package) = Both("atan2");
        var rng = new Random(10);
        for (int i = 0; i < 600; i++)
        {
            double y = (rng.NextDouble() - 0.5) * Math.Pow(10, rng.Next(-8, 9)), x = (rng.NextDouble() - 0.5) * Math.Pow(10, rng.Next(-8, 9));
            double want = Math.Atan2(y, x);
            Assert.True(Ulps((double)native([y, x])!, want) <= 1, $"native atan2({y:R}, {x:R})");
            Assert.True(Ulps((double)package([y, x])!, want) <= 8, $"package atan2({y:R}, {x:R}) = {package([y, x])}, want {want:R}");
        }
    }

    [Fact]
    public void HypotAgrees()
    {
        var (native, package) = Both("hypot");
        var rng = new Random(11);
        for (int i = 0; i < 400; i++)
        {
            double x = (rng.NextDouble() - 0.5) * Math.Pow(10, rng.Next(-150, 150)), y = (rng.NextDouble() - 0.5) * Math.Pow(10, rng.Next(-150, 150));
            double want = double.Hypot(x, y);
            Assert.True(Ulps((double)native([x, y])!, want) <= 1, $"native hypot({x:R}, {y:R})");
            Assert.True(Ulps((double)package([x, y])!, want) <= 6, $"package hypot({x:R}, {y:R}) = {package([x, y])}, want {want:R}");
        }
    }

    [Fact]
    public void PowAgrees()
    {
        var (native, package) = Both("pow");
        var rng = new Random(12);
        for (int i = 0; i < 600; i++)
        {
            double x = rng.NextDouble() * 20, y = (rng.NextDouble() - 0.5) * 20;
            if (i % 3 == 0) y = Math.Round(y);
            if (i % 7 == 0) x = -x;
            double want = Math.Pow(x, y);
            double n = (double)native([x, y])!, p = (double)package([x, y])!;
            Assert.True(Ulps(n, want) <= 1, $"native pow({x:R}, {y:R})");
            Assert.True(Ulps(p, want) <= 400, $"package pow({x:R}, {y:R}) = {p:R}, want {want:R}: {Ulps(p, want):F1} ulp");
        }
    }

    [Theory]
    [InlineData(2, 10, 1024.0)]
    [InlineData(2, -2, 0.25)]
    [InlineData(-2, 3, -8.0)]
    [InlineData(-2, 2, 4.0)]
    [InlineData(10, 0, 1.0)]
    [InlineData(0.5, 3, 0.125)]
    public void PowIsExactForSmallWholeExponents(double x, double y, double want)
    {
        var (native, package) = Both("pow");
        Assert.Equal(want, (double)native([x, y])!);
        Assert.Equal(want, (double)package([x, y])!);
    }

    // ---------------------------------------------------------------- rounding

    [Theory]
    [InlineData("floor", 2.5, 2)] [InlineData("floor", -2.5, -3)] [InlineData("floor", 3.0, 3)] [InlineData("floor", -0.5, -1)]
    [InlineData("ceil", 2.5, 3)] [InlineData("ceil", -2.5, -2)] [InlineData("ceil", 3.0, 3)] [InlineData("ceil", -0.5, 0)]
    [InlineData("round", 2.5, 2)] [InlineData("round", 3.5, 4)] [InlineData("round", -2.5, -2)] [InlineData("round", -3.5, -4)]
    [InlineData("round", 2.4, 2)] [InlineData("round", 2.6, 3)] [InlineData("round", -0.4, 0)] [InlineData("round", 0.5, 0)] [InlineData("round", 1.5, 2)]
    [InlineData("trunc", 2.9, 2)] [InlineData("trunc", -2.9, -2)] [InlineData("trunc", 0.4, 0)]
    public void RoundingGivesInts(string name, double x, int want)
    {
        var (native, package) = Both(name);
        Assert.Equal(new System.Numerics.BigInteger(want), native([x]));
        Assert.Equal(new System.Numerics.BigInteger(want), package([x]));
    }

    [Theory]
    [InlineData("floor")] [InlineData("ceil")] [InlineData("trunc")] [InlineData("round")]
    public void RoundingKeepsIntsAndHandlesHugeValues(string name)
    {
        var (native, package) = Both(name);
        var big = System.Numerics.BigInteger.Pow(10, 40);
        Assert.Equal(big, native([big]));
        Assert.Equal(big, package([big]));
        // 1e22 is a float with an exact integer value.
        var want = System.Numerics.BigInteger.Parse("10000000000000000000000");
        Assert.Equal(want, native([1e22]));
        Assert.Equal(want, package([1e22]));
    }

    [Theory]
    [InlineData("floor")] [InlineData("ceil")] [InlineData("trunc")] [InlineData("round")]
    public void RoundingNanAndInfinityAreErrors(string name)
    {
        var (native, package) = Both(name);
        foreach (var bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.Contains("cannot convert", Assert.Throws<StarlarkException>(() => native([bad])).RawMessage);
            Assert.Contains("cannot convert", Assert.Throws<StarlarkException>(() => package([bad])).RawMessage);
        }
    }

    // ---------------------------------------------------------------- special values

    private static readonly double NaN = double.NaN, Inf = double.PositiveInfinity;

    [Fact]
    public void SpecialValuesFollowTheCLibrary()
    {
        foreach (var form in new[] { 0, 1 })
        {
            object? Call(string name, params object?[] a) => (form == 0 ? Both(name).Native : Both(name).Package)(a);
            Assert.True(double.IsNaN((double)Call("sqrt", -1.0)!));
            Assert.Equal(Inf, Call("sqrt", Inf));
            Assert.True(double.IsNegative((double)Call("sqrt", -0.0)!));
            Assert.True(double.IsNaN((double)Call("asin", 1.5)!));
            Assert.True(double.IsNaN((double)Call("acos", -1.5)!));
            Assert.True(double.IsNaN((double)Call("tan", Inf)!));
            Assert.Equal(Math.PI, (double)Call("atan2", 0.0, -1.0)!);
            Assert.Equal(-Math.PI, (double)Call("atan2", -0.0, -1.0)!);
            Assert.Equal(0.0, (double)Call("atan2", 0.0, 1.0)!);
            Assert.Equal(Math.PI / 2, (double)Call("atan2", 1.0, 0.0)!);
            Assert.Equal(Math.PI / 4, (double)Call("atan2", Inf, Inf)!);
            Assert.Equal(3 * Math.PI / 4, (double)Call("atan2", Inf, -Inf)!, 15);
            Assert.Equal(Inf, Call("hypot", Inf, NaN));
            Assert.True(double.IsNaN((double)Call("hypot", NaN, 1.0)!));
            Assert.Equal(1.0, Call("pow", NaN, 0.0));
            Assert.Equal(1.0, Call("pow", 1.0, NaN));
            Assert.True(double.IsNaN((double)Call("pow", -8.0, 1.0 / 3)!));
            Assert.Equal(Inf, Call("pow", 0.0, -1.0));
            Assert.Equal(double.NegativeInfinity, Call("pow", -0.0, -3.0));
            Assert.Equal(0.0, Call("pow", 2.0, double.NegativeInfinity));
            Assert.Equal(Inf, Call("pow", 2.0, Inf));
            Assert.Equal(1.0, Call("pow", -1.0, Inf));
            Assert.Equal(Inf, Call("pow", 10.0, 400.0));
            Assert.Equal(0.0, Call("pow", 10.0, -400.0));
            Assert.Equal(true, Call("isnan", NaN));
            Assert.Equal(false, Call("isnan", Inf));
            Assert.Equal(true, Call("isinf", double.NegativeInfinity));
            Assert.Equal(false, Call("isinf", 1.0));
            Assert.Equal(false, Call("isnan", System.Numerics.BigInteger.One));
        }
    }

    [Fact]
    public void QuadrantsOfAtan2()
    {
        foreach (var f in new[] { Both("atan2").Native, Both("atan2").Package })
            foreach (var (y, x) in new[] { (1.0, 1.0), (1.0, -1.0), (-1.0, -1.0), (-1.0, 1.0), (0.5, -3.0), (-7.0, -0.1) })
                Assert.True(Ulps((double)f([y, x])!, Math.Atan2(y, x)) <= 8, $"atan2({y}, {x})");
    }

    // ---------------------------------------------------------------- arguments

    [Theory]
    [InlineData("sqrt('x')")] [InlineData("pow(2, None)")] [InlineData("floor([])")] [InlineData("atan2(1, True)")]
    [InlineData("hypot({}, 1)")] [InlineData("isnan('nan')")] [InlineData("degrees(None)")]
    public void WrongTypesAreErrorsInBothForms(string call)
    {
        Assert.Throws<StarlarkException>(() => New().Execute("n.star", "r = " + call + "\n"));
        Assert.Throws<StarlarkException>(() => New().Execute("p.star", Load + "r = " + call + "\n"));
    }

    [Theory]
    [InlineData("sqrt()")] [InlineData("sqrt(1, 2)")] [InlineData("pow(1)")] [InlineData("atan2(1)")] [InlineData("floor(x = 1, y = 2)")]
    public void WrongArgumentCountsAreErrorsInBothForms(string call)
    {
        Assert.Throws<StarlarkException>(() => New().Execute("n.star", "r = " + call + "\n"));
        Assert.Throws<StarlarkException>(() => New().Execute("p.star", Load + "r = " + call + "\n"));
    }

    [Fact]
    public void IntsAreAcceptedWhereFloatsAre()
    {
        Assert.Equal(3.0, New().Execute("n.star", "r = sqrt(9)\n").Globals["r"]);
        Assert.Equal(3.0, New().Execute("p.star", Load + "r = sqrt(9)\n").Globals["r"]);
        Assert.Equal(8.0, New().Execute("n.star", "r = pow(2, 3)\n").Globals["r"]);
        Assert.Equal(5.0, New().Execute("p.star", Load + "r = hypot(3, 4)\n").Globals["r"]);
    }

    [Fact]
    public void TheCheckerKnowsTheNativeFunctions()
    {
        var typed = new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableTypes = DialectTypes.Enable } };
        var ok = typed.Typecheck("t.star", "def f(x: float) -> int:\n    return floor(sqrt(x) + pow(x, 2) + atan2(x, 1) + hypot(x, x) + tan(x) + asin(x) + acos(x))\n");
        Assert.Empty(ok.Errors);
        var bad = typed.Typecheck("t.star", "def f(x: float) -> str:\n    return sqrt(x)\n");
        Assert.NotEmpty(bad.Errors);
    }

    [Fact]
    public void ThePackageStaysWithinTheStepBudgetAndNeedsNoRecursion()
    {
        var it = new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true }, MaxSteps = 200_000 };
        it.Execute("t.star", Load + "r = [tan(1.0), asin(0.5), acos(0.5), atan2(1.0, 2.0), sqrt(2.0), pow(2.0, 0.5), hypot(3.0, 4.0), floor(2.5), ceil(2.5), trunc(2.5), round(2.5), degrees(1.0), radians(1.0)]\n");
    }
}
