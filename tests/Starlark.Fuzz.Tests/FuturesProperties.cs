using System.Numerics;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Starlark.Eval;
using Starlark.Eval.Futures;
using Starlark.Syntax;

namespace Starlark.Fuzz.Tests;

/// <summary>
/// Property tests for the futures extension: running work through <c>parallel_map</c> or <c>spawn</c> must give exactly
/// the values, errors and printed output that calling the function inline gives, however many workers there are.
/// </summary>
public class FuturesProperties
{
    private static Interpreter New(int workers, List<string>? output = null)
    {
        var it = new Interpreter
        {
            Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableFutures = true },
            FutureLimits = new FutureLimits { MaxConcurrency = workers, SpawnStepCost = 0 },
        };
        if (output is not null) it.Print = output.Add;
        return it;
    }

    private const string Library = """
        K = 7

        def f(x):
            if x % 11 == 10:
                fail("eleven " + str(x))
            return x * K + len(str(x))

        def g(xs):
            t = 0
            for x in xs:
                t += x
            return [t, len(xs), sorted(xs)]

        def noisy(x):
            print("saw", x)
            return x + 1
        """;

    private static (object? Value, string? Error) Run(Interpreter it, string call)
    {
        try
        {
            return (it.Execute("t", Library + "\n" + call).Globals["r"], null);
        }
        catch (StarlarkException e)
        {
            return (null, e.RawMessage);
        }
    }

    private static string Show((object? Value, string? Error) r) => r.Error ?? Repr.Of(r.Value);

    /// <summary>parallel_map over any list equals the list comprehension, including which error is raised.</summary>
    [Property]
    public bool ParallelMapEqualsInline(int[] xs, PositiveInt workers)
    {
        string list = "[" + string.Join(", ", xs.Select(x => x.ToString())) + "]";
        var inline = Run(New(1), $"r = [f(x) for x in {list}]");
        var parallel = Run(New(1 + workers.Get % 6), $"r = parallel_map(f, {list})");
        return Show(inline) == Show(parallel);
    }

    /// <summary>spawn followed by get equals the direct call.</summary>
    [Property]
    public bool SpawnGetEqualsInline(int x, PositiveInt workers)
    {
        var inline = Run(New(1), $"r = f({x})");
        var parallel = Run(New(1 + workers.Get % 6), $"r = spawn(f, {x}).get()");
        return Show(inline) == Show(parallel);
    }

    /// <summary>all_of over several spawns equals the list of direct calls.</summary>
    [Property]
    public bool AllOfEqualsInline(int[] xs)
    {
        string list = "[" + string.Join(", ", xs.Select(x => x.ToString())) + "]";
        var inline = Run(New(1), $"r = [f(x) for x in {list}]");
        var parallel = Run(New(3), $"r = all_of([spawn(f, x) for x in {list}])");
        return Show(inline) == Show(parallel);
    }

    /// <summary>A list argument is passed by copy: the task sees the values at spawn time and the caller keeps its own list.</summary>
    [Property]
    public bool ArgumentsAreCopied(int[] xs)
    {
        string list = "[" + string.Join(", ", xs.Select(x => x.ToString())) + "]";
        var result = Run(New(2),
            $"def run():\n    ys = {list}\n    fut = spawn(g, ys)\n    ys.append(1000)\n    return [fut.get(), len(ys)]\n\nr = run()");
        var expected = Run(New(1), $"def run():\n    ys = {list}\n    return [g(ys), len(ys) + 1]\n\nr = run()");
        return Show(result) == Show(expected);
    }

    /// <summary>Printed output is the same as inline output, in the same order.</summary>
    [Property]
    public bool OutputOrderMatchesInline(int[] xs, PositiveInt workers)
    {
        string list = "[" + string.Join(", ", xs.Select(x => x.ToString())) + "]";
        var inlineOut = new List<string>();
        var parallelOut = new List<string>();
        Run(New(1, inlineOut), $"r = [noisy(x) for x in {list}]");
        Run(New(1 + workers.Get % 6, parallelOut), $"r = parallel_map(noisy, {list})");
        return inlineOut.SequenceEqual(parallelOut);
    }

    /// <summary>Running the same parallel program many times always gives one outcome.</summary>
    [Property(MaxTest = 30)]
    public bool RepeatedRunsAgree(int[] xs)
    {
        string list = "[" + string.Join(", ", xs.Select(x => x.ToString())) + "]";
        var outcomes = new HashSet<string>();
        for (int i = 0; i < 5; i++)
        {
            outcomes.Add(Show(Run(New(1 + i % 4), $"r = parallel_map(f, {list})")));
        }
        return outcomes.Count == 1;
    }
}
