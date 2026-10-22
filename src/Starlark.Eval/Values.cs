using System.Numerics;
using System.Text;
using Starlark.Syntax;

namespace Starlark.Eval;

/// <summary>
/// Starlark values are plain CLR objects: <c>null</c> = None, <see cref="bool"/>, <see cref="BigInteger"/> (int),
/// <see cref="double"/>, <see cref="string"/>, <see cref="StarBytes"/>, <see cref="StarList"/>,
/// <see cref="StarTuple"/>, <see cref="StarDict"/>, <see cref="StarSet"/>, <see cref="StarRange"/> and
/// <see cref="ICallable"/>. Strings are sequences of UTF-16 code units (the spec's K = 16).
/// </summary>
public sealed class StarlarkException : Exception
{
    public Span? Span { get; private set; }
    public CodeMap? CodeMap { get; private set; }

    /// <summary>The bare message without a source snippet.</summary>
    public string RawMessage { get; }

    public StarlarkException(string message) : base(message) => RawMessage = message;

    public override string Message =>
        Span is { } s && CodeMap is { } cm ? SnippetRenderer.Render(RawMessage, cm, s) : RawMessage;

    /// <summary>
    /// Attach a location the first time the exception passes through a node with one. Always returns false so it
    /// can be used as an exception filter: the location is recorded during the first pass, with no catch/rethrow
    /// (and no unwinding cost) at each interpreter frame.
    /// </summary>
    internal bool AttachLocation(Span span, CodeMap cm)
    {
        if (Span is null)
        {
            Span = span;
            CodeMap = cm;
        }
        return false;
    }
}

/// <summary>Constructors for the error messages shared across the runtime.</summary>
internal static class Err
{
    public static StarlarkException Unsupported(string op, object? a, object? b) =>
        new($"Operation `{op}` not supported on types '{Types.Name(a)}' and '{Types.Name(b)}'");

    public static StarlarkException UnsupportedUnary(string op, object? a) =>
        new($"Operation `{op}` not supported on type '{Types.Name(a)}'");

    public static StarlarkException NotIterable(object? v) =>
        new($"Operation `iter()` not supported on type '{Types.Name(v)}': value is not iterable");

    public static StarlarkException Param(string name, string expected, object? actual) =>
        new($"Type of parameter `{name}` doesn't match, expected `{expected}`, actual `{Types.Name(actual)}` (got {Types.Name(actual)}, want {expected})");

    /// <summary>An index or slice bound of the wrong type.</summary>
    public static StarlarkException BadIndex(string what, object? actual) =>
        new($"Operation not supported: {what} must be an int (got {Types.Name(actual)}, want int)");
}

public sealed class StarBytes(byte[] value)
{
    public byte[] Value { get; } = value;
    public override bool Equals(object? obj) => obj is StarBytes b && Value.AsSpan().SequenceEqual(b.Value);
    public override int GetHashCode() => Hashing.Fnv1a(Value);
}

public sealed class StarTuple(object?[] items)
{
    public object?[] Items { get; } = items;
    public static readonly StarTuple Empty = new([]);
}

/// <summary>The opaque iterable returned by <c>string.elems()</c> and <c>bytes.elems()</c>.</summary>
public sealed class StarElems(object source)
{
    public object Source { get; } = source;
    public string TypeName => Source switch
    {
        string => "string.elems",
        StarUtf16 => "utf16.elems",
        _ => "bytes.elems",
    };

    public IEnumerable<object?> Items => Source switch
    {
        string s => s.Select(c => (object?)c.ToString()),
        StarUtf16 u => u.Value.Select(c => (object?)new StarUtf16(c.ToString())),
        StarBytes b => b.Value.Select(x => (object?)new BigInteger(x)),
        _ => [],
    };
}

public sealed class StarRange(BigInteger start, BigInteger stop, BigInteger step)
{
    public BigInteger Start { get; } = start;
    public BigInteger Stop { get; } = stop;
    public BigInteger Step { get; } = step;

    public BigInteger Count
    {
        get
        {
            if (Step > 0)
            {
                return Start >= Stop ? 0 : (Stop - Start + Step - 1) / Step;
            }
            return Start <= Stop ? 0 : (Start - Stop - Step - 1) / -Step;
        }
    }

    public BigInteger this[BigInteger i] => Start + Step * i;
}

/// <summary>Mutable containers share freezing and iteration-lock behaviour.</summary>
public abstract class Mutable
{
    public bool Frozen { get; private set; }
    private int _iterating;

    public virtual void Freeze() => Frozen = true;

