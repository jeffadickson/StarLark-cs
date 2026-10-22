using Starlark.Syntax;
using static Starlark.Syntax.Tests.TestSupport;

namespace Starlark.Syntax.Tests;

public class LexerTests
{
    [Fact]
    public void IsValidIdentifier()
    {
        Assert.Equal("foo", Lexer.LexExactlyOneIdentifier("foo"));
        Assert.Equal("foo", Lexer.LexExactlyOneIdentifier(" foo "));
        Assert.Null(Lexer.LexExactlyOneIdentifier("foo bar"));
        Assert.Null(Lexer.LexExactlyOneIdentifier("not"));
        Assert.Null(Lexer.LexExactlyOneIdentifier("123"));
    }

    [Fact]
    public void BytesLiteralOctalOverflowIsError()
    {
        // \400 = 256 in octal, which exceeds 255 and must be a lex error.
        var all = Lexer.LexAll(new CodeMap("x", "b\"\\400\""));
        Assert.Contains(all, l => l.IsError);
    }

    [Fact]
    public void IntLit()
    {
        LexerGolden("int_lit", "\n0 123\n0x7F 0x7d\n0B1011 0b1010\n0o755 0O753\n");
        // Starlark bans leading zeros (confusion with implicit octal).
        LexerFailGolden("int_lit", "x = 01");
    }

    [Fact]
    public void Indentation() =>
        LexerGolden("indentation", "\n+\n  -\n      /\n      *\n  =\n    %\n      .\n+=\n");

    [Fact]
    public void Symbols() =>
        LexerGolden("symbols", ", ; : += -= *= /= //= %= == != <= >= ** = < > - + * % / // . { } [ ] ( ) |\n,;:{}[]()|...");

    [Fact]
    public void Keywords() =>
        LexerGolden("keywords", "and else load break for not not  in continue if or def in pass elif return lambda");

    [Fact]
    public void NumberCollatedWithKeywordsOrIdentifier() =>
        LexerGolden("number_collated_with_keywords_or_identifier",
            "0in 1and 2else 3load 4break 5for 6not 7not  in 8continue 10identifier11");

    [Fact]
    public void Reserved() =>
        LexerFailGolden("reserved",
            "as import is class nonlocal del raise except try finally while from with global yield".Split(' '));

    [Fact]
    public void Comment() =>
        LexerGolden("comment",
            "\n# first comment\n  # second comment\na # third comment\n\n# But it should not eat everything\n[\n# comment inside list\n]\n");

    [Fact]
    public void Identifier() => LexerGolden("identifier", "a identifier CAPS _CAPS _0123");

    [Fact]
    public void StringLit()
    {
        Assert.Equal(
            "\"123\" \"123\" \"\" \"\" \"'\" \"\\\"\" \"\\\"\" \"'\" \"\\n\" \"\\\\w\" \n",
            Lex("'123' \"123\" '' \"\" '\\'' \"\\\"\" '\"' \"'\" '\\n' '\\w'"));

        LexerFailGolden("string_lit",
            "'\n'", "\"\n\"", "this = a + test + r\"", "test + ' of thing that", "test + ' of thing that\n'");

        // Multiline strings.
        Assert.Equal(
            "\"\" \"\\n\" \"\\n\" \"\" \"\\n\" \"\\n\" \n",
            Lex("'''''' '''\\n''' '''\n''' \"\"\"\"\"\" \"\"\"\\n\"\"\" \"\"\"\n\"\"\""));

        // Raw strings.
        Assert.Equal(
            "\"\" \"\" \"'\" \"\\\"\" \"\\\"\" \"'\" \"\\\\n\" \n",
            Lex("r'' r\"\" r'\\'' r\"\\\"\" r'\"' r\"'\" r'\\n'"));
    }

    [Fact]
    public void StringEscape()
    {
        LexerGolden("string_escape", "\n'\\0\\0\\1n'\n'\\0\\00\\000\\0000'\n'\\x000'\n'\\372x'\n");
        LexerFailGolden("string_escape",
            "test 'more \\xTZ",
            "test + 'more \\UFFFFFFFF overflows'",
            "test 'more \\x0yabc'",
            "test 'more \\x0");
    }

