using System.Globalization;
using System.Numerics;
using System.Text;

namespace Starlark.Eval;

/// <summary>
/// Extension to the specified formatting: printf-style flags, width and precision for <c>%</c> conversions
/// (<c>%-8s</c>, <c>%05d</c>, <c>%+.2f</c>) and the <c>{field:spec}</c> mini-language for <c>str.format</c> and
/// f-strings (fill, alignment, sign, <c>#</c>, <c>0</c>, width, grouping, precision, type).
/// </summary>
/// <remarks>
/// The specification defines neither; they are accepted because Python programmers expect them and because a
/// plain conversion such as <c>%d</c> or <c>{}</c> behaves exactly as before. Both routes end in <see cref="Compose"/>.
/// </remarks>
public static partial class StringFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The widest field or longest precision a format may ask for (a guard against <c>%99999999999d</c>).</summary>
    public const int MaxFieldWidth = 1_000_000;

    /// <summary>The parsed flags, width and precision of one <c>%</c> conversion.</summary>
    internal readonly record struct PercentSpec(bool Left, bool Plus, bool Space, bool Zero, bool Alt, int? Width, int? Prec);

    // ---------------------------------------------------------------- shared helpers

    /// <summary>Parses digits starting at <paramref name="i"/>; returns null if there are none.</summary>
    private static int? ReadNumber(string s, ref int i)
    {
        int start = i;
        long value = 0;
        while (i < s.Length && s[i] is >= '0' and <= '9')
        {
            value = value * 10 + (s[i] - '0');
            if (value > MaxFieldWidth) throw new StarlarkException($"Format width or precision is larger than {MaxFieldWidth}");
            i++;
        }
        return i == start ? null : (int)value;
    }

    /// <summary>
    /// How many characters a piece of text has for width and precision: Unicode code points, as in Python, in both string
    /// element modes. A surrogate pair counts once and is never cut in half; <c>len()</c> is unaffected and still counts
    /// the mode's own elements.
    /// </summary>
    internal static int CharCount(string s)
    {
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (!(char.IsLowSurrogate(s[i]) && i > 0 && char.IsHighSurrogate(s[i - 1]))) n++;
        }
        return n;
    }

    /// <summary>The first <paramref name="count"/> characters of <paramref name="s"/>, without splitting a surrogate pair.</summary>
    internal static string TakeChars(string s, int count)
    {
        int seen = 0, i = 0;
        while (i < s.Length && seen < count)
        {
            i += char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
            seen++;
        }
        return s[..i];
    }

    /// <summary>Pads <paramref name="body"/> to <paramref name="width"/>, putting any sign or prefix before zero padding.</summary>
    private static string Pad(string sign, string body, int? width, bool left, bool zero, string fill = " ", char align = '\0')
    {
        int len = CharCount(sign) + CharCount(body);
        if (width is not { } w || len >= w) return sign + body;
        int gap = w - len;
        if (align == '=')
        {
            // Padding goes between the sign (or prefix) and the digits, using the fill character.
            return sign + Repeat(zero ? "0" : fill, gap) + body;
        }
        if (zero && align == '\0')
        {
            return sign + Repeat("0", gap) + body;
        }
        char how = align != '\0' ? align : left ? '<' : '>';
        return how switch
        {
            '<' => sign + body + Repeat(fill, gap),
            '^' => Repeat(fill, gap / 2) + sign + body + Repeat(fill, gap - gap / 2),
            _ => Repeat(fill, gap) + sign + body,
        };
    }

    /// <summary>Repeats a fill (one code point, possibly a surrogate pair) <paramref name="count"/> times.</summary>
    private static string Repeat(string fill, int count) =>
        count <= 0 ? "" : fill.Length == 1 ? new string(fill[0], count) : string.Concat(Enumerable.Repeat(fill, count));

    private static string GroupDigits(string digits, char sep, int size)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % size == 0) sb.Append(sep);
            sb.Append(digits[i]);
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- "%" conversions with flags, width and precision

    /// <summary>
    /// Parses the flags, width and precision that follow a <c>%</c>. <paramref name="i"/> is left on the conversion
    /// character. A <c>*</c> width or precision takes its value from the next argument.
    /// </summary>
    internal static PercentSpec ParsePercentSpec(string fmt, ref int i, Func<object?> nextArg)
    {
        bool left = false, plus = false, space = false, zero = false, alt = false;
        for (; i < fmt.Length; i++)
        {
            switch (fmt[i])
            {
                case '-': left = true; continue;
                case '+': plus = true; continue;
                case ' ': space = true; continue;
                case '0': zero = true; continue;
                case '#': alt = true; continue;
            }
            break;
        }
        int? width = null, prec = null;
        if (i < fmt.Length && fmt[i] == '*')
        {
            i++;
            width = StarInt(nextArg(), "width");
            if (width < 0)
            {
                left = true;
                width = -width;
            }
        }
        else
        {
            width = ReadNumber(fmt, ref i);
        }
        if (i < fmt.Length && fmt[i] == '.')
        {
            i++;
            if (i < fmt.Length && fmt[i] == '*')
            {
                i++;
                prec = Math.Max(0, StarInt(nextArg(), "precision"));
            }
            else
            {
                prec = ReadNumber(fmt, ref i) ?? 0;
            }
        }
        if (i >= fmt.Length) throw new StarlarkException("Unexpected end of format string");
        return new PercentSpec(left, plus, space, zero && !left, alt, width, prec);
    }

    private static int StarInt(object? v, string what)
    {
        if (v is BigInteger n && BigInteger.Abs(n) <= MaxFieldWidth) return (int)n;
        throw new StarlarkException($"Format {what} given by `*` must be an int no larger than {MaxFieldWidth}");
    }

    /// <summary>Applies a parsed <c>%</c> spec to one conversion.</summary>
    internal static string ApplyPercent(char conv, object? operand, PercentSpec spec)
    {
        switch (conv)
        {
            case 's': return PadText(Repr.Str(operand), spec);
            case 'r': return PadText(Repr.Of(operand), spec);
            case 'c': return PadText(CharConversion(operand), spec);
            case 'd': case 'i':
                return Compose(ToInt(operand), 'd', spec.Plus, spec.Space, spec.Alt, spec.Zero, spec.Width, spec.Prec, spec.Left);
            case 'o': case 'x': case 'X':
                return Compose(ToInt(operand), conv, spec.Plus, spec.Space, spec.Alt, spec.Zero, spec.Width, spec.Prec, spec.Left);
            case 'e': case 'E': case 'f': case 'F': case 'g': case 'G':
                return Compose(ToFloat(operand), conv, spec.Plus, spec.Space, spec.Alt, spec.Zero, spec.Width, spec.Prec, spec.Left);
            default:
                throw new StarlarkException($"Unknown format specifier '%{conv}'");
        }
    }

    private static string PadText(string text, PercentSpec spec)
    {
        if (spec.Prec is { } p && p < CharCount(text)) text = TakeChars(text, p);
        return Pad("", text, spec.Width, spec.Left, zero: false);
    }

    private static string CharConversion(object? v)
    {
        if (v is BigInteger n && n >= 0 && n <= 0x10FFFF && !(n >= 0xD800 && n <= 0xDFFF)) return char.ConvertFromUtf32((int)n);
        if (v is string s && s.Length >= 1 && CodePointCount(s) == 1) return s;
        throw new StarlarkException("%c requires a valid code point or a single-character string");
    }

    // ---------------------------------------------------------------- the number formatter both routes use

    /// <summary>
    /// Formats a number. <paramref name="type"/> is one of <c>d x X o b e E f F g G %</c> (and <c>n</c> as <c>d</c>);
    /// <paramref name="number"/> is a <see cref="BigInteger"/> or a <see cref="double"/>.
    /// </summary>
    internal static string Compose(object number, char type, bool plus, bool space, bool alt, bool zero, int? width,
        int? prec, bool left, string fill = " ", char align = '\0', char grouping = '\0')
    {
        string sign;
        string body;
        string prefix = "";

        if (number is BigInteger big)
        {
            if (type is 'e' or 'E' or 'f' or 'F' or 'g' or 'G' or '%')
            {
                return Compose(Ops.BigToDouble(big), type, plus, space, alt, zero, width, prec, left, fill, align, grouping);
            }
            sign = big.Sign < 0 ? "-" : plus ? "+" : space ? " " : "";
            var abs = BigInteger.Abs(big);
            switch (type)
            {
                case 'x': body = ToBase(abs, 16, false); prefix = alt ? "0x" : ""; break;
                case 'X': body = ToBase(abs, 16, true); prefix = alt ? "0X" : ""; break;
                case 'o': body = ToBase(abs, 8, false); prefix = alt ? "0o" : ""; break;
                case 'b': body = ToBase(abs, 2, false); prefix = alt ? "0b" : ""; break;
                case 'd' or 'n': body = abs.ToString(Inv); break;
                default: throw new StarlarkException($"Unknown format code '{type}' for an int");
            }
            if (prec is { } minDigits && body.Length < minDigits) body = new string('0', minDigits - body.Length) + body;
            if (grouping != '\0') body = GroupDigits(body, grouping, type is 'd' or 'n' ? 3 : 4);
        }
        else
        {
            double d = (double)number;
            if (type is 'd' or 'x' or 'X' or 'o' or 'b' or 'n')
            {
                throw new StarlarkException($"Unknown format code '{type}' for a float");
            }
            bool neg = double.IsNegative(d) && !double.IsNaN(d);
            sign = neg ? "-" : plus ? "+" : space ? " " : "";
            double abs = Math.Abs(d);
            if (double.IsNaN(d)) body = "nan";
            else if (double.IsInfinity(d)) body = "inf";
            else
            {
                body = FloatBody(abs, type, prec, alt);
                if (grouping != '\0')
                {
                    int dot = body.IndexOf('.');
                    int e = body.IndexOfAny(['e', 'E']);
                    int cut = dot >= 0 ? dot : e >= 0 ? e : body.Length;
                    body = GroupDigits(body[..cut], grouping, 3) + body[cut..];
                }
            }
            if (type is 'E' or 'G' or 'F') body = body.ToUpperInvariant();
            // inf and nan are never zero padded
            if (double.IsNaN(d) || double.IsInfinity(d)) zero = false;
        }
        return Pad(sign + prefix, body, width, left, zero, fill, align);
    }

    private static string FloatBody(double abs, char type, int? prec, bool alt)
    {
        switch (type)
        {
            case 'f': case 'F': return abs.ToString("F" + (prec ?? 6), Inv);
            case '%': return (abs * 100).ToString("F" + (prec ?? 6), Inv) + "%";
            case 'e': case 'E':
            {
                string s = abs.ToString((prec ?? 6) == 0 ? "0e+00" : "0." + new string('0', prec ?? 6) + "e+00", Inv);
                return s;
            }
            case 'g': case 'G':
            {
                int p = prec is null ? 6 : Math.Max(1, prec.Value);
                if (abs == 0) return alt ? "0." + new string('0', p - 1) : "0";
                int exp = (int)Math.Floor(Math.Log10(abs));
                string text;
                if (exp < -4 || exp >= p)
                {
                    text = abs.ToString("0." + new string(alt ? '0' : '#', Math.Max(0, p - 1)) + "e+00", Inv);
                }
                else
                {
                    text = abs.ToString("F" + Math.Max(0, p - 1 - exp), Inv);
                    if (!alt && text.Contains('.')) text = text.TrimEnd('0').TrimEnd('.');
                }
                return text;
            }
            default:
                throw new StarlarkException($"Unknown format code '{type}' for a float");
        }
    }

    // ---------------------------------------------------------------- the "{value:spec}" mini-language

    /// <summary>
    /// Formats <paramref name="value"/> by a Python-style format spec:
    /// <c>[[fill]align][sign][#][0][width][,|_][.precision][type]</c>. An empty spec means plain <c>str</c>.
    /// </summary>
    public static string FormatWithSpec(object? value, string spec)
    {
        if (spec.Length == 0) return Repr.Str(value);
        int i = 0;
        string fill = " ";
        char align = '\0';
        // The fill is one code point (a surrogate pair counts as one), and is only a fill if an alignment follows it.
        int fillLength = spec.Length > 1 && char.IsHighSurrogate(spec[0]) && char.IsLowSurrogate(spec[1]) ? 2 : 1;
        if (spec.Length > fillLength && spec[fillLength] is '<' or '>' or '^' or '=')
        {
            fill = spec[..fillLength];
            align = spec[fillLength];
            i = fillLength + 1;
        }
        else if (spec.Length >= 1 && spec[0] is '<' or '>' or '^' or '=')
        {
            align = spec[0];
            i = 1;
        }
        bool plus = false, space = false;
        if (i < spec.Length && spec[i] is '+' or '-' or ' ')
        {
            plus = spec[i] == '+';
            space = spec[i] == ' ';
            i++;
        }
        bool alt = false;
        if (i < spec.Length && spec[i] == '#')
        {
            alt = true;
            i++;
        }
        bool zero = false;
        if (i < spec.Length && spec[i] == '0')
        {
            zero = true;
            i++;
        }
        int? width = ReadNumber(spec, ref i);
        char grouping = '\0';
        if (i < spec.Length && spec[i] is ',' or '_')
        {
            grouping = spec[i];
            i++;
        }
        int? prec = null;
        if (i < spec.Length && spec[i] == '.')
        {
            i++;
            prec = ReadNumber(spec, ref i) ?? throw new StarlarkException("Format specifier missing precision");
        }
        char type = '\0';
        if (i < spec.Length)
        {
            type = spec[i++];
            if (i < spec.Length) throw new StarlarkException($"Invalid format specifier '{spec}'");
        }

        switch (value)
        {
            case string or StarUtf16:
            {
                if (type is not ('\0' or 's')) throw new StarlarkException($"Unknown format code '{type}' for a string");
                string text = Repr.Str(value);
                if (prec is { } p && p < CharCount(text)) text = TakeChars(text, p);
                return Pad("", text, width, left: false, zero: false, fill, align == '\0' ? '<' : align);
            }
            case BigInteger n:
                return Compose(n, type == '\0' ? 'd' : type, plus, space, alt, zero, width, prec: null, left: false, fill, align, grouping);
            case double d:
                return Compose(d, type == '\0' ? 'g' : type, plus, space, alt, zero, width, prec, left: false, fill, align, grouping);
            case bool b:
                return Pad("", b ? "True" : "False", width, left: false, zero: false, fill, align == '\0' ? '<' : align);
            default:
            {
                if (type is not ('\0' or 's')) throw new StarlarkException($"Unknown format code '{type}' for a {Types.Name(value)}");
                return Pad("", Repr.Str(value), width, left: false, zero: false, fill, align == '\0' ? '<' : align);
            }
        }
    }
}
