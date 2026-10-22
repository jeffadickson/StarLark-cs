using System.Numerics;
using System.Text;
using Starlark.Syntax;

namespace Starlark.Eval;

/// <summary>
/// Starlark values are plain CLR objects: <c>null</c> = None, <see cref="bool"/>, <see cref="BigInteger"/> (int),
/// <see cref="double"/>, <see cref="string"/>, <see cref="StarBytes"/>, <see cref="StarList"/>,
/// <see cref="StarTuple"/>, <see cref="StarDict"/>, <see cref="StarRange"/> and <see cref="ICallable"/>.
/// </summary>
public sealed class StarlarkException : Exception
{
    public Span? Span { get; set; }
    public CodeMap? CodeMap { get; set; }

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

public sealed class StarBytes(byte[] value)
{
    public byte[] Value { get; } = value;
    public override bool Equals(object? obj) => obj is StarBytes b && Value.AsSpan().SequenceEqual(b.Value);
    public override int GetHashCode()
    {
        var h = new HashCode();
        h.AddBytes(Value);
        return h.ToHashCode();
    }
}

public sealed class StarTuple(object?[] items)
{
    public object?[] Items { get; } = items;
    public static readonly StarTuple Empty = new([]);
}

public sealed class StarRange(BigInteger start, BigInteger stop, BigInteger step)
{
    public BigInteger Start { get; } = start;
    public BigInteger Stop { get; } = stop;
    public BigInteger Step { get; } = step;

    public int Count
    {
        get
        {
            if (Step > 0)
            {
                return Start >= Stop ? 0 : (int)((Stop - Start + Step - 1) / Step);
            }
            return Start <= Stop ? 0 : (int)((Start - Stop - Step - 1) / -Step);
        }
    }

    public BigInteger this[int i] => Start + Step * i;
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
            throw new StarlarkException($"Cannot mutate an iterable {what} while iterating");
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
    public void AddRange(IEnumerable<object?> xs) { CheckMutable("list"); _items.AddRange(xs.ToList()); }
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
        if (!_map.ContainsKey(k))
        {
            CheckMutable("dict");
        }
        else if (Frozen)
        {
            CheckMutable("dict");
        }
        _map[k] = value;
    }

    public bool Remove(object? key, out object? value)
    {
        CheckMutable("dict");
        return _map.Remove(K(key), out value);
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
        }
    }
}

public interface ICallable
{
    string Name { get; }
    object? Call(Interpreter interp, IReadOnlyList<object?> args, IReadOnlyList<KeyValuePair<string, object?>> kwargs);
}

/// <summary>A native function.</summary>
public sealed class Builtin(string name, Func<Interpreter, IReadOnlyList<object?>, IReadOnlyList<KeyValuePair<string, object?>>, object?> fn) : ICallable
{
    public string Name { get; } = name;

    public object? Call(Interpreter interp, IReadOnlyList<object?> args, IReadOnlyList<KeyValuePair<string, object?>> kwargs) =>
        fn(interp, args, kwargs);
}

public static class Hashing
{
    public static void CheckHashable(object? v)
    {
        switch (v)
        {
            case StarList or StarDict:
                throw new StarlarkException($"unhashable type: '{Types.Name(v)}'");
            case StarTuple t:
                foreach (var x in t.Items) CheckHashable(x);
                break;
        }
    }

