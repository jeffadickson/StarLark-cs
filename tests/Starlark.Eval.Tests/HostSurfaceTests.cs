using System.Numerics;
using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>The opt-in <c>[Expose]</c> and <c>[Hide]</c> attributes: which members of a host object scripts can see.</summary>
public class ExposeAttributeTests
{
    private static Interpreter New() => new()
    {
        Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableTypes = DialectTypes.Enable },
    };

    /// <summary>A class with no attributes shows every public member, as before.</summary>
    public sealed class Open
    {
        public string Name { get; set; } = "n";
        public int Count { get; set; } = 3;
        public int Double(int x) => x * 2;
    }

    /// <summary>One marked member makes the class opt-in: only marked members are visible.</summary>
    public sealed class OptIn
    {
        [Expose] public string Name { get; set; } = "public";
        [Expose("renamed")] public int Count { get; set; } = 7;
        public string Secret { get; set; } = "hidden";
        [Expose] public int Add(int a, int b) => a + b;
        public int Internal() => 1;
    }

    /// <summary>[Hide] removes one member from an open class.</summary>
    public sealed class Mostly
    {
        public string Visible { get; set; } = "yes";
        [Hide] public string Password { get; set; } = "hunter2";
        [Hide] public int Wipe() => 0;
    }

    [Fact]
    public void ClassesWithoutAttributesAreUnchanged()
    {
        var it = New();
        it.Expose("o", new Open());
        var m = it.Execute("t", "r = [o.name, o.count, o.double(4)]\n");
        Assert.Equal("[\"n\", 3, 8]", Repr.Of(m.Globals["r"]));
    }

    [Fact]
    public void OnlyMarkedMembersAreVisible()
    {
        var it = New();
        it.Expose("o", new OptIn());
        var m = it.Execute("t", "r = [o.name, o.renamed, o.add(2, 3)]\n");
        Assert.Equal("[\"public\", 7, 5]", Repr.Of(m.Globals["r"]));
        Assert.Contains("no attribute", Assert.Throws<StarlarkException>(() => it.Execute("u", "x = o.secret\n")).RawMessage);
        Assert.Contains("no attribute", Assert.Throws<StarlarkException>(() => it.Execute("v", "x = o.internal()\n")).RawMessage);
        Assert.Contains("no attribute", Assert.Throws<StarlarkException>(() => it.Execute("w", "x = o.count\n")).RawMessage);   // renamed away
    }

    [Fact]
    public void HideRemovesOneMember()
    {
        var it = New();
        it.Expose("o", new Mostly());
        Assert.Equal("yes", it.Execute("t", "r = o.visible\n").Globals["r"]);
        Assert.Throws<StarlarkException>(() => it.Execute("u", "x = o.password\n"));
        Assert.Throws<StarlarkException>(() => it.Execute("v", "o.wipe()\n"));
    }

    [Fact]
    public void ListedNamesMatchTheVisibleSurface()
    {
        var it = New();
        it.Expose("o", new OptIn());
        var m = it.Execute("t", "r = dir(o)\n");
        var names = ((StarList)m.Globals["r"]!).Items.Select(x => (string)x!).OrderBy(x => x).ToList();
        Assert.Equal(["add", "name", "renamed"], names);
    }

    [Fact]
    public void ConvertedObjectsFollowTheSameRule()
    {
        var star = (StarStruct)Interop.ToStar(new OptIn())!;
        Assert.True(star.TryGet("name", out _));
        Assert.True(star.TryGet("renamed", out _));
        Assert.False(star.TryGet("secret", out _));
        var open = (StarStruct)Interop.ToStar(new Mostly())!;
        Assert.True(open.TryGet("visible", out _));
        Assert.False(open.TryGet("password", out _));
    }

    [Fact]
    public void TheCheckerSeesTheSameSurface()
    {
        var it = New();
        it.Expose("o", new OptIn());
        Assert.Empty(it.Typecheck("t", "def f():\n    a = o.name\n    b = o.add(1, 2)\n").Errors);
        Assert.Single(it.Typecheck("t", "def f():\n    a = o.secret\n").Errors);
        Assert.Single(it.Typecheck("t", "def f():\n    a = o.count\n").Errors);
    }
}

/// <summary>The JSON helpers for the host: a document in and out in one call.</summary>
public class HostJsonTests
{
    public sealed record Order(string Id, int Quantity, decimal Price, List<string> Tags, Address? Ship);

    public sealed record Address(string City, int PostCode);

    [Fact]
    public void JsonBecomesScriptValues()
    {
        var v = HostJson.ToScript("{\"a\": [1, 2.5, \"x\", null, true], \"b\": {\"c\": 3}}");
        Assert.Equal("{\"a\": [1, 2.5, \"x\", None, True], \"b\": {\"c\": 3}}", Repr.Of(v));
    }

    [Fact]
    public void ObjectsBecomeScriptValuesWithSnakeCaseNames()
    {
        var v = HostJson.ToScript(new Order("A1", 3, 9.5m, ["x", "y"], new Address("Oslo", 150)));
        var d = (StarDict)v!;
        Assert.Equal("A1", d.TryGet("id", out var id) ? id : null);
        Assert.Equal(new BigInteger(3), d.TryGet("quantity", out var q) ? q : null);
        Assert.Equal("Oslo", ((StarDict)(d.TryGet("ship", out var s) ? s : null)!).TryGet("city", out var c) ? c : null);
    }

