using System.Diagnostics;
using Starlark.Eval;
using Starlark.Syntax;

// A profiler for the interpreter: representative workloads, each measured for time, allocation and GC pressure, plus a
// sampling profile written by the runtime itself (see --trace), which needs no external tool and no code in the interpreter.
//
//   Starlark.Profile [--workload name] [--seconds 3] [--list]
//
// Run it in Release: `dotnet run -c Release --project bench/Starlark.Profile`.

var options = Options.Parse(args);
var workloads = Workloads.All();

if (options.List)
{
    foreach (var w in workloads) Console.WriteLine($"{w.Name,-18} {w.Description}");
    return 0;
}

Console.WriteLine($"{"workload",-18} {"ms/run",9} {"runs/s",9} {"alloc KB/run",13} {"gen0",5} {"gen1",5} {"gen2",5}");
Console.WriteLine(new string('-', 92));
foreach (var w in workloads)
{
    if (options.Only is not null && w.Name != options.Only) continue;
    var r = Measure.Run(w, options.Seconds);
    Console.WriteLine($"{w.Name,-18} {r.MsPerRun,9:F3} {r.RunsPerSecond,9:F1} {r.AllocatedKbPerRun,13:F1} {r.Gen0,5} {r.Gen1,5} {r.Gen2,5}");
}
return 0;

sealed record Options(string? Only, double Seconds, bool List)
{
    public static Options Parse(string[] args)
    {
        string? Get(string n) { int i = Array.IndexOf(args, "--" + n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        return new Options(Get("workload"), double.TryParse(Get("seconds"), out var s) ? s : 3, args.Contains("--list"));
    }
}

/// <summary>One program to measure: its source, dialect and an optional setup.</summary>
sealed record Workload(string Name, string Description, string Source, Func<Interpreter>? Make = null);

static class Workloads
{
    public static List<Workload> All() =>
    [
        new("loops", "nested for loops with int arithmetic",
            "def run():\n    t = 0\n    for i in range(300):\n        for j in range(300):\n            t += i * j % 7\n    return t\nr = run()\n"),
        new("calls", "many small function calls",
            "def f(a, b):\n    return a + b\ndef run():\n    t = 0\n    for i in range(60000):\n        t = f(t, i)\n    return t\nr = run()\n"),
        new("strings", "string building, formatting and methods",
            "def run():\n    out = []\n    for i in range(6000):\n        s = 'item-' + str(i)\n        out.append(s.upper() + '%d' % i + '{:>6}'.format(i))\n    return len(','.join(out))\nr = run()\n"),
        new("lists", "list comprehensions, slices, sorted",
            "def run():\n    xs = [(i * 37) % 1009 for i in range(8000)]\n    ys = sorted(xs)\n    zs = [x for x in ys[100:7000] if x % 3 == 0]\n    return len(zs) + sum_of(zs)\ndef sum_of(xs):\n    t = 0\n    for x in xs:\n        t += x\n    return t\nr = run()\n"),
        new("dicts", "dict insert, lookup, update and iteration",
            "def run():\n    d = {}\n    for i in range(6000):\n        d['k' + str(i % 500)] = i\n    t = 0\n    for k in d:\n        t += d[k]\n    for k, v in d.items():\n        t += v\n    return t\nr = run()\n"),
        new("records", "records and structs, field access",
            "P = record(x = int, y = int)\ndef run():\n    t = 0\n    for i in range(8000):\n        p = P(x = i, y = i + 1)\n        t += p.x * p.y\n    return t\nr = run()\n",
            () => new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableTypes = DialectTypes.Enable } }),
        new("closures", "lambdas and higher-order code",
            "def run():\n    adders = [(lambda x, k = k: x + k) for k in range(50)]\n    t = 0\n    for i in range(1500):\n        for f in adders:\n            t = f(t) % 100003\n    return t\nr = run()\n"),
        new("bigints", "arbitrary-precision arithmetic",
            "def run():\n    a = 1\n    for i in range(1, 600):\n        a = a * i\n    return len(str(a))\nr = run()\n"),
        new("floats", "float arithmetic and comparison",
            "def run():\n    t = 0.0\n    for i in range(120000):\n        t += (i * 0.5) / (i + 1.0)\n        if t > 1e9:\n            t = 0.0\n    return t\nr = run()\n"),
        new("parse", "lex and parse a 400-line program (no execution)", ParseSource(), null),
    ];

    private static string ParseSource()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 100; i++)
        {
            sb.Append($"def fn{i}(a, b = {i}, *rest, **kw):\n    x = [a + k for k in range(b) if k % 2 == 0]\n    d = {{'a': a, 'b': [b, {i}]}}\n    return len(x) + d['a']\n");
        }
        return sb.ToString();
    }
}

readonly record struct Result(double MsPerRun, double RunsPerSecond, double AllocatedKbPerRun, int Gen0, int Gen1, int Gen2);

static class Measure
{
    public static Result Run(Workload w, double seconds)
    {
        Interpreter Fresh() => w.Make?.Invoke() ?? new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true } };

        // Warm up the JIT (including tiered PGO) before measuring.
        for (int i = 0; i < 12; i++) RunOnce(w, Fresh());
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        long bytes0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        int runs = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            var it = Fresh();
            RunOnce(w, it);
            runs++;
        }
        double elapsed = sw.Elapsed.TotalSeconds;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - bytes0;
        return new Result(elapsed * 1000 / runs, runs / elapsed, bytes / 1024.0 / runs,
            GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2);
    }

    public static void RunOnce(Workload w, Interpreter it)
    {
        if (w.Name == "parse") AstModule.Parse("p.star", w.Source, Dialect.Standard);
        else it.Execute("w.star", w.Source);
    }
}
