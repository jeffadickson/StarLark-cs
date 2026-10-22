using System.Globalization;
using System.Numerics;
using System.Text;

namespace Starlark.Eval;

using Args = IReadOnlyList<object?>;
using Kwargs = IReadOnlyList<KeyValuePair<string, object?>>;

/// <summary>Methods on the built-in types: strings, lists, dicts, bytes.</summary>
public static class Methods
{
    private static readonly Dictionary<string, string[]> Table = new()
    {
        ["string"] =
        [
            "capitalize", "codepoints", "count", "elems", "endswith", "find", "format", "index", "isalnum", "isalpha",
            "isdigit", "islower", "isspace", "istitle", "isupper", "join", "lower", "lstrip", "partition", "removeprefix",
            "removesuffix", "replace", "rfind", "rindex", "rpartition", "rsplit", "rstrip", "split", "splitlines",
            "startswith", "strip", "title", "upper",
        ],
        ["list"] = ["append", "clear", "extend", "index", "insert", "pop", "remove"],
        ["dict"] = ["clear", "get", "items", "keys", "pop", "popitem", "setdefault", "update", "values"],
        ["bytes"] = ["elems"],
    };

    public static IEnumerable<string> Names(object? v) => Table.TryGetValue(Types.Name(v), out var n) ? n : [];

    public static bool TryGetAttr(object? target, string name, out object? value)
    {
        if (Names(target).Contains(name))
        {
            value = Bind(target, name);
            return true;
        }
        value = null;
        return false;
    }

    public static object? GetAttr(object? target, string name)
    {
        if (TryGetAttr(target, name, out var v)) return v;
        throw new StarlarkException($"Object of type '{Types.Name(target)}' has no attribute `{name}`");
    }

    private static object Bind(object? self, string name) =>
        new Builtin(name, (it, a, k) => Call(it, self, name, a, k));

    private static StarlarkException Arity(string fn, string expected) => new($"`{fn}` expects {expected}");

    private static string S(object? v, string fn) =>
        v is string s ? s : throw new StarlarkException($"Expected string argument to `{fn}`, got '{Types.Name(v)}'");

    private static int I(object? v, string fn) =>
        v is BigInteger i ? (int)i : throw new StarlarkException($"Expected int argument to `{fn}`, got '{Types.Name(v)}'");

    private static object? Call(Interpreter it, object? self, string name, Args a, Kwargs k) =>
        self switch
        {
            string s => StringMethod(it, s, name, a, k),
            StarList l => ListMethod(l, name, a, k),
            StarDict d => DictMethod(d, name, a, k),
            StarBytes b => b.Value.Select(x => (object?)new BigInteger(x)).ToList() is var xs && name == "elems" ? new StarList(xs) : null,
            _ => throw new StarlarkException($"no method `{name}`"),
        };

    // ---------------------------------------------------------------- strings

    /// <summary>Resolve optional [start, end) arguments (in code points) over a string of length <paramref name="len"/>.</summary>
    private static (int Start, int End) Bounds(Args a, int first, int len)
    {
        int Clamp(object? v, int dflt)
        {
            if (v is null) return dflt;
            var n = (BigInteger)v;
            if (n < 0) n += len;
            return (int)BigInteger.Clamp(n, 0, len);
        }
        return (Clamp(a.Count > first ? a[first] : null, 0), Clamp(a.Count > first + 1 ? a[first + 1] : null, len));
    }

    private static object? StringMethod(Interpreter it, string s, string name, Args a, Kwargs k)
    {
        switch (name)
        {
            case "upper": return s.ToUpperInvariant();
            case "lower": return s.ToLowerInvariant();
            case "capitalize": return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
            case "title":
            {
                var sb = new StringBuilder();
                bool prevLetter = false;
                foreach (char c in s)
                {
                    sb.Append(char.IsLetter(c) ? (prevLetter ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)) : c);
                    prevLetter = char.IsLetter(c);
                }
                return sb.ToString();
            }
            case "isalpha": return s.Length > 0 && s.All(char.IsLetter);
            case "isdigit": return s.Length > 0 && s.All(char.IsDigit);
            case "isalnum": return s.Length > 0 && s.All(char.IsLetterOrDigit);
            case "isspace": return s.Length > 0 && s.All(char.IsWhiteSpace);
            case "islower": return s.Any(char.IsLetter) && !s.Any(char.IsUpper);
            case "isupper": return s.Any(char.IsLetter) && !s.Any(char.IsLower);
            case "istitle":
                return s.Any(char.IsLetter) && (string)StringMethod(it, s, "title", [], []) == s;

