using System.Text;

namespace Starlark.Syntax;

/// <summary>A token with its span, or a lexical error. Errors are deferred so the parser can report them in order.</summary>
public readonly record struct Lexeme(int Start, Token? Token, int End, StarlarkSyntaxException? Error = null)
{
    public bool IsError => Error is not null;
}

/// <summary>A cursor over Unicode code points of a string, starting at an offset.</summary>
internal sealed class Cursor
{
    private readonly string _s;
    private readonly int _base;
    private int _idx;

    public Cursor(string s, int baseIndex, int offset)
    {
        _s = s;
        _base = baseIndex;
        _idx = baseIndex + offset;
    }

    /// <summary>Offset from the starting base, in UTF-16 code units.</summary>
    public int Pos => _idx - _base;

    /// <summary>Next code point, or -1 at the end.</summary>
    public int Next()
    {
        if (_idx >= _s.Length)
        {
            return -1;
        }
        char c = _s[_idx++];
        if (char.IsHighSurrogate(c) && _idx < _s.Length && char.IsLowSurrogate(_s[_idx]))
        {
            return char.ConvertToUtf32(c, _s[_idx++]);
        }
        return c;
    }

    /// <summary>Put back a code point previously returned by <see cref="Next"/>.</summary>
    public void Unnext(int cp) => _idx -= cp > 0xFFFF ? 2 : 1;
}

/// <summary>
/// Turns Starlark source into <see cref="Lexeme"/>s, including <c>Indent</c>/<c>Dedent</c> and
/// <c>Newline</c> tokens, and f-string pieces.
/// </summary>
public sealed class Lexer
{
    private sealed class FStringState(FStringQuote quote, bool raw)
    {
        public FStringQuote Quote { get; } = quote;
        public bool Raw { get; } = raw;

        /// <summary>0 means we're in string mode; &gt; 0 means we're lexing an expression.</summary>
        public int BraceDepth { get; set; }

        public int ParenDepth { get; set; }
        public int BracketDepth { get; set; }
    }

    private readonly CodeMap _codemap;
    private readonly RawLexer _raw;
    private readonly List<int> _indentLevels = new(20);
    private readonly Queue<Lexeme> _buffer = new(10);
    private readonly List<FStringState> _fstrings = [];
    private int _parens;
    private bool _done;

    public Lexer(CodeMap codemap)
    {
        _codemap = codemap;
        _raw = new RawLexer(codemap.Source);
        var e = CalculateIndent();
        if (e is not null)
        {
            _buffer.Enqueue(e.Value);
        }
    }

    /// <summary>Lex a whole file. Stops after the first error if <paramref name="stopAtError"/>.</summary>
    public static List<Lexeme> LexAll(CodeMap codemap, bool stopAtError = false)
    {
        var lexer = new Lexer(codemap);
        var all = new List<Lexeme>();
        while (lexer.Next() is { } l)
        {
            all.Add(l);
            if (stopAtError && l.IsError)
            {
                break;
            }
        }
        return all;
    }

    // ---------------------------------------------------------------- errors

    private Lexeme ErrSpan(string message, int start, int end) =>
        new(start, null, end, new StarlarkSyntaxException(message, new Span(start, Math.Max(start, end)), _codemap));

    private Lexeme ErrNow(Func<string, string> message) =>
        ErrSpan(message(_raw.Slice), _raw.TokStart, _raw.TokEnd);

    private static string MsgInvalidInput(string s) => $"Parse error: invalid input `{s}`";
    private static string MsgReserved(string s) => $"Parse error: cannot use reserved keyword `{s}`";
    private static string MsgStartsZero(string s) => $"Parse error: integer cannot have leading 0, got `{s}`";
    private static string MsgIntParse(string s) => $"Parse error: failed to parse integer: `{s}`";

    private const string MsgIndentation = "Parse error: incorrect indentation";
    private const string MsgTab = "Parse error: tabs are not allowed";
    private const string MsgUnfinishedString = "Parse error: unfinished string literal";
    private const string MsgEmptyEscape = "Parse error: missing string escape sequence, only saw `\\`";
    private const string MsgUnfinishedFExpr = "Parse error: f-string expression is missing closing `}`";

