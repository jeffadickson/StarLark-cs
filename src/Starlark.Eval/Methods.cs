using System.Numerics;
using System.Text;

namespace Starlark.Eval;

/// <summary>The method tables for the built-in types, as listed in the spec's "Built-in methods" section.</summary>
public static class Methods
{
    private static readonly Dictionary<string, Method> StringMethods = new();
    private static readonly Dictionary<string, Method> BytesMethods = new();
    private static readonly Dictionary<string, Method> ListMethods = new();
    private static readonly Dictionary<string, Method> DictMethods = new();
    private static readonly Dictionary<string, Method> SetMethods = new();

    static Methods()
    {
        RegisterStrings();
        RegisterBytes();
        RegisterLists();
        RegisterDicts();
        RegisterSets();
    }

    private static Dictionary<string, Method>? TableFor(object? v) => v switch
    {
        string => StringMethods,
        StarBytes => BytesMethods,
        StarList => ListMethods,
        StarDict => DictMethods,
        StarSet => SetMethods,
        _ => null,
    };

    public static IEnumerable<string> Names(object? v) => (IEnumerable<string>?)TableFor(v)?.Keys ?? [];

    public static bool TryGetAttr(object? target, string name, out object? value)
    {
        if (TableFor(target) is { } t && t.TryGetValue(name, out var m))
        {
            value = new BoundMethod(name, target!, m);
            return true;
        }
        value = null;
        return false;
    }

    public static object? GetAttr(object? target, string name)
    {
        if (TryGetAttr(target, name, out var v)) return v;
        throw new StarlarkException($"Object of type '{Types.Name(target)}' has no attribute `{name}`: no field or method named `{name}`");
    }

    // ---------------------------------------------------------------- helpers

    private static string StrArg(ArgReader a, string name) => a.Str(name);

    /// <summary>Resolve optional [start, end) arguments against a sequence length, clamping as in the spec's Indexing rules.</summary>
    private static (int Start, int End) Bounds(ArgReader a, int len)
    {
        int Clamp(BigInteger? v, int dflt)
        {
            if (v is null) return dflt;
            var n = v.Value;
            if (n < 0) n += len;
            return (int)BigInteger.Clamp(n, 0, len);
        }
        var s = Clamp(a.OptInt("start"), 0);
        var e = Clamp(a.OptInt("end"), len);
        return (s, e);
    }

    private static bool IsSpace(char c) => char.IsWhiteSpace(c) || c == '\u0085' || c == ' ';

    private static StarList StrList(IEnumerable<string> xs) => new(xs.Select(x => (object?)x));

    // ---------------------------------------------------------------- strings

    private static void RegisterStrings()
    {
        var m = StringMethods;

        m["capitalize"] = (_, self, a) =>
        {
            a.Done();
            var s = (string)self;
            return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
        };
        m["upper"] = (_, self, a) => { a.Done(); return ((string)self).ToUpperInvariant(); };
        m["lower"] = (_, self, a) => { a.Done(); return ((string)self).ToLowerInvariant(); };

        m["title"] = (_, self, a) =>
        {
            a.Done();
            var sb = new StringBuilder();
            bool prevLetter = false;
            foreach (char c in (string)self)
            {
                bool letter = char.IsLetter(c);
                sb.Append(letter ? (prevLetter ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)) : c);
                prevLetter = letter;
            }
            return sb.ToString();
        };

        m["isalpha"] = (_, self, a) => { a.Done(); var s = (string)self; return s.Length > 0 && s.All(char.IsLetter); };
        m["isdigit"] = (_, self, a) => { a.Done(); var s = (string)self; return s.Length > 0 && s.All(char.IsDigit); };
        m["isalnum"] = (_, self, a) => { a.Done(); var s = (string)self; return s.Length > 0 && s.All(char.IsLetterOrDigit); };
        m["isspace"] = (_, self, a) => { a.Done(); var s = (string)self; return s.Length > 0 && s.All(IsSpace); };
        m["islower"] = (_, self, a) =>
        {
            a.Done();
            var s = (string)self;
            return s.Any(c => char.IsLower(c) || char.IsUpper(c)) && !s.Any(char.IsUpper);
        };
        m["isupper"] = (_, self, a) =>
        {
            a.Done();
            var s = (string)self;
            return s.Any(c => char.IsLower(c) || char.IsUpper(c)) && !s.Any(char.IsLower);
        };
        m["istitle"] = (_, self, a) =>
        {
            a.Done();
            var s = (string)self;
            bool anyCased = false, prevCased = false;
            foreach (char c in s)
            {
                bool cased = char.IsUpper(c) || char.IsLower(c);
                if (cased)
                {
                    anyCased = true;
                    // A letter starting a word must be uppercase; one continuing a word, lowercase.
                    if (!prevCased && !char.IsUpper(c)) return false;
                    if (prevCased && !char.IsLower(c)) return false;
                }
                prevCased = cased;
            }
            return anyCased;
        };

