using System.Diagnostics;
using System.Numerics;
using System.Text.RegularExpressions;
using Starlark.Eval;
using Starlark.Eval.Futures;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>
/// The code samples of <c>docs/MANUAL.md</c>, run as tests. Each test repeats one sample marked
/// <c>&lt;!-- manual-sample: NAME --&gt;</c> in the manual and checks what the manual says it does, so the manual cannot go
/// out of date silently. <see cref="EveryMarkedSampleHasATest"/> keeps the two lists in step.
/// </summary>
public class ManualTests
{
    private static string ManualPath
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "docs", "MANUAL.md");
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException("docs/MANUAL.md not found above " + AppContext.BaseDirectory);
        }
    }

    [Fact]
    public void EveryMarkedSampleHasATest()
    {
        var marked = Regex.Matches(File.ReadAllText(ManualPath), @"<!-- manual-sample: (\w+) -->").Select(m => m.Groups[1].Value).ToList();
        var tested = typeof(ManualTests).GetMethods().Where(m => m.Name.StartsWith("Sample_")).Select(m => m.Name["Sample_".Length..]).ToHashSet();
        Assert.NotEmpty(marked);
        foreach (var name in marked) Assert.True(tested.Contains(name), $"manual sample '{name}' has no test Sample_{name}");
        foreach (var name in tested) Assert.True(marked.Contains(name), $"test Sample_{name} has no sample in the manual");
    }

    [Fact]
    public void Sample_hello()
    {
        var it = new Interpreter();
        var printed = new List<string>();
        it.Print = printed.Add;
        it.Execute("hello.star", "def greet(name):\n    return 'hello, ' + name\nprint(greet('world'))\n");
        Assert.Equal(["hello, world"], printed);
    }

    [Fact]
    public void Sample_globals()
    {
        var it = new Interpreter();
        var module = it.Execute("config.star", "replicas = 3\nname = 'web-' + str(replicas)\n");
        Assert.Equal("web-3", (string)module.Globals["name"]!);
    }

    [Fact]
    public void Sample_call()
    {
        var it = new Interpreter();
        var module = it.Execute("price.star", "def price(qty, unit):\n    return qty * unit\n");
        int total = it.Call<int>(module, "price", 4, 25);
        Assert.Equal(100, total);
    }

    [Fact]
    public void Sample_toplevel()
    {
        var it = new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true } };
        var printed = new List<string>();
        it.Print = printed.Add;
        it.Execute("loop.star", "for i in range(3):\n    print(i * i)\n");
        Assert.Equal(["0", "1", "4"], printed);
        // And without the switch, a top-level for is rejected, as the manual says.
        Assert.ThrowsAny<Exception>(() => new Interpreter().Execute("loop.star", "for i in range(3):\n    print(i)\n"));
    }

    [Fact]
    public void Sample_load()
    {
        var files = new Dictionary<string, string>
        {
            ["lib.star"] = "def double(x):\n    return 2 * x\n",
        };
        var it = new Interpreter { SourceLoader = id => files.GetValueOrDefault(id) };
        var module = it.Execute("main.star", "load('lib.star', 'double')\nresult = double(21)\n");
        Assert.Equal(new BigInteger(42), module.Globals["result"]);
    }

    [Fact]
    public void Sample_register()
    {
        var it = new Interpreter();
        it.Register("add", (int a, int b) => a + b);
        it.Register("greet", (string name, string greeting = "hello") => $"{greeting}, {name}");
        var m = it.Execute("t.star", "x = add(2, 3)\ny = greet('ada', greeting = 'hi')\n");
        Assert.Equal(new BigInteger(5), m.Globals["x"]);
        Assert.Equal("hi, ada", m.Globals["y"]);
    }

    /// <summary>The class from the manual's Expose sample.</summary>
    public sealed class Inventory
    {
        private readonly Dictionary<string, int> _stock = new() { ["apple"] = 3 };
        [Expose] public int Count(string item) => _stock.GetValueOrDefault(item);
        [Expose] public string Owner { get; init; } = "shop";
        public void Wipe() => _stock.Clear();
    }

    [Fact]
    public void Sample_expose()
    {
        var it = new Interpreter();
        it.Expose("inventory", new Inventory());
        var m = it.Execute("t.star", "n = inventory.count('apple')\nwho = inventory.owner\n");
        Assert.Equal(new BigInteger(3), m.Globals["n"]);
        Assert.Equal("shop", m.Globals["who"]);
        Assert.Throws<StarlarkException>(() => it.Execute("t.star", "inventory.wipe()\n"));
    }

    [Fact]
    public void Sample_limits()
    {
        var it = new Interpreter
        {
            Dialect = Dialect.Standard with { EnableTopLevelStmt = true },
            MaxSteps = 1_000_000,
            Timeout = TimeSpan.FromSeconds(2),
            MaxAllocatedBytes = 64L * 1024 * 1024,
        };
        var e = Assert.Throws<ExecutionLimitException>(() =>
            it.Execute("spin.star", "def spin():\n    for i in range(1000000000):\n        pass\nspin()\n"));
        Assert.Equal("steps", e.Limit);
    }

    [Fact]
    public void Sample_errors()
    {
        var it = new Interpreter();
        var e = Assert.Throws<StarlarkException>(() => it.Execute("bad.star", "d = {'a': 1}\nx = d['b']\n"));
        Assert.Contains("\"b\"", e.RawMessage);
        Assert.Contains("bad.star:2", e.Message);
    }

    [Fact]
    public void Sample_futures()
    {
        var it = new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableFutures = true } };
        var printed = new List<string>();
        it.Print = printed.Add;
        it.Execute("par.star", """
            def score(word):
                return len(word) * 10
            print(parallel_map(score, ["fig", "apple", "kiwi"]))
            f = spawn(score, "banana")
            print(f.get())
            """);
        Assert.Equal(["[30, 50, 40]", "60"], printed);
    }

    [Fact]
    public void Sample_hostfutures()
    {
        var it = new Interpreter
        {
            Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableFutures = true },
            FutureLimits = new FutureLimits { MaxHostOperations = 4 },
        };
        it.Register("fetch", async (string key) => { await Task.Delay(50); return key.ToUpperInvariant(); },
                    threadSafe: true, asFuture: true);
        var clock = Stopwatch.StartNew();
        var m = it.Execute("io.star", "fs = [fetch(k) for k in ['a', 'b', 'c']]\nr = all_of(fs)\n");
        Assert.Equal("[\"A\", \"B\", \"C\"]", Repr.Of(m.Globals["r"]));
        // The three 50 ms waits overlap. Allow a slow machine plenty of room, but not three waits in a row plus overhead.
        Assert.True(clock.ElapsedMilliseconds < 140 || it.LastRunStats!.PeakHostOperations == 3,
            $"took {clock.ElapsedMilliseconds} ms with peak {it.LastRunStats!.PeakHostOperations}");
        Assert.Equal(3, it.LastRunStats!.PeakHostOperations);
    }

    [Fact]
    public void Sample_types()
    {
        var it = new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableTypes = DialectTypes.Enable } };
        var m = it.Execute("t.star", """
            Point = record(x = int, y = int)
            def norm1(p: Point) -> int:
                return abs(p.x) + abs(p.y)
            r = norm1(Point(x = 3, y = -4))
            """);
        Assert.Equal(new BigInteger(7), m.Globals["r"]);
        var e = Assert.Throws<StarlarkException>(() => it.Execute("t.star", """
            Point = record(x = int, y = int)
            def norm1(p: Point) -> int:
                return abs(p.x) + abs(p.y)
            r = norm1("x")
            """));
        Assert.Contains("does not match the type annotation", e.RawMessage);
    }

    [Fact]
    public void Sample_typecheck()
    {
        var it = new Interpreter { Dialect = Dialect.Standard with { EnableTypes = DialectTypes.Enable } };
        it.Register("add", (int a, int b) => a + b);
        var result = it.Typecheck("t.star", "def f() -> str:\n    return add('two', 3)\n");
        Assert.Equal(2, result.Errors.Count);
    }

    /// <summary>The sample at the top of README.md.</summary>
    [Fact]
    public void TheReadmeSampleWorks()
    {
        var it = new Interpreter();
        it.Register("add", (int a, int b) => a + b);
        var module = it.Execute("main.star", "total = add(2, 3) * 10\n");
        Assert.Equal(new BigInteger(50), module.Globals["total"]);
    }

    // ---------------------------------------------------------------- facts the manual states in prose

    [Fact]
    public void TheQueueSampleWorks()
    {
        var it = new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true } };
        var m = it.Execute("bfs.star", """
            load("std/queue.star", "queue", "enqueue", "dequeue", "queue_empty")

            def bfs(start, neighbours):
                seen = {start: 0}
                q = queue([start])
                for _ in range(100000):
                    if queue_empty(q):
                        break
                    node = dequeue(q)
                    for n in neighbours(node):
                        if n not in seen:
                            seen[n] = seen[node] + 1
                            enqueue(q, n)
                return seen

            r = bfs(0, lambda n: [n + 1, n + 2] if n < 10 else [])[10]
            """);
        Assert.Equal(new BigInteger(5), m.Globals["r"]);
    }

    [Fact]
    public void StringLengthsAreAsTheManualSays()
    {
        var utf8 = new Interpreter().Execute("t.star", "n = len('héllo')\n");
        Assert.Equal(new BigInteger(6), utf8.Globals["n"]);
        var utf16 = new Interpreter { StringElements = StringElements.Utf16 }.Execute("t.star", "n = len('héllo')\n");
        Assert.Equal(new BigInteger(5), utf16.Globals["n"]);
    }

    [Fact]
    public void TheDefaultsInTheReferenceTableAreTheRealDefaults()
    {
        var it = new Interpreter();
        Assert.Equal(0, it.MaxSteps);
        Assert.Null(it.Timeout);
        Assert.Equal(0, it.MaxAllocatedBytes);
        Assert.Equal(150, it.MaxCallDepth);
        Assert.Equal(StringElements.Utf8, it.StringElements);
        Assert.False(it.AllowGlobalReassign);
        Assert.False(it.TypecheckBeforeExecute);
        var recursive = new Interpreter { Dialect = Dialect.Standard with { EnableRecursion = true } };
        Assert.Equal(1000, recursive.MaxCallDepth);
        var limits = new FutureLimits();
        Assert.Equal(4, limits.MaxConcurrency);
        Assert.Equal(1_000, limits.MaxLiveFutures);
        Assert.Equal(10_000, limits.MaxSpawns);
        Assert.Equal(8, limits.MaxHostOperations);
    }

    [Fact]
    public void TheNativeMathFunctionsTheManualListsAllExist()
    {
        string[] names = ["sin", "cos", "tan", "asin", "acos", "atan", "atan2", "exp", "log", "log2", "sqrt", "pow", "hypot",
                          "floor", "ceil", "trunc", "round", "degrees", "radians", "isnan", "isinf"];
        var it = new Interpreter();
        foreach (var n in names) Assert.Equal("builtin_function_or_method", (string)it.Execute("t.star", $"t = type({n})\n").Globals["t"]!);
        var m = it.Execute("t.star", "a = log(0)\nb = round(2.5)\nc = round(3.5)\n");
        Assert.Equal(double.NegativeInfinity, m.Globals["a"]);
        Assert.Equal(new BigInteger(2), m.Globals["b"]);
        Assert.Equal(new BigInteger(4), m.Globals["c"]);
    }
}
