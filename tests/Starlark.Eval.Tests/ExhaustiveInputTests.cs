using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>
/// The no-crash policy (QA-ROB-001) for every place that reads a small grammar from the user: each string up to a few
/// characters over a small alphabet either works or ends in a Starlark error. Anything else (another exception type, a hang)
/// is a bug. Exhaustive, so it does not depend on a random fuzz run happening to reach the input.
/// </summary>
public class ExhaustiveInputTests
{
    private static Interpreter New() => new()
    {
        Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableTypes = DialectTypes.Enable },
        MaxSteps = 100_000,
        MaxAllocatedBytes = 64 * 1024 * 1024,
        Print = _ => { },
    };

    /// <summary>Calls <paramref name="each"/> on every string of 1..maxLen characters from <paramref name="alphabet"/>, within a deadline.</summary>
    private static int ForEveryString(string alphabet, int maxLen, Action<string> each)
    {
        var buffer = new char[maxLen];
        int count = 0;
        void Walk(int length)
        {
            if (length > 0)
            {
                each(new string(buffer, 0, length));
                count++;
            }
            if (length == maxLen) return;
            foreach (char c in alphabet)
            {
                buffer[length] = c;
                Walk(length + 1);
            }
        }
        var task = Task.Run(() => Walk(0));
        Assert.True(task.Wait(TimeSpan.FromSeconds(120)), "exhaustive run did not finish in time");
        task.GetAwaiter().GetResult();
        return count;
    }

    /// <summary>Runs one program; only the two Starlark exception types may escape.</summary>
    private static void MustEndCleanly(Interpreter it, string program, string shown)
    {
        try
        {
            it.Execute("t.star", program);
        }
        catch (StarlarkException)
        {
        }
        catch (StarlarkSyntaxException)
        {
        }
        catch (Exception e)
        {
            throw new Xunit.Sdk.XunitException($"{e.GetType().Name}: {e.Message} on input {shown}");
        }
    }

    private static string Show(string s) => s.Replace("\n", "\\n").Replace("\r", "\\r");

    [Fact]
    public void ParserNeverFailsUncleanlyOnShortSyntaxSoup()
    {
        // Every statement and expression character the grammar uses, plus a newline and an indent space.
        int n = ForEveryString("a1,:=()[]{}.+*-\n x\"'", 5, src =>
        {
            try
            {
                AstModule.Parse("t.star", src, Dialect.Extended);
            }
            catch (StarlarkSyntaxException e)
            {
                _ = e.Message;   // rendering the diagnostic must not throw either
            }
            catch (Exception e)
            {
                throw new Xunit.Sdk.XunitException($"{e.GetType().Name}: {e.Message} on input {Show(src)}");
            }
        });
        Assert.True(n > 1_000_000);
    }

    /// <summary>
    /// The parser stops at the first lexical error, so only <c>LexAll</c> lexes on past an error, which is where an
    /// error-recovery bug (an error returned without consuming input) shows. Alphabets cover strings, f-strings and escapes.
    /// </summary>
    [Theory]
    [InlineData("f'\"{}:!x\\ ")]
    [InlineData("fbr'\"\\{}:x0\n")]
    [InlineData("a1,:=()[]{}.+*-\n x#")]
    public void LexingPastErrorsAlwaysMakesProgress(string alphabet)
    {
        int n = ForEveryString(alphabet, 6, src =>
        {
            try
            {
                var lexemes = Lexer.LexAll(new CodeMap("t.star", src));
                Assert.True(lexemes.Count <= 4 * src.Length + 8, $"{lexemes.Count} lexemes on input {Show(src)}");
            }
            catch (InvalidOperationException e)
            {
                throw new Xunit.Sdk.XunitException($"{e.Message} on input {Show(src)}");
            }
        });
        Assert.True(n > 100_000);
    }

    [Fact]
    public void PercentFormatNeverFailsUncleanly()
    {
        var it = New();
        int n = ForEveryString("%dsrxXeEfFgGcio.-+# *0123(m)'", 4, spec =>
            MustEndCleanly(it, $"r = '{spec}' % (1, 'a', 2.5, [1], {{'m': 3}})\n", Show(spec)));
        Assert.True(n > 100_000);
        n = ForEveryString("%dsr.-+*05(m)", 4, spec =>
            MustEndCleanly(it, $"r = '{spec}' % {{'m': 3}}\n", Show(spec)));
        Assert.True(n > 10_000);
    }

    [Fact]
    public void BraceFormatNeverFailsUncleanly()
    {
        var it = New();
        int n = ForEveryString("{}:!sr<>^=+-#0123.dxfe,a_[] ", 4, spec =>
            MustEndCleanly(it, $"r = '{spec}'.format(1, 'a', x = 2.5)\n", Show(spec)));
        Assert.True(n > 100_000);
    }

    [Fact]
    public void FStringFieldsNeverFailUncleanly()
    {
        var it = New();
        int n = ForEveryString("{}:!sr<>^=+-#0123.dxfe,x_ ", 4, body =>
            MustEndCleanly(it, $"x = 5\nr = f'{body}'\n", Show(body)));
        Assert.True(n > 100_000);
    }

    [Fact]
    public void StringEscapesNeverFailUncleanly()
    {
        var it = New();
        // Backslash sequences in plain, raw and bytes literals; the quote and newline cases are the interesting ones.
        foreach (var prefix in new[] { "", "r", "b" })
        {
            int n = ForEveryString("\\xuUn01aFg7'\"\n ", 4, body =>
                MustEndCleanly(it, $"r = {prefix}'{body}'\n", Show(body)));
            Assert.True(n > 10_000);
        }
    }

    [Fact]
    public void JsonDecodeNeverFailsUncleanly()
    {
        var it = New();
        int n = ForEveryString("{}[],:\"\ntfu-+.e019 ", 5, text =>
            MustEndCleanly(it, $"r = json.decode({PythonLike(text)})\n", Show(text)));
        Assert.True(n > 1_000_000);
    }

    /// <summary>A Starlark string literal for arbitrary text.</summary>
    private static string PythonLike(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") +"\"";
}