        m["elems"] = (_, self, a) => { a.Done(); return new StarElems(self); };

        m["strip"] = (_, self, a) => Strip((string)self, a.OptStr("cutset"), true, true);
        m["lstrip"] = (_, self, a) => Strip((string)self, a.OptStr("cutset"), true, false);
        m["rstrip"] = (_, self, a) => Strip((string)self, a.OptStr("cutset"), false, true);

        m["startswith"] = (_, self, a) => StartsEnds((string)self, a, starts: true);
        m["endswith"] = (_, self, a) => StartsEnds((string)self, a, starts: false);

        m["removeprefix"] = (_, self, a) =>
        {
            var s = (string)self; var p = StrArg(a, "x");
            return s.StartsWith(p, StringComparison.Ordinal) ? s[p.Length..] : s;
        };
        m["removesuffix"] = (_, self, a) =>
        {
            var s = (string)self; var p = StrArg(a, "x");
            return s.EndsWith(p, StringComparison.Ordinal) ? s[..^p.Length] : s;
        };

        m["find"] = (_, self, a) => Find((string)self, a, last: false, mustExist: false);
        m["rfind"] = (_, self, a) => Find((string)self, a, last: true, mustExist: false);
        m["index"] = (_, self, a) => Find((string)self, a, last: false, mustExist: true);
        m["rindex"] = (_, self, a) => Find((string)self, a, last: true, mustExist: true);

        m["count"] = (_, self, a) =>
        {
            var s = (string)self;
            var sub = StrArg(a, "sub");
            var (st, en) = Bounds(a, s.Length);
            a.Done();
            if (st > en) return BigInteger.Zero;
            string region = s[st..en];
            if (sub.Length == 0) return new BigInteger(region.Length + 1);
            int n = 0, pos = 0;
            while ((pos = region.IndexOf(sub, pos, StringComparison.Ordinal)) >= 0)
            {
                n++;
                pos += sub.Length;
            }
            return new BigInteger(n);
        };

        m["replace"] = (_, self, a) =>
        {
            var s = (string)self;
            var old = StrArg(a, "old");
            var neu = StrArg(a, "new");
            var cnt = a.OptInt("count");
            a.Done();
            int max = cnt is { } c && c >= 0 ? (int)BigInteger.Min(c, int.MaxValue) : -1;
            var sb = new StringBuilder();
            int pos = 0, done = 0;
            while (max < 0 || done < max)
            {
                int idx;
                if (old.Length == 0)
                {
                    idx = pos <= s.Length ? pos : -1;
                }
                else
                {
                    idx = s.IndexOf(old, pos, StringComparison.Ordinal);
                }
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
        };

        m["join"] = (_, self, a) =>
        {
            var iterable = a.Required("elements");
            a.Done();
            var parts = Ops.Iterate(iterable).Select(x =>
                x is string p ? p : throw Err.Param("elements", "string", x)).ToList();
            return string.Join((string)self, parts);
        };

        m["split"] = (_, self, a) => Split((string)self, a, right: false);
        m["rsplit"] = (_, self, a) => Split((string)self, a, right: true);

        m["splitlines"] = (_, self, a) =>
        {
            var s = (string)self;
            bool keep = a.Bool("keepends", false);
            a.Done();
            var lines = new List<string>();
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
            return StrList(lines);
        };

        m["partition"] = (_, self, a) => Partition((string)self, a, last: false);
        m["rpartition"] = (_, self, a) => Partition((string)self, a, last: true);

        m["format"] = (_, self, a) =>
        {
            var args = a.Rest();
            var kw = a.RestKw();
            return StringFormat.Format((string)self, args, kw);
        };
    }

    private static object Strip(string s, string? cutset, bool left, bool right)
    {
        bool Strippable(char ch) => cutset is null ? IsSpace(ch) : cutset.Contains(ch);
        int st = 0, en = s.Length;
        if (left) while (st < en && Strippable(s[st])) st++;
        if (right) while (en > st && Strippable(s[en - 1])) en--;
        return s[st..en];
    }

