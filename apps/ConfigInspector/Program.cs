using System.Globalization;
using Starlark.Eval;
using Starlark.Syntax;

namespace ConfigInspector;

/// <summary>The typed configuration the host application consumes.</summary>
public sealed record AppConfig(string Name, int Replicas, string LogLevel, double TimeoutS, Dictionary<string, bool> Features, List<string> Regions);

/// <summary>Layered, validated configuration loading with static type checks.</summary>
public static class Program
{
    /// <summary>Entry point. Pass <c>--check [env...]</c> to exit non-zero when any environment is invalid.</summary>
    public static int Main(string[] args) => Run(Console.Out, args);

    private static Interpreter New() => new()
    {
        Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableTypes = DialectTypes.Enable },
        SourceLoader = Scripts.Read,
        Print = _ => { },
        MaxSteps = 1_000_000,
    };

    /// <summary>
    /// Typechecks then loads one environment. Returns the config, or null after writing the problems to
    /// <paramref name="output"/> with source snippets.
    /// </summary>
    public static AppConfig? Load(string env, TextWriter output)
    {
        var file = env + ".star";
        var interp = New();
        var check = interp.Typecheck(file, Scripts.Read(file)!);
        if (!check.Ok)
        {
            foreach (var d in check.Errors) output.WriteLine(d.Render().TrimEnd());
            return null;
        }
        try
        {
            var module = interp.Execute(file, Scripts.Read(file)!);
            return (AppConfig)Interop.FromStar(module.Globals["config"], typeof(AppConfig))!;
        }
        catch (StarlarkException e)
        {
            output.WriteLine(e.Message.TrimEnd());
            return null;
        }
    }

    /// <summary>Loads every environment, prints the results and returns the process exit code.</summary>
    public static int Run(TextWriter output, string[] args)
    {
        bool check = args.Contains("--check");
        // Without --check the demo shows the broken overlay too; with it, only the real environments are validated.
        var named = args.Where(a => !a.StartsWith("--")).ToArray();
        var envs = named.Length > 0 ? named : check ? new[] { "dev", "prod" } : new[] { "dev", "prod", "broken" };
        int failures = 0;
        foreach (var env in envs)
        {
            output.WriteLine($"== {env} ==");
            var cfg = Load(env, output);
            if (cfg is null) { failures++; output.WriteLine($"{env}: INVALID"); continue; }
            output.WriteLine($"name={cfg.Name} replicas={cfg.Replicas} log={cfg.LogLevel} timeout={cfg.TimeoutS.ToString("F1", CultureInfo.InvariantCulture)}s");
            output.WriteLine("regions=" + string.Join(",", cfg.Regions));
            output.WriteLine("features=" + string.Join(", ", cfg.Features.OrderBy(f => f.Key).Select(f => $"{f.Key}:{(f.Value ? "on" : "off")}")));
        }
        output.WriteLine(failures == 0 ? "check passed" : $"check failed: {failures} environment(s) invalid");
        return check && failures > 0 ? 1 : 0;
    }
}
