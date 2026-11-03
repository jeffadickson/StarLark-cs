using Starlark.Syntax;

namespace Starlark.Syntax.Tests;

/// <summary>Inputs found by the soak run that once made the lexer loop forever, growing memory without bound.</summary>
public class LexerRegressionTests
{
    /// <summary>Lexes on a worker with a deadline, so a regression fails the test instead of exhausting memory.</summary>
    private static int LexCount(string src)
    {
        var task = Task.Run(() => Lexer.LexAll(new CodeMap("t.star", src)).Count);
        Assert.True(task.Wait(TimeSpan.FromSeconds(5)), $"lexing {src.Length} chars did not finish");
        return task.Result;
    }

    [Theory]
    [InlineData("f'{:")]            // format spec with no closing brace at the end of input
    [InlineData("f'{x:")]
    [InlineData("f'{x:abc")]
    [InlineData("f'{x:ab\ncd}'")]   // spec runs into a line break
    [InlineData("f'{x:ab'")]        // spec runs into the closing quote
    [InlineData("f'{x:{y}}'")]      // nested replacement field is rejected, lexing continues after it
    public void BrokenFormatSpecsEndWithBoundedErrors(string src)
    {
        Assert.InRange(LexCount(src), 1, 100);
    }
}