            case "elems": return new StarList(CodePoints.Split(s).Select(x => (object?)x));
            case "codepoints":
                return new StarList(CodePoints.Split(s).Select(x => (object?)new BigInteger(char.ConvertToUtf32(x, 0))));

            case "strip": return Strip(s, a, true, true);
            case "lstrip": return Strip(s, a, true, false);
            case "rstrip": return Strip(s, a, false, true);

            case "startswith": return StartsEnds(s, a, true);
            case "endswith": return StartsEnds(s, a, false);

            case "removeprefix":
            {
                var p = S(a[0], name);
                return s.StartsWith(p, StringComparison.Ordinal) ? s[p.Length..] : s;
            }
            case "removesuffix":
            {
                var p = S(a[0], name);
                return s.EndsWith(p, StringComparison.Ordinal) ? s[..^p.Length] : s;
            }

            case "find": return Find(s, a, name, last: false, throwIfMissing: false);
            case "rfind": return Find(s, a, name, last: true, throwIfMissing: false);
            case "index": return Find(s, a, name, last: false, throwIfMissing: true);
            case "rindex": return Find(s, a, name, last: true, throwIfMissing: true);

            case "count":
            {
                var sub = S(a[0], name);
                var cps = CodePoints.Split(s);
                var (st, en) = Bounds(a, 1, cps.Count);
                string region = string.Concat(cps.Skip(st).Take(Math.Max(0, en - st)));
                if (sub.Length == 0) return new BigInteger(CodePoints.Length(region) + 1);
                int n = 0, pos = 0;
                while ((pos = region.IndexOf(sub, pos, StringComparison.Ordinal)) >= 0) { n++; pos += sub.Length; }
                return new BigInteger(n);
            }

            case "replace":
            {
                var old = S(a[0], name);
                var neu = S(a[1], name);
                int max = a.Count > 2 && a[2] is BigInteger m ? (int)m : -1;
                if (max < 0 && old.Length > 0) return s.Replace(old, neu, StringComparison.Ordinal);
                var sb = new StringBuilder();
                int pos = 0, done = 0;
                while (max < 0 || done < max)
                {
                    int idx = old.Length == 0 ? (pos <= s.Length ? pos : -1) : s.IndexOf(old, pos, StringComparison.Ordinal);
                    if (idx < 0) break;
                    sb.Append(s, pos, idx - pos).Append(neu);
                    if (old.Length == 0)
                    {
                        if (idx < s.Length) sb.Append(s[idx]);
                        pos = idx + 1;
                    }
                    else
                    {
                        pos = idx + old.Length;
                    }
                    done++;
                }
                if (pos < s.Length) sb.Append(s, pos, s.Length - pos);
                return sb.ToString();
            }

            case "join":
            {
                var parts = Ops.Iterate(a[0]).Select(x => x is string p ? p : throw new StarlarkException($"join() expects strings, got '{Types.Name(x)}'"));
                return string.Join(s, parts);
            }

            case "split": return Split(s, a, rightSplit: false);
            case "rsplit": return Split(s, a, rightSplit: true);

            case "splitlines":
            {
                bool keep = a.Count > 0 && Types.Truthy(a[0]);
                var lines = new List<object?>();
                int start = 0;
                for (int i = 0; i < s.Length; i++)
                {
                    if (s[i] == '\n' || s[i] == '\r')
                    {
                        int end = i;
                        if (s[i] == '\r' && i + 1 < s.Length && s[i + 1] == '\n') i++;
                        lines.Add(keep ? s[start..(i + 1)] : s[start..end]);
                        start = i + 1;
                    }
                }
                if (start < s.Length) lines.Add(s[start..]);
                return new StarList(lines);
            }

