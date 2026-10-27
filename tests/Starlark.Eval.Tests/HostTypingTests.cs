using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>The static checker uses the .NET signatures of registered host functions and exposed objects.</summary>
public class HostTypingTests
{
    private static Interpreter New() => new()
    {
        Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableTypes = DialectTypes.Enable },
    };

    private static List<string> Errors(Interpreter it, string code) =>
        it.Typecheck("t", code).Errors.Select(e => e.Message).ToList();

    public sealed record Point(int X, int Y);

    public sealed class Account
    {
        public string Owner { get; set; } = "";
        public int Balance { get; set; }
        public List<string> Tags { get; set; } = [];
        public bool Withdraw(int amount) => true;
    }

    /// <summary>A call with the wrong argument type is reported before the script runs.</summary>
    [Fact]
    public void WrongArgumentTypesAreReported()
    {
        var it = New();
        it.Register("add", (int a, int b) => a + b);
        var errors = Errors(it, "def f():\n    add('x', 1)\n");
        Assert.Single(errors);
        Assert.Contains("Expected type `int` but got `str`", errors[0]);
    }

    /// <summary>Missing and unexpected arguments are reported, and C# defaults make parameters optional.</summary>
    [Fact]
    public void ArityAndDefaults()
    {
        var it = New();
        it.Register("greet", (string name, string greeting = "Hello") => greeting + name);
        Assert.Empty(Errors(it, "def f():\n    greet('a')\n    greet('a', greeting = 'Hi')\n    greet(name = 'a')\n"));
        Assert.Contains("Missing required parameter `name`", Assert.Single(Errors(it, "def f():\n    greet()\n")));
        Assert.Contains("Unexpected parameter named `nope`", Assert.Single(Errors(it, "def f():\n    greet('a', nope = 1)\n")));
        Assert.Contains("Too many positional arguments", Assert.Single(Errors(it, "def f():\n    greet('a', 'b', 'c')\n")));
    }

    /// <summary>Camel-case C# parameter names are callable by their snake_case form and by the original name.</summary>
    [Fact]
    public void ParameterNamesAcceptBothSpellings()
    {
        var it = New();
        it.Register("move", (int deltaX, int deltaY) => deltaX + deltaY);
        Assert.Empty(Errors(it, "def f():\n    move(delta_x = 1, delta_y = 2)\n    move(deltaX = 1, deltaY = 2)\n"));
    }

    /// <summary>The checker accepts everything the run-time conversion accepts.</summary>
    [Fact]
    public void PermissiveWhereRuntimeIs()
    {
        var it = New();
        it.Register("scale", (double factor, string label) => factor);
        it.Register("total", (List<int> xs) => xs.Sum());
        it.Register("maybe", (int? n) => n);
        Assert.Empty(Errors(it, "def f():\n    scale(2, 'a')\n    scale(2.5, utf16('a'))\n    total([1, 2])\n    total((1, 2))\n    maybe(None)\n    maybe(3)\n"));
        Assert.Single(Errors(it, "def f():\n    total(['a'])\n"));
        Assert.Single(Errors(it, "def f():\n    maybe('a')\n"));
    }

    /// <summary>The result type flows into later expressions.</summary>
    [Fact]
    public void ResultTypesFlow()
    {
        var it = New();
        it.Register("count", () => 3);
        it.Register("name", () => "x");
        it.Register("names", () => new List<string>());
        var result = it.Typecheck("t", "def f():\n    a = count()\n    b = name()\n    c = names()\n    d = a + b\n");
        Assert.Equal("int", result.Types["a"].ToString());
        Assert.Equal("str", result.Types["b"].ToString());
        Assert.Equal("list[str]", result.Types["c"].ToString());
        Assert.Contains(result.Errors, e => e.Message.Contains("Binary operator `+`"));
    }

    /// <summary>Async host functions are typed by their awaited result.</summary>
    [Fact]
    public void TasksAreTypedByTheirResult()
    {
        var it = New();
        it.Register("fetch", async (string url) => { await Task.Delay(1); return url.Length; });
        var result = it.Typecheck("t", "def f():\n    n = fetch('a')\n");
        Assert.Equal("int", result.Types["n"].ToString());
    }

    /// <summary>Injected parameters are not part of the script-visible signature.</summary>
    [Fact]
    public void InjectedParametersAreHidden()
    {
        var it = New();
        it.Register("twice", (Interpreter i, int n, CancellationToken ct) => n * 2);
        Assert.Empty(Errors(it, "def f():\n    twice(3)\n"));
        Assert.Single(Errors(it, "def f():\n    twice(3, 4)\n"));
    }

    /// <summary>An exposed object's properties and methods are typed.</summary>
    [Fact]
    public void ExposedObjectsAreTyped()
    {
        var it = New();
        it.Expose("acct", new Account());
        Assert.Empty(Errors(it, "def f():\n    a = acct.owner\n    b = acct.balance + 1\n    c = acct.withdraw(5)\n"));
        Assert.Contains("attribute `nope` is not available", Assert.Single(Errors(it, "def f():\n    acct.nope\n")));
        Assert.Contains("Expected type `int` but got `str`", Assert.Single(Errors(it, "def f():\n    acct.withdraw('x')\n")));
        var result = it.Typecheck("t", "def f():\n    a = acct.owner\n    t = acct.tags\n");
        Assert.Equal("str", result.Types["a"].ToString());
        Assert.Equal("list[str]", result.Types["t"].ToString());
    }

    /// <summary>A predeclared value converted from an object gets a struct type with its fields.</summary>
    [Fact]
    public void PredeclaredValuesAreTyped()
    {
        var it = New();
        it.DefineValue("origin", new Point(1, 2));
        Assert.Empty(Errors(it, "def f():\n    a = origin.x + origin.y\n"));
        Assert.Contains("attribute `z` is not available", Assert.Single(Errors(it, "def f():\n    origin.z\n")));
    }

    /// <summary>Re-registering a name replaces its type information, and a shadowing local wins.</summary>
    [Fact]
    public void ReplacementAndShadowing()
    {
        var it = New();
        it.Register("f", (int a) => a);
        it.Register("f", (string s) => s);
        Assert.Empty(Errors(it, "def g():\n    f('x')\n"));
        Assert.Single(Errors(it, "def g():\n    f(1)\n"));
        Assert.Empty(Errors(it, "def g(f):\n    f(1, 2, 3)\n"));
    }

    /// <summary>Everything the checker accepts here also runs, with the same arguments.</summary>
    [Fact]
    public void AcceptedProgramsRun()
    {
        var it = New();
        it.Register("add", (int a, int b) => a + b);
        it.Register("greet", (string name, string greeting = "Hello") => $"{greeting} {name}");
        const string code = "x = add(1, b = 2)\ny = greet('a')\n";
        Assert.Empty(it.Typecheck("t", code).Errors);
        var m = it.Execute("t", code);
        Assert.Equal("Hello a", m.Globals["y"]);
    }
}
