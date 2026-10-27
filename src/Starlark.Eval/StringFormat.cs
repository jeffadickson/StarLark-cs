using System.Globalization;
using System.Numerics;
using System.Text;

namespace Starlark.Eval;

/// <summary>Implements <c>"..." % args</c> (string interpolation) and <c>"...".format(...)</c>.</summary>
public static class StringFormat
{
    // ---------------------------------------------------------------- "format" % args

    public static string Percent(string fmt, object? arg)
    {
        // The operand is `args` itself when there is a single conversion, otherwise a tuple.
        var tuple = arg as StarTuple;
        object?[] args = tuple is not null ? tuple.Items : [arg];
        int next = 0;
        bool usedMapping = false;
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
                if (close < 0) throw new StarlarkException("Unterminated %(name) in format string");
                key = fmt[(i + 1)..close];
                i = close + 1;
                if (i >= fmt.Length) throw new StarlarkException("Unexpected end of format string");
            }

            object? Operand()
            {
                if (key is not null)
                {
                    usedMapping = true;
                    if (arg is not StarDict d) throw new StarlarkException("Format requires a mapping for %(name) conversions");
                    return d.TryGet(key, out var v) ? v : throw new StarlarkException($"Key `{key}` not found in dictionary");
                }
                if (next >= args.Length) throw new StarlarkException("Not enough arguments for format string");
                return args[next++];
            }

            switch (fmt[i])
            {
                case 's': sb.Append(Repr.Str(Operand())); break;
                case 'r': sb.Append(Repr.Of(Operand())); break;
                case 'd':
                case 'i':
                    sb.Append(ToInt(Operand()).ToString());
                    break;
                case 'o': sb.Append(ToBase(ToInt(Operand()), 8, false)); break;
                case 'x': sb.Append(ToBase(ToInt(Operand()), 16, false)); break;
                case 'X': sb.Append(ToBase(ToInt(Operand()), 16, true)); break;
                case 'e': case 'E': case 'f': case 'F': case 'g': case 'G':
                    sb.Append(FormatFloat(fmt[i], ToFloat(Operand())));
                    break;
                case 'c':
                {
                    var v = Operand();
                    if (v is BigInteger n && n >= 0 && n <= 0x10FFFF && !(n >= 0xD800 && n <= 0xDFFF))
                    {
                        sb.Append(char.ConvertFromUtf32((int)n));
                    }
                    else if (v is string s && s.Length >= 1 && CodePointCount(s) == 1)
                    {
                        sb.Append(s);
                    }
                    else
                    {
                        throw new StarlarkException("%c requires a valid code point or a single-character string");
                    }
                    break;
                }
                default:
                    throw new StarlarkException($"Unknown format specifier '%{fmt[i]}'");
            }
        }

        if (!usedMapping && next < args.Length)
        {
            throw new StarlarkException("Too many arguments for format string (not all arguments converted)");
        }
        return sb.ToString();
    }

    private static int CodePointCount(string s) => s.Count(ch => !char.IsLowSurrogate(ch));

    private static BigInteger ToInt(object? v)
    {
        switch (v)
        {
            case BigInteger i: return i;
            case double d:
                if (double.IsNaN(d) || double.IsInfinity(d)) throw new StarlarkException($"Cannot convert {Repr.FloatToString(d)} to an integer");
                return new BigInteger(Math.Truncate(d));
        }
        throw new StarlarkException($"Format requires a number, got '{Types.Name(v)}'");
    }

    private static double ToFloat(object? v) => v switch
    {
        double d => d,
        BigInteger i => Ops.BigToDouble(i),
        _ => throw new StarlarkException($"Format requires a number, got '{Types.Name(v)}'"),
    };

    private static string ToBase(BigInteger v, int b, bool upper)
    {
        if (v.IsZero) return "0";
        bool neg = v < 0;
        v = BigInteger.Abs(v);
        string digits = upper ? "0123456789ABCDEF" : "0123456789abcdef";
        var sb = new StringBuilder();
        while (v > 0)
        {
            sb.Insert(0, digits[(int)(v % b)]);
            v /= b;
        }
        return (neg ? "-" : "") + sb;
    }

    public static string FormatFloat(char spec, double d)
    {
        if (double.IsNaN(d)) return "nan";
        if (double.IsInfinity(d)) return d > 0 ? "+inf" : "-inf";
        switch (spec)
        {
            case 'f': case 'F':
                return d.ToString("F6", CultureInfo.InvariantCulture);
            case 'e': case 'E':
            {
                string s = d.ToString("0.000000e+00", CultureInfo.InvariantCulture);
                return spec == 'E' ? s.ToUpperInvariant() : s;
            }
            default:
                return Repr.FloatToString(d, spec == 'G' ? 'E' : 'e');
        }
    }

    // ---------------------------------------------------------------- "...".format(...)

    /// <summary><c>"{} {name} {0}".format(...)</c></summary>
    public static string Format(string fmt, IReadOnlyList<object?> args, IReadOnlyList<KeyValuePair<string, object?>> kwargs)
    {
        var sb = new StringBuilder();
        int auto = 0;
        bool sawManual = false;
        for (int i = 0; i < fmt.Length; i++)
        {
            char c = fmt[i];
            if (c == '{')
            {
                if (i + 1 < fmt.Length && fmt[i + 1] == '{')
                {
                    sb.Append('{');
                    i++;
                    continue;
                }
                int close = fmt.IndexOf('}', i);
                if (close < 0) throw new StarlarkException("Unmatched '{' in format string");
                string field = fmt[(i + 1)..close];
                i = close;
                if (field.Contains('{'))
                {
                    throw new StarlarkException("Nested replacement fields are not supported: unmatched '{' in format string");
                }

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
                    if (sawManual) throw new StarlarkException("Cannot switch from manual field specification to automatic field numbering");
                    if (auto >= args.Count) throw new StarlarkException($"No replacement found for index {auto}");
                    value = args[auto++];
                }
                else if (field.All(char.IsAsciiDigit))
                {
                    if (auto > 0) throw new StarlarkException("Cannot switch from automatic field numbering to manual field specification");
                    sawManual = true;
                    if (!int.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out int idx) || idx >= args.Count)
                    {
                        throw new StarlarkException($"No replacement found for index {field}");
                    }
                    value = args[idx];
                }
                else
                {
                    int bad = field.IndexOfAny(['.', '[']);
                    if (bad >= 0)
                    {
                        throw new StarlarkException($"Invalid character '{field[bad]}' inside replacement field");
                    }
                    var kv = kwargs.FirstOrDefault(k => k.Key == field);
                    if (kv.Key is null) throw new StarlarkException($"Missing argument '{field}': keyword not found");
                    value = kv.Value;
                }

                sb.Append(conv switch
                {
                    "s" => Repr.Str(value),
                    "r" => Repr.Of(value),
                    _ => throw new StarlarkException($"Unknown conversion '!{conv}': expected 's' or 'r'"),
                });
            }
            else if (c == '}')
            {
                if (i + 1 < fmt.Length && fmt[i + 1] == '}')
                {
                    sb.Append('}');
                    i++;
                    continue;
                }
                throw new StarlarkException("Single '}' in format string: Found '}' without matching '{'");
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
