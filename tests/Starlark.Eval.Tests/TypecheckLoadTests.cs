using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>The checker follows <c>load()</c> into the modules the host's loader supplies.</summary>
public class TypecheckLoadTests
{
    private static Interpreter New(Dictionary<string, string> modules) => new()
    {
        Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableTypes = DialectTypes.Enable },
        SourceLoader = id => modules.GetValueOrDefault(id),
    };

    /// <summary>A type error inside a loaded module is reported before anything runs.</summary>
    [Fact]
    public void ErrorsInLoadedModulesAreReported()
    {
        var it = New(new() { ["lib.star"] = "def f() -> int:\n    return 'x'\n" });
        var result = it.Typecheck("main.star", "load('lib.star', 'f')\nf()\n");
        Assert.Single(result.Errors);
        Assert.Equal("lib.star", result.Errors[0].CodeMap.FileName);
    }

    /// <summary>With <see cref="Interpreter.TypecheckBeforeExecute"/>, a bad loaded module stops the run.</summary>
    [Fact]
    public void BadLoadedModuleStopsExecution()
    {
        var it = New(new() { ["lib.star"] = "def f() -> int:\n    return 'x'\n" });
        it.TypecheckBeforeExecute = true;
        var ran = false;
        it.Define("mark", (_, _, _) => { ran = true; return null; });
        Assert.Throws<StarlarkException>(() => it.Execute("main.star", "mark()\nload('lib.star', 'f')\n"));
        Assert.False(ran);
    }

    /// <summary>Each module is checked once, so cycles and diamonds terminate and do not duplicate errors.</summary>
    [Fact]
    public void CyclesAndDiamondsTerminate()
    {
        var it = New(new()
        {
            ["a.star"] = "load('b.star', 'y')\nx = 1\n",
            ["b.star"] = "load('a.star', 'x')\ny = 1\n",
            ["c.star"] = "load('b.star', 'y')\nz = 1\n",
        });
        Assert.Empty(it.Typecheck("main.star", "load('a.star', 'x')\nload('c.star', 'z')\n").Errors);
    }

    /// <summary>A module the loader cannot find is left for the run to report, not a checker error.</summary>
    [Fact]
    public void UnknownModulesAreNotCheckerErrors()
    {
        var it = New([]);
        Assert.Empty(it.Typecheck("main.star", "load('missing.star', 'x')\n").Errors);
    }
}
