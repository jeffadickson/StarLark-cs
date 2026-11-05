using System.Numerics;

namespace Starlark.Eval;

/// <summary>
/// The fast elementary functions, backed by <see cref="Math"/>: <c>sin</c>, <c>cos</c>, <c>exp</c>, <c>log</c>,
/// <c>log2</c> and <c>atan</c>. They are the host's fast implementation; the pure Starlark package <c>std/approx_math.star</c> offers the same names
/// as a portable alternative that a script loads explicitly (a loaded name hides the predeclared one).
/// </summary>
/// <remarks>
/// They follow the C and Go math libraries rather than Python's: <c>log(0)</c> is <c>-inf</c>, the log of a negative number
/// and <c>sin(inf)</c> are <c>nan</c>, and <c>exp</c> overflows to <c>inf</c>. None of those is an error. A value that is not an
/// int or a float is an error, and so is an int too large for a float.
/// </remarks>
public static class InternalMath
{
    /// <summary>Predeclares the six functions in <paramref name="universe"/>.</summary>
    public static void Install(Dictionary<string, object?> universe)
    {
        Unary(universe, "sin", Math.Sin);
        Unary(universe, "cos", Math.Cos);
        Unary(universe, "exp", Math.Exp);
        Unary(universe, "log2", Math.Log2);
        Unary(universe, "atan", Math.Atan);

        // log(x) is the natural logarithm; log(x, base) is log(x) / log(base), and base 1 is a division by zero as in Go.
        universe["log"] = new Builtin("log", (_, args, kwargs) =>
        {
            var r = new ArgReader("log", args, kwargs);
            double x = Number("x", r.Required("x"));
            double result;
            if (r.Has("base"))
            {
                double b = Number("base", r.Required("base"));
                if (b == 1.0) throw new StarlarkException("log: division by zero");
                result = Math.Log(x) / Math.Log(b);
            }
            else
            {
                result = Math.Log(x);
            }
            r.Done();
            return result;
        });
    }

    private static void Unary(Dictionary<string, object?> universe, string name, Func<double, double> function) =>
        universe[name] = new Builtin(name, (_, args, kwargs) =>
        {
            var r = new ArgReader(name, args, kwargs);
            double x = Number("x", r.Required("x"));
            r.Done();
            return function(x);
        });

    /// <summary>An int or a float as a double; anything else is an error naming the parameter.</summary>
    private static double Number(string parameter, object? value)
    {
        switch (value)
        {
            case double d:
                return d;
            case BigInteger i:
                double f = Ops.BigToDouble(i);
                if (double.IsInfinity(f)) throw new StarlarkException("int too large to convert to float");
                return f;
            default:
                throw Err.Param(parameter, "int or float", value);
        }
    }
}
