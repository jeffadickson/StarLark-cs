using System.Diagnostics;
using Starlark.Eval;
using Starlark.Syntax;

// In-process timing of starlark-cs on the shared workloads in bench/RustCompare/workloads, the C# half of the
// starlark-rust comparison (QA-PERF-002). Output format matches bench/RustCompare (tab-separated).
//
//   Starlark.RustCompare [--dir path] [--seconds 3] [--workload name] [--once name]
//
// Each run: a fresh Interpreter, then Execute (parse, resolve, execute) of the source, the same unit of work the Rust
// harness times (AstModule::parse + eval_module in a fresh Module). `--once name` runs one workload once and exits,
// for cold-start process timing.

string? Get(string n) { int i = Array.IndexOf(args, "--" + n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
string dir = Get("dir") ?? Path.Combine("bench", "RustCompare", "workloads");
double seconds = double.TryParse(Get("seconds"), System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 3;
string? only = Get("workload");
string[] names = ["loops", "calls", "strings", "lists", "dicts", "records", "closures", "bigints", "floats", "parse", "quicksort", "config", "textproc"];
var dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableTypes = DialectTypes.Enable };

string Load(string n) => n == "parse" ? ParseSource() : File.ReadAllText(Path.Combine(dir, n + ".star"));
Interpreter Fresh() => new() { Dialect = dialect };

string RunOnce(string n, string src)
{
    if (n == "parse") { AstModule.Parse("parse", src, dialect); return "-"; }
    var m = Fresh().Execute(n, src);
    return m.TryGet("r", out var r) ? Repr.Of(r) : "";
}

if (Get("once") is { } one)
{
    try { RunOnce(one, Load(one)); return 0; }
    catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
}

Console.WriteLine("workload\tmedian_ms\tp10_ms\tp90_ms\tmin_ms\truns\tresult");
foreach (var n in names)
{
    if (only is not null && only != n) continue;
    var src = Load(n);
    string result;
    try { result = RunOnce(n, src); }
    catch (Exception e) { Console.WriteLine($"{n}\tERROR\t{e.Message.ReplaceLineEndings(" ")}"); continue; }
    for (int i = 0; i < 12; i++) RunOnce(n, src);
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    var times = new List<double>();
    var total = Stopwatch.StartNew();
    while (total.Elapsed.TotalSeconds < seconds || times.Count < 5)
    {
        long t0 = Stopwatch.GetTimestamp();
        RunOnce(n, src);
        times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
    }
    times.Sort();
    double Q(double p) => times[(int)Math.Round((times.Count - 1) * p)];
    Console.WriteLine(FormattableString.Invariant($"{n}\t{Q(0.5):F4}\t{Q(0.1):F4}\t{Q(0.9):F4}\t{times[0]:F4}\t{times.Count}\t{result}"));
}
return 0;

static string ParseSource()
{
    var sb = new System.Text.StringBuilder();
    for (int i = 0; i < 100; i++)
        sb.Append($"def fn{i}(a, b = {i}, *rest, **kw):\n    x = [a + k for k in range(b) if k % 2 == 0]\n    d = {{'a': a, 'b': [b, {i}]}}\n    return len(x) + d['a']\n");
    return sb.ToString();
}