    [Fact]
    public void SimpleExample() =>
        LexerGolden("simple_example",
            "\"\"\"A docstring.\"\"\"\n\ndef _impl(ctx):\n  # Print Hello, World!\n  print('Hello, World!')\n");

    [Fact]
    public void EscapeNewline() => LexerGolden("escape_newline", "\na \\\nb\n");

    [Fact]
    public void MultilineTriple() =>
        LexerGolden("multiline_triple", "\ncmd = \"\"\"A \\\n    B \\\n    C \\\n    \"\"\"");

    [Fact]
    public void Span()
    {
        var expected = new List<(int, Token, int)>
        {
            (0, Token.Of(TokenKind.Newline), 1),
            (1, Token.Of(TokenKind.Def), 4),
            (5, Token.Identifier("test"), 9),
            (9, Token.Of(TokenKind.OpeningRound), 10),
            (10, Token.Identifier("a"), 11),
            (11, Token.Of(TokenKind.ClosingRound), 12),
            (12, Token.Of(TokenKind.Colon), 13),
            (13, Token.Of(TokenKind.Newline), 14),
            (14, Token.Of(TokenKind.Indent), 16),
            (16, Token.Identifier("fail"), 20),
            (20, Token.Of(TokenKind.OpeningRound), 21),
            (21, Token.Identifier("a"), 22),
            (22, Token.Of(TokenKind.ClosingRound), 23),
            (23, Token.Of(TokenKind.Newline), 24),
            (24, Token.Of(TokenKind.Newline), 25),
            (25, Token.Of(TokenKind.Dedent), 25),
            (25, Token.Identifier("test"), 29),
            (29, Token.Of(TokenKind.OpeningRound), 30),
            (30, Token.String("abc"), 35),
            (35, Token.Of(TokenKind.ClosingRound), 36),
            (36, Token.Of(TokenKind.Newline), 37),
            (37, Token.Of(TokenKind.Newline), 37),
        };
        var actual = LexTokens("\ndef test(a):\n  fail(a)\n\ntest(\"abc\")\n")
            .Select(l => (l.Start, l.Token!, l.End)).ToList();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void FinalComment() => LexerGolden("final_comment", "\nx\n# test");

    [Fact]
    public void Dedent() =>
        LexerGolden("dedent", "\ndef stuff():\n  if 1:\n    if 1:\n      pass\n  pass\n");

    [Fact]
    public void Operators() => LexerGolden("operators", "\n1+-2\n1+------2\n///==/+-\n");

    [Fact]
    public void ErrorMessages() =>
        LexerFailGolden("error_messages",
            "unknown $&%+ operator",
            "an 'incomplete string\nends",
            "an + 'invalid escape \\x3  character'",
            "leading_zero = 003 + 8",
            "reserved_word = raise + 1");

    [Fact]
    public void FloatLit() =>
        LexerGolden("float_lit", "\n0.0 0. .0\n1e10 1e+10 1e-10\n1.1e10 1.1e+10 1.1e-10\n0. .123 3.14 .2e3 1E+4\n");

    [Fact]
    public void FString() =>
        LexerGolden("f_string",
            "\nf\"basic1 {stuff1}\"\nf'basic2 {stuff2}'\n\n# Raw f-string\n\nfr'' fr\"\" fr'\\'' fr\"\\\"\" fr'\"' fr\"'\" fr'\\n'\n");

    [Fact]
    public void FStringExpressions()
    {
        // The program lives in the golden file itself; extract it so there is a single source of truth.
        string golden = ReadGolden("lexer", "f_string_expressions");
        const string start = "Program:\n";
        int a = golden.IndexOf(start, StringComparison.Ordinal) + start.Length;
        int b = golden.IndexOf("\n\nTokens:\n", StringComparison.Ordinal);
        LexerGolden("f_string_expressions", golden[a..b]);
    }
}
