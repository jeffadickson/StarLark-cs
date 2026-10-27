using System.Numerics;
using System.Text;
using Starlark.Syntax;

namespace Starlark.Eval;

/// <summary>Equality, ordering, arithmetic, indexing and iteration.</summary>
public static class Ops
{
    // ---------------------------------------------------------------- numbers

    private static bool IsNum(object? v) => v is BigInteger or double;

    /// <summary>
    /// Converts an int to the nearest double, rounding ties to even. <c>(double)BigInteger</c> truncates instead,
    /// which disagrees with the reference implementations for values above 2^53.
    /// </summary>
    public static double BigToDouble(BigInteger i)
    {
        if (i >= long.MinValue && i <= long.MaxValue) return (double)(long)i;
        var magnitude = BigInteger.Abs(i);
        int shift = (int)magnitude.GetBitLength() - 64;
        ulong top = (ulong)(magnitude >> shift);
        if (!(magnitude & ((BigInteger.One << shift) - 1)).IsZero) top |= 1;   // sticky bit keeps ties-to-even exact
        double r = shift > 1100 ? double.PositiveInfinity : Math.ScaleB((double)top, shift);
        return i.Sign < 0 ? -r : r;
    }

    private static double ToDouble(object? v)
    {
        if (v is double d) return d;
        var i = (BigInteger)v!;
        double r = BigToDouble(i);
        if (double.IsInfinity(r))
        {
            throw new StarlarkException("int too large to convert to float");
        }
        return r;
    }

    /// <summary>Exact three-way comparison of an int and a float; NaN is greater than everything.</summary>
    private static int CompareIntFloat(BigInteger i, double d)
    {
        if (double.IsNaN(d)) return -1;
        if (double.IsPositiveInfinity(d)) return -1;
        if (double.IsNegativeInfinity(d)) return 1;
        double floor = Math.Floor(d);
        var fi = new BigInteger(floor);
        int c = i.CompareTo(fi);
        if (c != 0) return c;
        return d > floor ? -1 : 0;
    }

    private static int CompareFloats(double x, double y)
    {
        bool xn = double.IsNaN(x), yn = double.IsNaN(y);
        if (xn || yn) return xn && yn ? 0 : xn ? 1 : -1;
        return x < y ? -1 : x > y ? 1 : 0;
    }

    // ---------------------------------------------------------------- equality & ordering