    protected void CheckMutable(string what)
    {
        if (Frozen)
        {
            throw new StarlarkException($"Immutable {what}: cannot modify a frozen value");
        }
        if (_iterating > 0)
        {
            throw new StarlarkException(
                $"Cannot mutate an iterable for an iterator while iterating: the {what} is temporarily immutable");
        }
    }

    internal void BeginIter() => _iterating++;
    internal void EndIter() => _iterating--;
}

public sealed class StarList : Mutable
{
    private readonly List<object?> _items;

    public StarList() => _items = [];
    public StarList(IEnumerable<object?> items) => _items = [.. items];

    public int Count => _items.Count;
    public object? this[int i] => _items[i];
    public IReadOnlyList<object?> Items => _items;

    public override void Freeze()
    {
        if (!Frozen)
        {
            base.Freeze();
            foreach (var x in _items) Freezer.Freeze(x);
        }
    }

    public void Add(object? x) { CheckMutable("list"); _items.Add(x); }
    public void AddRange(IEnumerable<object?> xs) { var l = xs.ToList(); CheckMutable("list"); _items.AddRange(l); }
    public void Insert(int i, object? x) { CheckMutable("list"); _items.Insert(i, x); }
    public void RemoveAt(int i) { CheckMutable("list"); _items.RemoveAt(i); }
    public void Clear() { CheckMutable("list"); _items.Clear(); }
    public void Set(int i, object? x) { CheckMutable("list"); _items[i] = x; }
}

internal sealed class NoneKey
{
    public static readonly NoneKey Instance = new();
}

public sealed class StarDict : Mutable
{
    private readonly OrderedDictionary<object, object?> _map = new(ValueComparer.Instance);

    private static object K(object? key)
    {
        Hashing.CheckHashable(key);
        return key ?? NoneKey.Instance;
    }

    private static object? Unk(object k) => k is NoneKey ? null : k;

    public int Count => _map.Count;

    public IEnumerable<KeyValuePair<object?, object?>> Entries =>
        _map.Select(kv => new KeyValuePair<object?, object?>(Unk(kv.Key), kv.Value));

    public IEnumerable<object?> Keys => _map.Keys.Select(Unk);
    public IEnumerable<object?> Values => _map.Values;

    public bool TryGet(object? key, out object? value) => _map.TryGetValue(K(key), out value);
    public bool ContainsKey(object? key) => _map.ContainsKey(K(key));

    public void Set(object? key, object? value)
    {
        var k = K(key);
        CheckMutable("dict");
        _map[k] = value;
    }

    public bool Remove(object? key, out object? value)
    {
        var k = K(key);
        CheckMutable("dict");
        return _map.Remove(k, out value);
    }

    public void Clear() { CheckMutable("dict"); _map.Clear(); }

    public override void Freeze()
    {
        if (!Frozen)
        {
            base.Freeze();
            foreach (var kv in _map)
            {
                Freezer.Freeze(Unk(kv.Key));
                Freezer.Freeze(kv.Value);
            }
        }
    }
}

/// <summary>An insertion-ordered set of hashable values.</summary>
public sealed class StarSet : Mutable
{
    private readonly OrderedDictionary<object, bool> _map = new(ValueComparer.Instance);

    private static object K(object? key)
    {
        Hashing.CheckHashable(key);
        return key ?? NoneKey.Instance;
    }

    private static object? Unk(object k) => k is NoneKey ? null : k;

    public int Count => _map.Count;
    public IEnumerable<object?> Items => _map.Keys.Select(Unk);
    public bool Contains(object? x) => _map.ContainsKey(K(x));

    public bool Add(object? x)
    {
        var k = K(x);
        CheckMutable("set");
        return _map.TryAdd(k, true);
    }

    public bool Remove(object? x)
    {
        var k = K(x);
        CheckMutable("set");
        return _map.Remove(k);
    }

    public void Clear() { CheckMutable("set"); _map.Clear(); }

    /// <summary>Remove and return the first element in iteration order.</summary>
    public object? Pop()
    {
        CheckMutable("set");
        var first = _map.GetAt(0).Key;
        _map.RemoveAt(0);
        return Unk(first);
    }

    public override void Freeze()
    {
        if (!Frozen)
        {
            base.Freeze();
            foreach (var k in _map.Keys) Freezer.Freeze(Unk(k));
        }
    }
}

public static class Freezer
{
    public static void Freeze(object? v)
    {
        switch (v)
        {
            case Mutable m:
                m.Freeze();
                break;
            case StarTuple t:
                foreach (var x in t.Items) Freeze(x);
                break;
            case StarFunction f:
                f.FreezeContents();
                break;
            case StarStruct st:
                st.Freeze();
                break;
            case StarRecord rec:
                rec.Freeze();
                break;
        }
    }
}

