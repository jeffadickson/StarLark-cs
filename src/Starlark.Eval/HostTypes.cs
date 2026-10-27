using System.Collections;
using System.Numerics;
using System.Reflection;
using Starlark.Eval.TypeCheck;

namespace Starlark.Eval;

/// <summary>
/// Maps .NET types to the static checker's <see cref="Ty"/>, so calls to registered host functions and uses of exposed
/// objects are checked before a script runs. Parameter types are as permissive as the run-time conversion in
/// <see cref="Interop.FromStar"/> (an int is accepted where a double is wanted, a str or utf16 where a string is
/// wanted), so the checker never rejects a call that would work.
/// </summary>
public static class HostTypes
{
    private const int MaxDepth = 4;

    /// <summary>The checker signature of a delegate: its script-visible parameters and result.</summary>
    public static Signature SignatureOf(string name, MethodBase method)
    {
        var ps = new List<SigParam>();
        foreach (var p in method.GetParameters())
        {
            if (p.ParameterType == typeof(Interpreter) || p.ParameterType == typeof(CancellationToken)) continue;
            string pname = p.Name ?? "arg";
            string snake = Interop.SnakeCase(pname);
            ps.Add(new SigParam(snake, ParamMode.PosOrName, ForParameter(p.ParameterType), !p.HasDefaultValue)
            {
                Alias = snake == pname ? null : pname,
            });
        }
        var result = method is MethodInfo mi ? ForResult(mi.ReturnType, 0) : Ty.Any;
        return new Signature(ps, result, null);
    }

    /// <summary>The checker type of a host object exposed with <see cref="Interop.Expose"/>: a struct of its members.</summary>
    public static Ty ForExposed(object target) => Ty0(target.GetType());

    private static Ty Ty0(Type type)
    {
        var fields = new List<KeyValuePair<string, Ty>>();
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
        {
            fields.Add(new(Interop.SnakeCase(p.Name), ForResult(p.PropertyType, 1)));
        }
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName))
        {
            var sig = SignatureOf(m.Name, m);
            fields.Add(new(Interop.SnakeCase(m.Name), new Ty.CallableTy(sig.Params.Select(p => p.Type).ToList(), sig.Result)));
        }
        return new Ty.StructTy(fields);
    }

    /// <summary>The type of a delegate as a value: a callable with known parameter and result types.</summary>
    public static Ty ForDelegate(Delegate d)
    {
        var sig = SignatureOf(d.Method.Name, d.Method);
        return new Ty.CallableTy(sig.Params.Select(p => p.Type).ToList(), sig.Result);
    }

    /// <summary>What a script may pass for a parameter of the given .NET type.</summary>
    public static Ty ForParameter(Type t)
    {
        if (Nullable.GetUnderlyingType(t) is { } inner) return Ty.Union([ForParameter(inner), Ty.None]);
        if (t == typeof(bool)) return Ty.Bool;
        if (t == typeof(string)) return Ty.Union([Ty.Str, Ty.Utf16]);
        if (t == typeof(char) || t == typeof(Guid) || t == typeof(DateTime)) return Ty.Str;
        if (t == typeof(double) || t == typeof(float) || t == typeof(decimal) || t == typeof(TimeSpan)) return Ty.Union([Ty.Float, Ty.Int]);
        if (IsInteger(t)) return Ty.Int;
        if (t == typeof(byte[])) return Ty.Bytes;
        if (t.IsEnum) return Ty.Any;   // a name or an enum value
        if (t.IsArray) return Sequence(ForParameter(t.GetElementType()!));
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition();
            var a = t.GetGenericArguments();
            if (def == typeof(Dictionary<,>) || def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>))
            {
                return new Ty.DictTy(ForParameter(a[0]), ForParameter(a[1]));
            }
            if (def == typeof(List<>) || def == typeof(IList<>) || def == typeof(IEnumerable<>) || def == typeof(IReadOnlyList<>)
                || def == typeof(ICollection<>) || def == typeof(IReadOnlyCollection<>) || def == typeof(HashSet<>) || def == typeof(ISet<>))
            {
                return Sequence(ForParameter(a[0]));
            }
        }
        return Ty.Any;   // object, classes and records (bound from struct, record or dict), delegates
    }

    private static Ty Sequence(Ty item) => Ty.Union([new Ty.ListTy(item), new Ty.TupleOfTy(item), new Ty.SetTy(item)]);

    /// <summary>What a script receives from a value of the given .NET type, mirroring <see cref="Interop.ToStar"/>.</summary>
    public static Ty ForResult(Type t, int depth)
    {
        if (t == typeof(void) || t == typeof(Task) || t == typeof(ValueTask)) return Ty.None;
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition();
            if (def == typeof(Task<>) || def == typeof(ValueTask<>)) return ForResult(t.GetGenericArguments()[0], depth);
        }
        if (Nullable.GetUnderlyingType(t) is { } inner) return Ty.Union([ForResult(inner, depth), Ty.None]);
        if (t == typeof(bool)) return Ty.Bool;
        if (t == typeof(string) || t == typeof(char) || t == typeof(Guid) || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t.IsEnum) return Ty.Str;
        if (t == typeof(double) || t == typeof(float) || t == typeof(decimal) || t == typeof(TimeSpan)) return Ty.Float;
        if (IsInteger(t) || t == typeof(BigInteger) || t == typeof(Int128) || t == typeof(UInt128)) return Ty.Int;
        if (t == typeof(byte[])) return Ty.Bytes;
        if (typeof(IHostValue).IsAssignableFrom(t) || t == typeof(object)) return Ty.Any;
        if (typeof(Delegate).IsAssignableFrom(t)) return Ty.Callable;
        if (t.IsArray) return new Ty.ListTy(ForResult(t.GetElementType()!, depth + 1));
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition();
            var a = t.GetGenericArguments();
            if (typeof(IDictionary).IsAssignableFrom(t) || def == typeof(IReadOnlyDictionary<,>))
            {
                return new Ty.DictTy(ForResult(a[0], depth + 1), ForResult(a[1], depth + 1));
            }
            if (typeof(IEnumerable).IsAssignableFrom(t) && a.Length == 1) return new Ty.ListTy(ForResult(a[0], depth + 1));
        }
        if (t.FullName?.StartsWith("System.ValueTuple", StringComparison.Ordinal) == true) return Ty.Tuple;
        if (typeof(IEnumerable).IsAssignableFrom(t) || !t.IsClass && !t.IsValueType) return Ty.Any;
        if (depth >= MaxDepth) return Ty.Any;
        // Any other object becomes a struct of its public readable properties.
        var fields = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .Select(p => new KeyValuePair<string, Ty>(Interop.SnakeCase(p.Name), ForResult(p.PropertyType, depth + 1)))
            .ToList();
        return new Ty.StructTy(fields);
    }

    private static bool IsInteger(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) || t == typeof(sbyte)
        || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort);
}
