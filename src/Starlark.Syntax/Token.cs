using System.Globalization;
using System.Numerics;

namespace Starlark.Syntax;

public enum TokenKind
{
    /// <summary>Span includes the leading <c>#</c>; the text does not.</summary>
    Comment,
    Indent,
    Dedent,
    Newline,
    Identifier,
    Int,
    Float,
    String,
    Bytes,

    // F-string tokens.
    FStringStart,
    FStringText,
    FStringExprStart,
    FStringExprEnd,
    FStringBang,
    FStringEnd,

    // Keywords.
    And, Break, Continue, Def, Elif, Else, For, If, In, Lambda, Load, Not, Or, Pass, Return,

    // Symbols.
    Comma, Semicolon, Colon,
    PlusEqual, MinusEqual, StarEqual, SlashEqual, SlashSlashEqual, PercentEqual,
    EqualEqual, BangEqual, LessEqual, GreaterEqual,
    StarStar, MinusGreater, Equal, LessThan, GreaterThan,
    Minus, Plus, Star, Percent, Slash, SlashSlash, Dot,
    Ampersand, Pipe, Caret, LessLess, GreaterGreater, Tilde,
    AmpersandEqual, PipeEqual, CaretEqual, LessLessEqual, GreaterGreaterEqual,
    Ellipsis,

    // Brackets.
    OpeningSquare, OpeningCurly, OpeningRound, ClosingSquare, ClosingCurly, ClosingRound,
}

/// <summary>Quote style for f-strings.</summary>
public enum FStringQuote
{
    Single,
    Double,
    TripleSingle,
    TripleDouble,
}

/// <summary>Conversion specifier for f-string expressions.</summary>
public enum FStringConv
{
    None,
    Str,
    Repr,
}

