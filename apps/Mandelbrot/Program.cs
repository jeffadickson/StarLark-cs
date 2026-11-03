using System.Diagnostics;
using System.Numerics;
using Starlark.Eval;
using Starlark.Eval.Futures;
using Starlark.Syntax;

namespace Mandelbrot;

/// <summary>A named window onto the complex plane.</summary>
/// <param name="Name">The name used on the command line.</param>
/// <param name="CenterX">Real part of the centre.</param>
/// <param name="CenterY">Imaginary part of the centre.</param>
/// <param name="Span">Width of the window in the plane.</param>
/// <param name="Cycle">How many iterations one trip round the colour cycle takes (a smaller number gives tighter bands).</param>
public sealed record View(string Name, double CenterX, double CenterY, double Span, double Cycle)
{
    /// <summary>The views shipped with the demo.</summary>
    public static readonly IReadOnlyList<View> All =
    [
        new("full", -0.6, 0.0, 3.2, 48),
        new("seahorse", -0.7453, 0.1127, 0.0105, 40),
        new("spiral", -0.7435, 0.1314, 0.0062, 36),
        new("elephant", 0.2755, 0.0, 0.07, 40),
    ];
}

/// <summary>
/// Draws the Mandelbrot set from a Starlark script, one text row per task with <c>parallel_map</c>, in 24-bit colour.
/// The host runs the same script with 1, 2, 4 and 8 workers, checks that the picture is identical every time, and
/// reports the speed-up.
/// </summary>
public static class Program
{
    /// <summary>Runs the demo. Options: <c>--view name</c>, <c>--quick</c>, <c>--plain</c> (no colour), <c>--list</c>.</summary>
    public static int Main(string[] args)
    {
        if (args.Contains("--list"))
        {
            foreach (var v in View.All) Console.WriteLine($"{v.Name,-10} centre ({v.CenterX}, {v.CenterY}) span {v.Span}");
            return 0;
        }
        string name = args.SkipWhile(a => a != "--view").Skip(1).FirstOrDefault() ?? "full";
        var view = View.All.FirstOrDefault(v => v.Name == name);
        if (view is null)
        {
            Console.Error.WriteLine($"unknown view '{name}'; try --list");
            return 2;
        }
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var size = args.Contains("--quick") ? (60, 22) : (110, 38);
        return Run(Console.Out, size, view, ansi: !args.Contains("--plain"));
    }

    /// <summary>Renders the picture and prints it with timings. Returns 0 if every worker count gave the same picture.</summary>
    public static int Run(TextWriter output, (int Width, int Height) size, View view, bool ansi, int[]? workerCounts = null)
    {
        workerCounts ??= [1, 2, 4, 8];
        string source = Read("mandelbrot.star") ?? throw new InvalidOperationException("mandelbrot.star is missing");

        string? reference = null;
        bool identical = true;
        double baseline = 0;
        output.WriteLine($"Mandelbrot set, view '{view.Name}', {size.Width}x{size.Height * 2} pixels in Starlark, one row per task");
        output.WriteLine();

        foreach (int workers in workerCounts)
        {
            var it = new Interpreter
            {
                Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableFutures = true },
                MaxSteps = 4_000_000_000,
                Timeout = TimeSpan.FromMinutes(5),
                FutureLimits = new FutureLimits { MaxConcurrency = workers, MaxLiveFutures = 1_000, SpawnStepCost = 0 },
            };
            var module = it.Execute("mandelbrot.star", source);

            var clock = Stopwatch.StartNew();
            var rows = (StarList)it.CallValue(module.Globals["render"],
                [new BigInteger(size.Width), new BigInteger(size.Height), view.CenterX, view.CenterY, view.Span, ansi, view.Cycle])!;
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