    public static bool Equal(object? a, object? b)
    {
        switch (a, b)
        {
            case (null, null): return true;
            case (bool x, bool y): return x == y;
            case (BigInteger x, BigInteger y): return x == y;
            case (double x, double y): return CompareFloats(x, y) == 0;
            case (BigInteger x, double y): return CompareIntFloat(x, y) == 0;
            case (double x, BigInteger y): return CompareIntFloat(y, x) == 0;
            case (string x, string y): return x == y;
            // A utf16 is a different type from a string, so the two are never equal (as 1 != "1").
            case (StarUtf16 x, StarUtf16 y): return x.Equals(y);
            case (StarBytes x, StarBytes y): return x.Equals(y);
            case (StarList x, StarList y): return ReferenceEquals(x, y) || SeqEqual(x.Items, y.Items);
            case (StarTuple x, StarTuple y): return SeqEqual(x.Items, y.Items);
            case (StarDict x, StarDict y):
                if (ReferenceEquals(x, y)) return true;
                if (x.Count != y.Count) return false;
                foreach (var kv in x.Entries)
                {
                    if (!y.TryGet(kv.Key, out var yv) || !Equal(kv.Value, yv)) return false;
                }
                return true;
            case (StarStruct x, StarStruct y):
                // Equal when they have the same fields with equal values; field order does not matter.
                return x.Fields.Count == y.Fields.Count &&
                       x.Fields.All(kv => y.TryGet(kv.Key, out var yv) && Equal(kv.Value, yv));
            case (StarRecord x, StarRecord y):
                return ReferenceEquals(x.Type, y.Type) && SeqEqual(x.Values, y.Values);
            case (StarEnumValue x, StarEnumValue y):
                return ReferenceEquals(x.Type, y.Type) && x.Index == y.Index;
            case (StarSet x, StarSet y):
                return x.Count == y.Count && x.Items.All(y.Contains);
            case (StarRange x, StarRange y):
                return x.Count == y.Count && (x.Count.IsZero || (x.Start == y.Start && (x.Count.IsOne || x.Step == y.Step)));
            case (StarElems x, StarElems y): return ReferenceEquals(x, y);
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
    public static int Compare(object? a, object? b, string op = "<")
    {
        switch (a, b)
        {
            case (bool x, bool y): return x.CompareTo(y);
            case (BigInteger x, BigInteger y): return x.CompareTo(y);
            case (double x, double y): return CompareFloats(x, y);
            case (BigInteger x, double y): return CompareIntFloat(x, y);
            case (double x, BigInteger y): return -CompareIntFloat(y, x);
            case (string x, string y): return CompareStrings(x, y);
            case (StarUtf16 x, StarUtf16 y): return CompareStrings(x.Value, y.Value);
            case (StarBytes x, StarBytes y): return Math.Sign(x.Value.AsSpan().SequenceCompareTo(y.Value));
            case (StarList x, StarList y): return SeqCompare(x.Items, y.Items, op);
            case (StarTuple x, StarTuple y): return SeqCompare(x.Items, y.Items, op);
        }
        throw Err.Unsupported(op, a, b);
    }

    /// <summary>Lexicographic by code point (UTF-16 code unit order is not order-preserving for surrogates).</summary>
    private static int CompareStrings(string x, string y)
    {
        int n = Math.Min(x.Length, y.Length);
        for (int i = 0; i < n; i++)
        {
            if (x[i] == y[i]) continue;
            bool xs = char.IsSurrogate(x[i]), ys = char.IsSurrogate(y[i]);
            if (xs != ys) return xs ? 1 : -1; // supplementary code points sort after all BMP characters
            return x[i] < y[i] ? -1 : 1;
        }
        return x.Length.CompareTo(y.Length);
    }

    private static int SeqCompare(IReadOnlyList<object?> a, IReadOnlyList<object?> b, string op)
    {
        for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            if (Equal(a[i], b[i])) continue;
            return Compare(a[i], b[i], op);
        }
        return a.Count.CompareTo(b.Count);
    }

    // ---------------------------------------------------------------- operators

    public static object? Binary(BinOp op, object? a, object? b)
    {
        switch (op)
        {
            case BinOp.Equal: return Equal(a, b);
            case BinOp.NotEqual: return !Equal(a, b);
            case BinOp.Less: return Compare(a, b, "<") < 0;
            case BinOp.Greater: return Compare(a, b, ">") > 0;
            case BinOp.LessOrEqual: return Compare(a, b, "<=") <= 0;
            case BinOp.GreaterOrEqual: return Compare(a, b, ">=") >= 0;
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
                if (a is StarSet s1 && b is StarSet s2) return SetOp(s1, s2, (l, _) => l.Where(s2.Contains));
                throw Err.Unsupported("&", a, b);
            case BinOp.BitOr:
                if (a is BigInteger x2 && b is BigInteger y2) return x2 | y2;
                // `int | str`, `list[int] | None`: combining types.
                if ((TypeEval.IsTypeLike(a) && (TypeEval.IsTypeLike(b) || b is null)) ||
                    (a is null && TypeEval.IsTypeLike(b)))
                {
                    return TypeEval.Union(a, b);
                }
                if (a is StarDict d1 && b is StarDict d2)
                {
                    var r = new StarDict();
                    foreach (var kv in d1.Entries) r.Set(kv.Key, kv.Value);
                    foreach (var kv in d2.Entries) r.Set(kv.Key, kv.Value);
                    return r;
                }
                if (a is StarSet u1 && b is StarSet u2) return SetOp(u1, u2, (l, r) => l.Concat(r));
                throw Err.Unsupported("|", a, b);
            case BinOp.BitXor:
                if (a is BigInteger x3 && b is BigInteger y3) return x3 ^ y3;
                if (a is StarSet e1 && b is StarSet e2)
                {
                    return SetOp(e1, e2, (l, r) => l.Where(x => !e2.Contains(x)).Concat(r.Where(x => !e1.Contains(x))));
                }
                throw Err.Unsupported("^", a, b);
            case BinOp.LeftShift:
                if (a is BigInteger x4 && b is BigInteger y4)
                {
                    if (y4 < 0) throw new StarlarkException("Negative left shift count");
                    if (y4 > int.MaxValue) throw new StarlarkException("Integer overflow in left shift");
                    return x4 << (int)y4;
                }
                throw Err.Unsupported("<<", a, b);
            case BinOp.RightShift:
                if (a is BigInteger x5 && b is BigInteger y5)
                {
                    if (y5 < 0) throw new StarlarkException("Negative right shift count");
                    if (y5 > int.MaxValue) return x5.Sign < 0 ? BigInteger.MinusOne : BigInteger.Zero;
                    return x5 >> (int)y5;
                }
                throw Err.Unsupported(">>", a, b);
            default:
                throw new InvalidOperationException(op.ToString());
        }
    }

    private static StarSet SetOp(StarSet a, StarSet b, Func<IEnumerable<object?>, IEnumerable<object?>, IEnumerable<object?>> f)
    {
        var res = new StarSet();
        foreach (var x in f(a.Items, b.Items).ToList()) res.Add(x);
        return res;
    }

    public static bool Contains(object? container, object? item)
    {
        switch (container)
        {
            case string s:
                if (item is string sub) return s.Contains(sub, StringComparison.Ordinal);
                if (item is StarUtf16 usub) return s.Contains(usub.Value, StringComparison.Ordinal);
                throw new StarlarkException($"'in <string>' requires string as left operand, not '{Types.Name(item)}'");
            case StarUtf16 u:
                if (item is StarUtf16 usub2) return u.Value.Contains(usub2.Value, StringComparison.Ordinal);
                if (item is string sub2) return u.Value.Contains(sub2, StringComparison.Ordinal);
                throw new StarlarkException($"'in <utf16>' requires string or utf16 as left operand, not '{Types.Name(item)}'");
            case StarBytes b:
                if (item is StarBytes sb) return b.Value.AsSpan().IndexOf(sb.Value) >= 0;
                if (item is BigInteger bi)
                {
                    if (bi < 0 || bi > 255) throw new StarlarkException("int in bytes: value out of range 0..255");
                    return b.Value.Contains((byte)bi);
                }
                throw new StarlarkException($"'in <bytes>' requires bytes or int as left operand, not '{Types.Name(item)}'");
            case StarDict d: return d.ContainsKey(item);
            case StarSet st: return st.Contains(item);
            case StarList l: return l.Items.Any(x => Equal(x, item));
            case StarTuple t: return t.Items.Any(x => Equal(x, item));
            case StarRange r:
                if (item is not BigInteger n) throw new StarlarkException($"'in <range>' requires int as left operand, not '{Types.Name(item)}'");
                if (r.Count.IsZero) return false;
                var off = n - r.Start;
                return (r.Step > 0 ? off >= 0 && n < r.Stop : off <= 0 && n > r.Stop) && (off % r.Step).IsZero;
        }
        throw Err.Unsupported("in", item, container);
    }

    public static object? Add(object? a, object? b)
    {
        switch (a, b)
        {
            case (BigInteger x, BigInteger y): return x + y;
            case (string x, string y): return x + y;
            case (StarUtf16 x, StarUtf16 y): return new StarUtf16(x.Value + y.Value);
            case (StarBytes x, StarBytes y): return new StarBytes([.. x.Value, .. y.Value]);
            case (StarList x, StarList y): return new StarList(x.Items.Concat(y.Items));
            case (StarTuple x, StarTuple y): return new StarTuple([.. x.Items, .. y.Items]);
        }
        if (IsNum(a) && IsNum(b)) return ToDouble(a) + ToDouble(b);
        throw Err.Unsupported("+", a, b);
    }

    public static object? Subtract(object? a, object? b)
    {
        if (a is BigInteger x && b is BigInteger y) return x - y;
        if (IsNum(a) && IsNum(b)) return ToDouble(a) - ToDouble(b);
        if (a is StarSet s1 && b is StarSet s2) return SetOp(s1, s2, (l, _) => l.Where(e => !s2.Contains(e)));
        throw Err.Unsupported("-", a, b);
    }

    private static object? Repeat(object? seq, BigInteger n)
    {
        if (n > int.MaxValue) throw new StarlarkException("Repetition count too large");
        int count = n < 0 ? 0 : (int)n;
        switch (seq)
        {
            case StarUtf16 u:
                return new StarUtf16((string)Repeat(u.Value, n)!);
            case string s:
                if ((long)s.Length * count > int.MaxValue / 2) throw new StarlarkException("Result of repetition is too large");
                return count == 0 ? "" : new StringBuilder(s.Length * count).Insert(0, s, count).ToString();
            case StarBytes b:
                return new StarBytes(Enumerable.Repeat(b.Value, count).SelectMany(x => x).ToArray());
            case StarList l:
                if ((long)l.Count * count > 100_000_000) throw new StarlarkException("Result of repetition is too large");
                return new StarList(Enumerable.Repeat(l.Items, count).SelectMany(x => x));
            case StarTuple t:
                if ((long)t.Items.Length * count > 100_000_000) throw new StarlarkException("Result of repetition is too large");
                return new StarTuple(Enumerable.Repeat(t.Items, count).SelectMany(x => x).ToArray());
        }
        return null;
    }

    public static object? Multiply(object? a, object? b)
    {
        if (a is BigInteger x && b is BigInteger y) return x * y;
        if (IsNum(a) && IsNum(b)) return ToDouble(a) * ToDouble(b);
        if (b is BigInteger n && Repeat(a, n) is { } r1) return r1;
        if (a is BigInteger m && Repeat(b, m) is { } r2) return r2;
        throw Err.Unsupported("*", a, b);
    }

    public static object? Divide(object? a, object? b)
    {
        if (!IsNum(a) || !IsNum(b)) throw Err.Unsupported("/", a, b);
        double d = ToDouble(b);
        if (d == 0) throw new StarlarkException("Division by zero");
        return ToDouble(a) / d;
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
            double d = ToDouble(b);
            if (d == 0) throw new StarlarkException("Floating-point division by zero");
            return Math.Floor(ToDouble(a) / d);
        }
        throw Err.Unsupported("//", a, b);
    }