            case "partition":
            case "rpartition":
            {
                var sep = S(a[0], name);
                if (sep.Length == 0) throw new StarlarkException("Empty separator");
                int idx = name == "partition" ? s.IndexOf(sep, StringComparison.Ordinal) : s.LastIndexOf(sep, StringComparison.Ordinal);
                if (idx < 0)
                {
                    return name == "partition" ? new StarTuple([s, "", ""]) : new StarTuple(["", "", s]);
                }
                return new StarTuple([s[..idx], sep, s[(idx + sep.Length)..]]);
            }

            case "format": return StringFormat.Format(s, a, k);
        }
        throw new StarlarkException($"string has no method `{name}`");
    }

    private static string Strip(string s, Args a, bool left, bool right)
    {
        char[]? chars = a.Count > 0 && a[0] is string c ? c.ToCharArray() : null;
        bool Strippable(char ch) => chars is null ? char.IsWhiteSpace(ch) : chars.Contains(ch);
        int st = 0, en = s.Length;
        if (left) while (st < en && Strippable(s[st])) st++;
        if (right) while (en > st && Strippable(s[en - 1])) en--;
        return s[st..en];
    }

    private static object? StartsEnds(string s, Args a, bool starts)
    {
        bool Test(string p) => starts ? s.StartsWith(p, StringComparison.Ordinal) : s.EndsWith(p, StringComparison.Ordinal);
        return a[0] switch
        {
            string p => Test(p),
            StarTuple t => t.Items.Any(x => Test(S(x, starts ? "startswith" : "endswith"))),
            var v => throw new StarlarkException($"Expected string or tuple of strings, got '{Types.Name(v)}'"),
        };
    }

    private static object? Find(string s, Args a, string name, bool last, bool throwIfMissing)
    {
        var sub = S(a[0], name);
        var cps = CodePoints.Split(s);
        var (st, en) = Bounds(a, 1, cps.Count);
        string region = string.Concat(cps.Skip(st).Take(Math.Max(0, en - st)));
        int idx = last ? region.LastIndexOf(sub, StringComparison.Ordinal) : region.IndexOf(sub, StringComparison.Ordinal);
        if (idx < 0)
        {
            if (throwIfMissing) throw new StarlarkException($"Substring `{sub}` not found in `{s}`");
            return new BigInteger(-1);
        }
        return new BigInteger(st + CodePoints.Length(region[..idx]));
    }

    private static object? Split(string s, Args a, bool rightSplit)
    {
        int max = a.Count > 1 && a[1] is BigInteger m ? (int)m : -1;
        var parts = new List<string>();
        if (a.Count == 0 || a[0] is null)
        {
            // Split on runs of whitespace.
            var words = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (max >= 0 && words.Count > max + 1)
            {
                if (rightSplit)
                {
                    var tail = words.Skip(words.Count - max).ToList();
                    string head = s.TrimEnd();
                    for (int i = 0; i < max; i++) head = head[..head.LastIndexOfAny([' ', '\t', '\n', '\r'])].TrimEnd();
                    words = [head, .. tail];
                }
                else
                {
                    string rest = s.TrimStart();
                    var head = new List<string>();
                    for (int i = 0; i < max; i++)
                    {
                        int sp = rest.IndexOfAny([' ', '\t', '\n', '\r']);
                        head.Add(rest[..sp]);
                        rest = rest[sp..].TrimStart();
                    }
                    head.Add(rest);
                    words = head;
                }
            }
            return new StarList(words.Select(x => (object?)x));
        }
        var sep = S(a[0], "split");
        if (sep.Length == 0) throw new StarlarkException("Empty separator");
        if (!rightSplit)
        {
            int pos = 0;
            while (max < 0 || parts.Count < max)
            {
                int idx = s.IndexOf(sep, pos, StringComparison.Ordinal);
                if (idx < 0) break;
                parts.Add(s[pos..idx]);
                pos = idx + sep.Length;
            }
            parts.Add(s[pos..]);
        }
        else
        {
            int end = s.Length;
            while (max < 0 || parts.Count < max)
            {
                int idx = end - sep.Length < 0 ? -1 : s.LastIndexOf(sep, end - sep.Length, StringComparison.Ordinal);
                if (idx < 0) break;
                parts.Insert(0, s[(idx + sep.Length)..end]);
                end = idx;
            }
            parts.Insert(0, s[..end]);
        }
        return new StarList(parts.Select(x => (object?)x));
    }

    // ---------------------------------------------------------------- lists

    private static object? ListMethod(StarList l, string name, Args a, Kwargs k)
    {
        switch (name)
        {
            case "append": l.Add(a[0]); return null;
            case "extend": l.AddRange(Ops.Iterate(a[0]).ToList()); return null;
            case "clear": l.Clear(); return null;
            case "insert":
            {
                int i = I(a[0], name);
                if (i < 0) i = Math.Max(0, i + l.Count);
                l.Insert(Math.Min(i, l.Count), a[1]);
                return null;
            }
            case "pop":
            {
                if (l.Count == 0) throw new StarlarkException("pop from empty list");
                int i = a.Count > 0 ? I(a[0], name) : -1;
                if (i < 0) i += l.Count;
                if (i < 0 || i >= l.Count) throw new StarlarkException($"Index `{i}` is out of bounds");
                var v = l[i];
                l.RemoveAt(i);
                return v;
            }
            case "remove":
            {
                for (int i = 0; i < l.Count; i++)
                {
                    if (Ops.Equal(l[i], a[0]))
                    {
                        l.RemoveAt(i);
                        return null;
                    }
                }
                throw new StarlarkException($"Item {Repr.Of(a[0])} not found in list");
            }
            case "index":
            {
                int start = a.Count > 1 ? I(a[1], name) : 0;
                int end = a.Count > 2 ? I(a[2], name) : l.Count;
                for (int i = Math.Max(0, start < 0 ? start + l.Count : start); i < Math.Min(l.Count, end < 0 ? end + l.Count : end); i++)
                {
                    if (Ops.Equal(l[i], a[0])) return new BigInteger(i);
                }
                throw new StarlarkException($"Item {Repr.Of(a[0])} not found in list");
            }
        }
        throw new StarlarkException($"list has no method `{name}`");
    }

    // ---------------------------------------------------------------- dicts

    private static object? DictMethod(StarDict d, string name, Args a, Kwargs k)
    {
        switch (name)
        {
            case "get":
                return d.TryGet(a[0], out var v) ? v : a.Count > 1 ? a[1] : null;
            case "keys": return new StarList(d.Keys);
            case "values": return new StarList(d.Values);
            case "items": return new StarList(d.Entries.Select(kv => (object?)new StarTuple([kv.Key, kv.Value])));
            case "clear": d.Clear(); return null;
            case "pop":
                if (d.Remove(a[0], out var popped)) return popped;
                if (a.Count > 1) return a[1];
                throw new StarlarkException($"Key `{Repr.Of(a[0])}` not found in dictionary");
            case "popitem":
            {
                if (d.Count == 0) throw new StarlarkException("popitem(): dictionary is empty");
                var first = d.Entries.First();
                d.Remove(first.Key, out _);
                return new StarTuple([first.Key, first.Value]);
            }
            case "setdefault":
            {
                if (d.TryGet(a[0], out var existing)) return existing;
                var dflt = a.Count > 1 ? a[1] : null;
                d.Set(a[0], dflt);
                return dflt;
            }
            case "update":
            {
                if (a.Count > 0)
                {
                    if (a[0] is StarDict src)
                    {
                        foreach (var kv in src.Entries.ToList()) d.Set(kv.Key, kv.Value);
                    }
                    else
                    {
                        foreach (var pair in Ops.Iterate(a[0]).ToList())
                        {
                            var kv = Ops.Iterate(pair).ToList();
                            if (kv.Count != 2) throw new StarlarkException("update() element is not a (key, value) pair");
                            d.Set(kv[0], kv[1]);
                        }
                    }
                }
                foreach (var kv in k) d.Set(kv.Key, kv.Value);
                return null;
            }
        }
        throw new StarlarkException($"dict has no method `{name}`");
    }
}
