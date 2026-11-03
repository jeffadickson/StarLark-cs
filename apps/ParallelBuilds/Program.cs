using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Starlark.Eval;
using Starlark.Eval.Futures;
using Starlark.Syntax;

namespace ParallelBuilds;

/// <summary>The answer a build gives the script: <c>r.name</c>, <c>r.ok</c>, <c>r.millis</c>, <c>r.log</c>.</summary>
/// <param name="Name">The component built.</param>
/// <param name="Ok">Whether the build succeeded.</param>
/// <param name="Millis">How long it took, in milliseconds.</param>
/// <param name="Log">A one-line summary of the output.</param>
public sealed record BuildResult(string Name, bool Ok, int Millis, string Log);

/// <summary>One finished build, for the timeline.</summary>
/// <param name="Name">The component.</param>
/// <param name="StartMs">When it started, relative to the start of the scenario.</param>
/// <param name="EndMs">When it finished.</param>
public sealed record Span(string Name, long StartMs, long EndMs);

/// <summary>What one scenario cost.</summary>
/// <param name="Name">What was run.</param>
/// <param name="Seconds">Wall-clock time.</param>
/// <param name="PeakHostOperations">The most builds in flight at once.</param>
/// <param name="HostCalls">How many builds were started.</param>
public sealed record Outcome(string Name, double Seconds, int PeakHostOperations, long HostCalls);

/// <summary>
/// The common use of futures in a Starlark program: wait on several slow things at once. The host provides
/// <c>run_build</c>, a function that really takes time (it awaits a delay standing in for a compiler), registered
/// <c>asFuture</c> so a script call starts the build and returns a future instead of waiting. The same script is run
/// serially, in parallel, with a broken build, and with more builds than may run at once, and the host prints a timeline
/// of when each build ran.
/// </summary>
public static class Program
{
    /// <summary>Runs the demo. <c>--quick</c> shortens the builds (used by the tests).</summary>
    public static int Main(string[] args) => Run(Console.Out, args.Contains("--quick") ? 0.1 : 1.0).Count == 5 ? 0 : 1;

    /// <summary>
    /// Runs all five scenarios and writes the story to <paramref name="output"/>.
    /// </summary>
    /// <param name="output">Where to write.</param>
    /// <param name="scale">Multiplier on every build time (1.0 is the real demo).</param>
    /// <returns>One outcome per scenario: serial, parallel, good release, broken release, many.</returns>
    public static IReadOnlyList<Outcome> Run(TextWriter output, double scale)
    {
        var spans = new ConcurrentBag<Span>();
        var clock = Stopwatch.StartNew();
        long origin = 0;

        var it = new Interpreter
        {
            Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableFutures = true },
            Print = output.WriteLine,
            MaxSteps = 10_000_000,
            Timeout = TimeSpan.FromMinutes(1),
            FutureLimits = new FutureLimits { MaxHostOperations = 4 },
        };

        // The build itself: really waits, then reports. The "compiler" is a delay, so the demo needs no toolchain.
        it.Register("run_build", async (string name, int millis, bool fail) =>
        {
            long start = clock.ElapsedMilliseconds - origin;
            await Task.Delay((int)(millis * scale));
            long end = clock.ElapsedMilliseconds - origin;
            spans.Add(new Span(name, start, end));
            return new BuildResult(name, !fail, (int)(end - start), fail ? "error: cannot find symbol Widget" : $"built {name}.dll");
        }, threadSafe: true, asFuture: true);

        var module = it.Execute("pipeline.star", Scripts.Read("pipeline.star") ?? throw new InvalidOperationException("pipeline.star is missing"));

        var outcomes = new List<Outcome>();
        void Scenario(string title, string function)
        {
            output.WriteLine(title);
            while (spans.TryTake(out _)) { }
            origin = clock.ElapsedMilliseconds;
            var sw = Stopwatch.StartNew();
            it.CallValue(module.Globals[function], []);
            double seconds = sw.Elapsed.TotalSeconds;
            var stats = it.LastRunStats;
            outcomes.Add(new Outcome(function, seconds, stats?.PeakHostOperations ?? 0, stats?.HostCalls ?? 0));
            output.WriteLine(Timeline(spans.OrderBy(s => s.StartMs).ThenBy(s => s.Name).ToList(), seconds));
            output.WriteLine($"  total {seconds,5:F2}s   builds started {stats?.HostCalls}   most in flight at once {stats?.PeakHostOperations}");
            output.WriteLine();
        }

        output.WriteLine("Waiting on three builds, with and without futures");
        output.WriteLine("(core 0.7s, web 1.1s, docs 0.5s; times below scale with the demo setting)");
        output.WriteLine();
        Scenario("1. One after another: get() straight after each start", "serial");
        Scenario("2. All at once: start all three, then all_of()", "parallel");
        Scenario("3. A release: build in parallel, package only if all three succeed", "good_release");
        Scenario("4. The same release with a broken web build: packaging is skipped", "broken_release");
        Scenario("5. Twelve builds, host cap of four in flight: the script works in batches of four", "many");

        double serial = outcomes[0].Seconds, parallel = outcomes[1].Seconds;
        output.WriteLine($"Serial {serial:F2}s, parallel {parallel:F2}s: {serial / parallel:F1}x faster, by overlapping the waiting. The script is the same plain program either way.");
        return outcomes;
    }

    /// <summary>A bar chart of when each build ran: the left edge is its start, the right edge its end.</summary>
    internal static string Timeline(IReadOnlyList<Span> spans, double totalSeconds)
    {
        const int width = 56;
        double scale = width / Math.Max(1.0, totalSeconds * 1000);
        var sb = new StringBuilder();
        foreach (var s in spans)
        {
            int from = Math.Min(width - 1, (int)(s.StartMs * scale));
            int to = Math.Max(from + 1, Math.Min(width, (int)(s.EndMs * scale)));
            sb.Append($"  {s.Name,-8}|{new string(' ', from)}{new string('#', to - from)}{new string(' ', width - to)}| {s.StartMs,5} to {s.EndMs,5} ms\n");
        }
        return sb.ToString().TrimEnd('\n');
    }
}