    public static object? Percent(object? a, object? b)
    {
        if (a is string fmt) return StringFormat.Percent(fmt, b);
        if (a is StarUtf16 ufmt) return new StarUtf16(StringFormat.Percent(ufmt.Value, b));
        if (a is BigInteger x && b is BigInteger y)
        {
            if (y.IsZero) throw new StarlarkException("Integer modulo by zero");
            var r = BigInteger.Remainder(x, y);
            if (!r.IsZero && (r.Sign < 0) != (y.Sign < 0)) r += y;
            return r;
        }
        if (IsNum(a) && IsNum(b))
        {
            double d = ToDouble(b);
            if (d == 0) throw new StarlarkException("Floating-point modulo by zero");
            double r = ToDouble(a) % d;
            if (r != 0 && (r < 0) != (d < 0)) r += d;
            return r;
        }
        throw Err.Unsupported("%", a, b);
    }

    public static object? Negate(object? v) => v switch
    {
        BigInteger i => -i,
        double d => -d,
        _ => throw Err.UnsupportedUnary("-", v),
    };

    public static object? Plus(object? v) => v switch
    {
        BigInteger or double => v,
        _ => throw Err.UnsupportedUnary("+", v),
    };

    public static object? BitNot(object? v) => v is BigInteger i ? -i - 1 : throw Err.UnsupportedUnary("~", v);