    private static string MsgBadEscape(string s) => $"Parse error: invalid string escape sequence `{s}`";

    // ---------------------------------------------------------------- comments & indentation

    private Lexeme MakeComment(int start, int end)
    {
        var text = _codemap.Source.Substring(start, end - start);
        if (!text.StartsWith('#'))
        {
            return ErrSpan("Comment span is computed incorrectly (internal error)", start, start);
        }
        text = text[1..];
        // Remove the trailing `\r` if it exists. Comments do not contain `\n`.
        if (text.EndsWith('\r'))
        {
            return new Lexeme(start, Token.Comment(text[..^1]), end - 1);
        }
        return new Lexeme(start, Token.Comment(text), end);
    }

    /// <summary>
    /// We have just seen a newline: read the next line's indentation and queue Indent/Dedent tokens.
    /// Returns an error lexeme if the indentation is invalid.
    /// </summary>
    private Lexeme? CalculateIndent()
    {
        string src = _codemap.Source;
        int baseEnd = _raw.TokEnd;
        int p = baseEnd; // absolute scan position
        int spaces = 0;
        int tabs = 0;
        int indentStart = baseEnd;

        while (true)
        {
            if (p >= src.Length)
            {
                _raw.Bump(p - baseEnd);
                return null;
            }
            char c = src[p++];
            if (c == ' ')
            {
                spaces++;
            }
            else if (c == '\t')
            {
                tabs++;
            }
            else if (c == '\n')
            {
                // A line that is entirely blank gets emitted as a newline; don't consume this newline.
                _raw.Bump(p - 1 - baseEnd);
                return null;
            }
            else if (c == '\r')
            {
                // Ignored entirely.
            }
            else if (c == '#')
            {
                // A line that is all comments only emits comment tokens.
                spaces = 0;
                tabs = 0;
                int start = p - 1;
                bool sawNewline = false;
                while (p < src.Length)
                {
                    if (src[p++] == '\n')
                    {
                        sawNewline = true;
                        break;
                    }
                }
                if (!sawNewline)
                {
                    _buffer.Enqueue(MakeComment(start, p));
                    _raw.Bump(p - baseEnd);
                    return null;
                }
                _buffer.Enqueue(MakeComment(start, p - 1));
                indentStart = p;
            }
            else
            {
                break;
            }
        }

        _raw.Bump(p - 1 - baseEnd); // the last character broke us out of the loop
        int indent = spaces + tabs * 8;
        if (tabs > 0)
        {
            return ErrSpan(MsgTab, _raw.TokStart, _raw.TokStart);
        }
        int now = _indentLevels.Count > 0 ? _indentLevels[^1] : 0;

        if (indent > now)
        {
            _indentLevels.Add(indent);
            _buffer.Enqueue(new Lexeme(indentStart, Token.Of(TokenKind.Indent), _raw.TokEnd));
        }
        else if (indent < now)
        {
            int dedents = 1;
            _indentLevels.RemoveAt(_indentLevels.Count - 1);
            while (true)
            {
                int n = _indentLevels.Count > 0 ? _indentLevels[^1] : 0;
                if (n == indent)
                {
                    break;
                }
                if (n > indent)
                {
                    dedents++;
                    _indentLevels.RemoveAt(_indentLevels.Count - 1);
                }
                else
                {
                    return ErrSpan(MsgIndentation, _raw.TokStart, _raw.TokEnd);
                }
            }
            for (int i = 0; i < dedents; i++)
            {
                // Each dedent is only a position, so adjacent dedents don't overlap.
                _buffer.Enqueue(new Lexeme(indentStart, Token.Of(TokenKind.Dedent), indentStart));
            }
        }
        return null;
    }

    private Lexeme Wrap(Token token) => new(_raw.TokStart, token, _raw.TokEnd);

    // ---------------------------------------------------------------- string escapes

    private static void AppendCodePoint(StringBuilder sb, int cp)
    {
        if (cp >= 0xD800 && cp <= 0xDFFF)
        {
            sb.Append((char)cp);
        }
        else
        {
            sb.Append(char.ConvertFromUtf32(cp));
        }
    }