/// <summary>An integer literal: an <see cref="int"/> if it fits, otherwise a big integer.</summary>
public readonly struct TokenInt : IEquatable<TokenInt>
{
    public BigInteger Value { get; }

    public TokenInt(BigInteger value) => Value = value;

    public bool IsI32 => Value >= int.MinValue && Value <= int.MaxValue;

    /// <summary>Parse unsigned digits in the given radix (2..16).</summary>
    public static TokenInt FromStrRadix(string digits, int radix)
    {
        if (digits.Length == 0)
        {
            throw new FormatException("empty integer");
        }
        BigInteger acc = BigInteger.Zero;
        foreach (char c in digits)
        {
            int d = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => throw new FormatException($"bad digit '{c}'"),
            };
            if (d >= radix)
            {
                throw new FormatException($"bad digit '{c}' for radix {radix}");
            }
            acc = acc * radix + d;
        }
        return new TokenInt(acc);
    }

    public bool Equals(TokenInt other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is TokenInt t && Equals(t);
    public override int GetHashCode() => Value.GetHashCode();
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A lexical token, with payload for the kinds that carry one.</summary>
public sealed class Token : IEquatable<Token>
{
    private static readonly Dictionary<TokenKind, Token> Simple = Enum.GetValues<TokenKind>()
        .ToDictionary(k => k, k => new Token(k));

    public TokenKind Kind { get; }

    /// <summary>Identifier name, string contents, comment text (without <c>#</c>), or f-string text.</summary>
    public string? Text { get; }

    public TokenInt IntValue { get; }
    public double FloatValue { get; }
    public byte[]? BytesValue { get; }
    public FStringQuote Quote { get; }

    private Token(TokenKind kind, string? text = null, TokenInt intValue = default, double floatValue = 0,
        byte[]? bytes = null, FStringQuote quote = default)
    {
        Kind = kind;
        Text = text;
        IntValue = intValue;
        FloatValue = floatValue;
        BytesValue = bytes;
        Quote = quote;
    }

    public static Token Of(TokenKind kind) => Simple[kind];
    public static Token Identifier(string name) => new(TokenKind.Identifier, name);
    public static Token String(string s) => new(TokenKind.String, s);
    public static Token Comment(string s) => new(TokenKind.Comment, s);
    public static Token FStringText(string s) => new(TokenKind.FStringText, s);
    public static Token Int(TokenInt i) => new(TokenKind.Int, intValue: i);
    public static Token Float(double d) => new(TokenKind.Float, floatValue: d);
    public static Token Bytes(byte[] b) => new(TokenKind.Bytes, bytes: b);
    public static Token FStringStart(FStringQuote q) => new(TokenKind.FStringStart, quote: q);

    public bool Is(TokenKind kind) => Kind == kind;

    public bool Equals(Token? other)
    {
        if (other is null || Kind != other.Kind)
        {
            return false;
        }
        return Kind switch
        {
            TokenKind.Identifier or TokenKind.String or TokenKind.Comment or TokenKind.FStringText => Text == other.Text,
            TokenKind.Int => IntValue.Equals(other.IntValue),
            TokenKind.Float => FloatValue.Equals(other.FloatValue),
            TokenKind.Bytes => BytesValue.AsSpan().SequenceEqual(other.BytesValue),
            TokenKind.FStringStart => Quote == other.Quote,
            _ => true,
        };
    }

    public override bool Equals(object? obj) => Equals(obj as Token);
    public override int GetHashCode() => HashCode.Combine(Kind, Text);

    /// <summary>Human-readable description, as used in parse error messages.</summary>
    public override string ToString() => Kind switch
    {
        TokenKind.Indent => "new indentation block",
        TokenKind.Dedent => "end of indentation block",
        TokenKind.Newline => "new line",
        TokenKind.And => "keyword 'and'",
        TokenKind.Else => "keyword 'else'",
        TokenKind.Load => "keyword 'load'",
        TokenKind.Break => "keyword 'break'",
        TokenKind.For => "keyword 'for'",
        TokenKind.Not => "keyword 'not'",
        TokenKind.Continue => "keyword 'continue'",
        TokenKind.If => "keyword 'if'",
        TokenKind.Or => "keyword 'or'",
        TokenKind.Def => "keyword 'def'",
        TokenKind.In => "keyword 'in'",
        TokenKind.Pass => "keyword 'pass'",
        TokenKind.Elif => "keyword 'elif'",
        TokenKind.Return => "keyword 'return'",
        TokenKind.Lambda => "keyword 'lambda'",
        TokenKind.Comma => "symbol ','",
        TokenKind.Semicolon => "symbol ';'",
        TokenKind.Colon => "symbol ':'",
        TokenKind.PlusEqual => "symbol '+='",
        TokenKind.MinusEqual => "symbol '-='",
        TokenKind.StarEqual => "symbol '*='",
        TokenKind.SlashEqual => "symbol '/='",
        TokenKind.SlashSlashEqual => "symbol '//='",
        TokenKind.PercentEqual => "symbol '%='",
        TokenKind.EqualEqual => "symbol '=='",
        TokenKind.BangEqual => "symbol '!='",
        TokenKind.LessEqual => "symbol '<='",
        TokenKind.GreaterEqual => "symbol '>='",
        TokenKind.StarStar => "symbol '**'",
        TokenKind.MinusGreater => "symbol '->'",
        TokenKind.Equal => "symbol '='",
        TokenKind.LessThan => "symbol '<'",
        TokenKind.GreaterThan => "symbol '>'",
        TokenKind.Minus => "symbol '-'",
        TokenKind.Plus => "symbol '+'",
        TokenKind.Star => "symbol '*'",
        TokenKind.Percent => "symbol '%'",
        TokenKind.Slash => "symbol '/'",
        TokenKind.SlashSlash => "symbol '//'",
        TokenKind.Dot => "symbol '.'",
        TokenKind.Ampersand => "symbol '&'",
        TokenKind.Pipe => "symbol '|'",
        TokenKind.Caret => "symbol '^'",
        TokenKind.LessLess => "symbol '<<'",
        TokenKind.GreaterGreater => "symbol '>>'",
        TokenKind.Tilde => "symbol '~'",
        TokenKind.AmpersandEqual => "symbol '&='",
        TokenKind.PipeEqual => "symbol '|='",
        TokenKind.CaretEqual => "symbol '^='",
        TokenKind.LessLessEqual => "symbol '<<='",
        TokenKind.GreaterGreaterEqual => "symbol '>>='",
        TokenKind.Ellipsis => "symbol '...'",
        TokenKind.OpeningSquare => "symbol '['",
        TokenKind.OpeningCurly => "symbol '{'",
        TokenKind.OpeningRound => "symbol '('",
        TokenKind.ClosingSquare => "symbol ']'",
        TokenKind.ClosingCurly => "symbol '}'",
        TokenKind.ClosingRound => "symbol ')'",
        TokenKind.Identifier => $"identifier '{Text}'",
        TokenKind.Int => $"integer literal '{IntValue}'",
        TokenKind.Float => $"float literal '{RustFormat.DisplayF64(FloatValue)}'",
        TokenKind.String => $"string literal {RustFormat.DebugStr(Text!)}",
        TokenKind.FStringStart => "f-string start",
        TokenKind.FStringText => $"f-string text {RustFormat.DebugStr(Text!)}",
        TokenKind.FStringExprStart => "f-string expression start '{'",
        TokenKind.FStringExprEnd => "f-string expression end '}'",
        TokenKind.FStringBang => "f-string bang '!'",
        TokenKind.FStringEnd => "f-string end",
        TokenKind.Bytes => $"bytes literal ({BytesValue!.Length} bytes)",
        TokenKind.Comment => $"comment '{Text}'",
        _ => Kind.ToString(),
    };
}

/// <summary>
/// Formatting that matches Rust's <c>Display</c>/<c>Debug</c> output, so diagnostics and golden
/// files from the original implementation can be compared byte for byte.
/// </summary>
public static class RustFormat
{
    /// <summary>Shortest round-trip decimal digits and exponent: value = 0.DIGITS × 10^exp10 is NOT used;
    /// instead value = D.DDD × 10^exp10 with <paramref name="digits"/> holding D.DDD without the dot.</summary>
    public static void Decompose(double abs, out string digits, out int exp10)
    {
        string r = abs.ToString("E16", CultureInfo.InvariantCulture);
        // Find the shortest "R" representation then normalise it.
        string s = abs.ToString("R", CultureInfo.InvariantCulture);
        int e = 0;
        int ePos = s.IndexOfAny(['E', 'e']);
        if (ePos >= 0)
        {
            e = int.Parse(s[(ePos + 1)..], CultureInfo.InvariantCulture);
            s = s[..ePos];
        }
        int dot = s.IndexOf('.');
        string intPart = dot >= 0 ? s[..dot] : s;
        string frac = dot >= 0 ? s[(dot + 1)..] : "";
        string all = intPart + frac;
        int pointPos = intPart.Length; // digits before the decimal point
        int lead = 0;
        while (lead < all.Length - 1 && all[lead] == '0')
        {
            lead++;
        }
        all = all[lead..];
        pointPos -= lead;
        all = all.TrimEnd('0');
        if (all.Length == 0)
        {
            all = "0";
        }
        digits = all;
        exp10 = e + pointPos - 1;
        _ = r;
    }

    /// <summary>Rust <c>{}</c> for f64: never uses an exponent.</summary>
    public static string DisplayF64(double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsPositiveInfinity(d)) return "inf";
        if (double.IsNegativeInfinity(d)) return "-inf";
        string sign = double.IsNegative(d) ? "-" : "";
        double abs = Math.Abs(d);
        if (abs == 0) return sign + "0";
        Decompose(abs, out string digits, out int exp10);
        return sign + PlainDecimal(digits, exp10, minFrac: 0);
    }

    /// <summary>Rust <c>{:?}</c> for f64.</summary>
    public static string DebugF64(double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsPositiveInfinity(d)) return "inf";
        if (double.IsNegativeInfinity(d)) return "-inf";
        string sign = double.IsNegative(d) ? "-" : "";
        double abs = Math.Abs(d);
        if (abs == 0) return sign + "0.0";
        Decompose(abs, out string digits, out int exp10);
        if (abs >= 1e16 || abs < 1e-4)
        {
            string mant = digits.Length == 1 ? digits : digits[0] + "." + digits[1..];
            return $"{sign}{mant}e{exp10}";
        }
        return sign + PlainDecimal(digits, exp10, minFrac: 1);
    }

    public static string PlainDecimal(string digits, int exp10, int minFrac)
    {
        string intPart;
        string frac;
        if (exp10 >= 0)
        {
            if (digits.Length <= exp10 + 1)
            {
                intPart = digits + new string('0', exp10 + 1 - digits.Length);
                frac = "";
            }
            else
            {
                intPart = digits[..(exp10 + 1)];
                frac = digits[(exp10 + 1)..];
            }
        }
        else
        {
            intPart = "0";
            frac = new string('0', -exp10 - 1) + digits;
        }
        if (frac.Length < minFrac)
        {
            frac = frac.PadRight(minFrac, '0');
        }
        return frac.Length == 0 ? intPart : intPart + "." + frac;
    }

    /// <summary>Rust <c>{:?}</c> for a string.</summary>
    public static string DebugStr(string s)
    {
        var sb = new System.Text.StringBuilder("\"");
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            int cp = c;
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                cp = char.ConvertToUtf32(c, s[i + 1]);
                i++;
            }
            switch (cp)
            {
                case '\0': sb.Append("\\0"); break;
                case '\t': sb.Append("\\t"); break;
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                default:
                    if (NeedsUnicodeEscape(cp))
                    {
                        sb.Append("\\u{").Append(cp.ToString("x", CultureInfo.InvariantCulture)).Append('}');
                    }
                    else
                    {
                        sb.Append(char.ConvertFromUtf32(cp));
                    }
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    private static bool NeedsUnicodeEscape(int cp)
    {
        if (cp >= 0xD800 && cp <= 0xDFFF)
        {
            return true;
        }
        if (cp == ' ')
        {
            return false;
        }
        var cat = CharUnicodeInfo.GetUnicodeCategory(cp);
        return cat is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
            or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
            or UnicodeCategory.SpaceSeparator
            or UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark;
    }
}
