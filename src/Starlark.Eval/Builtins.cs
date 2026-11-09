using System.Globalization;
using System.Numerics;
using System.Text;

namespace Starlark.Eval;

using Args = IReadOnlyList<object?>;
using Kwargs = IReadOnlyList<KeyValuePair<string, object?>>;

/// <summary>The predeclared functions and constants from the spec's "Built-in constants and functions" section.</summary>
public static class Builtins
{
    private static void Add(Dictionary<string, object?> u, string name, Func<Interpreter, ArgReader, object?> fn) =>
        u[name] = new Builtin(name, (it, a, k) =>
        {
            var r = new ArgReader(name, a, k);
            var result = fn(it, r);
            r.Done();
            return result;
        });

    public static void Install(Dictionary<string, object?> u)
    {
        u["None"] = null;
        u["True"] = true;
        u["False"] = false;

        Add(u, "abs", (_, a) => a.Required("x") switch
        {
            BigInteger i => Ops.CheckedInt(BigInteger.Abs(i), "abs"),
            double d => Math.Abs(d),
            var v => throw Err.Param("x", "int or float", v),
        });

        // chr(i): the string holding the one Unicode code point i. Go's wording is kept for the error cases.
        Add(u, "chr", (_, a) =>
        {
            var v = a.Required("i");
            if (v is not BigInteger n) throw new StarlarkException($"chr: got {Types.Name(v)}, want int");
            if (n < 0) throw new StarlarkException($"chr: Unicode code point {n} out of range (<0)");
            if (n > 0x10FFFF) throw new StarlarkException($"chr: Unicode code point U+{n.ToString("X").TrimStart('0')} out of range (>0x10FFFF)");
            if (n >= 0xD800 && n <= 0xDFFF) throw new StarlarkException($"chr: Unicode code point U+{(int)n:X4} is a surrogate, not a character");
            return char.ConvertFromUtf32((int)n);
        });

        // ord(s): the code point of a one-character string, or the byte of a one-byte bytes value. (Go also reads a string cut
        // inside a character as U+FFFD; here such a cut is an error when slicing, so that string cannot exist.)
        Add(u, "ord", (_, a) =>
        {
            var v = a.Required("s");
            switch (v)
            {
                case string s:
                {
                    var runes = CodePoints.Runes(s).ToList();
                    if (runes.Count != 1) throw new StarlarkException($"ord: string encodes {runes.Count} Unicode code points, want 1");
                    return new BigInteger(runes[0]);
                }
                case StarUtf16 u16:
                {
                    var runes = CodePoints.Runes(u16.Value).ToList();
                    if (runes.Count != 1) throw new StarlarkException($"ord: string encodes {runes.Count} Unicode code points, want 1");
                    return new BigInteger(runes[0]);
                }
                case StarBytes b:
                    if (b.Value.Length != 1) throw new StarlarkException($"ord: bytes has length {b.Value.Length}, want 1");
                    return new BigInteger(b.Value[0]);
                default:
                    throw new StarlarkException($"ord: got {Types.Name(v)}, want string or bytes");
            }
        });

        Add(u, "all", (_, a) => Ops.IterateStepped(a.Required("x")).All(Types.Truthy));
        Add(u, "any", (_, a) => Ops.IterateStepped(a.Required("x")).Any(Types.Truthy));
        Add(u, "bool", (_, a) => Types.Truthy(a.Optional("x", false)));

        Add(u, "bytes", (_, a) => a.Required("x") switch
        {
            StarBytes b => b,
            string s => new StarBytes(Encoding.UTF8.GetBytes(s)),
            StarUtf16 u16 => new StarBytes(Encoding.UTF8.GetBytes(u16.Value)),
            var v when v is StarList or StarTuple or StarRange or StarSet or StarElems or StarDict =>
                new StarBytes(BytesOf(v)),
            var v => throw new StarlarkException(
                $"got {Types.Name(v)}, want string, bytes, or iterable of int"),
        });

        u["dict"] = new Builtin("dict", MakeDict);
        InternalMath.Install(u);
        u["json"] = JsonModule.Create();
        u["typing"] = TypingModule.Create();

        // utf16(x): a string whose elements are UTF-16 code units whatever the interpreter's string mode is.
        // utf16(s).bytes() style access is deliberately absent: convert with str() or bytes() instead.
        Add(u, "utf16", (_, a) => Utf16.Convert(a.Required("x")));

        Add(u, "dir", (_, a) => new StarList(Methods.Names(a.Required("x")).OrderBy(x => x, StringComparer.Ordinal).Select(x => (object?)x)));

        Add(u, "enumerate", (_, a) =>
        {
            var seq = a.Required("x");
            var start = a.OptInt("start") ?? BigInteger.Zero;
            var i = start;
            return new StarList(Ops.Materialize(seq).Select(x => (object?)new StarTuple([i++, x])));
        });

        u["fail"] = new Builtin("fail", (_, a, k) =>
        {
            string sep = " ";
            foreach (var kv in k)
            {
                if (kv.Key == "sep") sep = kv.Value as string ?? throw Err.Param("sep", "string", kv.Value);
            }
            throw new StarlarkException("fail: " + JoinChecked(sep, a.Select(Repr.Str).ToList(), "fail: ".Length));
        });

        Add(u, "float", (_, a) => ToFloat(a.Optional("x", 0.0)));

        Add(u, "getattr", (_, a) =>
        {
            var obj = a.Required("x");
            var name = a.Str("name");
            bool hasDefault = a.Has("default");
            var dflt = a.Optional("default");
            if (Methods.TryGetAttr(obj, name, out var v)) return v;
            if (hasDefault) return dflt;
            throw new StarlarkException($"Object of type '{Types.Name(obj)}' has no attribute `{name}`: no field or method");
        });
        Add(u, "hasattr", (_, a) =>
        {
            var obj = a.Required("x");
            return Methods.TryGetAttr(obj, a.Str("name"), out var ignored);
        });

        Add(u, "hash", (_, a) => a.Required("x") switch
        {
            string s => new BigInteger(Hashing.JavaStringHash(s)),
            StarUtf16 u => new BigInteger(Hashing.JavaStringHash(u.Value)),
            StarBytes b => new BigInteger(Hashing.Fnv1a(b.Value)),
            var v => throw Err.Param("x", "string or bytes", v),
        });

        u["int"] = new Builtin("int", MakeInt);

        Add(u, "len", (_, a) => Ops.Len(a.Required("x")));
        Add(u, "list", (_, a) => new StarList(a.Has("x") ? Ops.Materialize(a.Required("x")) : []));

        u["max"] = new Builtin("max", (it, a, k) => MinMax(it, "max", a, k, 1));
        u["min"] = new Builtin("min", (it, a, k) => MinMax(it, "min", a, k, -1));

        u["print"] = new Builtin("print", (it, a, k) =>
        {
            string sep = " ";
            foreach (var kv in k)
            {
                if (kv.Key == "sep") sep = kv.Value as string ?? throw Err.Param("sep", "string", kv.Value);
                else throw new StarlarkException($"Unexpected parameter named `{kv.Key}` for call to `print`");
            }
            var line = JoinChecked(sep, a.Select(Repr.Str).ToList());
            // A task's output is held back and printed by whoever collects it, so output order does not depend on scheduling.
            if (ExecutionContext.Current is { Output: { } buffered } task)
            {
                long bytes = line.Length * 2L;
                if (task.OutputBytes + bytes > ExecutionContext.MaxTaskOutputBytes)
                {
                    throw new ExecutionLimitException("memory", $"A task printed more than {ExecutionContext.MaxTaskOutputBytes} bytes");
                }
                // All the tasks of the run together are capped too, since up to MaxLiveFutures of them may hold output.
                task.Run?.BufferOutput(bytes);
                task.OutputBytes += bytes;
                buffered.Add(line);
            }
            else
            {
                try { it.Print(line); }
                catch (Exception e) when (e is not (StarlarkException or ExecutionLimitException or OperationCanceledException))
                {
                    throw new StarlarkException($"print: the output sink failed: {e.Message}", e);
                }
            }
            return null;
        });

        Add(u, "range", (_, a) =>
        {
            var first = a.Int("start");
            var second = a.OptInt("stop");
            var step = a.OptInt("step") ?? BigInteger.One;
            if (step.IsZero) throw new StarlarkException("range() step argument must not be zero");
            return second is null ? new StarRange(0, first, step) : new StarRange(first, second.Value, step);
        });

        Add(u, "repr", (_, a) => Repr.Of(a.Required("x")));
        Add(u, "reversed", (_, a) => new StarList(Ops.Materialize(a.Required("x")).AsEnumerable().Reverse()));
        Add(u, "set", (_, a) =>
        {
            var s = new StarSet();
            if (a.Has("x")) foreach (var x in Ops.Materialize(a.Required("x"))) s.Add(x);
            return s;
        });

        u["sorted"] = new Builtin("sorted", (it, a, k) =>
        {
            var r = new ArgReader("sorted", a, k);
            var items = Ops.Materialize(r.Required("x"));
            var key = r.Optional("key");
            var reverse = r.Bool("reverse", false);
            r.Done();
            return new StarList(Sort(it, items, key, reverse));
        });

        Add(u, "str", (_, a) =>
        {
            var v = a.Required("x");
            return v is string or StarBytes or StarUtf16 ? Repr.Str(v) : Repr.Of(v);
        });
        Add(u, "tuple", (_, a) => new StarTuple(a.Has("x") ? Ops.Materialize(a.Required("x")).ToArray() : []));
        Add(u, "type", (_, a) => Types.Name(a.Required("x")));

        u["zip"] = new Builtin("zip", (_, a, k) =>
        {
            if (k.Count > 0) throw new StarlarkException($"Unexpected parameter named `{k[0].Key}` for call to `zip`");
            var lists = a.Select(x => Ops.Materialize(x)).ToList();
            int n = lists.Count == 0 ? 0 : lists.Min(l => l.Count);
            return new StarList(Enumerable.Range(0, n).Select(i => (object?)new StarTuple(lists.Select(l => l[i]).ToArray())));
        });

        InstallTypeSystem(u);
    }