    private static int DigitValue(int c, int radix)
    {
        int d = c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'z' => c - 'a' + 10,
            >= 'A' and <= 'Z' => c - 'A' + 10,
            _ => int.MaxValue,
        };
        return d < radix ? d : -1;
    }

    /// <summary>
    /// We've potentially seen one character; consume between <paramref name="min"/> and <paramref name="max"/>
    /// digits and treat them as an integer in <paramref name="radix"/>. Returns -1 on failure.
    /// </summary>
    private static int EscapeChar(Cursor it, int min, int max, int radix)
    {
        long value = 0;
        int count = 0;
        while (count < max)
        {
            int c = it.Next();
            if (c < 0)
            {
                if (count >= min)
                {
                    break;
                }
                return -1;
            }
            int d = DigitValue(c, radix);
            if (d < 0)
            {
                if (count >= min)
                {
                    it.Unnext(c);
                    break;
                }
                return -1;
            }
            count++;
            value = value * radix + d;
        }
        if (value > 0x10FFFF || (value >= 0xD800 && value <= 0xDFFF))
        {
            return -1;
        }
        return (int)value;
    }

    /// <summary>We have seen a backslash; parse what comes next. Returns false on a bad escape.</summary>
    private static bool Escape(Cursor it, StringBuilder res)
    {
        int c = it.Next();
        switch (c)
        {
            case 'n': res.Append('\n'); return true;
            case 'r': res.Append('\r'); return true;
            case 't': res.Append('\t'); return true;
            case 'a': res.Append('\a'); return true;
            case 'b': res.Append('\b'); return true;
            case 'f': res.Append('\f'); return true;
            case 'v': res.Append('\v'); return true;
            case '\n': return true;
            case '\r':
                // Windows newline incoming; we expect a \n next, which we ignore.
                return it.Next() == '\n';
            case 'x':
            case 'u':
            case 'U':
            {
                int n = c == 'x' ? 2 : c == 'u' ? 4 : 8;
                int v = EscapeChar(it, n, n, 16);
                if (v < 0)
                {
                    return false;
                }
                AppendCodePoint(res, v);
                return true;
            }
            case -1:
                return false;
            case >= '0' and <= '7':
            {
                it.Unnext(c);
                int v = EscapeChar(it, 1, 3, 8);
                if (v < 0)
                {
                    return false;
                }
                AppendCodePoint(res, v);
                return true;
            }
            case '"' or '\'' or '\\':
                res.Append((char)c);
                return true;
            default:
                res.Append('\\');
                AppendCodePoint(res, c);
                return true;
        }
    }

    /// <summary>Like <see cref="Escape"/>, but appends bytes. Non-ASCII is encoded as UTF-8.</summary>
    private static bool EscapeBytes(Cursor it, List<byte> res)
    {
        int c = it.Next();
        switch (c)
        {
            case 'n': res.Add((byte)'\n'); return true;
            case 'r': res.Add((byte)'\r'); return true;
            case 't': res.Add((byte)'\t'); return true;
            case 'a': res.Add(0x07); return true;
            case 'b': res.Add(0x08); return true;
            case 'f': res.Add(0x0C); return true;
            case 'v': res.Add(0x0B); return true;
            case '\n': return true;
            case '\r': return it.Next() == '\n';
            case 'x':
            {
                int v = EscapeChar(it, 2, 2, 16);
                if (v < 0)
                {
                    return false;
                }
                // \xHH: store the low byte directly (Latin-1 / bytes-literal semantics).
                res.Add((byte)v);
                return true;
            }
            case 'u':
            case 'U':
            {
                int n = c == 'u' ? 4 : 8;
                int v = EscapeChar(it, n, n, 16);
                if (v < 0)
                {
                    return false;
                }
                AppendUtf8(res, v);
                return true;
            }
            case -1:
                return false;
            case >= '0' and <= '7':
            {
                it.Unnext(c);
                int v = EscapeChar(it, 1, 3, 8);
                if (v < 0 || v > 255)
                {
                    return false;
                }
                res.Add((byte)v);
                return true;
            }
            case '"' or '\'' or '\\':
                res.Add((byte)c);
                return true;
            default:
                res.Add((byte)'\\');
                AppendUtf8(res, c);
                return true;
        }
    }

    private static void AppendUtf8(List<byte> res, int cp)
    {
        if (cp >= 0xD800 && cp <= 0xDFFF)
        {
            cp = 0xFFFD;
        }
        res.AddRange(Encoding.UTF8.GetBytes(char.ConvertFromUtf32(cp)));
    }

    // ---------------------------------------------------------------- string literals

    /// <summary>Tracks whether we've reached the closing quote(s).</summary>
    private sealed class QuoteStop(char quote, bool triple)
    {
        private int _count;

        public bool Check(int c)
        {
            if (!triple)
            {
                return c == quote;
            }
            if (c == quote)
            {
                _count++;
                return _count == 3;
            }
            _count = 0;
            return false;
        }
    }

    /// <summary>
    /// Parse a string or bytes literal body. The opening quote has been consumed by the raw lexer.
    /// </summary>
    private Lexeme LexQuoted(char quote, bool raw, bool bytes)
    {
        bool triple = _raw.RemainderStartsWith(quote == '"' ? "\"\"" : "''");
        var stop = new QuoteStop(quote, triple);
        string src = _codemap.Source;

        int stringStart = _raw.TokStart;
        int stringEnd = _raw.TokEnd; // before any tracked stuff; == start of remainder
        int baseIdx = stringEnd;

        var fast = new Cursor(src, baseIdx, 0);
        if (triple)
        {
            fast.Next();
            fast.Next();
        }
        int contentsStart = fast.Pos;

        StringBuilder? sres = null;
        List<byte>? bres = null;
        Cursor it;

        // Fast path: as long as the result is a slice of the original, with no changes.
        while (true)
        {
            int c = fast.Next();
            if (c < 0)
            {
                return ErrSpan(
                    !bytes && InFStringExprMode ? MsgUnfinishedFExpr : MsgUnfinishedString,
                    stringStart, stringEnd + fast.Pos);
            }
            if (stop.Check(c))
            {
                int contentsEnd = fast.Pos - (triple ? 3 : 1);
                string contents = src.Substring(baseIdx + contentsStart, contentsEnd - contentsStart);
                _raw.Bump(fast.Pos);
                int end = stringEnd + fast.Pos;
                return new Lexeme(stringStart, bytes ? Token.Bytes(Encoding.UTF8.GetBytes(contents)) : Token.String(contents), end);
            }
            if (c == '\\' || c == '\r' || (c == '\n' && !triple))
            {
                string prefix = src.Substring(baseIdx + contentsStart, fast.Pos - 1 - contentsStart);
                if (bytes)
                {
                    bres = [.. Encoding.UTF8.GetBytes(prefix)];
                }
                else
                {
                    sres = new StringBuilder(prefix, prefix.Length + 10);
                }
                it = new Cursor(src, baseIdx, fast.Pos - 1);
                break;
            }
        }

        // Slow path: character by character, with escapes and possible errors.
        int ch;
        while ((ch = it.Next()) >= 0)
        {
            if (stop.Check(ch))
            {
                _raw.Bump(it.Pos);
                if (triple)
                {
                    // Remove the two extra quote chars already pushed.
                    if (bytes)
                    {
                        bres!.RemoveRange(Math.Max(0, bres.Count - 2), Math.Min(2, bres.Count));
                    }
                    else
                    {
                        sres!.Length -= 2;
                    }
                }
                int end = stringEnd + it.Pos;
                return new Lexeme(stringStart, bytes ? Token.Bytes([.. bres!]) : Token.String(sres!.ToString()), end);
            }
            if (ch == '\n' && !triple)
            {
                // Will raise an error about out of chars, but don't include the final \n in the count.
                stringEnd -= 1;
                break;
            }
            if (ch == '\r')
            {
                // We just ignore these in all modes.
                continue;
            }
            if (ch == '\\')
            {
                if (raw)
                {
                    int n = it.Next();
                    if (n < 0)
                    {
                        break; // out of chars
                    }
                    if (n != '\'' && n != '"')
                    {
                        Append(sres, bres, '\\');
                    }
                    Append(sres, bres, n);
                }
                else
                {
                    int pos = it.Pos;
                    bool ok = bytes ? EscapeBytes(it, bres!) : Escape(it, sres!);
                    if (!ok)
                    {
                        string bad = src.Substring(baseIdx + pos, it.Pos - pos);
                        return ErrSpan(
                            bad.Length == 0 ? MsgEmptyEscape : MsgBadEscape(bad),
                            stringEnd + pos - 1, stringEnd + it.Pos);
                    }
                }
                continue;
            }
            Append(sres, bres, ch);
        }

        // We ran out of characters.
        return ErrSpan(MsgUnfinishedString, stringStart, stringEnd + it.Pos);
    }

    private static void Append(StringBuilder? sres, List<byte>? bres, int cp)
    {
        if (sres is not null)
        {
            AppendCodePoint(sres, cp);
        }
        else
        {
            AppendUtf8(bres!, cp);
        }
    }

    // ---------------------------------------------------------------- main loop

    private bool InFStringStringMode => _fstrings.Count > 0 && _fstrings[^1].BraceDepth == 0;
    private bool InFStringExprMode => _fstrings.Count > 0 && _fstrings[^1].BraceDepth > 0;

    private bool AtFStringExprTopLevel =>
        _fstrings.Count > 0 && _fstrings[^1] is { BraceDepth: 1, ParenDepth: 0, BracketDepth: 0 };

    /// <summary>The next lexeme, or null at the end of input.</summary>
    public Lexeme? Next()
    {
        while (true)
        {
            // If we're in f-string string mode, use specialised lexing.
            if (InFStringStringMode)
            {
                return LexFStringContent();
            }
            if (_buffer.Count > 0)
            {
                return _buffer.Dequeue();
            }
            if (_done)
            {
                return null;
            }

            // Check for an f-string conversion specifier (!) before regular lexing.
            if (AtFStringExprTopLevel && _raw.TokEnd < _codemap.Source.Length && _codemap.Source[_raw.TokEnd] == '!'
                && !_raw.RemainderStartsWith("!="))
            {
                int start = _raw.TokEnd;
                _raw.Bump(1);
                return new Lexeme(start, Token.Of(TokenKind.FStringBang), start + 1);
            }

            var r = _raw.Next(out var tok);
            if (r == RawResult.End)
            {
                _done = true;
                int pos = _raw.TokEnd;
                for (int i = 0; i < _indentLevels.Count; i++)
                {
                    _buffer.Enqueue(new Lexeme(pos, Token.Of(TokenKind.Dedent), pos));
                }
                _indentLevels.Clear();
                return Wrap(Token.Of(TokenKind.Newline));
            }
            if (r == RawResult.Error)
            {
                return ErrNow(MsgInvalidInput);
            }

            switch (tok.Raw)
            {
                case RawKind.Tabs:
                    _buffer.Enqueue(ErrSpan(MsgTab, _raw.TokStart, _raw.TokStart));
                    continue;

                case RawKind.Newline:
                {
                    if (_parens == 0 && !InFStringExprMode)
                    {
                        int s = _raw.TokStart;
                        int e = _raw.TokEnd;
                        var err = CalculateIndent();
                        if (err is not null)
                        {
                            return err;
                        }
                        return new Lexeme(s, Token.Of(TokenKind.Newline), e);
                    }
                    continue;
                }

                case RawKind.Reserved:
                    return ErrNow(MsgReserved);

                case RawKind.DecInt:
                {
                    string s = _raw.Slice;
                    if (s.Length > 1 && s[0] == '0')
                    {
                        return ErrNow(MsgStartsZero);
                    }
                    return IntToken(s, 10);
                }
                case RawKind.OctInt:
                    return IntToken(_raw.Slice[2..], 8);
                case RawKind.HexInt:
                    return IntToken(_raw.Slice[2..], 16);
                case RawKind.BinInt:
                    return IntToken(_raw.Slice[2..], 2);

                case RawKind.Float:
                    return Wrap(Token.Float(tok.FloatValue));

                case RawKind.Identifier:
                    return Wrap(Token.Identifier(_raw.Slice));

                case RawKind.Comment:
                    return Wrap(Token.Comment(_raw.Slice[1..]));

                case RawKind.DoubleQuote:
                    return LexQuoted('"', raw: _raw.TokEnd - _raw.TokStart == 2, bytes: false);
                case RawKind.SingleQuote:
                    return LexQuoted('\'', raw: _raw.TokEnd - _raw.TokStart == 2, bytes: false);

                case RawKind.BDoubleQuote:
                    // span len: b" = 2, br" = 3, rb" = 3
                    return LexQuoted('"', raw: _raw.TokEnd - _raw.TokStart >= 3, bytes: true);
                case RawKind.BSingleQuote:
                    return LexQuoted('\'', raw: _raw.TokEnd - _raw.TokStart >= 3, bytes: true);

                case RawKind.FDoubleQuote:
                    return StartFString('"', "\"\"", FStringQuote.Double, FStringQuote.TripleDouble);
                case RawKind.FSingleQuote:
                    return StartFString('\'', "''", FStringQuote.Single, FStringQuote.TripleSingle);

                default:
                    return LexPlain(tok.Kind);
            }
        }
    }

    private Lexeme IntToken(string digits, int radix)
    {
        try
        {
            return Wrap(Token.Int(TokenInt.FromStrRadix(digits, radix)));
        }
        catch (FormatException)
        {
            return ErrNow(MsgIntParse);
        }
    }

    private Lexeme LexPlain(TokenKind kind)
    {
        var token = Token.Of(kind);
        switch (kind)
        {
            case TokenKind.OpeningCurly:
                _parens++;
                // Track nested braces in f-string expressions.
                if (InFStringExprMode)
                {
                    _fstrings[^1].BraceDepth++;
                }
                return Wrap(token);
            case TokenKind.OpeningRound:
                _parens++;
                TrackFString(s => s.ParenDepth++);
                return Wrap(token);
            case TokenKind.OpeningSquare:
                _parens++;
                TrackFString(s => s.BracketDepth++);
                return Wrap(token);
            case TokenKind.ClosingCurly:
                // Does this close an f-string expression?
                if (AtFStringExprTopLevel)
                {
                    _fstrings[^1].BraceDepth = 0;
                    return Wrap(Token.Of(TokenKind.FStringExprEnd));
                }
                if (_fstrings.Count > 0 && _fstrings[^1].BraceDepth > 0)
                {
                    _fstrings[^1].BraceDepth--;
                }
                _parens--;
                return Wrap(token);
            case TokenKind.ClosingRound:
                _parens--;
                TrackFString(s => s.ParenDepth = Math.Max(0, s.ParenDepth - 1));
                return Wrap(token);
            case TokenKind.ClosingSquare:
                _parens--;
                TrackFString(s => s.BracketDepth = Math.Max(0, s.BracketDepth - 1));
                return Wrap(token);
            default:
                return Wrap(token);
        }
    }

    private void TrackFString(Action<FStringState> f)
    {
        if (_fstrings.Count > 0 && _fstrings[^1].BraceDepth > 0)
        {
            f(_fstrings[^1]);
        }
    }

    // ---------------------------------------------------------------- f-strings

    private Lexeme StartFString(char quote, string tripleMarker, FStringQuote single, FStringQuote triple)
    {
        bool raw = _raw.TokEnd - _raw.TokStart == 3;
        bool isTriple = _raw.RemainderStartsWith(tripleMarker);
        if (isTriple)
        {
            _raw.Bump(2); // consume the extra quotes
        }
        var q = isTriple ? triple : single;
        _fstrings.Add(new FStringState(q, raw));
        return Wrap(Token.FStringStart(q));
    }

    private static char QuoteChar(FStringQuote q) =>
        q is FStringQuote.Single or FStringQuote.TripleSingle ? '\'' : '"';

    private static bool IsTriple(FStringQuote q) => q is FStringQuote.TripleSingle or FStringQuote.TripleDouble;

    /// <summary>Lex f-string text between <c>{}</c> (or from the start to the first <c>{</c>, or the end).</summary>
    private Lexeme? LexFStringContent()
    {
        if (_fstrings.Count == 0)
        {
            return null;
        }
        var state = _fstrings[^1];
        char quoteChar = QuoteChar(state.Quote);
        bool isTriple = IsTriple(state.Quote);
        bool raw = state.Raw;

        int start = _raw.TokEnd;
        var it = new Cursor(_codemap.Source, start, 0);
        var text = new StringBuilder();
        int quoteCount = 0;

        while (true)
        {
            int c = it.Next();
            if (c < 0)
            {
                // Unterminated f-string.
                return ErrSpan(MsgUnfinishedString, start, start + it.Pos);
            }
            if (c == '{')
            {
                int n = it.Next();
                if (n == '{')
                {
                    text.Append('{');
                    quoteCount = 0;
                }
                else
                {
                    if (n >= 0)
                    {
                        it.Unnext(n);
                    }
                    // Position is right after `{`; back up to before it.
                    int bracePos = it.Pos - 1;
                    _raw.Bump(bracePos);
                    return EmitFStringExprStart(start, text.ToString(), bracePos);
                }
            }
            else if (c == '}')
            {
                int n = it.Next();
                if (n == '}')
                {
                    text.Append('}');
                    quoteCount = 0;
                }
                else
                {
                    if (n >= 0)
                    {
                        it.Unnext(n);
                    }
                    // Standalone `}` outside an expression is an error.
                    int bracePos = it.Pos - 1;
                    return ErrSpan(MsgInvalidInput("}"), start + bracePos, start + bracePos + 1);
                }
            }
            else if (c == quoteChar)
            {
                quoteCount++;
                if (isTriple)
                {
                    if (quoteCount == 3)
                    {
                        // End of a triple-quoted f-string. Remove the two quotes we added.
                        text.Length -= 2;
                        _raw.Bump(it.Pos);
                        return EmitFStringEnd(start, text.ToString(), start + it.Pos, 3);
                    }
                    text.Append((char)c);
                }
                else
                {
                    _raw.Bump(it.Pos);
                    return EmitFStringEnd(start, text.ToString(), start + it.Pos, 1);
                }
            }
            else if (c == '\\' && !raw)
            {
                quoteCount = 0;
                if (!Escape(it, text))
                {
                    return ErrSpan(MsgBadEscape("\\"), start + it.Pos - 1, start + it.Pos);
                }
            }
            else if (c == '\\')
            {
                quoteCount = 0;
                // In raw mode, backslash is literal except before the closing quote character.
                int n = it.Next();
                if (n == quoteChar)
                {
                    text.Append((char)n);
                }
                else if (n >= 0)
                {
                    text.Append('\\');
                    AppendCodePoint(text, n);
                }
                else
                {
                    text.Append('\\');
                }
            }
            else if (c == '\n' && !isTriple)
            {
                return ErrSpan(
                    InFStringExprMode ? MsgUnfinishedFExpr : MsgUnfinishedString,
                    start, start + it.Pos);
            }
            else if (c == '\r')
            {
                quoteCount = 0; // ignore carriage returns
            }
            else
            {
                quoteCount = 0;
                AppendCodePoint(text, c);
            }
        }
    }

    private Lexeme EmitFStringEnd(int start, string text, int end, int quoteLen)
    {
        _fstrings.RemoveAt(_fstrings.Count - 1);
        int quoteStart = end - quoteLen;
        if (text.Length > 0)
        {
            _buffer.Enqueue(new Lexeme(quoteStart, Token.Of(TokenKind.FStringEnd), end));
            return new Lexeme(start, Token.FStringText(text), quoteStart);
        }
        return new Lexeme(quoteStart, Token.Of(TokenKind.FStringEnd), end);
    }

    private Lexeme EmitFStringExprStart(int start, string text, int bracePos)
    {
        // Enter expression mode.
        _fstrings[^1].BraceDepth = 1;
        _raw.Bump(1); // consume the `{`

        int braceStart = start + bracePos;
        if (text.Length > 0)
        {
            _buffer.Enqueue(new Lexeme(braceStart, Token.Of(TokenKind.FStringExprStart), braceStart + 1));
            return new Lexeme(start, Token.FStringText(text), braceStart);
        }
        return new Lexeme(braceStart, Token.Of(TokenKind.FStringExprStart), braceStart + 1);
    }

    /// <summary>If <paramref name="s"/> is exactly one identifier, return it.</summary>
    public static string? LexExactlyOneIdentifier(string s)
    {
        var raw = new RawLexer(s);
        if (raw.Next(out var tok) != RawResult.Token || tok.Raw != RawKind.Identifier)
        {
            return null;
        }
        string ident = raw.Slice;
        return raw.Next(out _) == RawResult.End ? ident : null;
    }
}