    // ---------------------------------------------------------------- iteration

    /// <summary>Iterate any iterable, locking mutable containers against modification while iterating.</summary>
    public static IEnumerable<object?> Iterate(object? v)
    {
        switch (v)
        {
            case StarList l: return IterLocked(l, () => l.Items);
            case StarTuple t: return t.Items;
            case StarDict d: return IterLocked(d, () => d.Keys.ToList());
            case StarSet s: return IterLocked(s, () => s.Items.ToList());
            case StarRange r: return IterateRange(r);
            case StarElems e: return e.Items;
            case StarEnumType et: return et.Instances.Cast<object?>().ToList();
        }
        throw Err.NotIterable(v);
    }

    private static IEnumerable<object?> IterateRange(StarRange r)
    {
        var n = r.Count;
        for (BigInteger i = 0; i < n; i++) yield return r[i];
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

    public static BigInteger Len(object? v) => v switch
    {
        string s => Strings.Len(s),
        StarUtf16 u => u.Value.Length,
        StarBytes b => b.Value.Length,
        StarList l => l.Count,
        StarTuple t => t.Items.Length,
        StarDict d => d.Count,
        StarSet s => s.Count,
        StarEnumType et => et.Instances.Count,
        StarRange r => r.Count,
        _ => throw new StarlarkException($"Type '{Types.Name(v)}' has no len()"),
    };

    // ---------------------------------------------------------------- indexing

    private static int NormIndex(BigInteger i, int len)
    {
        var orig = i;
        if (i < 0) i += len;
        if (i < 0 || i >= len) throw new StarlarkException($"Index `{orig}` is out of bound for a sequence of length {len}");
        return (int)i;
    }

    public static object? Index(object? target, object? index)
    {
        if (target is StarDict d)
        {
            if (d.TryGet(index, out var v)) return v;
            throw new StarlarkException($"Key `{Repr.Of(index)}` not found in dictionary");
        }
        if (target is StarEnumType et)
        {
            if (index is not BigInteger ei) throw Err.BadIndex("index", index);
            return et.Instances[NormIndex(ei, et.Instances.Count)];
        }
        if (target is not (StarList or StarTuple or string or StarUtf16 or StarBytes or StarRange))
        {
            throw new StarlarkException($"Type '{Types.Name(target)}' is not indexable");
        }
        if (index is not BigInteger i)
        {
            throw Err.BadIndex("index", index);
        }
        switch (target)
        {
            case StarList l: return l[NormIndex(i, l.Count)];
            case StarTuple t: return t.Items[NormIndex(i, t.Items.Length)];
            case string s: return Strings.At(s, NormIndex(i, Strings.Len(s)));
            case StarUtf16 u: return new StarUtf16(u.Value[NormIndex(i, u.Value.Length)].ToString());
            case StarBytes b: return new BigInteger(b.Value[NormIndex(i, b.Value.Length)]);
            case StarRange r:
            {
                var n = r.Count;
                var j = i < 0 ? i + n : i;
                if (j < 0 || j >= n) throw new StarlarkException($"Index `{i}` is out of bound");
                return r[j];
            }
        }
        throw new InvalidOperationException();
    }

    public static void SetIndex(object? target, object? index, object? value)
    {
        switch (target)
        {
            case StarDict d:
                d.Set(index, value);
                return;
            case StarList l:
                if (index is not BigInteger i) throw Err.Unsupported("[]=", target, index);
                l.Set(NormIndex(i, l.Count), value);
                return;
        }
        throw new StarlarkException($"Type '{Types.Name(target)}' does not support item assignment");
    }

    /// <summary>Resolve the spec's slice rules to the list of selected element indices.</summary>
    public static List<int> SliceIndices(int len, object? start, object? stop, object? step)
    {
        if (start is not null and not BigInteger) throw Err.BadIndex("slice start", start);
        if (stop is not null and not BigInteger) throw Err.BadIndex("slice stop", stop);
        if (step is not null and not BigInteger) throw Err.BadIndex("slice step", step);

        BigInteger st = step is null ? 1 : (BigInteger)step;
        if (st.IsZero) throw new StarlarkException("Slice step cannot be zero");

        BigInteger N = len;
        BigInteger Adjust(BigInteger v) => v < 0 ? v + N : v;

        BigInteger lo, hi;
        if (st > 0)
        {
            lo = start is null ? 0 : BigInteger.Clamp(Adjust((BigInteger)start), 0, N);
            hi = stop is null ? N : BigInteger.Clamp(Adjust((BigInteger)stop), 0, N);
        }
        else
        {
            lo = start is null ? N - 1 : BigInteger.Clamp(Adjust((BigInteger)start), -1, N - 1);
            hi = stop is null ? -1 : BigInteger.Clamp(Adjust((BigInteger)stop), -1, N - 1);
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

    public static object? Slice(object? target, object? start, object? stop, object? step)
    {
        switch (target)
        {
            case StarList l:
                return new StarList(SliceIndices(l.Count, start, stop, step).Select(i => l[i]));
            case StarTuple t:
                return new StarTuple(SliceIndices(t.Items.Length, start, stop, step).Select(i => t.Items[i]).ToArray());
            case string s:
                return Strings.Pick(s, SliceIndices(Strings.Len(s), start, stop, step));
            case StarUtf16 u:
                // UTF-16 slicing may cut a surrogate pair in half; that is allowed, as in Java and .NET.
                return new StarUtf16(new string(SliceIndices(u.Value.Length, start, stop, step).Select(i => u.Value[i]).ToArray()));
            case StarBytes b:
                return new StarBytes(SliceIndices(b.Value.Length, start, stop, step).Select(i => b.Value[i]).ToArray());
            case StarRange r:
            {
                if (r.Count > int.MaxValue) throw new StarlarkException("range too large to slice");
                var idx = SliceIndices((int)r.Count, start, stop, step);
                return new StarList(idx.Select(i => (object?)r[i]));
            }
        }
        throw new StarlarkException($"Type '{Types.Name(target)}' is not sliceable");
    }
}
