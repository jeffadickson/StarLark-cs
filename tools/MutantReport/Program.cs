using System.Text.Json;

// Summarises a Stryker.NET report so survivors can be read at a glance.
//
//   MutantReport [report.html | StrykerOutput-dir] [--file Methods.cs] [--brief] [--limit 40]
//
// With no path it uses the newest report under ./StrykerOutput. The HTML report embeds the full data as JSON, so no
// extra reporter has to be enabled. Output: one score line per file, then the undetected mutants grouped by kind with
// the source line, so the weak spots show up without scrolling a 100-line list.

string? path = args.FirstOrDefault(a => !a.StartsWith("--") && !IsOptionValue(a));
string? fileFilter = Option("file");
bool brief = args.Contains("--brief");
int limit = int.TryParse(Option("limit"), out var l) ? l : 60;

string report = Resolve(path);
Console.WriteLine($"report: {report}");

var html = File.ReadAllText(report);
int start = html.IndexOf("app.report = ", StringComparison.Ordinal);
if (start < 0)
{
    Console.Error.WriteLine("no embedded report data found");
    return 2;
}
var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(html[(start + "app.report = ".Length)..]), isFinalBlock: false, default);
using var doc = JsonDocument.ParseValue(ref reader);

var detected = new[] { "Killed", "Timeout" };
var undetected = new[] { "Survived", "NoCoverage" };

foreach (var file in doc.RootElement.GetProperty("files").EnumerateObject())
{
    string name = Path.GetFileName(file.Name.Replace('\\', '/'));
    if (fileFilter is not null && !name.Equals(fileFilter, StringComparison.OrdinalIgnoreCase)) continue;
    var source = file.Value.GetProperty("source").GetString()!.Replace("\r", "").Split('\n');
    var mutants = file.Value.GetProperty("mutants").EnumerateArray()
        .Select(m => (Status: m.GetProperty("status").GetString()!, Kind: m.GetProperty("mutatorName").GetString()!,
            Line: m.GetProperty("location").GetProperty("start").GetProperty("line").GetInt32(),
            Replacement: m.TryGetProperty("replacement", out var r) ? r.GetString() ?? "" : ""))
        .ToList();
    int killed = mutants.Count(m => detected.Contains(m.Status));
    var bad = mutants.Where(m => undetected.Contains(m.Status)).ToList();
    if (killed + bad.Count == 0) continue;
    double score = 100.0 * killed / (killed + bad.Count);
    Console.WriteLine($"\n{name}: {score:F1}%  killed={killed} survived={bad.Count(m => m.Status == "Survived")} nocoverage={bad.Count(m => m.Status == "NoCoverage")}");
    if (bad.Count == 0 || fileFilter is null && !brief && bad.Count > 200) continue;

    foreach (var group in bad.GroupBy(m => Kind(m.Kind)).OrderByDescending(g => g.Count()))
    {
        Console.WriteLine($"  {group.Key}: {group.Count()}");
        if (brief) continue;
        foreach (var m in group.OrderBy(m => m.Line).Take(limit))
        {
            string line = source[m.Line - 1].Trim();
            string tag = m.Status == "NoCoverage" ? "uncovered" : "survived ";
            Console.WriteLine($"    L{m.Line,-5} {tag} {Clip(line, 90)}");
        }
    }
}
return 0;

string? Option(string name)
{
    int i = Array.IndexOf(args, "--" + name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

bool IsOptionValue(string a)
{
    int i = Array.IndexOf(args, a);
    return i > 0 && args[i - 1] is "--file" or "--limit";
}

static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "...";

// Group the dozens of Stryker mutator names into a few kinds that suggest what test is missing.
static string Kind(string mutator) => mutator switch
{
    "String mutation" => "message/string text not asserted",
    "Statement mutation" => "statement removed (often a missing argument/validity check)",
    var m when m.StartsWith("Equality") || m.StartsWith("Conditional") => "boundary or branch not pinned",
    var m when m.StartsWith("Arithmetic") || m.StartsWith("Linq") => "arithmetic or query not pinned",
    var m when m.StartsWith("Logical") || m.StartsWith("Boolean") => "logic not pinned",
    _ => mutator,
};

static string Resolve(string? p)
{
    if (p is not null && File.Exists(p)) return p;
    string dir = p ?? "StrykerOutput";
    var newest = Directory.GetFiles(dir, "mutation-report.html", SearchOption.AllDirectories)
        .OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
    return newest ?? throw new FileNotFoundException($"no mutation-report.html under {dir}");
}
