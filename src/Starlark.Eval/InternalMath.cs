using System.Numerics;

namespace Starlark.Eval;

/// <summary>
/// The fast elementary functions, backed by <see cref="Math"/>: <c>_sin</c>, <c>_cos</c>, <c>_exp</c>, <c>_log</c>,
/// <c>_log2</c> and <c>_atan</c>. The leading underscore marks them as internal: they are the host's own implementation, meant
/// to be called by library code (the pure Starlark package <c>std/approx_math.star</c> exports the public names), not by ordinary scripts.
/// </summary>
/// <remarks>
/// They follow the C and Go math libraries rather than Python's: <c>_log(0)</c> is <c>-inf</c>, the log of a negative number
/// and <c>_sin(inf)</c> are <c>nan</c>, and <c>_exp</c> overflows to <c>inf</c>. None of those is an error. A value that is not an
/// int or a float is an error, and so is an int too large for a float.
/// </remarks>
public static class InternalMath
{
    /// <summary>Predeclares the six functions in <paramref name="universe"/>.</summary>
    public static void Install(Dictionary<string, object?> universe)
    {
        Unary(universe, "_sin", Math.Sin);
        Unary(universe, "_cos", Math.Cos);
        Unary(universe, "_exp", Math.Exp);
        Unary(universe, "_log2", Math.Log2);
        Unary(universe, "_atan", Math.Atan);

        // _log(x) is the natural logarithm; _log(x, base) is log(x) / log(base), and base 1 is a division by zero as in Go.
        universe["_log"] = new Builtin("_log", (_, args, kwargs) =>
        {
            var r = new ArgReader("_log", args, kwargs);
            double x = Number("x", r.Required("x"));
            double result;
            if (r.Has("base"))
            {
                double b = Number("base", r.Required("base"));
                if (b == 1.0) throw new StarlarkException("_log: division by zero");
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
