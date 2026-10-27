using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Text;

namespace Starlark.Eval;

/// <summary>
/// Automatic conversion between CLR objects and Starlark values, so a host does not have to build
/// <see cref="StarList"/>, <see cref="StarDict"/> and <see cref="StarStruct"/> by hand.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ToStar"/> maps numbers, strings, enums (to their names), byte arrays, dictionaries, sequences and
/// delegates. Any other object becomes a <see cref="StarStruct"/> of its public readable properties, converted
/// recursively. The result is a snapshot: later changes to the object are not seen by the script.
/// </para>
/// <para>
/// <see cref="FromStar"/> goes the other way, converting a Starlark value to a requested CLR type. Structs, records and
/// dicts bind to a class or <c>record</c> by property name (matching either the exact name or its snake_case form).
/// </para>
/// </remarks>
public static class Interop
{
    private const int MaxDepth = 32;

    /// <summary>Converts a PascalCase or camelCase identifier to snake_case: <c>MaxRetryCount</c> becomes <c>max_retry_count</c>.</summary>
    public static string SnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c))
            {
                bool startsWord = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])
                    || (char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1])));
                if (startsWord) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>Converts a CLR value to a Starlark value.</summary>
    /// <param name="value">The value to convert.</param>
    /// <param name="snakeCase">Whether property names of converted objects become snake_case (the default).</param>
    public static object? ToStar(object? value, bool snakeCase = true) => ToStar(value, snakeCase, 0);

    private static object? ToStar(object? value, bool snake, int depth)
    {
        if (depth > MaxDepth) throw new StarlarkException("Cannot convert host object: nesting is too deep (cyclic?)");
        switch (value)
        {
            case null: return null;
            case bool or string or double or BigInteger: return value;
            case IHostValue:
                return value;   // a host-defined Starlark value
            case StarUtf16 or StarBytes or StarList or StarTuple or StarDict or StarSet or StarStruct or StarRecord
                or StarEnumValue or StarRange or ICallable:
                return value;   // already a Starlark value
            case sbyte or byte or short or ushort or int or uint or long or ulong: return new BigInteger(Convert.ToDecimal(value));
            case Int128 i128: return (BigInteger)i128;
            case UInt128 u128: return (BigInteger)u128;
            case float f: return (double)f;
            case decimal m: return (double)m;
            case char c: return c.ToString();
            case Enum e: return e.ToString();
            case DateTime dt: return dt.ToString("o");
            case DateTimeOffset dto: return dto.ToString("o");
            case TimeSpan ts: return ts.TotalSeconds;
            case Guid g: return g.ToString();
            case byte[] bytes: return new StarBytes(bytes);
            case Delegate d: return FromDelegate(d, snake);
            case IDictionary dict:
            {
                var result = new StarDict();
                foreach (DictionaryEntry kv in dict) result.Set(ToStar(kv.Key, snake, depth + 1), ToStar(kv.Value, snake, depth + 1));
                return result;
            }
            case IEnumerable seq:
            {
                var items = new List<object?>();
                foreach (var x in seq) items.Add(ToStar(x, snake, depth + 1));
                return new StarList(items);
            }
        }
        var type = value.GetType();
        if (type.IsGenericType && type.FullName!.StartsWith("System.ValueTuple", StringComparison.Ordinal))
        {
            return new StarTuple(type.GetFields().Select(f => ToStar(f.GetValue(value), snake, depth + 1)).ToArray());
        }
        return new StarStruct(Properties(type).Select(p =>
            new KeyValuePair<string, object?>(snake ? SnakeCase(p.Name) : p.Name, ToStar(p.GetValue(value), snake, depth + 1))));
    }

    private static IEnumerable<PropertyInfo> Properties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0);

    /// <summary>Wraps a delegate as a named callable Starlark function, converting arguments and result.</summary>
    public static Builtin Wrap(string name, Delegate d) =>
        new(name, (interp, args, kwargs) => Invoke(interp, d.Target, d.Method, args, kwargs, true));

    /// <summary>Wraps a delegate as a callable Starlark function, converting arguments and result.</summary>
    private static Builtin FromDelegate(Delegate d, bool snake) =>
        new(d.Method.Name, (interp, args, kwargs) => Invoke(interp, d.Target, d.Method, args, kwargs, snake));

    private static object? Invoke(Interpreter interp, object? target, MethodBase method, IReadOnlyList<object?> args,
        IReadOnlyList<KeyValuePair<string, object?>> kwargs, bool snake)
    {
        var parameters = method.GetParameters();
        var bound = new object?[parameters.Length];
        var set = new bool[parameters.Length];

        // An Interpreter or CancellationToken parameter is supplied by the host, not by the script.
        var visible = new List<int>();
        for (int i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].ParameterType == typeof(Interpreter))
            {
                bound[i] = interp;
                set[i] = true;
            }
            else if (parameters[i].ParameterType == typeof(CancellationToken))
            {
                bound[i] = ExecutionContext.Current?.Token ?? interp.Cancellation;
                set[i] = true;
            }
            else
            {
                visible.Add(i);
            }
        }
        if (args.Count > visible.Count)
        {
            throw new StarlarkException($"Too many positional arguments to `{method.Name}`: got {args.Count}, want at most {visible.Count}");
        }
        for (int j = 0; j < args.Count; j++)
        {
            int i = visible[j];
            bound[i] = FromStar(args[j], parameters[i].ParameterType);
            set[i] = true;
        }
        foreach (var (name, value) in kwargs)
        {
            int index = visible.FirstOrDefault(i => parameters[i].Name == name || (parameters[i].Name is not null && SnakeCase(parameters[i].Name!) == name), -1);
            if (index < 0) throw new StarlarkException($"Unexpected parameter named `{name}` for call to `{method.Name}`");
            if (set[index]) throw new StarlarkException($"Parameter `{name}` was given twice in call to `{method.Name}`");
            bound[index] = FromStar(value, parameters[index].ParameterType);
            set[index] = true;
        }
        for (int i = 0; i < parameters.Length; i++)
        {
            if (set[i]) continue;
            if (!parameters[i].HasDefaultValue)
            {
                throw new StarlarkException($"Missing parameter `{parameters[i].Name}` for call to `{method.Name}`");
            }
            bound[i] = parameters[i].DefaultValue is DBNull ? null : parameters[i].DefaultValue;
        }
        try
        {
            var result = method.Invoke(target, bound);
            if (result is ValueTask vt) result = vt.AsTask();
            else if (result is not null && result.GetType() is { IsGenericType: true } rt
                     && rt.GetGenericTypeDefinition() == typeof(ValueTask<>))
            {
                result = rt.GetMethod("AsTask")!.Invoke(result, null);
            }
            if (result is Task task)
            {
                interp.AwaitHostTask(task);
                result = task.GetType().IsGenericType && task.GetType().GetProperty("Result") is { } p
                    && p.PropertyType.Name != "VoidTaskResult" ? p.GetValue(task) : null;
            }
            return ToStar(result, snake);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            if (e.InnerException is StarlarkException or ExecutionLimitException or OperationCanceledException)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            }
            throw new StarlarkException($"Host function `{method.Name}` failed: {e.InnerException.Message}");
        }
    }

    /// <summary>
    /// Exposes a host object to scripts as a struct: public properties become fields (a snapshot taken now) and public
    /// methods become live calls on the object. Names are snake_case unless <paramref name="snakeCase"/> is false.
    /// </summary>
    public static StarStruct Expose(object target, bool snakeCase = true)
    {
        var fields = new List<KeyValuePair<string, object?>>();
        var type = target.GetType();
        foreach (var p in Properties(type))
        {
            fields.Add(new(snakeCase ? SnakeCase(p.Name) : p.Name, ToStar(p.GetValue(target), snakeCase)));
        }
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                     .Where(m => !m.IsSpecialName))
        {
            var method = m;
            fields.Add(new(snakeCase ? SnakeCase(m.Name) : m.Name,
                new Builtin(m.Name, (interp, args, kwargs) => Invoke(interp, target, method, args, kwargs, snakeCase))));
        }
        return new StarStruct(fields);
    }

    /// <summary>Converts a Starlark value to a CLR value of the given type.</summary>
    /// <exception cref="StarlarkException">The value cannot be represented as <paramref name="type"/>.</exception>
    public static object? FromStar(object? value, Type type)
    {
        if (type == typeof(object)) return FromStarNatural(value);
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            return value is null ? null : FromStar(value, underlying);
        }
        if (value is null)
        {
            if (type.IsValueType) throw Mismatch(value, type);
            return null;
        }
        if (type.IsInstanceOfType(value) && !type.IsPrimitive) return value;

        if (type == typeof(bool)) return value is bool b ? b : throw Mismatch(value, type);
        if (type == typeof(string)) return value as string ?? (value is StarUtf16 u ? u.Value : value is StarEnumValue { Value: string ev } ? ev : throw Mismatch(value, type));
        if (type == typeof(char)) return value is string { Length: 1 } s ? s[0] : throw Mismatch(value, type);
        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
        {
            double d = value switch { double x => x, BigInteger i => Ops.BigToDouble(i), _ => throw Mismatch(value, type) };
            return type == typeof(double) ? d : type == typeof(float) ? (float)d : (decimal)d;
        }
        if (type.IsEnum)
        {
            if (value is string name && Enum.TryParse(type, name, ignoreCase: false, out var e)) return e;
            if (value is StarEnumValue ev && Enum.TryParse(type, ev.Value?.ToString(), out var e2)) return e2;
            throw Mismatch(value, type);
        }
        if (type == typeof(BigInteger)) return value is BigInteger bi ? bi : throw Mismatch(value, type);
        if (IsInteger(type))
        {
            if (value is not BigInteger n) throw Mismatch(value, type);
            var (min, max) = IntegerRange(type);
            if (n < min || n > max) throw new StarlarkException($"Value {n} does not fit in {type.Name}");
            return type == typeof(int) ? (int)n : type == typeof(long) ? (long)n : type == typeof(short) ? (short)n
                : type == typeof(byte) ? (byte)n : type == typeof(sbyte) ? (sbyte)n : type == typeof(uint) ? (uint)n
                : type == typeof(ulong) ? (ulong)n : (ushort)n;
        }
        if (type == typeof(byte[])) return value is StarBytes sb ? sb.Value : throw Mismatch(value, type);
        if (type == typeof(TimeSpan)) return value is double or BigInteger ? TimeSpan.FromSeconds((double)FromStar(value, typeof(double))!) : throw Mismatch(value, type);
        if (type == typeof(DateTime)) return value is string dts ? DateTime.Parse(dts, null, System.Globalization.DateTimeStyles.RoundtripKind) : throw Mismatch(value, type);
        if (type == typeof(Guid)) return value is string gs && Guid.TryParse(gs, out var g) ? g : throw Mismatch(value, type);

        if (type.IsArray)
        {
            var elem = type.GetElementType()!;
            var items = SequenceOf(value, type);
            var arr = Array.CreateInstance(elem, items.Count);
            for (int i = 0; i < items.Count; i++) arr.SetValue(FromStar(items[i], elem), i);
            return arr;
        }
        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments();
            if (def == typeof(Dictionary<,>) || def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>))
            {
                if (value is not StarDict sd) throw Mismatch(value, type);
                var dict = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(args))!;
                foreach (var (k, v) in sd.Entries) dict.Add(FromStar(k, args[0])!, FromStar(v, args[1]));
                return dict;
            }
            if (def == typeof(List<>) || def == typeof(IList<>) || def == typeof(IEnumerable<>) || def == typeof(IReadOnlyList<>)
                || def == typeof(ICollection<>) || def == typeof(IReadOnlyCollection<>))
            {
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(args))!;
                foreach (var x in SequenceOf(value, type)) list.Add(FromStar(x, args[0]));
                return list;
            }
            if (def == typeof(HashSet<>) || def == typeof(ISet<>))
            {
                var set = Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(args))!;
                var add = set.GetType().GetMethod("Add")!;
                foreach (var x in SequenceOf(value, type)) add.Invoke(set, [FromStar(x, args[0])]);
                return set;
            }
        }
        return Bind(value, type);
    }

    /// <summary>Converts a Starlark value to the natural CLR type: long or BigInteger, double, string, List, Dictionary.</summary>
    public static object? FromStarNatural(object? value) => value switch
    {
        null or bool or string or double => value,
        BigInteger i => i >= long.MinValue && i <= long.MaxValue ? (long)i : i,
        StarUtf16 u => u.Value,
        StarBytes b => b.Value,
        StarList l => l.Items.Select(FromStarNatural).ToList(),
        StarTuple t => t.Items.Select(FromStarNatural).ToList(),
        StarSet s => s.Items.Select(FromStarNatural).ToList(),
        StarDict d => d.Entries.ToDictionary(kv => FromStarNatural(kv.Key)!, kv => FromStarNatural(kv.Value)),
        StarStruct st => st.Fields.ToDictionary(kv => kv.Key, kv => FromStarNatural(kv.Value)),
        StarRecord r => r.Type.Fields.Select((fl, i) => (fl.Name, Value: r.Values[i])).ToDictionary(x => x.Name, x => FromStarNatural(x.Value)),
        StarEnumValue e => e.Value,
        _ => value,
    };

    private static (BigInteger Min, BigInteger Max) IntegerRange(Type t) =>
        t == typeof(int) ? (int.MinValue, int.MaxValue) : t == typeof(long) ? (long.MinValue, long.MaxValue)
        : t == typeof(short) ? (short.MinValue, short.MaxValue) : t == typeof(byte) ? (byte.MinValue, byte.MaxValue)
        : t == typeof(sbyte) ? (sbyte.MinValue, sbyte.MaxValue) : t == typeof(uint) ? (uint.MinValue, uint.MaxValue)
        : t == typeof(ulong) ? (ulong.MinValue, ulong.MaxValue) : (ushort.MinValue, ushort.MaxValue);

    private static bool IsInteger(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) || t == typeof(sbyte)
        || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort);

    private static List<object?> SequenceOf(object? value, Type wanted) => value switch
    {
        StarList l => [.. l.Items],
        StarTuple t => [.. t.Items],
        StarSet s => [.. s.Items],
        _ => throw Mismatch(value, wanted),
    };

    /// <summary>Builds an instance of <paramref name="type"/> from a struct, record or string-keyed dict.</summary>
    private static object? Bind(object? value, Type type)
    {
        var source = new Dictionary<string, object?>();
        switch (value)
        {
            case StarStruct st: foreach (var kv in st.Fields) source[kv.Key] = kv.Value; break;
            case StarRecord r: for (int i = 0; i < r.Type.Fields.Count; i++) source[r.Type.Fields[i].Name] = r.Values[i]; break;
            case StarDict d:
                foreach (var (k, v) in d.Entries)
                {
                    if (k is not string key) throw Mismatch(value, type);
                    source[key] = v;
                }
                break;
            default: throw Mismatch(value, type);
        }

        bool Lookup(string name, out object? found) => source.TryGetValue(name, out found) || source.TryGetValue(SnakeCase(name), out found);

        // Prefer the constructor with the most parameters (a record's primary constructor).
        var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault()
                   ?? throw new StarlarkException($"Cannot convert to {type.Name}: it has no public constructor");
        var ps = ctor.GetParameters();
        var args = new object?[ps.Length];
        var used = new HashSet<string>();
        for (int i = 0; i < ps.Length; i++)
        {
            if (Lookup(ps[i].Name!, out var found))
            {
                args[i] = FromStar(found, ps[i].ParameterType);
                used.Add(ps[i].Name!);
            }
            else if (ps[i].HasDefaultValue)
            {
                args[i] = ps[i].DefaultValue is DBNull ? null : ps[i].DefaultValue;
            }
            else if (!ps[i].ParameterType.IsValueType || Nullable.GetUnderlyingType(ps[i].ParameterType) is not null)
            {
                args[i] = null;
            }
            else
            {
                throw new StarlarkException($"Cannot convert to {type.Name}: missing field `{SnakeCase(ps[i].Name!)}`");
            }
        }
        var instance = ctor.Invoke(args);
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite && p.SetMethod!.IsPublic))
        {
            if (used.Contains(p.Name) || ps.Any(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase))) continue;
            if (Lookup(p.Name, out var found)) p.SetValue(instance, FromStar(found, p.PropertyType));
        }
        return instance;
    }

    private static StarlarkException Mismatch(object? value, Type type) =>
        new($"Cannot convert a value of type '{Types.Name(value)}' to the host type {type.Name}");
}
