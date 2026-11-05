using System.Text.RegularExpressions;
using Starlark.Eval;
using Starlark.Eval.Futures;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>
/// <see cref="FutureLimits.RunInline"/>: on platforms with no threads (browser WebAssembly) tasks run on the spawning thread.
/// The promise is that a script cannot tell the difference: every futures example program gives the same output and the
/// same error inline as with worker threads.
/// </summary>
public class InlineFuturesTests
{
    private static (string Output, string? Error) Run(string source, bool inline, Dialect dialect)
    {
        var printed = new List<string>();
        var it = new Interpreter
        {
            Dialect = dialect,
            Print = printed.Add,
            FutureLimits = new FutureLimits { RunInline = inline },
            MaxSteps = 50_000_000,
        };
        string? error = null;
        try
        {
            it.Execute("t.star", source);
        }
        catch (StarlarkException e)
        {
            error = e.RawMessage;
        }
        return (string.Join("\n", printed), error);
    }

    public static IEnumerable<object[]> FuturesExamples() =>
        ExampleProgramTests.All()
            .Select(a => (ExampleProgramTests.Example)a[0])
            .Where(e => (e.One("dialect") ?? "").Contains("futures"))
            .Select(e => new object[] { e });

    [Fact]
    public void ThereAreFuturesExamplesToCompare() => Assert.True(FuturesExamples().Count() >= 25);

    [Theory]
    [MemberData(nameof(FuturesExamples))]
    public void InlineAndThreadedRunsAgree(ExampleProgramTests.Example ex)
    {
        var dialect = Dialect.Standard with
        {
            EnableTopLevelStmt = true,
            EnableFutures = true,
            EnableNondeterministicFutures = (ex.One("dialect") ?? "").Contains("nondeterministic"),
        };
        var threaded = Run(ex.Source, inline: false, dialect);
        var inline = Run(ex.Source, inline: true, dialect);
        Assert.Equal(threaded.Output, inline.Output);
        Assert.Equal(threaded.Error, inline.Error);
    }

    [Fact]
    public void InlineTasksStillGetTheirOwnCopiesAndOrderedOutput()
    {
        const string script = """
            def grow(xs):
                xs.append(1)
                print("task", len(xs))
                return len(xs)
            def run():
                xs = [0]
                fs = [spawn(grow, xs) for _ in range(3)]
                print("caller", len(xs))
                return all_of(fs)
            print(run())
            """;
        var dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableFutures = true };
        var (output, error) = Run(script, inline: true, dialect);
        Assert.Null(error);
        // Each task appended to its own copy, the caller's list is untouched, and task output is joined in program order.
        Assert.Equal("caller 1\ntask 2\ntask 2\ntask 2\n[2, 2, 2]", output);
    }

    [Fact]
    public void InlineRunsStartNoThreads()
    {
        var it = new Interpreter
        {
            Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableFutures = true },
            FutureLimits = new FutureLimits { RunInline = true },
        };
        int before = System.Diagnostics.Process.GetCurrentProcess().Threads.Count;
        it.Execute("t.star", "def sq(x):\n    return x * x\nr = parallel_map(sq, range(200))\n");
        Assert.Equal(1, it.LastRunStats!.PeakConcurrency);
        Assert.True(System.Diagnostics.Process.GetCurrentProcess().Threads.Count <= before + 1);
    }

    [Fact]
    public void TheDefaultIsThreadsOnThisPlatform() => Assert.False(FutureLimits.ThreadsUnavailable);
}