    // ---------------------------------------------------------------- types, records, enums, structs

    private static void InstallTypeSystem(Dictionary<string, object?> u)
    {
        Add(u, "isinstance", (_, a) =>
        {
            var value = a.Required("value");
            var ty = TypeEval.ToTy(a.Required("type"));
            return ty.Matches(value);
        });

        // eval_type(T) turns a type expression into a first-class type value that can be matched and printed.
        Add(u, "eval_type", (_, a) => new TypeValue(TypeEval.ToTy(a.Required("type"))));

        Add(u, "field", (_, a) =>
        {
            var ty = TypeEval.ToTy(a.Required("type"));
            bool has = a.Has("default");
            var dflt = a.Optional("default");
            if (has && !ty.Matches(dflt))
            {
                throw new StarlarkException(
                    $"Default value `{Repr.Of(dflt)}` of type `{Types.Name(dflt)}` does not match the field type `{ty}`");
            }
            // A default is shared by every record built from the type, so it must not be changeable.
            if (has) Freezer.Freeze(dflt);
            return new FieldSpec(ty, dflt, has);
        });

        u["record"] = new Builtin("record", (_, a, k) =>
        {
            if (a.Count > 0) throw new StarlarkException("record() takes only named arguments");
            var fields = new List<RecordField>();
            foreach (var (name, spec) in k)
            {
                if (spec is FieldSpec fs)
                {
                    fields.Add(new RecordField(name, fs.Type, fs.Default, fs.HasDefault));
                }
                else
                {
                    fields.Add(new RecordField(name, TypeEval.ToTy(spec), null, false));
                }
            }
            return new StarRecordType(fields);
        });

        u["enum"] = new Builtin("enum", (_, a, k) =>
        {
            if (k.Count > 0) throw new StarlarkException("enum() takes only positional arguments");
            var seen = new StarSet();
            foreach (var x in a)
            {
                if (!seen.Add(x)) throw new StarlarkException($"Duplicate enum value {Repr.Of(x)}");
            }
            return new StarEnumType(a.ToList());
        });

        u["struct"] = new Builtin("struct", (_, a, k) =>
        {
            if (a.Count > 0) throw new StarlarkException("struct() takes only named arguments");
            return new StarStruct(k);
        });
    }

