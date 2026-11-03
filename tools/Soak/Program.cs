using System.Diagnostics;
using System.Text.Json;

// Long-running soak: runs the seeded fuzzers and the Hedgehog/FsCheck properties over and over with fresh seeds,
// for a fixed duration, and keeps everything needed to reproduce a failure.
//
//   Soak --tests <Starlark.Fuzz.Tests.dll> [--out soak-out] [--start-at HH:mm] [--hours 10] [--workers 4]
//        [--iterations 200000] [--property-tests 5000] [--round-minutes 30] [--resume]
//        [--child-gb 3] [--min-free-gb 4] [--min-disk-gb 10]
//
// Each round is one `dotnet test` process with its own STARLARK_FUZZ_SEED range, so a hung or runaway input kills only
// that round. A round that exceeds --round-minutes is killed and recorded as a hang. Failing rounds keep their full
// log under <out>/failures. A checkpoint is written after every round so --resume continues where it stopped.
//
// Guardrails, so an unattended run cannot take the machine down: every child gets a heap cap (--child-gb) and is killed
// and recorded as a MEMORY failure if its working set passes it; if free memory on the machine falls below --min-free-gb or
// free disk on the output drive below --min-disk-gb, the biggest child is killed and new rounds wait until there is room.

var opt = Options.Parse(args);
Directory.CreateDirectory(opt.Out);
Directory.CreateDirectory(Path.Combine(opt.Out, "failures"));
Directory.CreateDirectory(Path.Combine(opt.Out, "rounds"));

var checkpointPath = Path.Combine(opt.Out, "checkpoint.json");
var state = opt.Resume && File.Exists(checkpointPath)
    ? JsonSerializer.Deserialize<State>(File.ReadAllText(checkpointPath)) ?? new State()
    : new State();

if (opt.StartAt is { } startAt)
{
    var now = DateTime.Now;
    var start = now.Date + startAt;
    if (start < now) start = start.AddDays(1);
    Log($"waiting until {start:yyyy-MM-dd HH:mm} to start");
    while (DateTime.Now < start) Thread.Sleep(TimeSpan.FromSeconds(Math.Min(30, (start - DateTime.Now).TotalSeconds + 0.1)));
}

var deadline = DateTime.Now.AddHours(opt.Hours) - TimeSpan.FromSeconds(state.ElapsedSeconds);
Log($"soak started; deadline {deadline:yyyy-MM-dd HH:mm}; {opt.Workers} worker(s); out={opt.Out}");

var gate = new object();
int faultStreak = 0;   // consecutive rounds that died instantly with no output; see RunRound
long nextSeed = state.NextSeed;
var peakMemory = 0L;
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Log("stop requested; finishing current rounds"); };

var progress = Task.Run(async () =>
{
    while (!cts.IsCancellationRequested && DateTime.Now < deadline)
    {
        await Task.Delay(TimeSpan.FromMinutes(1));
        lock (gate)
        {
            Log($"rounds={state.Rounds} failures={state.Failures} hangs={state.Hangs} nextSeed={nextSeed} peakChildMB={peakMemory / 1048576} remaining={(deadline - DateTime.Now):hh\\:mm\\:ss}");
        }
    }
});

var workers = Enumerable.Range(0, opt.Workers).Select(w => Task.Run(() =>
{
    while (!cts.IsCancellationRequested && DateTime.Now < deadline)
    {
        long seed;
        lock (gate)
        {
            seed = nextSeed;
            nextSeed += 1_000_000;   // each round owns a seed block, so rounds never repeat each other
        }
        RunRound(seed, w);
    }
})).ToArray();

Task.WaitAll(workers);
state.ElapsedSeconds = (long)(opt.Hours * 3600 - Math.Max(0, (deadline - DateTime.Now).TotalSeconds));
Save();
Log($"soak finished: rounds={state.Rounds} failures={state.Failures} hangs={state.Hangs}");
return state.Failures + state.Hangs == 0 ? 0 : 1;

