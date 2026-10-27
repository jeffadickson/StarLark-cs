using Starlark.Eval;
using Starlark.Syntax;

namespace BuildRules;

/// <summary>One declared build target.</summary>
public sealed record Target(string Name, string Kind, List<string> Srcs, List<string> Deps, string Opt);

/// <summary>Thrown when the dependency graph is not a DAG or refers to unknown targets.</summary>
public sealed class GraphException(string message) : Exception(message);

/// <summary>A Bazel-like build planner driven by Starlark BUILD files.</summary>
public static class Program
{
    /// <summary>Entry point: runs the demo against standard output.</summary>
    public static int Main() { Run(Console.Out); return 0; }

    /// <summary>Evaluates a BUILD script and returns the targets it declared, in declaration order.</summary>
    public static List<Target> LoadTargets(string buildFile)
    {
        var targets = new List<Target>();
        var interp = new Interpreter
        {
            Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableTypes = DialectTypes.Enable },
            SourceLoader = id => Scripts.Read(id.TrimStart('/').Replace(':', '/')),
            Print = _ => { },
        };
        // Host function the rule library calls; duplicates are a script error with a location.
        interp.Register("declare", (string name, string kind, List<string> srcs, List<string> deps, string opt) =>
        {
            if (targets.Any(t => t.Name == name)) throw new InvalidOperationException($"duplicate target '{name}'");
            targets.Add(new Target(name, kind, srcs, deps, opt));
            return name;
        });
        interp.Execute(buildFile, Scripts.Read(buildFile)!);
        return targets;
    }

    /// <summary>Orders targets so every dependency precedes its dependents; throws <see cref="GraphException"/> on a cycle.</summary>
    public static List<Target> Plan(IReadOnlyList<Target> targets)
    {
        var byName = targets.ToDictionary(t => t.Name);
        var state = new Dictionary<string, int>();   // 1 = visiting, 2 = done
        var order = new List<Target>();
        var path = new List<string>();
        void Visit(Target t)
        {
            if (state.GetValueOrDefault(t.Name) == 2) return;
            path.Add(t.Name);
            if (state.GetValueOrDefault(t.Name) == 1)
                throw new GraphException("dependency cycle: " + string.Join(" -> ", path.SkipWhile(p => p != t.Name)));
            state[t.Name] = 1;
            foreach (var d in t.Deps)
            {
                if (!byName.TryGetValue(d, out var dep)) throw new GraphException($"{t.Name} depends on unknown target '{d}'");
                Visit(dep);
            }
            state[t.Name] = 2;
            path.RemoveAt(path.Count - 1);
            order.Add(t);
        }
        foreach (var t in targets) Visit(t);
        return order;
    }

    /// <summary>Runs the whole demo, writing to <paramref name="output"/>.</summary>
    public static void Run(TextWriter output)
    {
        output.WriteLine("== Build plan for BUILD.star ==");
        var plan = Plan(LoadTargets("BUILD.star"));
        for (int i = 0; i < plan.Count; i++)
        {
            var t = plan[i];
            output.WriteLine($"[{i + 1}/{plan.Count}] {t.Kind,-10} {t.Name,-8} -{t.Opt} srcs={t.Srcs.Count} deps=[{string.Join(", ", t.Deps)}]");
        }

        output.WriteLine();
        output.WriteLine("== Cycle detection ==");
        try { Plan(LoadTargets("BUILD_cycle.star")); }
        catch (GraphException e) { output.WriteLine("error: " + e.Message); }

        output.WriteLine();
        output.WriteLine("== Script error with snippet ==");
        try { LoadTargets("BUILD_bad.star"); }
        catch (StarlarkException e) { output.WriteLine(e.Message.TrimEnd()); }
    }
}