    /// <summary>
    /// <c>bytes(iterable)</c>: a known length is checked against the ceilings before anything is built, and every element
    /// is a step, so a huge range or list is refused or stopped by the limits.
    /// </summary>
    private static byte[] BytesOf(object v)
    {
        if (Ops.KnownLength(v) is { } n)
        {
            if (n >= Ops.MaxRepeatElements) throw new StarlarkException($"bytes: cannot build {n} bytes");
            MemoryBudget.Check((long)n);
        }
        var result = new List<byte>();
        foreach (var x in Ops.IterateStepped(v))
        {
            if (result.Count >= Ops.MaxRepeatElements - 1) throw new StarlarkException("bytes: too many elements");
            result.Add(ByteOf(x));
        }
        return [.. result];
    }

    private static byte ByteOf(object? x)
    {
        if (x is BigInteger i && i >= 0 && i <= 255) return (byte)i;
        throw new StarlarkException($"bytes(): element {Repr.Of(x)} is not an int in the range 0-255");
    }

    // ---------------------------------------------------------------- dict()

    private static void AddPairs(StarDict d, object? source)
    {
        if (source is StarDict src)
        {
            foreach (var kv in src.Entries.ToList()) d.Set(kv.Key, kv.Value);
            return;
        }
        foreach (var pair in Ops.Materialize(source))
        {
            List<object?> kv;
            try
            {
                kv = Ops.Materialize(pair);
            }
            catch (StarlarkException)
            {
                throw new StarlarkException($"dict() element is not iterable: cannot convert '{Types.Name(pair)}' to a (key, value) pair");
            }
            if (kv.Count != 2) throw new StarlarkException($"dict() element has the wrong length: expected 2, got {kv.Count}");
            d.Set(kv[0], kv[1]);
        }
    }