    public static int Hash(object? v) => v switch
    {
        null => 0,
        bool b => b ? 1231 : 1237,
        BigInteger i => i.GetHashCode(),
        double d => d == Math.Floor(d) && !double.IsInfinity(d) ? new BigInteger(d).GetHashCode() : d.GetHashCode(),
        string s => s.GetHashCode(),
        StarBytes b => b.GetHashCode(),
        StarTuple t => t.Items.Aggregate(17, (h, x) => h * 31 + Hash(x)),
        StarRange r => HashCode.Combine(r.Start, r.Stop, r.Step),
        _ => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(v),
    };
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
        StarBytes => "bytes",
        StarList => "list",
        StarTuple => "tuple",
        StarDict => "dict",
        StarRange => "range",
        ICallable => "function",
        _ => v.GetType().Name,
    };

    public static bool Truthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        BigInteger i => !i.IsZero,
        double d => d != 0.0,
        string s => s.Length > 0,
        StarBytes b => b.Value.Length > 0,
        StarList l => l.Count > 0,
        StarTuple t => t.Items.Length > 0,
        StarDict d => d.Count > 0,
        StarRange r => r.Count > 0,
        _ => true,
    };
}

/// <summary>String conversions: <c>str()</c>, <c>repr()</c>.</summary>
public static class Repr
{
    public static string FloatToString(double d)
    {
        if (double.IsNaN(d)) return "nan";
        if (double.IsPositiveInfinity(d)) return "+inf";
        if (double.IsNegativeInfinity(d)) return "-inf";
        if (d == 0) return double.IsNegative(d) ? "-0.0" : "0.0";
        string sign = d < 0 ? "-" : "";
        RustFormat.Decompose(Math.Abs(d), out string digits, out int exp);
        if (exp < -5 || exp >= 16)
        {
            string mant = digits.Length == 1 ? digits : digits[0] + "." + digits[1..];
            return $"{sign}{mant}e{(exp < 0 ? '-' : '+')}{Math.Abs(exp):00}";
        }
        return sign + RustFormat.PlainDecimal(digits, exp, 1);
    }

    public static string Str(object? v) => v is string s ? s : Of(v);

    public static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case < ' ' or '\u007f': sb.Append("\\x").Append(((int)c).ToString("x2")); break;
                default: sb.Append(c); break;
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
        StarBytes b => new BytesLiteral(b.Value, default).ToString(),
        StarList l => "[" + string.Join(", ", l.Items.Select(Of)) + "]",
        StarTuple t => t.Items.Length == 1 ? $"({Of(t.Items[0])},)" : "(" + string.Join(", ", t.Items.Select(Of)) + ")",
        StarDict d => "{" + string.Join(", ", d.Entries.Select(kv => $"{Of(kv.Key)}: {Of(kv.Value)}")) + "}",
        StarRange r => r.Step == 1 ? $"range({r.Start}, {r.Stop})" : $"range({r.Start}, {r.Stop}, {r.Step})",
        ICallable f => $"<function {f.Name}>",
        _ => v.ToString() ?? "",
    };
}

/// <summary>Equality, ordering, and arithmetic.</summary>
public static class Ops
{
    public static bool Equal(object? a, object? b)
    {
        switch (a, b)
        {
            case (null, null): return true;
            case (bool x, bool y): return x == y;
            case (BigInteger x, BigInteger y): return x == y;
            case (double x, double y): return x == y;
            case (BigInteger x, double y): return (double)x == y;
            case (double x, BigInteger y): return x == (double)y;
            case (string x, string y): return x == y;
            case (StarBytes x, StarBytes y): return x.Equals(y);
            case (StarList x, StarList y): return SeqEqual(x.Items, y.Items);
            case (StarTuple x, StarTuple y): return SeqEqual(x.Items, y.Items);
            case (StarDict x, StarDict y):
                if (x.Count != y.Count) return false;
                foreach (var kv in x.Entries)
                {
                    if (!y.TryGet(kv.Key, out var yv) || !Equal(kv.Value, yv)) return false;
                }
                return true;
            case (StarRange x, StarRange y): return x.Count == y.Count && (x.Count == 0 || (x.Start == y.Start && x.Step == y.Step));
            default: return ReferenceEquals(a, b);
        }
    }

