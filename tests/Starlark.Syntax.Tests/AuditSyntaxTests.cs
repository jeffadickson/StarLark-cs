using System.Diagnostics;
using System.Reflection;
using Starlark.Syntax;

namespace Starlark.Syntax.Tests;

/// <summary>Regression tests for the crash and railway-error audit findings in the lexer and the error renderer.</summary>
public class AuditSyntaxTests
{
    private static StarlarkSyntaxException Err(string src) =>
        Assert.Throws<StarlarkSyntaxException>(() => AstModule.Parse("t.star", src, Dialect.AllOptionsInternal));

    // C11: every format spec searched to the end of the f-string for a quote, so many specs on one line took quadratic time.
    [Fact]
    public void ManyFormatSpecsInOneFStringLexInLinearTime()
    {
        var src = "a = 1\nx = f'" + string.Concat(Enumerable.Repeat("{a:}", 250_000)) + "'\n";
        var sw = Stopwatch.StartNew();
        var task = Task.Run(() => AstModule.Parse("t.star", src, Dialect.AllOptionsInternal));
        Assert.True(task.Wait(TimeSpan.FromSeconds(10)), "parse did not finish");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"took {sw.Elapsed}");
    }

    // C11: the one-pass search must still reject a spec that runs into a quote or a line break, in single and triple quotes.
    [Theory]
    [InlineData("f'{x:ab'")]
    [InlineData("f'{x:ab\ncd}'")]
    [InlineData("f'''{x:ab'''")]
    [InlineData("f'''{x:a\nb}'''")]
    public void UnclosedFormatSpecIsStillAnError(string src)
    {
        Assert.Contains("format spec is not closed", Err("x = 1\ny = " + src + "\n").RawMessage);
    }

    // C25: the snippet copied every line of the span, so an unterminated string near the top of a big file gave a huge message.
    [Fact]
    public void SnippetOfAHugeMultiLineSpanIsBounded()
    {
        var e = Err("x = '''" + new string('\n', 200_000));
        Assert.True(e.Message.Length < 4000, $"message is {e.Message.Length} chars");
        Assert.Contains("--> t.star:1:5", e.Message);
    }

    // C25: one very long line is shown clipped around the error, with the full position in the header.
    [Fact]
    public void SnippetOfAHugeLineIsBounded()
    {
        var e = Err("x = " + new string('a', 200_000) + " $\n");
        Assert.True(e.Message.Length < 2000, $"message is {e.Message.Length} chars");
        Assert.Contains("--> t.star:1:200006", e.Message);
        Assert.Contains("$", e.Message.Split('\n')[3]);
    }

    // C37: an invalid character that does not print (CR, form feed, lone surrogate, BOM) is escaped in the message.
    [Theory]
    [InlineData("x = 1\ry = 2", "\\u{d}")]
    [InlineData("x = 1 \f", "\\u{c}")]
    [InlineData("x = 1 \uFEFF", "\\u{feff}")]
    [InlineData("SURROGATE", "\\u{d800}")]   // xUnit mangles a lone surrogate in attribute data, so it is built here
    public void InvalidInputMessageEscapesUnprintableCharacters(string src, string shown)
    {
        if (src == "SURROGATE") src = "x = " + (char)0xD800;
        var msg = Err(src).RawMessage;
        Assert.Contains("invalid input `" + shown + "`", msg);
        Assert.DoesNotContain(msg, ch => char.IsControl(ch) || char.IsSurrogate(ch) || ch == '\uFEFF');
    }

    // C38: a bad escape in an f-string gets the same message as in an ordinary string.
    [Theory]
    [InlineData("\\xZZ")]
    [InlineData("\\U00110000")]
    public void FStringBadEscapeMatchesPlainString(string esc)
    {
        Assert.Equal(Err("x = '" + esc + "'\n").RawMessage, Err("x = f'" + esc + "'\n").RawMessage);
    }

    // C39: the public FromStrRadix threw FormatException; it is no longer part of the public API.
    [Fact]
    public void TokenIntHasNoThrowingPublicParser()
    {
        Assert.Null(typeof(TokenInt).GetMethod("FromStrRadix", BindingFlags.Public | BindingFlags.Static));
    }

    // R1: a stray closing bracket drove the bracket counter negative, after which no newlines or indents were emitted.
    [Fact]
    public void StrayClosingBracketDoesNotStopIndentation()
    {
        var toks = Lexer.LexAll(new CodeMap("t.star", ")\nif x:\n  y\n"));
        Assert.Contains(toks, l => l.Token is { Kind: TokenKind.Indent });
    }
}