    private static object? MakeDict(Interpreter it, Args a, Kwargs k)
    {
        if (a.Count > 1) throw new StarlarkException($"Too many positional arguments to `dict`: expected 1, got {a.Count}");
        var d = new StarDict();
        if (a.Count == 1)
        {
            if (a[0] is null) throw new StarlarkException("dict(): got NoneType, want iterable");
            AddPairs(d, a[0]);
        }
        foreach (var kv in k) d.Set(kv.Key, kv.Value);
        return d;
    }

    // ---------------------------------------------------------------- int() and float()

    private static object? MakeInt(Interpreter it, Args a, Kwargs k)
    {
        var r = new ArgReader("int", a, k);
        var v = r.Optional("x", BigInteger.Zero);
        bool hasBase = r.Has("base");
        var baseArg = r.Optional("base");
        r.Done();

        if (v is string s)
        {
            int radix = 10;
            if (hasBase)
            {
                if (baseArg is not BigInteger bi) throw Err.Param("base", "int", baseArg);
                if (bi != 0 && (bi < 2 || bi > 36)) throw new StarlarkException($"{bi} is not a valid base: must be 0 or between 2 and 36");
                radix = (int)bi;
            }
            return ParseInt(s, radix);
        }
        if (hasBase)
        {
            throw new StarlarkException("int() can't convert non-string with explicit base");
        }
        switch (v)
        {
            case BigInteger or bool:
                return v is bool b ? (b ? BigInteger.One : BigInteger.Zero) : v;
            case double d:
                if (double.IsNaN(d) || double.IsInfinity(d))
                {
                    throw new StarlarkException($"Cannot convert {Repr.FloatToString(d)} to int");
                }
                return new BigInteger(Math.Truncate(d));
        }
        throw new StarlarkException($"int() cannot convert value of type '{Types.Name(v)}'");
    }

