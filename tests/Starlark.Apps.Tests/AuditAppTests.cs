using Basic;

namespace Starlark.Apps.Tests;

/// <summary>
/// Regression tests for the crash and railway-error audit findings in the BASIC machine and the command-line hosts. Every BASIC test
/// bounds the machine (a slice cap), so a regression fails fast instead of growing without limit.
/// </summary>
public class AuditAppTests
{
    private static (string Status, string Text) Run(string program, int maxSlices = 200)
    {
        var m = new Machine();
        m.TakeOutput();
        m.Load(program);
        var status = m.Enter("RUN", maxSlices);
        return (status, m.TakeOutput().Replace("\r", ""));
    }

    // C3: ON..GOSUB did not check the 400-entry GOSUB limit, so a recursive ON..GOSUB grew the return stack without bound.
    [Fact]
    public void OnGosubChecksTheReturnStackLimit()
    {
        var (status, text) = Run("10 ON 1 GOSUB 10", maxSlices: 3);   // the limit is hit within the first slice
        Assert.Equal("done", status);
        Assert.Contains("?OM ERROR IN 10", text);
    }

    // C21: a typed line number with thousands of digits escaped as a Starlark error (?INTERNAL ERROR) instead of ?SN ERROR.
    [Fact]
    public void HugeLineNumberIsASyntaxError()
    {
        var m = new Machine();
        m.TakeOutput();
        m.Enter(new string('9', 400_000) + " PRINT 1");
        Assert.Contains("?SN ERROR", m.TakeOutput());
        m.Enter("00010 PRINT 5");   // leading zeros are still accepted
        m.Enter("LIST");
        Assert.Contains("10 PRINT 5", m.TakeOutput());
    }

    // C22: the compiled-expression cache key line*4096+pos collided between lines once a line had more than 4096 tokens.
    [Fact]
    public void LongLinesDoNotShareCompiledExpressions()
    {
        var ones = string.Concat(Enumerable.Repeat("1;", 2048));
        var (status, text) = Run($"5 GOTO 11\n10 PRINT {ones}5:END\n11 PRINT 7:GOTO 10", maxSlices: 2000);
        Assert.Equal("done", status);
        Assert.DoesNotContain("ERROR", text);
        Assert.EndsWith(" 1  5 \nREADY\n>", text.Replace("\n\n", "\n"));
    }

    // C4: a program file that broke the step budget while loading or running ended the process with an unhandled exception.
    [Fact]
    public void ProgramFileOverBudgetIsABreakNotACrash()
    {
        var file = "10 DEF FNA(X)=X+1\n20 DEF FNB(X)=FNA(X)+FNA(X)+FNA(X)+FNA(X)\n30 DEF FNC(X)=FNB(X)+FNB(X)+FNB(X)+FNB(X)\n"
            + "40 DEF FND(X)=FNC(X)+FNC(X)+FNC(X)+FNC(X)\n50 DEF FNE(X)=FND(X)+FND(X)+FND(X)+FND(X)\n60 PRINT FNE(1)\n";
        var sw = new StringWriter();
        Assert.Equal(0, Basic.Program.Run(sw, new StringReader("PRINT 2\n"), file, maxSteps: 200_000));
        Assert.Contains("BREAK (steps limit)", sw.ToString());
        Assert.Contains(" 2 ", sw.ToString());   // the session carries on after the break
    }

    // C5: in EDIT mode a non-BMP character arrives as two UTF-16 units, and the low one made KeyName throw.
    [Fact]
    public void EditModeAcceptsANonBmpCharacter()
    {
        var sw = new StringWriter();
        Assert.Equal(0, Basic.Program.Run(sw, new StringReader("10 PRINT 1\nEDIT 10\nI\U0001F600\n"), null));
    }

    /// <summary>Runs a top-level-statement tool's entry point in-process (its Program class is internal, so it is reached by reflection).</summary>
    private static int RunTool(string assembly, params string[] args)
    {
        var entry = System.Reflection.Assembly.Load(assembly).EntryPoint!;
        var err = Console.Error;
        Console.SetError(new StringWriter());
        try
        {
            // Bounded: a tool that wrongly starts serving would otherwise never return.
            var run = Task.Run(() => (int)entry.Invoke(null, [args])!);
            Assert.True(run.Wait(TimeSpan.FromSeconds(60)), $"{assembly} did not return");
            return run.Result;
        }
        finally
        {
            Console.SetError(err);
        }
    }

    // C34: a failed `git show` was taken as an empty old version and reported as DIFFERENT (exit 1) instead of as an error.
    [Fact]
    public void CommentCheckReportsAFailedGitShow()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent!;
        var cwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(dir.FullName);
        try
        {
            Assert.Equal(2, RunTool("CommentCheck", "same", "no-such-ref-zz9", "apps/Basic/scripts/basic.star"));
        }
        finally
        {
            Directory.SetCurrentDirectory(cwd);
        }
    }

    // C35: an out-of-range port was accepted and then crashed Kestrel; a port that is not a number silently became 8765.
    [Theory]
    [InlineData("99999")]
    [InlineData("-1")]
    [InlineData("eighty")]
    public void ServeRejectsABadPort(string port)
    {
        Assert.Equal(2, RunTool("Serve", AppContext.BaseDirectory, port));
    }

    // C23: an unknown environment name crashed ConfigInspector with NullReferenceException.
    [Fact]
    public void ConfigInspectorReportsAnUnknownEnvironment()
    {
        var sw = new StringWriter();
        Assert.Equal(1, ConfigInspector.Program.Run(sw, ["--check", "nosuch"]));
        Assert.Contains("no such environment: nosuch", sw.ToString());
    }
}