void RunRound(long seed, int worker)
{
    var psi = new ProcessStartInfo("dotnet", $"test \"{opt.Tests}\" --no-build")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    psi.Environment["DOTNET_GCHeapHardLimit"] = ((long)(opt.ChildGb * 1073741824)).ToString("X");   // hex, as the runtime expects
    psi.Environment["STARLARK_FUZZ_SEED"] = seed.ToString();
    psi.Environment["STARLARK_FUZZ_ITERATIONS"] = opt.Iterations.ToString();
    psi.Environment["STARLARK_PROPERTY_TESTS"] = opt.PropertyTests.ToString();

    WaitForRoom();
    var log = new System.Text.StringBuilder();
    using var p = new Process { StartInfo = psi };
    p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
    p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
    var started = DateTime.Now;
    p.Start();
    p.BeginOutputReadLine();
    p.BeginErrorReadLine();

    bool hung = false, overMemory = false;
    var limit = TimeSpan.FromMinutes(opt.RoundMinutes);
    while (!p.WaitForExit(5000))
    {
        long used = TrackMemory(p);
        if (used > (long)(opt.ChildGb * 1073741824 * 1.1) || LowOnResources(out _))
        {
            overMemory = true;
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            break;
        }
        if (DateTime.Now - started > limit || (DateTime.Now > deadline + TimeSpan.FromMinutes(opt.RoundMinutes)))
        {
            hung = true;
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            break;
        }
    }
    p.WaitForExit();
    string text;
    lock (log) text = log.ToString();
    int exitCode = p.ExitCode;
    bool failed = !hung && (overMemory || exitCode != 0);

    // A round that dies at once with no output at all did not test anything: the machine or the test host broke under it
    // (the process was killed, the SDK was updated, ...). That is an environment fault, not a failure of the code under
    // test, and it must not be counted as one or burn through seeds. Several in a row stop the soak with a clear message.
    bool environmentFault = failed && !overMemory && text.Trim().Length == 0 && DateTime.Now - started < TimeSpan.FromSeconds(10);
    if (environmentFault)
    {
        int streak = Interlocked.Increment(ref faultStreak);
        Log($"ENVIRONMENT FAULT round seed={seed}: exit code {exitCode} after {(DateTime.Now - started).TotalSeconds:F1}s with no output ({streak} in a row)");
        if (streak >= 5)
        {
            Log("five rounds in a row died instantly with no output; stopping. Fix the environment, then restart with --resume.");
            cts.Cancel();
        }
        lock (gate) { nextSeed = Math.Min(nextSeed, seed); }   // the seed block was never tested, so hand it out again
        Thread.Sleep(TimeSpan.FromSeconds(10));
        return;
    }
    Interlocked.Exchange(ref faultStreak, 0);

    lock (gate)
    {
        state.Rounds++;
        if (hung) state.Hangs++;
        else if (failed) state.Failures++;
        state.NextSeed = nextSeed;
        state.ElapsedSeconds = (long)(opt.Hours * 3600 - Math.Max(0, (deadline - DateTime.Now).TotalSeconds));
        Save();
    }
    string name = $"seed-{seed}-w{worker}";
    if (hung || failed)
    {
        File.WriteAllText(Path.Combine(opt.Out, "failures", name + (hung ? "-HANG" : overMemory ? "-MEMORY" : "") + ".log"),
            $"seed={seed} iterations={opt.Iterations} propertyTests={opt.PropertyTests} hung={hung} overMemory={overMemory} exitCode={exitCode}\n{text}");
        Log($"{(hung ? "HANG" : overMemory ? "MEMORY" : "FAIL")} round seed={seed} (log kept in failures/)");
    }
    else
    {
        File.WriteAllText(Path.Combine(opt.Out, "rounds", name + ".log"), text.Length > 4000 ? text[^4000..] : text);
    }
}