    private static bool SeqEqual(IReadOnlyList<object?> a, IReadOnlyList<object?> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!Equal(a[i], b[i])) return false;
        }
        return true;
    }

    /// <summary>Three-way comparison; throws if the types are not orderable.</summary>
    public static int Compare(object? a, object? b)
    {
        switch (a, b)
        {
            case (bool x, bool y): return x.CompareTo(y);
            case (BigInteger x, BigInteger y): return x.CompareTo(y);
            case (double x, double y): return CmpDouble(x, y);
            case (BigInteger x, double y): return CmpDouble((double)x, y);
            case (double x, BigInteger y): return CmpDouble(x, (double)y);
            case (string x, string y): return Math.Sign(string.CompareOrdinal(x, y));
            case (StarBytes x, StarBytes y): return x.Value.AsSpan().SequenceCompareTo(y.Value);
            case (StarList x, StarList y): return SeqCompare(x.Items, y.Items);
            case (StarTuple x, StarTuple y): return SeqCompare(x.Items, y.Items);
        }
        throw new StarlarkException($"Operation `<` not supported on types '{Types.Name(a)}' and '{Types.Name(b)}'");
    }

    private static int CmpDouble(double x, double y)
    {
        if (double.IsNaN(x) || double.IsNaN(y))
        {
            throw new StarlarkException("NaN is not orderable");
        }
        return x.CompareTo(y);
    }

    private static int SeqCompare(IReadOnlyList<object?> a, IReadOnlyList<object?> b)
    {
        for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            int c = Compare(a[i], b[i]);
            if (c != 0) return c;
        }
        return a.Count.CompareTo(b.Count);
    }

    private static StarlarkException Unsupported(string op, object? a, object? b) =>
        new($"Operation `{op}` not supported on types '{Types.Name(a)}' and '{Types.Name(b)}'");

    private static bool IsNum(object? v) => v is BigInteger or double;
    private static double D(object? v) => v is double d ? d : (double)(BigInteger)v!;

    public static object? Binary(BinOp op, object? a, object? b)
    {
        switch (op)
        {
            case BinOp.Equal: return Equal(a, b);
            case BinOp.NotEqual: return !Equal(a, b);
            case BinOp.Less: return Compare(a, b) < 0;
            case BinOp.Greater: return Compare(a, b) > 0;
            case BinOp.LessOrEqual: return Compare(a, b) <= 0;
            case BinOp.GreaterOrEqual: return Compare(a, b) >= 0;
            case BinOp.In: return Contains(b, a);
            case BinOp.NotIn: return !Contains(b, a);
            case BinOp.Add: return Add(a, b);
            case BinOp.Subtract: return Subtract(a, b);
            case BinOp.Multiply: return Multiply(a, b);
            case BinOp.Divide: return Divide(a, b);
            case BinOp.FloorDivide: return FloorDivide(a, b);
            case BinOp.Percent: return Percent(a, b);
            case BinOp.BitAnd:
                if (a is BigInteger x1 && b is BigInteger y1) return x1 & y1;
                if (a is StarDict && b is StarDict) throw Unsupported("&", a, b);
                throw Unsupported("&", a, b);
            case BinOp.BitOr:
                if (a is BigInteger x2 && b is BigInteger y2) return x2 | y2;
                if (a is StarDict d1 && b is StarDict d2)
                {
                    var r = new StarDict();
                    foreach (var kv in d1.Entries) r.Set(kv.Key, kv.Value);
                    foreach (var kv in d2.Entries) r.Set(kv.Key, kv.Value);
                    return r;
                }
                throw Unsupported("|", a, b);
            case BinOp.BitXor:
                if (a is BigInteger x3 && b is BigInteger y3) return x3 ^ y3;
                throw Unsupported("^", a, b);
            case BinOp.LeftShift:
                if (a is BigInteger x4 && b is BigInteger y4)
                {
                    if (y4 < 0) throw new StarlarkException("Negative left shift count");
                    return x4 << (int)y4;
                }
                throw Unsupported("<<", a, b);
            case BinOp.RightShift:
                if (a is BigInteger x5 && b is BigInteger y5)
                {
                    if (y5 < 0) throw new StarlarkException("Negative right shift count");
                    return x5 >> (int)y5;
                }
                throw Unsupported(">>", a, b);
            default:
                throw new InvalidOperationException(op.ToString());
        }
    }

    public static bool Contains(object? container, object? item)
    {
        switch (container)
        {
            case string s:
                if (item is string sub) return s.Contains(sub, StringComparison.Ordinal);
                throw new StarlarkException($"'in <string>' requires string as left operand, not '{Types.Name(item)}'");
            case StarBytes b:
                if (item is StarBytes sb) return b.Value.AsSpan().IndexOf(sb.Value) >= 0;
                if (item is BigInteger bi) return bi >= 0 && bi <= 255 && b.Value.Contains((byte)bi);
                throw new StarlarkException($"'in <bytes>' requires bytes or int as left operand, not '{Types.Name(item)}'");
            case StarDict d: return d.ContainsKey(item);
            case StarList l: return l.Items.Any(x => Equal(x, item));
            case StarTuple t: return t.Items.Any(x => Equal(x, item));
            case StarRange r: return item is BigInteger n && Enumerable.Range(0, r.Count).Any(i => r[i] == n);
        }
        throw new StarlarkException($"Operation `in` not supported on types '{Types.Name(item)}' and '{Types.Name(container)}'");
    }

    public static object? Add(object? a, object? b)
    {
        switch (a, b)
        {
            case (BigInteger x, BigInteger y): return x + y;
            case (string x, string y): return x + y;
            case (StarBytes x, StarBytes y): return new StarBytes([.. x.Value, .. y.Value]);
            case (StarList x, StarList y): return new StarList(x.Items.Concat(y.Items));
            case (StarTuple x, StarTuple y): return new StarTuple([.. x.Items, .. y.Items]);
        }
        if (IsNum(a) && IsNum(b)) return D(a) + D(b);
        throw Unsupported("+", a, b);
    }

    public static object? Subtract(object? a, object? b)
    {
        if (a is BigInteger x && b is BigInteger y) return x - y;
        if (IsNum(a) && IsNum(b)) return D(a) - D(b);
        throw Unsupported("-", a, b);
    }

    private static object? Repeat(object? seq, BigInteger n)
    {
        int count = n < 0 ? 0 : checked((int)n);
        switch (seq)
        {
            case string s:
                return count == 0 ? "" : new StringBuilder(s.Length * count).Insert(0, s, count).ToString();
            case StarBytes b:
                return new StarBytes(Enumerable.Repeat(b.Value, count).SelectMany(x => x).ToArray());
            case StarList l:
                return new StarList(Enumerable.Repeat(l.Items, count).SelectMany(x => x));
            case StarTuple t:
                return new StarTuple(Enumerable.Repeat(t.Items, count).SelectMany(x => x).ToArray());
        }
        return null;
    }

    public static object? Multiply(object? a, object? b)
    {
        if (a is BigInteger x && b is BigInteger y) return x * y;
        if (IsNum(a) && IsNum(b)) return D(a) * D(b);
        if (b is BigInteger n && Repeat(a, n) is { } r1) return r1;
        if (a is BigInteger m && Repeat(b, m) is { } r2) return r2;
        throw Unsupported("*", a, b);
    }

    public static object? Divide(object? a, object? b)
    {
        if (!IsNum(a) || !IsNum(b)) throw Unsupported("/", a, b);
        double d = D(b);
        if (d == 0) throw new StarlarkException("Division by zero");
        return D(a) / d;
    }

    public static object? FloorDivide(object? a, object? b)
    {
        if (a is BigInteger x && b is BigInteger y)
        {
            if (y.IsZero) throw new StarlarkException("Integer division by zero");
            var q = BigInteger.DivRem(x, y, out var rem);
            if (!rem.IsZero && (rem.Sign < 0) != (y.Sign < 0)) q -= 1;
            return q;
        }
        if (IsNum(a) && IsNum(b))
        {
            double d = D(b);
            if (d == 0) throw new StarlarkException("Floating-point division by zero");
            return Math.Floor(D(a) / d);
        }
        throw Unsupported("//", a, b);
    }

    public static object? Percent(object? a, object? b)
    {
        if (a is string fmt) return StringFormat.Percent(fmt, b);
        if (a is BigInteger x && b is BigInteger y)
        {
            if (y.IsZero) throw new StarlarkException("Integer modulo by zero");
            var r = BigInteger.Remainder(x, y);
            if (!r.IsZero && (r.Sign < 0) != (y.Sign < 0)) r += y;
            return r;
        }
        if (IsNum(a) && IsNum(b))
        {
            double d = D(b);
            if (d == 0) throw new StarlarkException("Floating-point modulo by zero");
            double r = Math.IEEERemainder(D(a), d);
            r = D(a) % d;
            if (r != 0 && (r < 0) != (d < 0)) r += d;
            return r;
        }
        throw Unsupported("%", a, b);
    }

    public static object? Negate(object? v) => v switch
    {
        BigInteger i => -i,
        double d => -d,
        _ => throw new StarlarkException($"Unary operator `-` is not available on the type `{Types.Name(v)}`"),
    };

    public static object? Plus(object? v) => v switch
    {
        BigInteger or double => v,
        _ => throw new StarlarkException($"Unary operator `+` is not available on the type `{Types.Name(v)}`"),
    };

    public static object? BitNot(object? v) => v is BigInteger i
        ? -i - 1
        : throw new StarlarkException($"Unary operator `~` is not available on the type `{Types.Name(v)}`");

    // ---------------------------------------------------------------- iteration & indexing

    /// <summary>Iterate any iterable, locking mutable containers against modification while iterating.</summary>
    public static IEnumerable<object?> Iterate(object? v)
    {
        switch (v)
        {
            case StarList l:
                return IterLocked(l, () => l.Items);
            case StarTuple t:
                return t.Items;
            case StarDict d:
                return IterLocked(d, () => d.Keys.ToList());
            case StarRange r:
                return Enumerable.Range(0, r.Count).Select(i => (object?)r[i]);
            case string s:
                throw new StarlarkException("Type 'string' is not iterable");
            case StarBytes:
                throw new StarlarkException("Type 'bytes' is not iterable");
        }
        throw new StarlarkException($"Type '{Types.Name(v)}' is not iterable");
    }

    private static IEnumerable<object?> IterLocked(Mutable m, Func<IEnumerable<object?>> source)
    {
        m.BeginIter();
        try
        {
            foreach (var x in source())
            {
                yield return x;
            }
        }
        finally
        {
            m.EndIter();
        }
    }

    public static List<object?> ToList(object? v) => v is StarList l ? [.. l.Items] : Iterate(v).ToList();

    public static int Len(object? v) => v switch
    {
        string s => CodePoints.Length(s),
        StarBytes b => b.Value.Length,
        StarList l => l.Count,
        StarTuple t => t.Items.Length,
        StarDict d => d.Count,
        StarRange r => r.Count,
        _ => throw new StarlarkException($"Type '{Types.Name(v)}' has no len()"),
    };

    private static int NormIndex(BigInteger i, int len)
    {
        if (i < 0) i += len;
        if (i < 0 || i >= len) throw new StarlarkException($"Index `{i}` is out of bounds");
        return (int)i;
    }

    public static object? Index(object? target, object? index)
    {
        if (target is StarDict d)
        {
            if (d.TryGet(index, out var v)) return v;
            throw new StarlarkException($"Key `{Repr.Of(index)}` not found in dictionary");
        }
        if (index is not BigInteger i)
        {
            throw new StarlarkException($"Index must be an int, not '{Types.Name(index)}'");
        }
        switch (target)
        {
            case StarList l: return l[NormIndex(i, l.Count)];
            case StarTuple t: return t.Items[NormIndex(i, t.Items.Length)];
            case string s:
            {
                var cps = CodePoints.Split(s);
                return cps[NormIndex(i, cps.Count)];
            }
            case StarBytes b: return new BigInteger(b.Value[NormIndex(i, b.Value.Length)]);
            case StarRange r: return r[NormIndex(i, r.Count)];
        }
        throw new StarlarkException($"Type '{Types.Name(target)}' is not indexable");
    }

    public static void SetIndex(object? target, object? index, object? value)
    {
        switch (target)
        {
            case StarDict d:
                d.Set(index, value);
                return;
            case StarList l:
                if (index is not BigInteger i)
                {
                    throw new StarlarkException($"Index must be an int, not '{Types.Name(index)}'");
                }
                l.Set(NormIndex(i, l.Count), value);
                return;
        }
        throw new StarlarkException($"Type '{Types.Name(target)}' does not support item assignment");
    }

    /// <summary>Python-style slice bounds → (start, stop, step) as sequence indices.</summary>
    public static List<int> SliceIndices(int len, object? start, object? stop, object? step)
    {
        BigInteger st = step is null ? 1 : step is BigInteger s ? s : throw new StarlarkException("slice step must be an int");
        if (st.IsZero) throw new StarlarkException("Slice step cannot be zero");
        BigInteger Get(object? v, BigInteger dflt)
        {
            if (v is null) return dflt;
            if (v is not BigInteger n) throw new StarlarkException("slice indices must be int or None");
            return n;
        }

        BigInteger lo, hi;
        if (st > 0)
        {
            lo = Math.Clamp(Clip(Get(start, 0), len), 0, len);
            hi = Math.Clamp(Clip(Get(stop, len), len), 0, len);
        }
        else
        {
            lo = Math.Clamp(Clip(Get(start, len - 1), len), -1, len - 1);
            hi = Math.Clamp(Clip(Get(stop, -1 - (BigInteger)len), len), -1, len - 1);
            if (stop is null) hi = -1;
        }
        var res = new List<int>();
        if (st > 0)
        {
            for (BigInteger i = lo; i < hi; i += st) res.Add((int)i);
        }
        else
        {
            for (BigInteger i = lo; i > hi; i += st) res.Add((int)i);
        }
        return res;
    }

    private static int Clip(BigInteger i, int len)
    {
        if (i < 0) i += len;
        return (int)BigInteger.Clamp(i, -1_000_000_000, 1_000_000_000);
    }

    public static object? Slice(object? target, object? start, object? stop, object? step)
    {
        switch (target)
        {
            case StarList l:
                return new StarList(SliceIndices(l.Count, start, stop, step).Select(i => l[i]));
            case StarTuple t:
                return new StarTuple(SliceIndices(t.Items.Length, start, stop, step).Select(i => t.Items[i]).ToArray());
            case string s:
            {
                var cps = CodePoints.Split(s);
                return string.Concat(SliceIndices(cps.Count, start, stop, step).Select(i => cps[i]));
            }
            case StarBytes b:
                return new StarBytes(SliceIndices(b.Value.Length, start, stop, step).Select(i => b.Value[i]).ToArray());
            case StarRange r:
                return new StarList(SliceIndices(r.Count, start, stop, step).Select(i => (object?)r[i]));
        }
        throw new StarlarkException($"Type '{Types.Name(target)}' is not sliceable");
    }
}

/// <summary>Starlark strings are sequences of Unicode code points; .NET strings are UTF-16.</summary>
public static class CodePoints
{
    public static List<string> Split(string s)
    {
        var res = new List<string>(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                res.Add(s.Substring(i, 2));
                i++;
            }
            else
            {
                res.Add(s[i].ToString());
            }
        }
        return res;
    }

    public static int Length(string s)
    {
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (!(char.IsLowSurrogate(s[i]) && i > 0 && char.IsHighSurrogate(s[i - 1]))) n++;
        }
        return n;
    }
}
