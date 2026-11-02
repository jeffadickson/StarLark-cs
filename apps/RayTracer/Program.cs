using System.Diagnostics;
using System.Reflection;
using Starlark.Eval;
using Starlark.Eval.Futures;
using Starlark.Syntax;

namespace RayTracer;

/// <summary>
/// Renders a scene described in Starlark, one row per task, using <c>parallel_map</c>. The host runs the same script
/// with 1, 2, 4 and 8 workers, checks that the picture is byte-for-byte identical every time, and reports the speed-up.
/// </summary>
public static class Program
{
    /// <summary>Runs the demo. Pass <c>--quick</c> for a small picture (used by the tests).</summary>
    public static int Main(string[] args) => Run(Console.Out, args.Contains("--quick") ? (48, 20) : (96, 40));

    /// <summary>Renders the picture and prints it with timings. Returns 0 if every worker count gave the same picture.</summary>
    public static int Run(TextWriter output, (int Width, int Height) size, int[]? workerCounts = null)
    {
        workerCounts ??= [1, 2, 4, 8];
        string source = Read("render.star") ?? throw new InvalidOperationException("render.star is missing");

        string? reference = null;
        bool identical = true;
        double baseline = 0;
        output.WriteLine($"Ray tracing {size.Width}x{size.Height} in Starlark, one row per task");
        output.WriteLine();

        foreach (int workers in workerCounts)
        {
            var it = new Interpreter
            {
                Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableFutures = true },
                MaxSteps = 2_000_000_000,
                Timeout = TimeSpan.FromMinutes(2),
                FutureLimits = new FutureLimits { MaxConcurrency = workers, MaxLiveFutures = 1_000, SpawnStepCost = 0 },
            };
            var module = it.Execute("render.star", source);

            var clock = Stopwatch.StartNew();
            var rows = (StarList)it.CallValue(module.Globals["render"], [new System.Numerics.BigInteger(size.Width), new System.Numerics.BigInteger(size.Height)])!;
            double seconds = clock.Elapsed.TotalSeconds;
            string picture = string.Join("\n", rows.Items.Select(r => (string)r!));

            if (reference is null)
            {
                reference = picture;
                baseline = seconds;
                output.WriteLine(picture);
                output.WriteLine();
            }
            else if (picture != reference)
            {
                identical = false;
            }

            var stats = it.LastRunStats;
            output.WriteLine($"{workers,2} worker(s): {seconds,6:F2}s   speed-up {baseline / seconds,4:F1}x   tasks {stats?.Spawns,4}   peak parallel {stats?.PeakConcurrency}   identical picture: {picture == reference}");
        }

        output.WriteLine();
        output.WriteLine(identical
            ? "Every worker count produced exactly the same picture."
            : "MISMATCH: the picture depended on the number of workers!");
        return identical ? 0 : 1;
    }

    private static string? Read(string name)
    {
        var asm = typeof(Program).Assembly;
        var resource = asm.GetManifestResourceNames().FirstOrDefault(n => n.Replace((char)92, '/') == "scripts/" + name);
        if (resource is null) return null;
        using var s = asm.GetManifestResourceStream(resource)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