// Memory of this round's biggest process under the round's process tree. It must never throw: the machine being short of memory
// is exactly when this runs, and an exception here once took the whole soak down.
long TrackMemory(Process p)
{
    long bytes = 0;
    try
    {
        p.Refresh();
        bytes = p.WorkingSet64;
        foreach (var child in Process.GetProcessesByName("testhost"))
        {
            try { bytes = Math.Max(bytes, child.WorkingSet64); } catch (Exception) { }
            finally { child.Dispose(); }
        }
        lock (gate) peakMemory = Math.Max(peakMemory, bytes);
    }
    catch (Exception)
    {
    }
    return bytes;
}

// True when the machine is short of memory or the output drive is short of disk. Never throws.
bool LowOnResources(out string why)
{
    why = "";
    try
    {
        var info = GC.GetGCMemoryInfo();
        double freeGb = (info.TotalAvailableMemoryBytes - info.MemoryLoadBytes) / 1073741824.0;
        if (freeGb < opt.MinFreeGb) { why = $"only {freeGb:F1} GB of memory free"; return true; }
        double diskGb = new DriveInfo(Path.GetPathRoot(opt.Out)!).AvailableFreeSpace / 1073741824.0;
        if (diskGb < opt.MinDiskGb) { why = $"only {diskGb:F1} GB of disk free"; return true; }
    }
    catch (Exception)
    {
    }
    return false;
}

// Block a new round until the machine has room; gives up (and stops the soak) after an hour.
void WaitForRoom()
{
    var waited = Stopwatch.StartNew();
    while (LowOnResources(out var why))
    {
        if (waited.Elapsed.TotalSeconds < 1) Log($"waiting for room: {why}");
        if (waited.Elapsed > TimeSpan.FromHours(1)) { Log("no room after an hour; stopping"); cts.Cancel(); return; }
        Thread.Sleep(TimeSpan.FromSeconds(30));
    }
}

void Save()
{
    File.WriteAllText(checkpointPath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
}

static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

/// <summary>Progress that survives a restart.</summary>
sealed class State
{
    public long NextSeed { get; set; } = 1;
    public int Rounds { get; set; }
    public int Failures { get; set; }
    public int Hangs { get; set; }
    public long ElapsedSeconds { get; set; }
}

/// <summary>Command-line options.</summary>
sealed record Options(string Tests, string Out, TimeSpan? StartAt, double Hours, int Workers, int Iterations,
    int PropertyTests, double RoundMinutes, bool Resume, double ChildGb, double MinFreeGb, double MinDiskGb)
{
    public static Options Parse(string[] args)
    {
        string Get(string name, string fallback)
        {
            int i = Array.IndexOf(args, "--" + name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }
        string tests = Get("tests", "");
        if (tests.Length == 0 || !File.Exists(tests))
        {
            Console.Error.WriteLine("usage: Soak --tests <path to Starlark.Fuzz.Tests.dll> [--out dir] [--start-at HH:mm] [--hours 10] [--workers 4] [--iterations 200000] [--property-tests 5000] [--round-minutes 30] [--resume] [--child-gb 3] [--min-free-gb 4] [--min-disk-gb 10]");
            Environment.Exit(2);
        }
        string startAt = Get("start-at", "");
        return new Options(
            Path.GetFullPath(tests),
            Path.GetFullPath(Get("out", "soak-out")),
            startAt.Length == 0 ? null : TimeSpan.Parse(startAt),
            double.Parse(Get("hours", "10")),
            int.Parse(Get("workers", "4")),
            int.Parse(Get("iterations", "200000")),
            int.Parse(Get("property-tests", "5000")),
            double.Parse(Get("round-minutes", "30")),
            args.Contains("--resume"),
            double.Parse(Get("child-gb", "3")),
            double.Parse(Get("min-free-gb", "4")),
            double.Parse(Get("min-disk-gb", "10")));
    }
}