    private static BigInteger ParseInt(string orig, int radix)
    {
        string s = orig;
        bool neg = false;
        if (s.StartsWith('-')) { neg = true; s = s[1..]; }
        else if (s.StartsWith('+')) s = s[1..];

        (string Prefix, int Radix)[] prefixes = [("0x", 16), ("0o", 8), ("0b", 2)];
        string lower = s.ToLowerInvariant();
        foreach (var (prefix, pr) in prefixes)
        {
            // A matching prefix is permitted (base 0 infers the base from it); a mismatched one is part of the digits.
            if (lower.StartsWith(prefix) && (radix == 0 || radix == pr))
            {
                s = s[2..];
                radix = pr;
                break;
            }
        }
        if (radix == 0) radix = 10;
        if (s.Length == 0) throw new StarlarkException($"Cannot parse `{orig}` as an integer in base {radix}");

        // Sub-quadratic and bounded by the int size cap, so a string of a million digits is an error, not a long stall.
        if (s.Length > 4096) ExecutionContext.Current?.Charge(s.Length / 64);
        if (!Syntax.BigIntText.TryParse(s, radix, out var acc, out bool tooLarge))
        {
            throw new StarlarkException(tooLarge
                ? $"int() argument has too many digits: the result would have more than {Ops.MaxIntBits} bits, the limit on the size of an int"
                : $"Cannot parse `{orig}` as an integer in base {radix}");
        }
        return neg ? -acc : acc;
    }

    private static object? ToFloat(object? v)
    {
        switch (v)
        {
            case double: return v;
            case BigInteger i:
            {
                double d = Ops.BigToDouble(i);
                if (double.IsInfinity(d)) throw new StarlarkException("int too large to convert to float");
                return d;
            }
            case bool b: return b ? 1.0 : 0.0;
            case string s:
            {
                string t = s.Trim();
                string body = t.TrimStart('+', '-').ToLowerInvariant();
                bool neg = t.StartsWith('-');
                if (body is "inf" or "infinity") return neg ? double.NegativeInfinity : double.PositiveInfinity;
                if (body == "nan") return double.NaN;
                if (t.Length > 0 && !t.Contains(',') &&
                    double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    if (double.IsInfinity(parsed)) throw new StarlarkException($"float literal too large: `{s}`");
                    return parsed;
                }
                throw new StarlarkException($"Cannot parse `{s}` as a float");
            }
        }
        throw new StarlarkException($"float() cannot convert value of type '{Types.Name(v)}'");
    }

    // ---------------------------------------------------------------- sorted(), min(), max()

    /// <summary>Lists at least this long are sorted in parallel when every key is a plain number or string.</summary>
    internal const int ParallelSortThreshold = 8192;

