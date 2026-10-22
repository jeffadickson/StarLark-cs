using System.Globalization;
using System.Numerics;
using System.Text;

namespace Starlark.Eval;

using Args = IReadOnlyList<object?>;
using Kwargs = IReadOnlyList<KeyValuePair<string, object?>>;

/// <summary>Helper for binding the arguments of native functions.</summary>
internal sealed class ArgReader(string fn, Args args, Kwargs kwargs)
{
    private int _pos;

    public object? Required(string name)
    {
        if (_pos < args.Count) return args[_pos++];
        foreach (var kv in kwargs) if (kv.Key == name) return kv.Value;
        throw new StarlarkException($"Missing parameter `{name}` for call to `{fn}`");
    }

    public object? Optional(string name, object? dflt = null)
    {
        if (_pos < args.Count) return args[_pos++];
        foreach (var kv in kwargs) if (kv.Key == name) return kv.Value;
        return dflt;
    }

    public bool Has(string name) => _pos < args.Count || kwargs.Any(k => k.Key == name);

    public object? Kw(string name, object? dflt = null)
    {
        foreach (var kv in kwargs) if (kv.Key == name) return kv.Value;
        return dflt;
    }

    public void Done()
    {
        if (_pos < args.Count) throw new StarlarkException($"Too many positional arguments to `{fn}`");
    }
}

public static class Builtins
{
    private static BigInteger Int(object? v, string what = "argument") =>
        v is BigInteger i ? i : v is bool b ? (b ? 1 : 0) : throw new StarlarkException($"Expected int for {what}, got '{Types.Name(v)}'");

    internal static string Str(object? v, string what = "argument") =>
        v is string s ? s : throw new StarlarkException($"Expected string for {what}, got '{Types.Name(v)}'");

    private static void Add(Dictionary<string, object?> u, string name, Func<Interpreter, Args, Kwargs, object?> fn) =>
        u[name] = new Builtin(name, fn);

