using System.Globalization;
using System.Numerics;
using System.Text;

namespace Starlark.Eval;

/// <summary>The <c>json</c> module: <c>json.encode(x)</c> and <c>json.decode(s)</c>, plus <c>json.indent</c>.</summary>
public static class JsonModule
{
    private const int MaxDepth = 128;

    public static StarStruct Create() => new(
    [
        new("encode", new Builtin("encode", (_, a, k) =>
        {
            var r = new ArgReader("json.encode", a, k);
            var x = r.Required("x");
            r.Done();
            var sb = new StringBuilder();
            Encode(sb, x, new HashSet<object>(ReferenceEqualityComparer.Instance), 0);
            return sb.ToString();
        })),
        new("decode", new Builtin("decode", (_, a, k) =>
        {
            var r = new ArgReader("json.decode", a, k);
            string s = r.Str("x");
            var dflt = r.Has("default");
            var fallback = r.Optional("default");
            r.Done();
            try
            {
                return new Decoder(s).Run();
            }
            catch (StarlarkException) when (dflt)
            {
                return fallback;
            }
        })),
        new("indent", new Builtin("indent", (_, a, k) =>
        {
            var r = new ArgReader("json.indent", a, k);
            string s = r.Str("s");
            string prefix = r.OptStr("prefix") ?? "";
            string indent = r.OptStr("indent") ?? "\t";
            r.Done();
            return Indent(s, prefix, indent);
        })),
    ]);

    // ---------------------------------------------------------------- encode

