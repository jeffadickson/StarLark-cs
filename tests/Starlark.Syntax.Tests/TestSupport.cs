using System.Text;
using Starlark.Syntax;

namespace Starlark.Syntax.Tests;

internal static class TestSupport
{
    private static string GoldenDir(string kind) => Path.Combine(AppContext.BaseDirectory, "Goldens", kind);

    /// <summary>The golden file text with the generated header removed.</summary>
    public static string ReadGolden(string kind, string name)
    {
        string text = File.ReadAllText(Path.Combine(GoldenDir(kind), name + ".golden")).Replace("\r\n", "\n");
        const string marker = "# ```\n\n";
        int i = text.IndexOf(marker, StringComparison.Ordinal);
        return text[(i + marker.Length)..].TrimEnd();
    }

    /// <summary>Compare against a golden file, or rewrite it when STARLARK_CS_REGENERATE_GOLDENS is set.</summary>
    public static void AssertGolden(string kind, string name, string actual)
    {
        Assert.Equal(ReadGolden(kind, name), actual.TrimEnd());
    }

    public static Dialect All => Dialect.AllOptionsInternal;

    public static AstModule ParseAst(string program, Dialect? dialect = null) =>
        AstModule.Parse("assert.bzl", program, dialect ?? All);

    public static string Parse(string program, Dialect? dialect = null) => ParseAst(program, dialect).Statement.ToString();

    public static StarlarkSyntaxException ParseErr(string program, Dialect? dialect = null) =>
        Assert.Throws<StarlarkSyntaxException>(() => ParseAst(program, dialect));

    /// <summary>Build the "Program: ... Error: ..." text used by the original's failure goldens.</summary>
    public static string FailText(string name, Dialect dialect, IEnumerable<string> programs)
    {
        var sb = new StringBuilder();
        int i = 0;
        foreach (var raw in programs)
        {
            if (i++ != 0)
            {
                sb.Append('\n');
            }
            string program = raw.Trim();
            var e = Assert.Throws<StarlarkSyntaxException>(() => AstModule.Parse(name, program, dialect));
            sb.Append("Program:\n").Append(program).Append("\n\nError:\n").Append(e.Message).Append('\n');
        }
        return sb.ToString();
    }

    public static void ParseFails(string name, params string[] programs) =>
        AssertGolden("grammar", name, FailText(name, All, programs));

    public static void ParseFailsWith(string name, Dialect dialect, params string[] programs) =>
        AssertGolden("grammar", name, FailText(name, dialect, programs));

    // ------------------------------------------------------------------ lexer helpers

    public static List<Lexeme> LexTokens(string program)
    {
        var all = Lexer.LexAll(new CodeMap("assert.bzl", program));
        var errors = all.Where(l => l.IsError).ToList();
        Assert.True(errors.Count == 0, $"expected lex success but failed\nCode: {program}\nError: {errors.FirstOrDefault().Error}");

        // Positions must be monotonic.
        int pos = 0;
        foreach (var l in all)
        {
            Assert.True(pos <= l.Start, $"Span of {l.Token} incorrect: {pos} > {l.Start}");
            Assert.True(l.Start <= l.End, $"Span of {l.Token} incorrect: {l.Start} > {l.End}");
            pos = l.End;
        }

        // CRLF newlines must not change the tokens, only the positions.
        string crlf = program.Replace("\r\n", "\n").Replace("\n", "\r\n");
        var with = Lexer.LexAll(new CodeMap("assert.bzl", crlf));
        Assert.Equal(all.Select(l => l.Token).ToList(), with.Select(l => l.Token).ToList());
        return all;
    }

    /// <summary>Token text as produced by the original's test-only <c>unlex</c>.</summary>
    public static string Unlex(Token t)
    {
        switch (t.Kind)
        {
            case TokenKind.Indent: return "\t";
            case TokenKind.Newline: return "\n";
            case TokenKind.Dedent: return "#dedent";
            case TokenKind.String: return JsonString(t.Text!);
            default:
                string s = t.ToString();
                int first = s.IndexOf('\'');
                return first >= 0 && s.EndsWith('\'') && first != s.Length - 1 ? s[(first + 1)..^1] : s;
        }
    }

    private static string JsonString(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case < ' ': sb.Append("\\u").Append(((int)c).ToString("x4")); break;
                default: sb.Append(c); break;
            }
        }
        return sb.Append('"').ToString();
    }

    public static string Lex(string program) => string.Join(" ", LexTokens(program).Select(l => Unlex(l.Token!)));

    public static string LexGoldenText(string program)
    {
        program = program.Trim();
        var sb = new StringBuilder();
        sb.Append("Program:\n").Append(program).Append("\n\nTokens:\n");
        var tokens = LexTokens(program).Select(l => (l.Start, Text: l.Token!.ToString(), l.End)).ToList();
        int width = tokens.Count == 0 ? 0 : tokens.Max(t => t.Text.Length);
        foreach (var (start, text, end) in tokens)
        {
            string source = program.Substring(start, end - start).Replace("\n", "\\n");
            sb.Append(text.PadRight(width)).Append("  # ").Append(source).Append('\n');
        }
        return sb.ToString();
    }

    public static void LexerGolden(string name, string program) =>
        AssertGolden("lexer", name, LexGoldenText(program));

    public static void LexerFailGolden(string name, params string[] programs)
    {
        var sb = new StringBuilder();
        int i = 0;
        foreach (var raw in programs)
        {
            if (i++ != 0)
            {
                sb.Append('\n');
            }
            string program = raw.Trim();
            var all = Lexer.LexAll(new CodeMap("x", program), stopAtError: true);
            var err = all.First(l => l.IsError).Error!;
            sb.Append("Program:\n").Append(program).Append("\n\nError:\n").Append(err.Message).Append('\n');
        }
        AssertGolden("lexer", name + ".fail", sb.ToString());
    }
}