    public static void Install(Dictionary<string, object?> u)
    {
        u["None"] = null;
        u["True"] = true;
        u["False"] = false;

        Add(u, "len", (_, a, k) => new BigInteger(Ops.Len(new ArgReader("len", a, k).Required("x"))));
        Add(u, "type", (_, a, k) => Types.Name(new ArgReader("type", a, k).Required("x")));
        Add(u, "repr", (_, a, k) => Repr.Of(new ArgReader("repr", a, k).Required("x")));
        Add(u, "str", (_, a, k) => Repr.Str(new ArgReader("str", a, k).Required("x")));
        Add(u, "bool", (_, a, k) => Types.Truthy(new ArgReader("bool", a, k).Optional("x", false)));
        Add(u, "abs", (_, a, k) => new ArgReader("abs", a, k).Required("x") switch
        {
            BigInteger i => BigInteger.Abs(i),
            double d => Math.Abs(d),
            var v => throw new StarlarkException($"abs() not supported on '{Types.Name(v)}'"),
        });
        Add(u, "hash", (_, a, k) => new BigInteger(new ArgReader("hash", a, k).Required("x") is string s ? StableStringHash(s)
            : throw new StarlarkException("hash() requires a string")));

        Add(u, "print", (it, a, k) =>
        {
            string sep = k.FirstOrDefault(kv => kv.Key == "sep").Value as string ?? " ";
            it.Print(string.Join(sep, a.Select(Repr.Str)));
            return null;
        });
        Add(u, "fail", (_, a, k) =>
        {
            string sep = k.FirstOrDefault(kv => kv.Key == "sep").Value as string ?? " ";
            throw new StarlarkException("fail: " + string.Join(sep, a.Select(Repr.Str)));
        });

        Add(u, "int", (_, a, k) => ToInt(a, k));
        Add(u, "float", (_, a, k) => ToFloat(new ArgReader("float", a, k).Optional("x", 0.0)));
        Add(u, "list", (_, a, k) => new StarList(Ops.Iterate(new ArgReader("list", a, k).Optional("x", new StarTuple([])))));
        Add(u, "tuple", (_, a, k) => new StarTuple(Ops.Iterate(new ArgReader("tuple", a, k).Optional("x", new StarTuple([]))).ToArray()));
        Add(u, "dict", (_, a, k) =>
        {
            var d = new StarDict();
            if (a.Count > 1) throw new StarlarkException("dict() takes at most 1 positional argument");
            if (a.Count == 1)
            {
                if (a[0] is StarDict src)
                {
                    foreach (var kv in src.Entries) d.Set(kv.Key, kv.Value);
                }
                else
                {
                    foreach (var pair in Ops.Iterate(a[0]))
                    {
                        var kv = Ops.Iterate(pair).ToList();
                        if (kv.Count != 2) throw new StarlarkException("dict() element is not a (key, value) pair");
                        d.Set(kv[0], kv[1]);
                    }
                }
            }
            foreach (var kv in k) d.Set(kv.Key, kv.Value);
            return d;
        });
        Add(u, "bytes", (_, a, k) => new ArgReader("bytes", a, k).Required("x") switch
        {
            string s => new StarBytes(Encoding.UTF8.GetBytes(s)),
            StarBytes b => b,
            var v => new StarBytes(Ops.Iterate(v).Select(x => (byte)Int(x)).ToArray()),
        });

        Add(u, "range", (_, a, k) =>
        {
            var r = new ArgReader("range", a, k);
            var first = Int(r.Required("start"));
            var second = r.Optional("stop");
            var step = r.Optional("step", BigInteger.One);
            var st = Int(step);
            if (st.IsZero) throw new StarlarkException("range() step argument must not be zero");
            return second is null ? new StarRange(0, first, st) : new StarRange(first, Int(second), st);
        });

        Add(u, "enumerate", (_, a, k) =>
        {
            var r = new ArgReader("enumerate", a, k);
            var seq = r.Required("it");
            var start = Int(r.Optional("start", BigInteger.Zero));
            int i = 0;
            return new StarList(Ops.Iterate(seq).ToList().Select(x => (object?)new StarTuple([start + i++, x])));
        });
        Add(u, "zip", (_, a, _) =>
        {
            var lists = a.Select(x => Ops.Iterate(x).ToList()).ToList();
            int n = lists.Count == 0 ? 0 : lists.Min(l => l.Count);
            return new StarList(Enumerable.Range(0, n).Select(i => (object?)new StarTuple(lists.Select(l => l[i]).ToArray())));
        });
        Add(u, "reversed", (_, a, k) => new StarList(Ops.Iterate(new ArgReader("reversed", a, k).Required("x")).Reverse()));
        Add(u, "all", (_, a, k) => Ops.Iterate(new ArgReader("all", a, k).Required("x")).All(Types.Truthy));
        Add(u, "any", (_, a, k) => Ops.Iterate(new ArgReader("any", a, k).Required("x")).Any(Types.Truthy));

        Add(u, "sorted", (it, a, k) =>
        {
            var r = new ArgReader("sorted", a, k);
            var items = Ops.Iterate(r.Required("x")).ToList();
            return new StarList(Sort(it, items, r.Kw("key"), Types.Truthy(r.Kw("reverse", false))));
        });
        Add(u, "min", (it, a, k) => MinMax(it, "min", a, k, -1));
        Add(u, "max", (it, a, k) => MinMax(it, "max", a, k, 1));

        Add(u, "getattr", (_, a, k) =>
        {
            var r = new ArgReader("getattr", a, k);
            var obj = r.Required("x");
            var name = Str(r.Required("attr"));
            var dflt = r.Optional("default", Interpreter.Missing.Value);
            if (Methods.TryGetAttr(obj, name, out var v)) return v;
            if (dflt is Interpreter.Missing) throw new StarlarkException($"Object of type '{Types.Name(obj)}' has no attribute `{name}`");
            return dflt;
        });
        Add(u, "hasattr", (it0, a, k) =>
        {
            var r = new ArgReader("hasattr", a, k);
            return Methods.TryGetAttr(r.Required("x"), Str(r.Required("attr")), out _);
        });
        Add(u, "dir", (_, a, k) => new StarList(Methods.Names(new ArgReader("dir", a, k).Required("x")).OrderBy(x => x, StringComparer.Ordinal)));
    }