    private static void Encode(StringBuilder sb, object? v, HashSet<object> inProgress, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new StarlarkException("Value is nested too deeply to serialize to JSON");
        }
        switch (v)
        {
            case null: sb.Append("null"); return;
            case bool b: sb.Append(b ? "true" : "false"); return;
            case BigInteger i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); return;
            case double d:
                if (double.IsNaN(d) || double.IsInfinity(d))
                {
                    throw new StarlarkException($"cannot encode non-finite float {Repr.FloatToString(d)} as JSON");
                }
                sb.Append(JsonFloat(d));
                return;
            case string s: Quote(sb, s); return;
            case StarEnumValue e: Encode(sb, e.Value, inProgress, depth + 1); return;
        }

        if (!inProgress.Add(v))
        {
            throw new StarlarkException($"Cycle detected when serializing value of type `{Types.Name(v)}` to JSON");
        }
        try
        {
            switch (v)
            {
                case StarList l:
                    WriteArray(sb, l.Items, inProgress, depth);
                    break;
                case StarTuple t:
                    WriteArray(sb, t.Items, inProgress, depth);
                    break;
                case StarSet s:
                    WriteArray(sb, s.Items.ToList(), inProgress, depth);
                    break;
                case StarDict d:
                {
                    // JSON object keys must be strings. Output keeps insertion order, as the Rust crate does
                    // (Go and Bazel sort the keys instead).
                    var entries = new List<(string Key, object? Value)>();
                    foreach (var kv in d.Entries)
                    {
                        if (kv.Key is not string key)
                        {
                            throw new StarlarkException(
                                $"dict has {Types.Name(kv.Key)} key, want string: JSON object keys must be strings");
                        }
                        entries.Add((key, kv.Value));
                    }
                    WriteObject(sb, entries, inProgress, depth);
                    break;
                }
                case StarStruct st:
                    WriteObject(sb, st.Fields.Select(f => (f.Key, f.Value)).ToList(), inProgress, depth);
                    break;
                case StarRecord rec:
                    WriteObject(sb, rec.Type.Fields.Select((f, i) => (f.Name, rec.Values[i])).ToList(), inProgress, depth);
                    break;
                default:
                    throw new StarlarkException($"Value of type `{Types.Name(v)}` cannot be serialized to JSON");
            }
        }
        finally
        {
            inProgress.Remove(v);
        }
    }

    private static void WriteArray(StringBuilder sb, IReadOnlyList<object?> items, HashSet<object> inProgress, int depth)
    {
        sb.Append('[');
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) sb.Append(',');
            Encode(sb, items[i], inProgress, depth + 1);
        }
        sb.Append(']');
    }

    private static void WriteObject(StringBuilder sb, IReadOnlyList<(string Key, object? Value)> entries,
        HashSet<object> inProgress, int depth)
    {
        sb.Append('{');
        for (int i = 0; i < entries.Count; i++)
        {
            if (i > 0) sb.Append(',');
            Quote(sb, entries[i].Key);
            sb.Append(':');
            Encode(sb, entries[i].Value, inProgress, depth + 1);
        }
        sb.Append('}');
    }

    /// <summary>The shortest round-trip form, always with a decimal point or exponent so it decodes as a float.</summary>
    private static string JsonFloat(double d)
    {
        string s = d.ToString("R", CultureInfo.InvariantCulture);
        if (s.Contains('E'))
        {
            // 1E+20 -> 1e+20
            s = s.Replace("E+", "e+").Replace("E-", "e-");
        }
        else if (!s.Contains('.'))
        {
            s += ".0";
        }
        return s;
    }

    private static void Quote(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case < ' ': sb.Append("\\u").Append(((int)c).ToString("x4")); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
    }

    // ---------------------------------------------------------------- decode

    private sealed class Decoder(string text)
    {
        private int _pos;

        private StarlarkException Error(string msg) => new($"json.decode: {msg} at offset {_pos}");

        public object? Run()
        {
            SkipWs();
            var v = Value(MaxDepth);
            SkipWs();
            if (_pos < text.Length) throw Error("unexpected character after JSON value");
            return v;
        }

        private void SkipWs()
        {
            while (_pos < text.Length && text[_pos] is ' ' or '\t' or '\n' or '\r') _pos++;
        }

        private object? Value(int remaining)
        {
            if (_pos >= text.Length) throw Error("unexpected end of input, expected value");
            switch (text[_pos])
            {
                case '{': return Obj(remaining);
                case '[': return Arr(remaining);
                case '"': return Str();
                case 't': Lit("true"); return true;
                case 'f': Lit("false"); return false;
                case 'n': Lit("null"); return null;
                case '-' or (>= '0' and <= '9'): return Num();
                default: throw Error($"unexpected character '{text[_pos]}', expected value");
            }
        }

        private void Lit(string word)
        {
            if (string.CompareOrdinal(text, _pos, word, 0, word.Length) != 0)
            {
                throw Error($"invalid literal, expected {word}");
            }
            _pos += word.Length;
        }

        private object? Obj(int remaining)
        {
            if (remaining <= 1) throw Error("recursion limit exceeded");
            _pos++;
            var d = new StarDict();
            SkipWs();
            if (Eat('}')) return d;
            while (true)
            {
                SkipWs();
                if (_pos >= text.Length || text[_pos] != '"') throw Error("expected string key in object");
                string key = Str();
                SkipWs();
                if (!Eat(':')) throw Error("expected ':' after object key");
                SkipWs();
                d.Set(key, Value(remaining - 1));
                SkipWs();
                if (Eat(',')) continue;
                if (Eat('}')) return d;
                throw Error("expected ',' or '}' in object");
            }
        }

        private object? Arr(int remaining)
        {
            if (remaining <= 1) throw Error("recursion limit exceeded");
            _pos++;
            var items = new List<object?>();
            SkipWs();
            if (Eat(']')) return new StarList(items);
            while (true)
            {
                SkipWs();
                items.Add(Value(remaining - 1));
                SkipWs();
                if (Eat(',')) continue;
                if (Eat(']')) return new StarList(items);
                throw Error("expected ',' or ']' in array");
            }
        }

        private bool Eat(char c)
        {
            if (_pos < text.Length && text[_pos] == c)
            {
                _pos++;
                return true;
            }
            return false;
        }

        private string Str()
        {
            _pos++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (_pos >= text.Length) throw Error("unterminated string");
                char c = text[_pos++];
                if (c == '"') return sb.ToString();
                if (c < ' ') throw Error("control character in string");
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                if (_pos >= text.Length) throw Error("unterminated escape");
                char e = text[_pos++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (_pos + 4 > text.Length ||
                            !int.TryParse(text.AsSpan(_pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cu))
                        {
                            throw Error("invalid \\u escape");
                        }
                        sb.Append((char)cu);
                        _pos += 4;
                        break;
                    default: throw Error($"invalid escape '\\{e}'");
                }
            }
        }

        private object? Num()
        {
            int start = _pos;
            if (text[_pos] == '-') _pos++;
            int digitsStart = _pos;
            while (_pos < text.Length && char.IsAsciiDigit(text[_pos])) _pos++;
            int intDigits = _pos - digitsStart;
            if (intDigits == 0) throw Error("invalid number");
            if (intDigits > 1 && text[digitsStart] == '0') throw Error("invalid number: leading zero");

            bool isFloat = false;
            if (_pos < text.Length && text[_pos] == '.')
            {
                isFloat = true;
                _pos++;
                int f = _pos;
                while (_pos < text.Length && char.IsAsciiDigit(text[_pos])) _pos++;
                if (_pos == f) throw Error("invalid number: digits expected after '.'");
            }
            if (_pos < text.Length && text[_pos] is 'e' or 'E')
            {
                isFloat = true;
                _pos++;
                if (_pos < text.Length && text[_pos] is '+' or '-') _pos++;
                int e = _pos;
                while (_pos < text.Length && char.IsAsciiDigit(text[_pos])) _pos++;
                if (_pos == e) throw Error("invalid number: digits expected in exponent");
            }

            string num = text[start.._pos];
            if (isFloat)
            {
                double d = double.Parse(num, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (double.IsInfinity(d)) throw Error("number out of range");
                return d;
            }
            // Integers stay exact at any size.
            return BigInteger.Parse(num, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        }
    }

    // ---------------------------------------------------------------- indent

    private static string Indent(string json, string prefix, string indent)
    {
        // Validate first so garbage in gives an error rather than garbage out.
        new Decoder(json).Run();

        var sb = new StringBuilder();
        int level = 0;
        bool inString = false;
        void NewLine()
        {
            sb.Append('\n').Append(prefix);
            for (int i = 0; i < level; i++) sb.Append(indent);
        }

        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];
            if (inString)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < json.Length) sb.Append(json[++i]);
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; sb.Append(c); break;
                case ' ' or '\t' or '\n' or '\r': break;
                case '{' or '[':
                {
                    char close = c == '{' ? '}' : ']';
                    int j = i + 1;
                    while (j < json.Length && json[j] is ' ' or '\t' or '\n' or '\r') j++;
                    if (j < json.Length && json[j] == close)
                    {
                        sb.Append(c).Append(close);
                        i = j;
                    }
                    else
                    {
                        sb.Append(c);
                        level++;
                        NewLine();
                    }
                    break;
                }
                case '}' or ']':
                    level--;
                    NewLine();
                    sb.Append(c);
                    break;
                case ',':
                    sb.Append(c);
                    NewLine();
                    break;
                case ':':
                    sb.Append(": ");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
