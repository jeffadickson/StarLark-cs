using System.Globalization;

namespace Starlark.Syntax;

internal enum RawKind
{
    /// <summary>A keyword, symbol or bracket: <see cref="RawToken.Kind"/> says which.</summary>
    Plain,
    Comment,
    Tabs,
    Newline,
    SingleQuote,
    DoubleQuote,
    FSingleQuote,
    FDoubleQuote,
    BSingleQuote,
    BDoubleQuote,
    Reserved,
    Identifier,
    DecInt,
    HexInt,
    BinInt,
    OctInt,
    Float,
}

internal struct RawToken
{
    public RawKind Raw;
    public TokenKind Kind;
    public double FloatValue;
}

internal enum RawResult
{
    Token,
    End,
    Error,
}

/// <summary>
/// The character-level tokenizer. This replaces the <c>logos</c>-generated lexer of the Rust original:
/// longest match wins, ties go to the more specific rule. It does not deal with indentation or
/// string bodies; <see cref="Lexer"/> does that on top.
/// </summary>
internal sealed class RawLexer
{
    private static readonly Dictionary<string, TokenKind> Keywords = new()
    {
        ["and"] = TokenKind.And,
        ["break"] = TokenKind.Break,
        ["continue"] = TokenKind.Continue,
        ["def"] = TokenKind.Def,
        ["elif"] = TokenKind.Elif,
        ["else"] = TokenKind.Else,
        ["for"] = TokenKind.For,
        ["if"] = TokenKind.If,
        ["in"] = TokenKind.In,
        ["lambda"] = TokenKind.Lambda,
        ["load"] = TokenKind.Load,
        ["not"] = TokenKind.Not,
        ["or"] = TokenKind.Or,
        ["pass"] = TokenKind.Pass,
        ["return"] = TokenKind.Return,
    };

    private static readonly HashSet<string> Reserved =
    [
        "as", "assert", "async", "await", "class", "del", "except", "finally", "from", "global",
        "import", "is", "nonlocal", "raise", "try", "while", "with", "yield",
    ];

    private readonly string _s;

    /// <summary>Start of the current token.</summary>
    public int TokStart { get; private set; }

    /// <summary>End of the current token; also where the remainder begins.</summary>
    public int TokEnd { get; private set; }

    public RawLexer(string source) => _s = source;

    public string Source => _s;

    public string Slice => _s.Substring(TokStart, TokEnd - TokStart);

    /// <summary>Extend the current token by <paramref name="n"/> characters.</summary>
    public void Bump(int n) => TokEnd += n;

    public bool RemainderStartsWith(string prefix) => string.CompareOrdinal(_s, TokEnd, prefix, 0, prefix.Length) == 0
        && TokEnd + prefix.Length <= _s.Length;

