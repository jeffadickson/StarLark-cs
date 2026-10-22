using System.Globalization;
using System.Numerics;
using System.Text;

namespace Starlark.Eval;

/// <summary>Implements <c>"..." % args</c> and <c>"...".format(...)</c>.</summary>
public static class StringFormat
{
    public static string Percent(string fmt, object? arg)
    {
        object?[] args = arg is StarTuple t ? t.Items : [arg];
        int next = 0;
        var sb = new StringBuilder();
        for (int i = 0; i < fmt.Length; i++)
        {
            char c = fmt[i];
            if (c != '%')
            {
                sb.Append(c);
                continue;
            }
            if (++i >= fmt.Length)
            {
                throw new StarlarkException("Unexpected end of format string");
            }
            if (fmt[i] == '%')
            {
                sb.Append('%');
                continue;
            }
            string? key = null;
            if (fmt[i] == '(')
            {
                int close = fmt.IndexOf(')', i);
                if (close < 0) throw new StarlarkException("Unterminated %(name)");
                key = fmt[(i + 1)..close];
                i = close + 1;
            }
            object? Next()
            {
                if (key is not null)
                {
                    if (arg is not StarDict d) throw new StarlarkException("format requires a mapping");
                    return d.TryGet(key, out var v) ? v : throw new StarlarkException($"Key `{key}` not found in dictionary");
                }
                if (next >= args.Length) throw new StarlarkException("Not enough arguments for format string");
                return args[next++];
            }
            if (i >= fmt.Length) throw new StarlarkException("Unexpected end of format string");
            switch (fmt[i])
            {
                case 's': sb.Append(Repr.Str(Next())); break;
                case 'r': sb.Append(Repr.Of(Next())); break;
                case 'd':
                case 'i':
                    sb.Append(ToInt(Next()).ToString());
                    break;
                case 'o': sb.Append(ToBase(ToInt(Next()), 8)); break;
                case 'x': sb.Append(ToBase(ToInt(Next()), 16)); break;
                case 'X': sb.Append(ToBase(ToInt(Next()), 16).ToUpperInvariant()); break;
                case 'e': case 'f': case 'g':
                case 'E': case 'F': case 'G':
                    sb.Append(Float(fmt[i], Next()));
                    break;
                case 'c':
                {
                    var v = Next();
                    sb.Append(v is BigInteger n ? char.ConvertFromUtf32((int)n) : v is string s && CodePoints.Length(s) == 1 ? s
                        : throw new StarlarkException("%c requires int or single-character string"));
                    break;
                }
                default:
                    throw new StarlarkException($"Unknown format specifier '%{fmt[i]}'");
            }
        }
        if (key_unused(args, next, arg)) throw new StarlarkException("Too many arguments for format string");
        return sb.ToString();

        static bool key_unused(object?[] args, int next, object? arg) => arg is not StarDict && next < args.Length;
    }

    private static BigInteger ToInt(object? v) => v switch
    {
        BigInteger i => i,
        bool b => b ? 1 : 0,
        double d => new BigInteger(Math.Truncate(d)),
        _ => throw new StarlarkException($"Format requires a number, got '{Types.Name(v)}'"),
    };

    private static string ToBase(BigInteger v, int b)
    {
        if (v.IsZero) return "0";
        bool neg = v < 0;
        v = BigInteger.Abs(v);
        var sb = new StringBuilder();
        while (v > 0)
        {
            sb.Insert(0, "0123456789abcdef"[(int)(v % b)]);
            v /= b;
        }
        return (neg ? "-" : "") + sb;
    }

    private static string Float(char spec, object? v)
    {
        double d = v switch
        {
            double x => x,
            BigInteger i => (double)i,
            _ => throw new StarlarkException($"Format requires a number, got '{Types.Name(v)}'"),
        };
        return spec switch
        {
            'f' or 'F' => d.ToString("F6", CultureInfo.InvariantCulture),
            'e' => FormatExp(d, false),
            'E' => FormatExp(d, true),
            _ => d.ToString("G6", CultureInfo.InvariantCulture),
        };
    }

    private static string FormatExp(double d, bool upper)
    {
        string s = d.ToString("0.000000e+00", CultureInfo.InvariantCulture);
        return upper ? s.ToUpperInvariant() : s;
    }

    /// <summary><c>"{} {name} {0!r}".format(...)</c></summary>
    public static string Format(string fmt, IReadOnlyList<object?> args, IReadOnlyList<KeyValuePair<string, object?>> kwargs)
    {
        var sb = new StringBuilder();
        int auto = 0;
        for (int i = 0; i < fmt.Length; i++)
        {
            char c = fmt[i];
            if (c == '{')
            {
                if (i + 1 < fmt.Length && fmt[i + 1] == '{') { sb.Append('{'); i++; continue; }
                int close = fmt.IndexOf('}', i);
                if (close < 0) throw new StarlarkException("Unmatched '{' in format string");
                string field = fmt[(i + 1)..close];
                i = close;
                string conv = "s";
                int bang = field.IndexOf('!');
                if (bang >= 0)
                {
                    conv = field[(bang + 1)..];
                    field = field[..bang];
                }
                object? value;
                if (field.Length == 0)
                {
                    if (auto >= args.Count) throw new StarlarkException("Not enough arguments for format string");
                    value = args[auto++];
                }
                else if (char.IsDigit(field[0]))
                {
                    int idx = int.Parse(field, CultureInfo.InvariantCulture);
                    if (idx >= args.Count) throw new StarlarkException("Not enough arguments for format string");
                    value = args[idx];
                }
                else
                {
                    var kv = kwargs.FirstOrDefault(k => k.Key == field);
                    if (kv.Key is null) throw new StarlarkException($"Missing argument '{field}'");
                    value = kv.Value;
                }
                sb.Append(conv switch
                {
                    "s" => Repr.Str(value),
                    "r" => Repr.Of(value),
                    _ => throw new StarlarkException($"Unknown conversion '!{conv}'"),
                });
            }
            else if (c == '}')
            {
                if (i + 1 < fmt.Length && fmt[i + 1] == '}') { sb.Append('}'); i++; continue; }
                throw new StarlarkException("Single '}' in format string");
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