public interface ICallable
{
    string Name { get; }
    string TypeName { get; }
    object? Call(Interpreter interp, IReadOnlyList<object?> args, IReadOnlyList<KeyValuePair<string, object?>> kwargs);
}

/// <summary>A native function.</summary>
public sealed class Builtin(string name, Func<Interpreter, IReadOnlyList<object?>, IReadOnlyList<KeyValuePair<string, object?>>, object?> fn) : ICallable
{
    public string Name { get; } = name;
    public string TypeName => "builtin_function_or_method";

    public object? Call(Interpreter interp, IReadOnlyList<object?> args, IReadOnlyList<KeyValuePair<string, object?>> kwargs) =>
        fn(interp, args, kwargs);
}

/// <summary>A method coupled to its receiver, as produced by <c>x.method</c>.</summary>
public sealed class BoundMethod(string name, object receiver, Method method) : ICallable
{
    public string Name { get; } = name;
    public object Receiver { get; } = receiver;
    public string TypeName => "builtin_function_or_method";

    public object? Call(Interpreter interp, IReadOnlyList<object?> args, IReadOnlyList<KeyValuePair<string, object?>> kwargs)
    {
        var r = new ArgReader(Name, args, kwargs);
        // utf16 arguments are accepted anywhere a string is.
        if (Receiver is StarUtf16 or string) r = r.Mapped(Utf16.Unwrap);
        var result = method(interp, Receiver, r);
        r.Done();
        return result;
    }
}

public delegate object? Method(Interpreter interp, object self, ArgReader args);

public static class Hashing
{
    public static void CheckHashable(object? v)
    {
        switch (v)
        {
            case StarList { Frozen: false } or StarDict { Frozen: false } or StarSet { Frozen: false }:
                throw new StarlarkException($"unhashable type: '{Types.Name(v)}' (value of type `{Types.Name(v)}` is not hashable)");
            case StarRange:
                throw new StarlarkException("unhashable type: 'range'");
            case StarElems:
                throw new StarlarkException("unhashable type: 'elems'");
            case StarTuple t:
                foreach (var x in t.Items) CheckHashable(x);
                break;
            case StarList l:
                foreach (var x in l.Items) CheckHashable(x);
                break;
        }
    }

    /// <summary>32-bit FNV-1a, which the spec mandates for <c>hash(bytes)</c>.</summary>
    public static int Fnv1a(ReadOnlySpan<byte> data)
    {
        uint h = 2166136261;
        foreach (byte b in data) h = (h ^ b) * 16777619;
        return unchecked((int)h);
    }

    /// <summary>Java's <c>String.hashCode</c> over the UTF-16 elements, as the spec mandates for <c>hash(string)</c>.</summary>
    public static int JavaStringHash(string s)
    {
        int h = 0;
        unchecked
        {
            foreach (char c in s) h = 31 * h + c;
        }
        return h;
    }

    public static int Hash(object? v)
    {
        switch (v)
        {
            case null: return 0;
            case bool b: return b ? 1231 : 1237;
            case BigInteger i: return i.GetHashCode();
            case double d:
                if (double.IsNaN(d)) return 0x7ff8;
                if (d == 0) return 0;
                if (!double.IsInfinity(d) && d == Math.Floor(d)) return new BigInteger(d).GetHashCode();
                return d.GetHashCode();
            case string s: return JavaStringHash(s);
            case StarUtf16 u: return JavaStringHash(u.Value) ^ 0x55555;
            case StarBytes b: return Fnv1a(b.Value);
            case StarTuple t: return t.Items.Aggregate(17, (h, x) => unchecked(h * 31 + Hash(x)));
            case StarList l: return l.Items.Aggregate(19, (h, x) => unchecked(h * 31 + Hash(x)));
            case StarDict d:
                return d.Entries.Aggregate(23, (h, kv) => unchecked(h + Hash(kv.Key) * 7 + Hash(kv.Value)));
            case StarSet s: return s.Items.Aggregate(29, (h, x) => unchecked(h + Hash(x)));
            default: return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(v);
        }
    }
}

/// <summary>Structural equality used for dict keys and <c>==</c>.</summary>
public sealed class ValueComparer : IEqualityComparer<object>
{
    public static readonly ValueComparer Instance = new();
    public new bool Equals(object? a, object? b) => Ops.Equal(a is NoneKey ? null : a, b is NoneKey ? null : b);
    public int GetHashCode(object o) => o is NoneKey ? 0 : Hashing.Hash(o);
}

