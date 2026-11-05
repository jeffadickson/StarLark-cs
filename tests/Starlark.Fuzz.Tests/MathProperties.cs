using System.Numerics;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Fuzz.Tests;

/// <summary>
/// FsCheck properties for the elementary functions, in both forms (the host's native versions and the pure Starlark package
/// <c>std/approx_math.star</c>). FsCheck's own double generator includes nan, both infinities, negative zero and the extremes,
/// so these cover the special values as well as ordinary numbers. Required by the no-crash policy (QA-ROB-001): any argument
/// gives a number or a Starlark error, and the two forms agree.
/// </summary>
public class MathProperties
{
    private static readonly string[] Unary =
        ["sin", "cos", "tan", "asin", "acos", "atan", "exp", "log", "log2", "sqrt", "floor", "ceil", "trunc", "round", "degrees", "radians", "isnan", "isinf"];
    private static readonly string[] Binary = ["atan2", "pow", "hypot", "log"];

    private static readonly Interpreter Native = Make();
    private static readonly Interpreter Package = Make();
    private static readonly Dictionary<string, object?> NativeFns = Native.Execute("n.star", "f = {" + string.Join(", ", Unary.Concat(Binary).Distinct().Select(n => $"'{n}': {n}")) + "}\n").Globals["f"] is StarDict d ? Fns(d) : [];
    private static readonly Dictionary<string, object?> PackageFns = Package.Execute("p.star",
        "load('std/approx_math.star', " + string.Join(", ", Unary.Concat(Binary).Distinct().Select(n => $"'{n}'")) + ")\nf = {" + string.Join(", ", Unary.Concat(Binary).Distinct().Select(n => $"'{n}': {n}")) + "}\n").Globals["f"] is StarDict e ? Fns(e) : [];

    private static Interpreter Make() => new() { Dialect = Dialect.Standard with { EnableTopLevelStmt = true }, MaxSteps = 5_000_000 };

    private static Dictionary<string, object?> Fns(StarDict d) => d.Entries.ToDictionary(kv => (string)kv.Key!, kv => kv.Value);

    /// <summary>Calls the function; returns the result, or null when it raised a Starlark error. Any other exception propagates and fails the property.</summary>
    private static (bool Ok, object? Value) Call(Interpreter it, Dictionary<string, object?> fns, string name, params object?[] args)
    {
        try { return (true, it.CallValue(fns[name], args)); }
        catch (StarlarkException) { return (false, null); }
    }

    private static bool Close(double a, double b)
    {
        if (double.IsNaN(a) || double.IsNaN(b)) return double.IsNaN(a) && double.IsNaN(b);
        if (double.IsInfinity(a) || double.IsInfinity(b)) return a == b;
        return Math.Abs(a - b) <= 1e-12 * Math.Max(Math.Abs(a), Math.Abs(b)) + 1e-300;
    }

    private static bool Same(object? a, object? b) => a switch
    {
        double x when b is double y => Close(x, y),
        _ => Equals(a, b),
    };

    // ---------------------------------------------------------------- never a crash

    /// <summary>Every one-argument function, native and package, returns a value or a Starlark error for any double.</summary>
    [Property(MaxTest = 300)]
    public bool UnaryFunctionsNeverCrashOnDoubles(double x)
    {
        foreach (var name in Unary)
        {
            Call(Native, NativeFns, name, x);
            Call(Package, PackageFns, name, x);
        }
        return true;
    }

    /// <summary>The same for any int, however large, which must convert or be rejected cleanly.</summary>
    [Property(MaxTest = 200)]
    public bool UnaryFunctionsNeverCrashOnInts(long low, int shift)
    {
        var big = new BigInteger(low) << Math.Clamp(Math.Abs(shift % 3000), 0, 3000);
        foreach (var name in Unary)
        {
            Call(Native, NativeFns, name, big);
            Call(Package, PackageFns, name, big);
        }
        return true;
    }

    /// <summary>Every two-argument function, native and package, returns a value or a Starlark error for any pair of doubles.</summary>
    [Property(MaxTest = 400)]
    public bool BinaryFunctionsNeverCrash(double x, double y)
    {
        foreach (var name in Binary)
        {
            Call(Native, NativeFns, name, x, y);
            Call(Package, PackageFns, name, x, y);
        }
        return true;
    }

    /// <summary>Values that are not numbers are Starlark errors, never anything else.</summary>
    [Property(MaxTest = 100)]
    public bool NonNumbersAreStarlarkErrors(NonNull<string> s, bool flag)
    {
        foreach (var name in Unary)
        {
            foreach (var (what, bad) in new (string, object?)[] { ("string", s.Get), ("bool", flag), ("null", null) })
                foreach (var (form, it, fns) in new[] { ("native", Native, NativeFns), ("package", Package, PackageFns) })
                    if (Call(it, fns, name, bad).Ok) throw new Exception($"{form} {name}({what}) {bad} was accepted");
        }
        return true;
    }

    // ---------------------------------------------------------------- native and package agree