    private static bool IsIdentStart(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_';
    private static bool IsIdentPart(char c) => IsIdentStart(c) || IsDigit(c);
    private static bool IsDigit(char c) => c is >= '0' and <= '9';
    private static bool IsHex(char c) => IsDigit(c) || c is (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    private char At(int i) => i < _s.Length ? _s[i] : '\0';

    public RawResult Next(out RawToken tok)
    {
        tok = default;
        int p = TokEnd;

        // Skip whitespace (spaces only) and escaped newlines.
        while (p < _s.Length)
        {
            char c = _s[p];
            if (c == ' ')
            {
                p++;
            }
            else if (c == '\\' && At(p + 1) == '\n')
            {
                p += 2;
            }
            else if (c == '\\' && At(p + 1) == '\r' && At(p + 2) == '\n')
            {
                p += 3;
            }
            else
            {
                break;
            }
        }

        TokStart = TokEnd = p;
        if (p >= _s.Length)
        {
            return RawResult.End;
        }

        char ch = _s[p];
        switch (ch)
        {
            case '\n':
                TokEnd = p + 1;
                tok.Raw = RawKind.Newline;
                return RawResult.Token;
            case '\r':
                if (At(p + 1) == '\n')
                {
                    TokEnd = p + 2;
                    tok.Raw = RawKind.Newline;
                    return RawResult.Token;
                }
                return Fail(p);
            case '\t':
            {
                int q = p;
                while (At(q) == '\t')
                {
                    q++;
                }
                TokEnd = q;
                tok.Raw = RawKind.Tabs;
                return RawResult.Token;
            }
            case '#':
            {
                int q = p;
                while (q < _s.Length && _s[q] != '\r' && _s[q] != '\n')
                {
                    q++;
                }
                TokEnd = q;
                tok.Raw = RawKind.Comment;
                return RawResult.Token;
            }
            case '\'':
                TokEnd = p + 1;
                tok.Raw = RawKind.SingleQuote;
                return RawResult.Token;
            case '"':
                TokEnd = p + 1;
                tok.Raw = RawKind.DoubleQuote;
                return RawResult.Token;
        }

        if (IsIdentStart(ch))
        {
            return LexWord(p, ref tok);
        }
        if (IsDigit(ch))
        {
            return LexNumber(p, ref tok);
        }
        if (ch == '.' && IsDigit(At(p + 1)))
        {
            int q = p + 1;
            while (IsDigit(At(q)))
            {
                q++;
            }
            q = ScanExponent(q);
            return Float(p, q, ref tok);
        }
        return LexSymbol(p, ref tok);
    }

    private RawResult Fail(int p)
    {
        // Consume one whole code point so the caller can carry on.
        int len = char.IsHighSurrogate(_s[p]) && p + 1 < _s.Length && char.IsLowSurrogate(_s[p + 1]) ? 2 : 1;
        TokEnd = p + len;
        return RawResult.Error;
    }

    private RawResult LexWord(int p, ref RawToken tok)
    {
        int q = p;
        while (q < _s.Length && IsIdentPart(_s[q]))
        {
            q++;
        }
        string word = _s.Substring(p, q - p);
        char next = At(q);
        if (next is '\'' or '"')
        {
            bool dq = next == '"';
            RawKind? prefixed = word switch
            {
                "r" => dq ? RawKind.DoubleQuote : RawKind.SingleQuote,
                "f" or "fr" => dq ? RawKind.FDoubleQuote : RawKind.FSingleQuote,
                "b" or "br" or "rb" => dq ? RawKind.BDoubleQuote : RawKind.BSingleQuote,
                _ => null,
            };
            if (prefixed is { } kind)
            {
                TokEnd = q + 1;
                tok.Raw = kind;
                return RawResult.Token;
            }
        }
        TokEnd = q;
        if (Reserved.Contains(word))
        {
            tok.Raw = RawKind.Reserved;
        }
        else if (Keywords.TryGetValue(word, out var kw))
        {
            tok.Raw = RawKind.Plain;
            tok.Kind = kw;
        }
        else
        {
            tok.Raw = RawKind.Identifier;
        }
        return RawResult.Token;
    }

    /// <summary>Scan an optional <c>[eE][-+]?[0-9]+</c> at <paramref name="q"/>; returns the new end.</summary>
    private int ScanExponent(int q)
    {
        if (At(q) is 'e' or 'E')
        {
            int r = q + 1;
            if (At(r) is '+' or '-')
            {
                r++;
            }
            if (IsDigit(At(r)))
            {
                while (IsDigit(At(r)))
                {
                    r++;
                }
                return r;
            }
        }
        return q;
    }

    private RawResult Float(int start, int end, ref RawToken tok)
    {
        TokEnd = end;
        tok.Raw = RawKind.Float;
        tok.FloatValue = ParseFloat(_s.Substring(start, end - start));
        return RawResult.Token;
    }

    internal static double ParseFloat(string text)
    {
        string t = text;
        if (t.StartsWith('.'))
        {
            t = "0" + t;
        }
        t = t.Replace(".e", ".0e").Replace(".E", ".0E");
        if (t.EndsWith('.'))
        {
            t += "0";
        }
        return double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private RawResult LexNumber(int p, ref RawToken tok)
    {
        if (_s[p] == '0')
        {
            char x = At(p + 1);
            RawKind? radixKind = x switch
            {
                'x' or 'X' when IsHex(At(p + 2)) => RawKind.HexInt,
                'b' or 'B' when At(p + 2) is '0' or '1' => RawKind.BinInt,
                'o' or 'O' when At(p + 2) is >= '0' and <= '7' => RawKind.OctInt,
                _ => null,
            };
            if (radixKind is { } k)
            {
                int q = p + 2;
                while (k switch
                {
                    RawKind.HexInt => IsHex(At(q)),
                    RawKind.BinInt => At(q) is '0' or '1',
                    _ => At(q) is >= '0' and <= '7',
                })
                {
                    q++;
                }
                TokEnd = q;
                tok.Raw = k;
                return RawResult.Token;
            }
        }

        int e = p;
        while (IsDigit(At(e)))
        {
            e++;
        }
        if (At(e) == '.')
        {
            int q = e + 1;
            while (IsDigit(At(q)))
            {
                q++;
            }
            q = ScanExponent(q);
            return Float(p, q, ref tok);
        }
        int ex = ScanExponent(e);
        if (ex != e)
        {
            return Float(p, ex, ref tok);
        }
        TokEnd = e;
        tok.Raw = RawKind.DecInt;
        return RawResult.Token;
    }

    private RawResult LexSymbol(int p, ref RawToken tok)
    {
        char c = _s[p];
        char c1 = At(p + 1);
        char c2 = At(p + 2);
        TokenKind kind;
        int len = 1;
        switch (c)
        {
            case ',': kind = TokenKind.Comma; break;
            case ';': kind = TokenKind.Semicolon; break;
            case ':': kind = TokenKind.Colon; break;
            case '(': kind = TokenKind.OpeningRound; break;
            case ')': kind = TokenKind.ClosingRound; break;
            case '[': kind = TokenKind.OpeningSquare; break;
            case ']': kind = TokenKind.ClosingSquare; break;
            case '{': kind = TokenKind.OpeningCurly; break;
            case '}': kind = TokenKind.ClosingCurly; break;
            case '~': kind = TokenKind.Tilde; break;
            case '+': (kind, len) = c1 == '=' ? (TokenKind.PlusEqual, 2) : (TokenKind.Plus, 1); break;
            case '-':
                (kind, len) = c1 == '=' ? (TokenKind.MinusEqual, 2) : c1 == '>' ? (TokenKind.MinusGreater, 2) : (TokenKind.Minus, 1);
                break;
            case '*':
                (kind, len) = c1 == '*' ? (TokenKind.StarStar, 2) : c1 == '=' ? (TokenKind.StarEqual, 2) : (TokenKind.Star, 1);
                break;
            case '/':
                (kind, len) = c1 == '/'
                    ? (c2 == '=' ? (TokenKind.SlashSlashEqual, 3) : (TokenKind.SlashSlash, 2))
                    : c1 == '=' ? (TokenKind.SlashEqual, 2) : (TokenKind.Slash, 1);
                break;
            case '%': (kind, len) = c1 == '=' ? (TokenKind.PercentEqual, 2) : (TokenKind.Percent, 1); break;
            case '=': (kind, len) = c1 == '=' ? (TokenKind.EqualEqual, 2) : (TokenKind.Equal, 1); break;
            case '!':
                if (c1 != '=')
                {
                    return Fail(p);
                }
                (kind, len) = (TokenKind.BangEqual, 2);
                break;
            case '<':
                (kind, len) = c1 == '<'
                    ? (c2 == '=' ? (TokenKind.LessLessEqual, 3) : (TokenKind.LessLess, 2))
                    : c1 == '=' ? (TokenKind.LessEqual, 2) : (TokenKind.LessThan, 1);
                break;
            case '>':
                (kind, len) = c1 == '>'
                    ? (c2 == '=' ? (TokenKind.GreaterGreaterEqual, 3) : (TokenKind.GreaterGreater, 2))
                    : c1 == '=' ? (TokenKind.GreaterEqual, 2) : (TokenKind.GreaterThan, 1);
                break;
            case '&': (kind, len) = c1 == '=' ? (TokenKind.AmpersandEqual, 2) : (TokenKind.Ampersand, 1); break;
            case '|': (kind, len) = c1 == '=' ? (TokenKind.PipeEqual, 2) : (TokenKind.Pipe, 1); break;
            case '^': (kind, len) = c1 == '=' ? (TokenKind.CaretEqual, 2) : (TokenKind.Caret, 1); break;
            case '.':
                (kind, len) = c1 == '.' && c2 == '.' ? (TokenKind.Ellipsis, 3) : (TokenKind.Dot, 1);
                break;
            default:
                return Fail(p);
        }
        TokEnd = p + len;
        tok.Raw = RawKind.Plain;
        tok.Kind = kind;
        return RawResult.Token;
    }
}