public static class Types
{
    public static string Name(object? v) => v switch
    {
        null => "NoneType",
        bool => "bool",
        BigInteger => "int",
        double => "float",
        string => "string",
        StarUtf16 => "utf16",
        StarBytes => "bytes",
        StarList => "list",
        StarTuple => "tuple",
        StarDict => "dict",
        StarSet => "set",
        StarRange => "range",
        StarElems e => e.TypeName,
        ICallable f => f.TypeName,
        StarStruct => "struct",
        StarRecord r => r.Type.Name,
        StarEnumValue e => e.Type.Name,
        _ => v.GetType().Name,
    };

    public static bool Truthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        BigInteger i => !i.IsZero,
        double d => d != 0.0,
        string s => s.Length > 0,
        StarUtf16 u => u.Value.Length > 0,
        StarBytes b => b.Value.Length > 0,
        StarList l => l.Count > 0,
        StarTuple t => t.Items.Length > 0,
        StarDict d => d.Count > 0,
        StarSet s => s.Count > 0,
        StarRange r => !r.Count.IsZero,
        _ => true,
    };
}

/// <summary>String conversions: <c>str()</c>, <c>repr()</c>.</summary>
public static class Repr
{
    private const int Precision = 6;

    /// <summary>
    /// The float format used by <c>str</c>, <c>repr</c> and <c>%g</c>: the shortest digits that round-trip,
    /// in scientific notation (with at least two exponent digits) when the exponent is below -5 or at least 6.
    /// </summary>
    public static string FloatToString(double d, char exponentChar = 'e')
    {
        if (double.IsNaN(d)) return "nan";
        if (double.IsPositiveInfinity(d)) return "+inf";
        if (double.IsNegativeInfinity(d)) return "-inf";
        if (d == 0) return double.IsNegative(d) ? "-0.0" : "0.0";
        string sign = d < 0 ? "-" : "";
        RustFormat.Decompose(Math.Abs(d), out string digits, out int exp);
        if (exp < -(Precision - 1) || exp >= Precision)
        {
            string mant = digits.Length == 1 ? digits : digits[0] + "." + digits[1..];
            return $"{sign}{mant}{exponentChar}{(exp < 0 ? '-' : '+')}{Math.Abs(exp):00}";
        }
        return sign + RustFormat.PlainDecimal(digits, exp, 1);
    }

    public static string Str(object? v) => v switch
    {
        string s => s,
        StarUtf16 u => u.Value,
        StarBytes b => Encoding.UTF8.GetString(b.Value),
        _ => Of(v),
    };

    public static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case < ' ' or '\u007f': sb.Append("\\x").Append(((int)c).ToString("x2")); break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    {
                        sb.Append(c).Append(s[++i]);
                    }
                    else if (char.IsSurrogate(c))
                    {
                        // A lone surrogate is not valid text; show it as an escape (the result is not a valid literal).
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    public static string Of(object? v) => v switch
    {
        null => "None",
        bool b => b ? "True" : "False",
        BigInteger i => i.ToString(),
        double d => FloatToString(d),
        string s => Quote(s),
        StarUtf16 u => "utf16(" + Quote(u.Value) + ")",
        StarBytes b => new BytesLiteral(b.Value, default).ToString(),
        StarList l => "[" + string.Join(", ", l.Items.Select(Of)) + "]",
        StarTuple t => t.Items.Length == 1 ? $"({Of(t.Items[0])},)" : "(" + string.Join(", ", t.Items.Select(Of)) + ")",
        StarDict d => "{" + string.Join(", ", d.Entries.Select(kv => $"{Of(kv.Key)}: {Of(kv.Value)}")) + "}",
        StarSet s => s.Count == 0 ? "set()" : "set([" + string.Join(", ", s.Items.Select(Of)) + "])",
        StarRange r => r.Step.IsOne ? $"range({r.Start}, {r.Stop})" : $"range({r.Start}, {r.Stop}, {r.Step})",
        StarElems e => e.Source is string s2 ? $"{Quote(s2)}.elems()" : $"{Of(e.Source)}.elems()",
        TypeValue t => t.Ty.ToString(),
        StarRecordType rt => $"<record type>",
        StarEnumType et => $"enum({string.Join(", ", et.Values.Select(Of))})",
        BoundMethod m => $"<built-in method {m.Name} of {Types.Name(m.Receiver)} value>",
        Builtin f => $"<built-in function {f.Name}>",
        ICallable f => $"<function {f.Name}>",
        StarStruct st => "struct(" + string.Join(", ", st.Fields.Select(kv => $"{kv.Key} = {Of(kv.Value)}")) + ")",
        StarRecord rec => rec.Type.Name + "(" + string.Join(", ", rec.Type.Fields.Select((fld, i) => $"{fld.Name}={Of(rec.Values[i])}")) + ")",
        StarEnumValue ev => ev.Type.Name + "(" + Of(ev.Value) + ")",
        _ => v.ToString() ?? "",
    };
}