    [Fact]
    public void ScriptValuesBecomeObjects()
    {
        var it = new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true } };
        var m = it.Execute("t", "o = {'id': 'B7', 'quantity': 4, 'price': 2.25, 'tags': ['a'], 'ship': {'city': 'Rome', 'post_code': 100}}\n");
        var order = HostJson.FromScript<Order>(m.Globals["o"]);
        Assert.Equal(new Order("B7", 4, 2.25m, ["a"], new Address("Rome", 100)).Id, order.Id);
        Assert.Equal(4, order.Quantity);
        Assert.Equal(2.25m, order.Price);
        Assert.Equal(["a"], order.Tags);
        Assert.Equal(new Address("Rome", 100), order.Ship);
    }

    [Fact]
    public void RoundTripThroughAScriptKeepsTheData()
    {
        var original = new Order("C9", 2, 1.5m, ["p", "q"], null);
        var back = HostJson.FromScript<Order>(HostJson.ToScript(original));
        Assert.Equal(original.Id, back.Id);
        Assert.Equal(original.Tags, back.Tags);
        Assert.Null(back.Ship);
    }

    [Fact]
    public void BadInputIsAScriptError()
    {
        Assert.Throws<StarlarkException>(() => HostJson.ToScript("{not json"));
        var bad = HostJson.ToScript("{\"quantity\": \"many\"}");
        var e = Assert.Throws<StarlarkException>(() => HostJson.FromScript<Order>(bad));
        Assert.Contains("does not fit Order", e.RawMessage);
        Assert.Throws<StarlarkException>(() => HostJson.FromScript<Order>(null));
    }

    [Fact]
    public void ToJsonMatchesTheScriptsOwnEncoder()
    {
        var it = new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true } };
        var m = it.Execute("t", "d = {'a': [1, 2], 'b': 'x'}\ne = json.encode(d)\n");
        Assert.Equal(m.Globals["e"], HostJson.ToJson(m.Globals["d"]));
    }
}

/// <summary>Script call stacks: an error names every function it passed through.</summary>
public class CallStackTests
{
    private static Interpreter New() => new() { Dialect = Dialect.Standard with { EnableTopLevelStmt = true } };

    private const string Program = "def inner(x):\n    return 10 // x\n\ndef middle(x):\n    y = inner(x)\n    return y\n\ndef outer():\n    return middle(0)\n\nvalue = outer()\n";

    private static StarlarkException Failure(string source) =>
        Assert.Throws<StarlarkException>(() => New().Execute("main.star", source));

    [Fact]
    public void FramesAreInnermostFirstAndEndAtTheModule()
    {
        var frames = Failure(Program).CallStack;
        Assert.Equal(["inner", "middle", "outer", "<module>"], frames.Select(f => f.Function).ToArray());
        Assert.Equal([2, 5, 9, 11], frames.Select(f => f.Line).ToArray());
    }

    [Fact]
    public void TracebackListsEveryCall()
    {
        var text = Failure(Program).MessageWithStack();
        Assert.Contains("Traceback (most recent call last):", text);
        Assert.Contains("main.star:11:9 in <module>", text);
        Assert.Contains("main.star:9:12 in outer", text);
        Assert.Contains("main.star:5:9 in middle", text);
        Assert.Contains("main.star:2:12 in inner", text);
        Assert.True(text.IndexOf("<module>") < text.IndexOf("in outer") && text.IndexOf("in outer") < text.IndexOf("in middle"));
    }

    [Fact]
    public void AnErrorAtTopLevelHasOneFrameAndNoTraceback()
    {
        var e = Failure("x = 1 // 0\n");
        Assert.Single(e.CallStack);
        Assert.Equal(e.Message, e.MessageWithStack());
    }

    [Fact]
    public void LambdasAndNestedFunctionsAppearByName()
    {
        var e = Failure("def f():\n    g = lambda: 1 // 0\n    return g()\n\nf()\n");
        Assert.Equal(["lambda", "f", "<module>"], e.CallStack.Select(x => x.Function).ToArray());
    }

    [Fact]
    public void HostFunctionsDoNotAddFrames()
    {
        var it = New();
        it.Register("boom", () => { throw new InvalidOperationException("no"); });
        var e = Assert.Throws<StarlarkException>(() => it.Execute("t", "def f():\n    boom()\n\nf()\n"));
        Assert.Equal(["f", "<module>"], e.CallStack.Select(x => x.Function).ToArray());
    }

    [Fact]
    public void StackIsKeptWhenAnErrorCrossesFromATask()
    {
        var it = new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableFutures = true } };
        var e = Assert.Throws<StarlarkException>(() =>
            it.Execute("t", "def deep(x):\n    return 1 // x\n\ndef work(x):\n    return deep(x)\n\nr = parallel_map(work, [0])\n"));
        Assert.Contains("deep", e.CallStack.Select(x => x.Function));
        Assert.Contains("work", e.CallStack.Select(x => x.Function));
    }

    [Fact]
    public void MessageIsUnchangedByStacks()
    {
        var e = Failure(Program);
        Assert.StartsWith("error: Integer division by zero", e.Message);
        Assert.Equal("Integer division by zero", e.RawMessage);
    }

    [Fact]
    public void RecursionAndLimitErrorsCarryStacksToo()
    {
        var e = Failure("def f(n):\n    return f(n)\n\nf(1)\n");
        Assert.Contains("called recursively", e.RawMessage);
        Assert.True(e.CallStack.Count >= 2);
    }
}
