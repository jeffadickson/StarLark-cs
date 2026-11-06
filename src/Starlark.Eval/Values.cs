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
        // The first node an unwinding exception reaches inside a function is where control was in that function.
        if (!_haveHere)
        {
            _here = (span, cm);
            _haveHere = true;
        }
        return false;
    }

    private readonly List<StackFrame> _frames = [];
    private (Span Span, CodeMap Map) _here;
    private bool _haveHere;

    /// <summary>
    /// The script functions the error passed through, innermost first, ending with the top-level code. Each entry
    /// names the function and the place inside it where control was: the failing statement for the innermost, the
    /// call for the others.
    /// </summary>
    public IReadOnlyList<StackFrame> CallStack
    {
        get
        {
            if (!_haveHere) return _frames;
            return [.. _frames, new StackFrame("<module>", _here.Span, _here.Map)];
        }
    }

    /// <summary>Called as the exception leaves a script function; closes that function's frame.</summary>
    internal bool LeaveFrame(string function)
    {
        _frames.Add(_haveHere ? new StackFrame(function, _here.Span, _here.Map) : new StackFrame(function, null, null));
        _haveHere = false;
        return false;
    }

    /// <summary>Copies another exception's call stack, for an error re-raised on another thread.</summary>
    internal void CopyStackFrom(StarlarkException other)
    {
        _frames.Clear();
        _frames.AddRange(other._frames);
        _here = other._here;
        _haveHere = other._haveHere;
    }

    /// <summary>
    /// The message followed by a traceback, innermost call last, in the style of other interpreters:
    /// <c>  file:line:col in function</c> for each frame. <see cref="Message"/> itself is unchanged.
    /// </summary>
    public string MessageWithStack()
    {
        var frames = CallStack;
        if (frames.Count <= 1) return Message;
        var sb = new System.Text.StringBuilder(Message).AppendLine().AppendLine();
        sb.AppendLine("Traceback (most recent call last):");
        for (int i = frames.Count - 1; i >= 0; i--)
        {
            var f = frames[i];
            sb.Append("  ").Append(f.Location ?? "<unknown>").Append(" in ").AppendLine(f.Function);
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>One entry of <see cref="StarlarkException.CallStack"/>.</summary>
/// <param name="Function">The function's name, or <c>&lt;module&gt;</c> for top-level code.</param>
/// <param name="Span">Where control was inside that function, or null if unknown.</param>
/// <param name="CodeMap">The source the span refers to.</param>
public sealed record StackFrame(string Function, Span? Span, CodeMap? CodeMap)
{
    /// <summary><c>file:line:column</c>, or null if the position is unknown.</summary>
    public string? Location
    {
        get
        {
            if (Span is not { } s || CodeMap is not { } cm) return null;
            var begin = cm.ResolveSpan(s).Begin;
            return $"{cm.FileName}:{begin}";
        }
    }

    /// <summary>The 1-based line, or 0 if unknown.</summary>
    public int Line => Span is { } s && CodeMap is { } cm ? cm.ResolveSpan(s).Begin.Line + 1 : 0;

    /// <summary><c>file:line:column: in function</c>, as Go prints a frame (or just <c>in function</c> if the position is unknown).</summary>
    public override string ToString() => Location is { } at ? $"{at}: in {Function}" : $"in {Function}";
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

/// <summary>
/// The opaque iterable returned by <c>string.elems()</c>, <c>elem_ords()</c>, <c>codepoints()</c>, <c>codepoint_ords()</c> and
/// <c>bytes.elems()</c>. Element iterables have a known length and can be indexed; code point iterables can be neither,
/// because a code point's position is not its offset.
/// </summary>
public sealed class StarElems(object source, bool ords = false, bool codepoints = false)
{
    public object Source { get; } = source;

    /// <summary>True if the iterable yields numbers (<c>elem_ords</c>, <c>codepoint_ords</c>) rather than strings.</summary>
    public bool Ords { get; } = ords;

    /// <summary>True if the iterable yields Unicode code points (<c>codepoints</c>, <c>codepoint_ords</c>) rather than elements.</summary>
    public bool Codepoints { get; } = codepoints;

    public string TypeName => Source switch
    {
        string => Codepoints ? "string.codepoints" : "string.elems",
        StarUtf16 => Codepoints ? "utf16.codepoints" : "utf16.elems",
        _ => "bytes.elems",
    };

    /// <summary>The method that made this iterable, for <c>repr</c>.</summary>
    public string MethodName => (Codepoints, Ords) switch
    {
        (true, true) => "codepoint_ords",
        (true, false) => "codepoints",
        (false, true) => "elem_ords",
        _ => "elems",
    };

    public IEnumerable<object?> Items
    {
        get
        {
            switch (Source)
            {
                case string s when Codepoints:
                    return Ords
                        ? CodePoints.Runes(s).Select(r => (object?)new BigInteger(r))
                        : CodePoints.Runes(s).Select(r => (object?)CodePoints.Text(r));
                case string s when Ords:
                    return Strings.Mode == StringElements.Utf8
                        ? System.Text.Encoding.UTF8.GetBytes(s).Select(x => (object?)new BigInteger(x))
                        : s.Select(c => (object?)new BigInteger(c));
                case string s:
                    return s.Select(c => (object?)c.ToString());
                case StarUtf16 u when Codepoints:
                    return Ords
                        ? CodePoints.Runes(u.Value).Select(r => (object?)new BigInteger(r))
                        : CodePoints.Runes(u.Value).Select(r => (object?)new StarUtf16(CodePoints.Text(r)));
                case StarUtf16 u when Ords:
                    return u.Value.Select(c => (object?)new BigInteger(c));
                case StarUtf16 u:
                    return u.Value.Select(c => (object?)new StarUtf16(c.ToString()));
                case StarBytes b:
                    return b.Value.Select(x => (object?)new BigInteger(x));
                default:
                    return [];
            }
        }
    }

    /// <summary>True if the iterable has a length and can be indexed (element iterables, not code point iterables).</summary>
    public bool IsIndexable => !Codepoints;

    /// <summary>The number of elements (only meaningful when <see cref="IsIndexable"/>).</summary>
    public int Length => Source switch
    {
        string s => Strings.Len(s),
        StarUtf16 u => u.Value.Length,
        StarBytes b => b.Value.Length,
        _ => 0,
    };

    /// <summary>The element at an index already known to be in range.</summary>
    public object? At(int i)
    {
        switch (Source)
        {
            case string s when Ords:
                return Strings.Mode == StringElements.Utf8 && s.Any(c => c >= 0x80)
                    ? new BigInteger(System.Text.Encoding.UTF8.GetBytes(s)[i])
                    : new BigInteger(s[i]);
            case string s:
                return Strings.At(s, i);
            case StarUtf16 u when Ords:
                return new BigInteger(u.Value[i]);
            case StarUtf16 u:
                return new StarUtf16(u.Value[i].ToString());
            case StarBytes b:
                return new BigInteger(b.Value[i]);
            default:
                return null;
        }
    }
}

/// <summary>Unicode code points of a .NET string. A lone surrogate, which is not text, reads as U+FFFD, as invalid bytes do in Go.</summary>
internal static class CodePoints
{
    public static IEnumerable<int> Runes(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                yield return char.ConvertToUtf32(c, s[++i]);
            }
            else
            {
                yield return char.IsSurrogate(c) ? 0xFFFD : c;
            }
        }
    }

    public static string Text(int codePoint) => char.ConvertFromUtf32(codePoint);
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
        if (v is Mutable or StarTuple) StackGuard.Check();
        switch (v)
        {
            case Mutable m:
                m.Freeze();
                break;
            case IHostValue h:
                h.Freeze();
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
            case BoundMethod bm:
                Freeze(bm.Receiver);
                break;
            case StarRecordType rt:
                foreach (var field in rt.Fields)
                {
                    if (field.HasDefault) Freeze(field.Default);
                }
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
        if (v is StarTuple or StarList or StarStruct or StarRecord) StackGuard.Check();
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
            // A struct or record is hashable when its fields are, like a tuple, so a key's contents cannot change.
            case StarStruct st:
                foreach (var kv in st.Fields) CheckHashable(kv.Value);
                break;
            case StarRecord r:
                foreach (var x in r.Values) CheckHashable(x);
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
        if (v is StarTuple or StarList or StarDict or StarSet or StarStruct or StarRecord) StackGuard.Check();
        switch (v)
        {
            case IHostValue h: return h.HashValue();
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
            // These must agree with Ops.Equal: a struct compares its fields in any order, a record its type and values in order.
            case StarStruct st:
                return st.Fields.Aggregate(31, (h, kv) => unchecked(h + JavaStringHash(kv.Key) * 7 + Hash(kv.Value)));
            case StarRecord r:
                return r.Values.Aggregate(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(r.Type), (h, x) => unchecked(h * 31 + Hash(x)));
            case StarEnumValue e:
                return unchecked(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e.Type) * 31 + e.Index);
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
        IHostValue h => h.TypeName,
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
        IHostValue h => h.Truthy(),
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

    public static string Of(object? v) => Of(v, null);

    /// <summary>
    /// The repr of <paramref name="v"/>. <paramref name="active"/> holds the lists, dicts and sets being printed right now, so a
    /// container that contains itself prints as <c>[...]</c>, <c>{...}</c> or <c>set([...])</c> instead of recursing forever,
    /// and <see cref="StackGuard"/> turns absurdly deep nesting into an error. The container cases are separate methods so
    /// this one allocates nothing for the common scalar case.
    /// </summary>
    private static string Of(object? v, HashSet<object>? active)
    {
        StackGuard.Check();
        return v switch
        {
            IHostValue h => h.Repr(),
            null => "None",
            bool b => b ? "True" : "False",
            BigInteger i => Syntax.BigIntText.ToString(i, 10),
            double d => FloatToString(d),
            string s => Quote(s),
            StarUtf16 u => "utf16(" + Quote(u.Value) + ")",
            StarBytes b => new BytesLiteral(b.Value, default).ToString(),
            StarList l => OfList(l, active),
            StarTuple t => OfTuple(t, active),
            StarDict d => OfDict(d, active),
            StarSet s => OfSet(s, active),
            // As the spec and Rust print it: range(10), range(1, 10), range(1, 10, 2).
            StarRange r => !r.Step.IsOne ? $"range({r.Start}, {r.Stop}, {r.Step})"
                : r.Start.IsZero ? $"range({r.Stop})" : $"range({r.Start}, {r.Stop})",
            StarElems e => $"{Of(e.Source, active)}.{e.MethodName}()",
            TypeValue t => t.Ty.ToString(),
            StarRecordType rt => $"<record type>",
            StarEnumType et => "enum(" + Join(et.Values, active) + ")",
            BoundMethod m => $"<built-in method {m.Name} of {Types.Name(m.Receiver)} value>",
            Builtin f => $"<built-in function {f.Name}>",
            ICallable f => $"<function {f.Name}>",
            StarStruct st => OfStruct(st, active),
            StarRecord rec => OfRecord(rec, active),
            StarEnumValue ev => ev.Type.Name + "(" + Of(ev.Value, active) + ")",
            _ => v.ToString() ?? "",
        };
    }

    private static HashSet<object> NewActive() => new(ReferenceEqualityComparer.Instance);

    private static string Join(IEnumerable<object?> items, HashSet<object>? active)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (var x in items)
        {
            if (!first) sb.Append(", ");
            first = false;
            sb.Append(Of(x, active));
            // Many references to one big value can make a repr far larger than any object in the program.
            MemoryBudget.CheckString(sb.Length);
        }
        return sb.ToString();
    }

    private static string OfList(StarList l, HashSet<object>? active)
    {
        active ??= NewActive();
        if (!active.Add(l)) return "[...]";
        try { return "[" + Join(l.Items, active) + "]"; }
        finally { active.Remove(l); }
    }

    // A tuple is immutable, so a cycle can only run through a list, dict or set inside it; those are tracked.
    private static string OfTuple(StarTuple t, HashSet<object>? active) =>
        t.Items.Length == 1 ? "(" + Of(t.Items[0], active) + ",)" : "(" + Join(t.Items, active) + ")";

    private static string OfDict(StarDict d, HashSet<object>? active)
    {
        active ??= NewActive();
        if (!active.Add(d)) return "{...}";
        try
        {
            var sb = new StringBuilder("{");
            bool first = true;
            foreach (var kv in d.Entries)
            {
                if (!first) sb.Append(", ");
                first = false;
                sb.Append(Of(kv.Key, active)).Append(": ").Append(Of(kv.Value, active));
                MemoryBudget.CheckString(sb.Length);
            }
            return sb.Append('}').ToString();
        }
        finally { active.Remove(d); }
    }

    private static string OfSet(StarSet s, HashSet<object>? active)
    {
        if (s.Count == 0) return "set()";
        active ??= NewActive();
        if (!active.Add(s)) return "set([...])";
        try { return "set([" + Join(s.Items, active) + "])"; }
        finally { active.Remove(s); }
    }

    private static string OfStruct(StarStruct st, HashSet<object>? active)
    {
        var sb = new StringBuilder("struct(");
        bool first = true;
        foreach (var kv in st.Fields)
        {
            if (!first) sb.Append(", ");
            first = false;
            sb.Append(kv.Key).Append(" = ").Append(Of(kv.Value, active));
            MemoryBudget.CheckString(sb.Length);
        }
        return sb.Append(')').ToString();
    }

    private static string OfRecord(StarRecord rec, HashSet<object>? active)
    {
        var sb = new StringBuilder(rec.Type.Name).Append('(');
        int i = 0;
        foreach (var fld in rec.Type.Fields)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(fld.Name).Append('=').Append(Of(rec.Values[i], active));
            MemoryBudget.CheckString(sb.Length);
            i++;
        }
        return sb.Append(')').ToString();
    }
}