    private static long StableStringHash(string s)
    {
        // FNV-1a over UTF-8, folded to a positive 32-bit value; stable across runs and platforms.
        uint h = 2166136261;
        foreach (byte b in Encoding.UTF8.GetBytes(s)) h = (h ^ b) * 16777619;
        return h & 0x7fffffff;
    }

    private static object? ToInt(Args a, Kwargs k)
    {
        var r = new ArgReader("int", a, k);
        var v = r.Required("x");
        var baseArg = r.Optional("base");
        switch (v)
        {
            case BigInteger or bool when baseArg is null:
                return Int(v);
            case double d when baseArg is null:
                if (double.IsNaN(d) || double.IsInfinity(d)) throw new StarlarkException($"Cannot convert {Repr.FloatToString(d)} to int");
                return new BigInteger(Math.Truncate(d));
            case string s:
                return ParseInt(s, baseArg is null ? 10 : (int)Int(baseArg), baseArg is null);
        }
        throw new StarlarkException($"int() cannot convert '{Types.Name(v)}'");
    }

    private static BigInteger ParseInt(string s, int radix, bool defaultBase)
    {
        string orig = s;
        s = s.Trim();
        bool neg = false;
        if (s.StartsWith('-')) { neg = true; s = s[1..]; }
        else if (s.StartsWith('+')) s = s[1..];
        string lower = s.ToLowerInvariant();
        (string prefix, int pr)[] prefixes = [("0x", 16), ("0o", 8), ("0b", 2)];
        foreach (var (prefix, pr) in prefixes)
        {
            if (lower.StartsWith(prefix) && (radix == pr || radix == 0 || defaultBase && false))
            {
                s = s[2..];
                radix = pr;
                break;
            }
        }
        if (radix == 0) radix = 10;
        if (defaultBase) radix = 10;
        if (s.Length == 0) throw new StarlarkException($"Cannot parse `{orig}` as an integer in base {radix}");
        BigInteger acc = 0;
        foreach (char c in s)
        {
            int d = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'z' ? c - 'a' + 10 : c is >= 'A' and <= 'Z' ? c - 'A' + 10 : 99;
            if (d >= radix) throw new StarlarkException($"Cannot parse `{orig}` as an integer in base {radix}");
            acc = acc * radix + d;
        }
        return neg ? -acc : acc;
    }

    private static object? ToFloat(object? v) => v switch
    {
        double => v,
        BigInteger i => (double)i,
        bool b => b ? 1.0 : 0.0,
        string s => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d : throw new StarlarkException($"Cannot parse `{s}` as float"),
        _ => throw new StarlarkException($"float() cannot convert '{Types.Name(v)}'"),
    };

    internal static List<object?> Sort(Interpreter it, List<object?> items, object? key, bool reverse)
    {
        var keys = key is null ? items : items.Select(x => it.CallValue(key, [x])).ToList();
        var idx = Enumerable.Range(0, items.Count).ToList();
        // A stable merge sort via OrderBy keeps equal elements in order, as the spec requires.
        var sorted = idx.OrderBy(i => keys[i], Comparer<object?>.Create(Ops.Compare)).ToList();
        if (reverse)
        {
            // Reverse while preserving stability: sort descending with ties in original order.
            sorted = idx.OrderByDescending(i => keys[i], Comparer<object?>.Create(Ops.Compare)).ToList();
        }
        return sorted.Select(i => items[i]).ToList();
    }

    private static object? MinMax(Interpreter it, string name, Args a, Kwargs k, int sign)
    {
        var items = a.Count == 1 ? Ops.Iterate(a[0]).ToList() : a.ToList();
        if (items.Count == 0) throw new StarlarkException($"{name}() argument is an empty sequence");
        var key = k.FirstOrDefault(kv => kv.Key == "key").Value;
        var keys = key is null ? items : items.Select(x => it.CallValue(key, [x])).ToList();
        int best = 0;
        for (int i = 1; i < items.Count; i++)
        {
            if (Ops.Compare(keys[i], keys[best]) * sign > 0) best = i;
        }
        return items[best];
    }
}
