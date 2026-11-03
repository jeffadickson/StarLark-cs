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

    /// <summary>
    /// Every string up to five characters over an alphabet heavy in f-string syntax must lex to the end. Exhaustive rather than
    /// random, so a lexer that returns an error without consuming input fails here in the normal run, not only in a long soak.
    /// The lexer itself throws if it stalls, so a stall shows up as an exception instead of an endless loop.
    /// </summary>
    [Fact]
    public void EveryShortFStringLikeInputLexesToTheEnd()
    {
        const string alphabet = "f'\"{}:!x\n([";
        var buffer = new char[5];
        int count = 0;
        void Walk(int length)
        {
            if (length > 0)
            {
                var src = new string(buffer, 0, length);
                try
                {
                    var n = Lexer.LexAll(new CodeMap("t.star", src)).Count;
                    Assert.True(n <= 4 * length + 8, $"{n} lexemes for {src.Length} characters: {src.Replace("\n", "\n")}");
                }
                catch (InvalidOperationException e)
                {
                    throw new Xunit.Sdk.XunitException($"{e.Message} on input: {src.Replace("\n", "\n")}");
                }
                count++;
            }
            if (length == buffer.Length) return;
            foreach (char c in alphabet)
            {
                buffer[length] = c;
                Walk(length + 1);
            }
        }
        var task = Task.Run(() => Walk(0));
        Assert.True(task.Wait(TimeSpan.FromSeconds(60)), "exhaustive lexing did not finish");
        Assert.True(count > 100_000);
    }
}
