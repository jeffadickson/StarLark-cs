using Starlark.Eval;
using Starlark.Syntax;

namespace Examples;

/// <summary>One service as the deployment tool needs it, read back from the configuration script.</summary>
/// <param name="Name">Service name.</param>
/// <param name="Image">Container image, including the tag.</param>
/// <param name="Replicas">How many instances.</param>
/// <param name="Port">The port it listens on.</param>
/// <param name="Env">Environment variables.</param>
public sealed record ServiceSpec(string Name, string Image, int Replicas, int Port, Dictionary<string, string> Env);

/// <summary>The outcome of loading one environment's configuration: the services, or the errors, never both.</summary>
/// <param name="Services">The services, when loading worked.</param>
/// <param name="Errors">What is wrong with the configuration, each with a file and line.</param>
public sealed record ConfigResult(IReadOnlyList<ServiceSpec> Services, IReadOnlyList<string> Errors)
{
    /// <summary>True when the configuration loaded and validated.</summary>
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// Example 2: deployment configuration written in Starlark instead of YAML.
///
/// A configuration is a program, so environments share defaults through functions and <c>load()</c> instead of copy and paste,
/// but it is hermetic: the files cannot read the disk, the environment variables, the network or the clock, so the same files
/// always produce the same deployment, on a laptop and in CI. The host reads the result back into typed C# records with
/// <see cref="Interpreter.Call{T}"/>-style conversion, validates it, and reports every problem with a file and line.
///
/// Files are looked up only in the configuration folder the host chose (here, embedded resources), so <c>load("../../secrets")</c>
/// finds nothing.
/// </summary>
public static class ConfigLoader
{
    /// <summary>Loads <c>config/&lt;environment&gt;.star</c>, which must define <c>services()</c> returning a list of structs.</summary>
    public static ConfigResult Load(string environment, Func<string, string?> files)
    {
        var it = new Interpreter
        {
            Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableFStrings = true },
            // Only names inside the configuration folder resolve. Anything else (an absolute path, "..") is simply not found.
            SourceLoader = name => name.Contains("..") || Path.IsPathRooted(name) ? null : files(name),
            MaxSteps = 1_000_000,
            Timeout = TimeSpan.FromSeconds(2),
        };
        var source = files(environment + ".star");
        if (source is null) return new ConfigResult([], [$"no configuration named {environment}"]);
        try
        {
            var module = it.Execute(environment + ".star", source);
            if (!module.TryGet("services", out var fn) || fn is not ICallable)
                return new ConfigResult([], [$"{environment}.star must define services()"]);
            if (it.CallValue(fn, []) is not StarList list)
                return new ConfigResult([], ["services() must return a list"]);

            var services = new List<ServiceSpec>();
            var errors = new List<string>();
            var ports = new List<(string Name, int Port)>();
            foreach (var item in list.Items)
            {
                if (item is not StarStruct s) { errors.Add("each service must be a struct(...)"); continue; }
                var spec = ToSpec(s, errors, ports);
                if (spec is not null) services.Add(spec);
            }
            // Cross-service rules belong to the host: they are policy, not configuration. They are checked over every service that
            // declared a valid port, including services with other mistakes, so the author sees every problem in one pass.
            foreach (var clash in ports.GroupBy(x => x.Port).Where(g => g.Count() > 1))
                errors.Add($"port {clash.Key} is used by {string.Join(" and ", clash.Select(x => x.Name))}");
            return errors.Count == 0 ? new ConfigResult(services, []) : new ConfigResult([], errors);
        }
        catch (Exception e) when (e is StarlarkException or StarlarkSyntaxException or ExecutionLimitException)
        {
            return new ConfigResult([], [e.Message]);
        }
    }

    /// <summary>
    /// Reads one struct into a <see cref="ServiceSpec"/>, adding a message to <paramref name="errors"/> for each bad field, and the
    /// service's port (when it is valid) to <paramref name="ports"/>. Returns null when any field is wrong.
    /// </summary>
    private static ServiceSpec? ToSpec(StarStruct s, List<string> errors, List<(string Name, int Port)> ports)
    {
        string name = s.TryGet("name", out var n) && n is string ns ? ns : "";
        string Field(string f) => $"service {(name == "" ? "?" : name)}: {f}";
        if (name == "") errors.Add(Field("name must be a non-empty string"));
        string image = s.TryGet("image", out var i) && i is string img ? img : "";
        if (!image.Contains(':')) errors.Add(Field($"image '{image}' must include a tag"));
        int replicas = s.TryGet("replicas", out var r) && r is System.Numerics.BigInteger rb && rb >= 1 && rb <= 100 ? (int)rb : -1;
        if (replicas < 0) errors.Add(Field("replicas must be an int from 1 to 100"));
        int port = s.TryGet("port", out var p) && p is System.Numerics.BigInteger pb && pb >= 1 && pb <= 65535 ? (int)pb : -1;
        if (port < 0) errors.Add(Field("port must be an int from 1 to 65535"));
        else ports.Add((name == "" ? "?" : name, port));
        var env = new Dictionary<string, string>();
        if (s.TryGet("env", out var e) && e is StarDict d)
        {
            foreach (var kv in d.Entries)
            {
                if (kv.Key is string k && kv.Value is string v) env[k] = v;
                else errors.Add(Field("env keys and values must be strings"));
            }
        }
        return name == "" || image == "" || replicas < 0 || port < 0 ? null : new ServiceSpec(name, image, replicas, port, env);
    }
}

/// <summary>Runs example 2 and writes what happens.</summary>
public static class ConfigDemo
{
    /// <summary>Loads the staging and production configurations, then a broken one.</summary>
    public static void Run(TextWriter output)
    {
        output.WriteLine("Example 2: deployment configuration");
        foreach (var env in new[] { "staging", "production", "broken" })
        {
            var result = ConfigLoader.Load(env, name => Scripts.Read("config/" + name));
            if (result.Ok)
            {
                output.WriteLine($"  {env}: {result.Services.Count} services");
                foreach (var s in result.Services)
                    output.WriteLine($"    {s.Name,-8} {s.Image,-22} x{s.Replicas} :{s.Port} {string.Join(" ", s.Env.Select(kv => kv.Key + "=" + kv.Value))}");
            }
            else
            {
                output.WriteLine($"  {env}: {result.Errors.Count} problem(s)");
                foreach (var error in result.Errors) output.WriteLine("    " + error.Split('\n')[0]);
            }
        }
    }
}