    /// <summary>For ordinary arguments the package gives the native answer (to a relative 1e-12), or both raise an error.</summary>
    [Property(MaxTest = 500)]
    public bool PackageAgreesWithNativeOnUnary(double x)
    {
        foreach (var name in Unary)
        {
            // sin, cos and tan lose accuracy past about 1.6 million in the package (documented); tan is ill-conditioned near its poles.
            if ((name is "sin" or "cos" or "tan") && Math.Abs(x) > 1e6) continue;
            if (name == "tan" && Math.Abs(Math.Tan(x)) > 1e6) continue;
            var n = Call(Native, NativeFns, name, x);
            var p = Call(Package, PackageFns, name, x);
            if (n.Ok != p.Ok || (n.Ok && !Same(n.Value, p.Value)))
                throw new Exception($"{name}({x:R}): native {n.Value}, package {p.Value}");
        }
        return true;
    }

    /// <summary>The same for the functions of two arguments, over a range where the package's pow stays accurate.</summary>
    [Property(MaxTest = 500)]
    public bool PackageAgreesWithNativeOnBinary(double x, double y)
    {
        foreach (var name in new[] { "atan2", "hypot" })
        {
            var n = Call(Native, NativeFns, name, x, y);
            var p = Call(Package, PackageFns, name, x, y);
            if (n.Ok != p.Ok || (n.Ok && !Same(n.Value, p.Value)))
                throw new Exception($"{name}({x:R}, {y:R}): native {n.Value}, package {p.Value}");
        }
        // pow and log with a base: only where the result is a normal number, since the package computes pow(x, y) as exp(y * log(x)).
        if (Math.Abs(y) < 50 && x > 1e-3 && x < 1e3)
        {
            var n = Call(Native, NativeFns, "pow", x, y);
            var p = Call(Package, PackageFns, "pow", x, y);
            if (!(n.Ok && p.Ok) || Math.Abs((double)n.Value! - (double)p.Value!) > 1e-10 * Math.Abs((double)n.Value!) + 1e-300)
                throw new Exception($"pow({x:R}, {y:R}): native {n.Value}, package {p.Value}");
        }
        return true;
    }

    // ---------------------------------------------------------------- identities

    /// <summary>sin² + cos² = 1 for the native and the package sine and cosine.</summary>
    [Property(MaxTest = 300)]
    public bool PythagoreanIdentity(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x) || Math.Abs(x) > 1e6) return true;
        foreach (var (it, fns) in new[] { (Native, NativeFns), (Package, PackageFns) })
        {
            double s = (double)Call(it, fns, "sin", x).Value!, c = (double)Call(it, fns, "cos", x).Value!;
            if (Math.Abs(s * s + c * c - 1) > 1e-12) return false;
        }
        return true;
    }

    /// <summary>floor(x) &lt;= x &lt;= ceil(x), trunc is toward zero, and round is within a half of x (native and package).</summary>
    [Property(MaxTest = 300)]
    public bool RoundingBracketsTheValue(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x)) return true;
        foreach (var (it, fns) in new[] { (Native, NativeFns), (Package, PackageFns) })
        {
            var fl = (BigInteger)Call(it, fns, "floor", x).Value!;
            var ce = (BigInteger)Call(it, fns, "ceil", x).Value!;
            var tr = (BigInteger)Call(it, fns, "trunc", x).Value!;
            var ro = (BigInteger)Call(it, fns, "round", x).Value!;
            if (fl != new BigInteger(Math.Floor(x)) || ce < fl || ce - fl > 1) throw new Exception($"floor/ceil of {x:R}: {fl}, {ce}");
            if (tr != (x < 0 ? ce : fl)) throw new Exception($"trunc of {x:R}: {tr}, floor {fl}, ceil {ce}");
            if (Math.Abs((double)ro - x) > 0.5) throw new Exception($"round of {x:R} is {ro}, more than a half away");
        }
        return true;
    }

    /// <summary>sqrt(x)² is x to within rounding, for the native and the package square root.</summary>
    [Property(MaxTest = 300)]
    public bool SqrtSquaresBack(PositiveInt n, double scale)
    {
        double x = n.Get * (double.IsFinite(scale) ? Math.Pow(10, Math.Clamp(Math.Round(scale) % 100, -100, 100)) : 1);
        foreach (var (it, fns) in new[] { (Native, NativeFns), (Package, PackageFns) })
        {
            double r = (double)Call(it, fns, "sqrt", x).Value!;
            if (Math.Abs(r * r - x) > 1e-15 * x) throw new Exception($"sqrt({x:R}) = {r:R}, squared {r * r:R}");
        }
        return true;
    }

    /// <summary>atan2(y, x) lies in [-pi, pi] and has the sign of y, for the native and the package version.</summary>
    [Property(MaxTest = 300)]
    public bool Atan2StaysInRange(double y, double x)
    {
        if (double.IsNaN(y) || double.IsNaN(x)) return true;
        foreach (var (it, fns) in new[] { (Native, NativeFns), (Package, PackageFns) })
        {
            double a = (double)Call(it, fns, "atan2", y, x).Value!;
            if (a < -Math.PI - 1e-15 || a > Math.PI + 1e-15) return false;
            if (y > 0 && a < 0 || y < 0 && a > 0) return false;
        }
        return true;
    }
}
