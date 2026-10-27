using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>The static checker knows the futures functions when the dialect enables them.</summary>
public class FuturesCheckerTests
{
    private static Interpreter New(bool futures = true) => new()
    {
        Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableTypes = DialectTypes.Enable, EnableFutures = futures },
    };

    private static List<string> Errors(Interpreter it, string code) => it.Typecheck("t", code).Errors.Select(e => e.Message).ToList();

    /// <summary>A correct use passes the checker.</summary>
    [Fact]
    public void CorrectUsePasses() =>
        Assert.Empty(Errors(New(), "def f(x: int) -> int:\n    return x * 2\n\ndef g():\n    a = parallel_map(f, [1, 2])\n    b = spawn(f, 3)\n    c = all_of([b])\n    return a\n"));

    /// <summary>parallel_map needs both arguments.</summary>
    [Fact]
    public void ParallelMapNeedsTwoArguments()
    {
        Assert.Contains("Missing required parameter `iterable`", Assert.Single(Errors(New(), "def f():\n    parallel_map(abs)\n")));
        var none = Errors(New(), "def f():\n    parallel_map()\n");
        Assert.Contains("Missing required parameter `function`", none);
        Assert.Contains("Missing required parameter `iterable`", none);
    }

    /// <summary>The result of parallel_map is a list, so list operations check and number operations do not.</summary>
    [Fact]
    public void ParallelMapReturnsAList()
    {
        var result = New().Typecheck("t", "def f():\n    r = parallel_map(abs, [1])\n    n = r + 1\n");
        Assert.Contains(result.Errors, e => e.Message.Contains("Binary operator `+`"));
    }

    /// <summary>A non-function first argument is reported.</summary>
    [Fact]
    public void FirstArgumentMustBeCallable() =>
        Assert.Single(Errors(New(), "def f():\n    parallel_map(5, [1])\n"));

    /// <summary>spawn takes any arguments after the function, positional or by name.</summary>
    [Fact]
    public void SpawnAcceptsArgumentsAndKeywords() =>
        Assert.Empty(Errors(New(), "def h(a, b = 1):\n    return a\n\ndef f():\n    spawn(h, 1, b = 2)\n    spawn(h, 1)\n"));

    /// <summary>With the flag off the names are unknown, as they are at run time.</summary>
    [Fact]
    public void NamesAreUnknownWithoutTheFlag()
    {
        var result = New(futures: false).Typecheck("t", "def f():\n    parallel_map(abs, [1])\n");
        Assert.NotNull(result);
    }
}