    /// <summary>
    /// A stable parallel merge sort for keys whose comparison is pure (all strings, all ints, or all non-NaN floats), so no script code
    /// or error can run on a worker thread. Returns null when the keys are not of that kind; the caller then sorts sequentially.
    /// The result is identical to the sequential stable sort.
    /// </summary>
    private static List<object?>? TryParallelSort(List<object?> items, List<object?> keys, bool reverse)
    {
        var first = keys[0];
        Func<object?, bool> ok = first switch
        {
            string => k => k is string,
            BigInteger => k => k is BigInteger,
            double => k => k is double d && !double.IsNaN(d),
            _ => static _ => false,
        };
        if (first is double d0 && double.IsNaN(d0)) return null;
        for (int i = 0; i < keys.Count; i++) if (!ok(keys[i])) return null;

        int n = items.Count, sign = reverse ? -1 : 1;
        var idx = new int[n];
        for (int i = 0; i < n; i++) idx[i] = i;
        // Ties fall back to the original position, so the sort is stable for either direction.
        int Cmp(int a, int b)
        {
            int c = sign * Ops.Compare(keys[a], keys[b]);
            return c != 0 ? c : a.CompareTo(b);
        }

        int parts = Math.Min(Environment.ProcessorCount, 16);
        var bounds = new int[parts + 1];
        for (int p = 0; p <= parts; p++) bounds[p] = (int)((long)n * p / parts);
        var comparer = Comparer<int>.Create(Cmp);
        Parallel.For(0, parts, p => Array.Sort(idx, bounds[p], bounds[p + 1] - bounds[p], comparer));

        var src = idx;
        var dst = new int[n];
        for (int width = 1; width < parts; width *= 2)
        {
            int w = width;
            var from = src;
            var to = dst;
            int pairs = (parts + 2 * w - 1) / (2 * w);
            Parallel.For(0, pairs, q =>
            {
                int lo = bounds[q * 2 * w];
                int mid = bounds[Math.Min(q * 2 * w + w, parts)];
                int hi = bounds[Math.Min(q * 2 * w + 2 * w, parts)];
                int a = lo, b = mid, o = lo;
                while (a < mid && b < hi) to[o++] = Cmp(from[a], from[b]) <= 0 ? from[a++] : from[b++];
                while (a < mid) to[o++] = from[a++];
                while (b < hi) to[o++] = from[b++];
            });
            (src, dst) = (dst, src);
        }

        var result = new List<object?>(n);
        for (int i = 0; i < n; i++) result.Add(items[src[i]]);
        return result;
    }

    internal static List<object?> Sort(Interpreter it, List<object?> items, object? key, bool reverse)
    {
        // The key function is called exactly once per element, in order.
        List<object?> keys = key is null ? items : items.Select(x => it.CallValue(key, [x])).ToList();
        if (items.Count >= ParallelSortThreshold && TryParallelSort(items, keys, reverse) is { } sorted) return sorted;
        var idx = Enumerable.Range(0, items.Count).ToArray();
        int sign = reverse ? -1 : 1;
        var cmp = Comparer<int>.Create((i, j) => sign * Ops.Compare(keys[i], keys[j]));
        try
        {
            // OrderBy is a stable sort, so equal elements keep their original order (also for reverse=True).
            return idx.OrderBy(i => i, cmp).Select(i => items[i]).ToList();
        }
        catch (InvalidOperationException e) when (e.InnerException is StarlarkException or ExecutionLimitException or OperationCanceledException)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException!).Throw();
            throw;   // unreachable
        }
        catch (InvalidOperationException e) when (e.InnerException is { } inner)
        {
            // A host value's CompareTo threw something else: host code failing, reported as such.
            throw new StarlarkException($"Comparison failed in sorted: {inner.Message}", inner);
        }
    }

    /// <summary>
    /// <c>string.Join</c> for messages built from script values (<c>print</c>, <c>fail</c>), checked against the string ceiling
    /// and the memory budget before anything is built. <paramref name="extra"/> counts chars the caller adds.
    /// </summary>
    internal static string JoinChecked(string sep, List<string> parts, long extra = 0)
    {
        long total = extra + (parts.Count > 1 ? (long)sep.Length * (parts.Count - 1) : 0);
        foreach (var p in parts) total += p.Length;
        MemoryBudget.CheckString(total);
        return string.Join(sep, parts);
    }

    private static object? MinMax(Interpreter it, string name, Args a, Kwargs k, int sign)
    {
        object? key = null;
        foreach (var kv in k)
        {
            if (kv.Key == "key") key = kv.Value;
            else throw new StarlarkException($"Unexpected parameter named `{kv.Key}` for call to `{name}`");
        }
        if (a.Count == 0) throw new StarlarkException($"{name}() requires at least one positional argument");
        var items = a.Count == 1 ? Ops.Materialize(a[0]) : a.ToList();
        if (items.Count == 0) throw new StarlarkException($"Empty sequence: {name}() argument is an empty sequence");
        var keys = key is null ? items : items.Select(x => it.CallValue(key, [x])).ToList();
        int best = 0;
        for (int i = 1; i < items.Count; i++)
        {
            if (Ops.Compare(keys[i], keys[best]) * sign > 0) best = i;
        }
        return items[best];
    }
}