    private static object StartsEnds(string s, ArgReader a, bool starts)
    {
        var arg = a.Required(starts ? "prefix" : "suffix");
        var (st, en) = Bounds(a, s.Length);
        a.Done();
        string region = st <= en ? s[st..en] : "";
        bool Test(string p) => starts ? region.StartsWith(p, StringComparison.Ordinal) : region.EndsWith(p, StringComparison.Ordinal);
        switch (arg)
        {
            case string p: return Test(p);
            case StarTuple t:
                foreach (var x in t.Items)
                {
                    if (x is not string)
                    {
                        throw Err.Param(starts ? "prefix" : "suffix", "string", x);
                    }
                }
                return t.Items.Any(x => Test((string)x!));
            default:
                throw Err.Param(starts ? "prefix" : "suffix", "string or tuple of strings", arg);
        }
    }

    private static object Find(string s, ArgReader a, bool last, bool mustExist)
    {
        var sub = StrArg(a, "sub");
        var (st, en) = Bounds(a, s.Length);
        a.Done();
        int idx = -1;
        if (st <= en)
        {
            string region = s[st..en];
            idx = last ? region.LastIndexOf(sub, StringComparison.Ordinal) : region.IndexOf(sub, StringComparison.Ordinal);
        }
        if (idx < 0)
        {
            if (mustExist) throw new StarlarkException($"Substring \"{sub}\" not found in \"{s}\"");
            return new BigInteger(-1);
        }
        return new BigInteger(st + idx);
    }

    private static object Partition(string s, ArgReader a, bool last)
    {
        var sep = StrArg(a, "x");
        a.Done();
        if (sep.Length == 0) throw new StarlarkException("Empty separator cannot be used for partitioning");
        int idx = last ? s.LastIndexOf(sep, StringComparison.Ordinal) : s.IndexOf(sep, StringComparison.Ordinal);
        if (idx < 0)
        {
            return last ? new StarTuple(["", "", s]) : new StarTuple([s, "", ""]);
        }
        return new StarTuple([s[..idx], sep, s[(idx + sep.Length)..]]);
    }

    private static object Split(string s, ArgReader a, bool right)
    {
        var sepArg = a.Optional("sep");
        var maxArg = a.OptInt("maxsplit");
        a.Done();
        int max = maxArg is { } m && m >= 0 ? (int)BigInteger.Min(m, int.MaxValue) : -1;

        if (sepArg is null)
        {
            return SplitWhitespace(s, max, right);
        }
        if (sepArg is not string sep) throw Err.Param("sep", "string", sepArg);
        if (sep.Length == 0) throw new StarlarkException("Empty separator cannot be used for splitting");

        var parts = new List<string>();
        if (!right)
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
                int idx = end - sep.Length < 0 ? -1 : s.LastIndexOf(sep, end - 1, end, StringComparison.Ordinal);
                if (idx < 0) break;
                parts.Insert(0, s[(idx + sep.Length)..end]);
                end = idx;
            }
            parts.Insert(0, s[..end]);
        }
        return StrList(parts);
    }

    private static object SplitWhitespace(string s, int max, bool right)
    {
        var result = new List<string>();
        if (!right)
        {
            int i = 0;
            while (true)
            {
                while (i < s.Length && IsSpace(s[i])) i++;
                if (i >= s.Length) break;
                if (max >= 0 && result.Count == max)
                {
                    result.Add(s[i..]);
                    return StrList(result);
                }
                int st = i;
                while (i < s.Length && !IsSpace(s[i])) i++;
                result.Add(s[st..i]);
            }
            return StrList(result);
        }

        int j = s.Length;
        while (true)
        {
            while (j > 0 && IsSpace(s[j - 1])) j--;
            if (j <= 0) break;
            if (max >= 0 && result.Count == max)
            {
                result.Insert(0, s[..j]);
                return StrList(result);
            }
            int en = j;
            while (j > 0 && !IsSpace(s[j - 1])) j--;
            result.Insert(0, s[j..en]);
        }
        return StrList(result);
    }

    // ---------------------------------------------------------------- bytes

    private static void RegisterBytes()
    {
        BytesMethods["elems"] = (_, self, a) => { a.Done(); return new StarElems(self); };
    }

    // ---------------------------------------------------------------- lists

    private static int ListIndex(BigInteger i, int len)
    {
        if (i < 0) i += len;
        if (i < 0 || i >= len) throw new StarlarkException($"Index `{i}` is out of bound for a list of length {len}");
        return (int)i;
    }

    private static void RegisterLists()
    {
        var m = ListMethods;

        m["append"] = (_, self, a) => { var x = a.Required("x"); a.Done(); ((StarList)self).Add(x); return null; };
        m["clear"] = (_, self, a) => { a.Done(); ((StarList)self).Clear(); return null; };
        m["extend"] = (_, self, a) =>
        {
            var x = a.Required("x");
            a.Done();
            var l = (StarList)self;
            l.AddRange(Ops.Iterate(x).ToList());
            return null;
        };
        m["insert"] = (_, self, a) =>
        {
            var l = (StarList)self;
            var i = a.Int("index");
            var x = a.Required("x");
            a.Done();
            if (i < 0) i += l.Count;
            l.Insert((int)BigInteger.Clamp(i, 0, l.Count), x);
            return null;
        };
        m["pop"] = (_, self, a) =>
        {
            var l = (StarList)self;
            var idx = a.OptInt("index");
            a.Done();
            if (l.Count == 0) throw new StarlarkException("Cannot pop from an empty list");
            int i = idx is { } v ? ListIndex(v, l.Count) : l.Count - 1;
            var result = l[i];
            l.RemoveAt(i);
            return result;
        };
        m["remove"] = (_, self, a) =>
        {
            var l = (StarList)self;
            var x = a.Required("x");
            a.Done();
            for (int i = 0; i < l.Count; i++)
            {
                if (Ops.Equal(l[i], x))
                {
                    l.RemoveAt(i);
                    return null;
                }
            }
            throw new StarlarkException($"Item {Repr.Of(x)} not found in list");
        };
        m["index"] = (_, self, a) =>
        {
            var l = (StarList)self;
            var x = a.Required("x");
            var (st, en) = Bounds(a, l.Count);
            a.Done();
            for (int i = st; i < en; i++)
            {
                if (Ops.Equal(l[i], x)) return new BigInteger(i);
            }
            throw new StarlarkException($"Item {Repr.Of(x)} not found in list");
        };
    }

    // ---------------------------------------------------------------- dicts

    private static void RegisterDicts()
    {
        var m = DictMethods;

        m["get"] = (_, self, a) =>
        {
            var key = a.Required("key");
            var dflt = a.Optional("default");
            a.Done();
            return ((StarDict)self).TryGet(key, out var v) ? v : dflt;
        };
        m["keys"] = (_, self, a) => { a.Done(); return new StarList(((StarDict)self).Keys); };
        m["values"] = (_, self, a) => { a.Done(); return new StarList(((StarDict)self).Values); };
        m["items"] = (_, self, a) =>
        {
            a.Done();
            return new StarList(((StarDict)self).Entries.Select(kv => (object?)new StarTuple([kv.Key, kv.Value])));
        };
        m["clear"] = (_, self, a) => { a.Done(); ((StarDict)self).Clear(); return null; };
        m["pop"] = (_, self, a) =>
        {
            var d = (StarDict)self;
            var key = a.Required("key");
            bool hasDefault = a.Has("default");
            var dflt = a.Optional("default");
            a.Done();
            if (d.Remove(key, out var v)) return v;
            if (hasDefault) return dflt;
            throw new StarlarkException($"Key `{Repr.Of(key)}` not found in dictionary");
        };
        m["popitem"] = (_, self, a) =>
        {
            a.Done();
            var d = (StarDict)self;
            if (d.Count == 0) throw new StarlarkException("popitem(): dictionary is empty");
            var first = d.Entries.First();
            d.Remove(first.Key, out var ignored);
            return new StarTuple([first.Key, first.Value]);
        };
        m["setdefault"] = (_, self, a) =>
        {
            var d = (StarDict)self;
            var key = a.Required("key");
            var dflt = a.Optional("default");
            a.Done();
            if (d.TryGet(key, out var existing)) return existing;
            d.Set(key, dflt);
            return dflt;
        };
        m["update"] = (_, self, a) =>
        {
            var d = (StarDict)self;
            bool hasPairs = a.Has("pairs") && a.RemainingPositional > 0;
            var pairs = a.Optional("pairs");
            var kw = a.RestKw();
            a.Done(allowKwargs: true);
            // The spec allows None here, but the reference test suites (Go and Rust) reject it.
            if (hasPairs && pairs is null)
            {
                throw new StarlarkException("update(): got NoneType, want iterable or dict (cannot be None)");
            }
            if (pairs is not null)
            {
                if (pairs is StarDict src)
                {
                    foreach (var kv in src.Entries.ToList()) d.Set(kv.Key, kv.Value);
                }
                else
                {
                    foreach (var pair in Ops.Iterate(pairs).ToList())
                    {
                        List<object?> kv;
                        try
                        {
                            kv = Ops.Iterate(pair).ToList();
                        }
                        catch (StarlarkException)
                        {
                            throw new StarlarkException($"dict update sequence element is not iterable: '{Types.Name(pair)}'");
                        }
                        if (kv.Count != 2) throw new StarlarkException("dict update sequence element has the wrong length (expected 2)");
                        d.Set(kv[0], kv[1]);
                    }
                }
            }
            foreach (var (k, v) in kw) d.Set(k, v);
            return null;
        };
    }

    // ---------------------------------------------------------------- sets

    private static List<object?> Others(ArgReader a)
    {
        var all = new List<object?>();
        foreach (var o in a.Rest())
        {
            foreach (var x in Ops.Iterate(o).ToList())
            {
                Hashing.CheckHashable(x);
                all.Add(x);
            }
        }
        return all;
    }

    private static StarSet Copy(StarSet s)
    {
        var r = new StarSet();
        foreach (var x in s.Items) r.Add(x);
        return r;
    }

    private static void RegisterSets()
    {
        var m = SetMethods;

        m["add"] = (_, self, a) => { var x = a.Required("x"); a.Done(); ((StarSet)self).Add(x); return null; };
        m["clear"] = (_, self, a) => { a.Done(); ((StarSet)self).Clear(); return null; };
        m["discard"] = (_, self, a) => { var x = a.Required("x"); a.Done(); ((StarSet)self).Remove(x); return null; };
        m["remove"] = (_, self, a) =>
        {
            var x = a.Required("x");
            a.Done();
            if (!((StarSet)self).Remove(x)) throw new StarlarkException($"Item {Repr.Of(x)} not found in set");
            return null;
        };
        m["pop"] = (_, self, a) =>
        {
            a.Done();
            var s = (StarSet)self;
            if (s.Count == 0) throw new StarlarkException("Cannot pop from an empty set");
            return s.Pop();
        };

        m["union"] = (_, self, a) =>
        {
            var r = Copy((StarSet)self);
            foreach (var x in Others(a)) r.Add(x);
            return r;
        };
        m["update"] = (_, self, a) =>
        {
            var s = (StarSet)self;
            foreach (var x in Others(a)) s.Add(x);
            return null;
        };
        m["intersection"] = (_, self, a) =>
        {
            var s = (StarSet)self;
            var r = Copy(s);
            foreach (var o in a.Rest())
            {
                var keep = SetOf(o);
                foreach (var x in r.Items.ToList()) if (!keep.Contains(x)) r.Remove(x);
            }
            return r;
        };
        m["intersection_update"] = (_, self, a) =>
        {
            var s = (StarSet)self;
            var sets = a.Rest().Select(SetOf).ToList();
            foreach (var x in s.Items.ToList())
            {
                if (sets.Any(o => !o.Contains(x))) s.Remove(x);
            }
            return null;
        };
        m["difference"] = (_, self, a) =>
        {
            var r = Copy((StarSet)self);
            foreach (var x in Others(a)) r.Remove(x);
            return r;
        };
        m["difference_update"] = (_, self, a) =>
        {
            var s = (StarSet)self;
            foreach (var x in Others(a)) s.Remove(x);
            return null;
        };
        m["symmetric_difference"] = (_, self, a) =>
        {
            var s = (StarSet)self;
            var other = SetOf(a.Required("x"));
            a.Done();
            var r = new StarSet();
            foreach (var x in s.Items) if (!other.Contains(x)) r.Add(x);
            foreach (var x in other.Items) if (!s.Contains(x)) r.Add(x);
            return r;
        };
        m["symmetric_difference_update"] = (_, self, a) =>
        {
            var s = (StarSet)self;
            var other = SetOf(a.Required("x"));
            a.Done();
            foreach (var x in other.Items.ToList())
            {
                if (s.Contains(x)) s.Remove(x); else s.Add(x);
            }
            return null;
        };
        m["isdisjoint"] = (_, self, a) =>
        {
            var s = (StarSet)self;
            var other = SetOf(a.Required("x"));
            a.Done();
            return !s.Items.Any(other.Contains);
        };
        m["issubset"] = (_, self, a) =>
        {
            var s = (StarSet)self;
            var other = SetOf(a.Required("x"));
            a.Done();
            return s.Items.All(other.Contains);
        };
        m["issuperset"] = (_, self, a) =>
        {
            var s = (StarSet)self;
            var other = SetOf(a.Required("x"));
            a.Done();
            return other.Items.All(s.Contains);
        };
    }

    /// <summary>View any iterable as a set (for membership), checking hashability of every element.</summary>
    public static StarSet SetOf(object? iterable)
    {
        if (iterable is StarSet s) return s;
        var r = new StarSet();
        foreach (var x in Ops.Iterate(iterable).ToList()) r.Add(x);
        return r;
    }
}
