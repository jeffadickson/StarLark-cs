using System.Text.RegularExpressions;

// Requirements-to-tests traceability, checked mechanically.
//
//   Trace [repo root] [--trx <results folder>]
//
// Reads every requirement in docs/requirements/*.sdoc (UID, STATUS, VERIFICATION) and every test in tests/**/*.cs, and reports:
//   - an Implemented requirement with no VERIFICATION line, or whose line names nothing that exists as a test class or method;
//   - names in a VERIFICATION line that match no test class, test method or tool/file in the repository (dangling references);
//   - test classes that no requirement mentions (informational: how much of the suite is traceable to a requirement).
// With --trx it also reads .trx result files and reports whether each named test passed, failed or did not run.
// Exit code 1 if any Implemented requirement is not traceable.

string root = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? Directory.GetCurrentDirectory());
string? trx = args.SkipWhile(a => a != "--trx").Skip(1).FirstOrDefault();

var reqs = new List<Req>();
foreach (var file in Directory.GetFiles(Path.Combine(root, "docs", "requirements"), "*.sdoc"))
{
    Req? cur = null;
    foreach (var raw in File.ReadAllLines(file))
    {
        var line = raw.TrimEnd();
        if (line == "[REQUIREMENT]") { cur = new Req(Path.GetFileNameWithoutExtension(file)); reqs.Add(cur); }
        else if (line.StartsWith("[")) cur = null;   // [SECTION], [/SECTION], [DOCUMENT]: its TITLE is not a requirement's
        else if (cur is null) continue;
        else if (line.StartsWith("UID: ")) cur.Uid = line[5..];
        else if (line.StartsWith("STATUS: ")) cur.Status = line[8..];
        else if (line.StartsWith("TITLE: ")) cur.Title = line[7..];
        else if (line.StartsWith("VERIFICATION: ")) cur.Verification = line[14..];
    }
}

// Every test class and test method in the test projects, and every other file/tool name that a VERIFICATION line may point at.
var classes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
var methodRx = new Regex(@"\[(Fact|Theory)[^\]]*\]\s*(?:\[[^\]]*\]\s*)*public\s+(?:async\s+)?\w+(?:<[^>]*>)?\s+(\w+)\s*\(", RegexOptions.Singleline);
// Only top-level classes (declared at the start of a line): nested helpers and C# held in test strings are not test classes.
var classRx = new Regex(@"^(?:public|internal)\s+(?:sealed\s+|static\s+)*class\s+(\w+)", RegexOptions.Multiline);
var projectOf = new Dictionary<string, string>(StringComparer.Ordinal);   // class -> test project folder name
foreach (var file in Directory.GetFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\")))
{
    string text = File.ReadAllText(file);
    string project = Path.GetRelativePath(Path.Combine(root, "tests"), file).Split(Path.DirectorySeparatorChar)[0];
    foreach (Match c in classRx.Matches(text))
    {
        int start = c.Index;
        int next = classRx.Match(text, start + 1) is { Success: true } n ? n.Index : text.Length;
        string body = text[start..next];
        projectOf[c.Groups[1].Value] = project;
        if (!classes.TryGetValue(c.Groups[1].Value, out var list)) classes[c.Groups[1].Value] = list = [];
        list.AddRange(methodRx.Matches(body).Select(m => m.Groups[2].Value));
        // FsCheck and Hedgehog properties are marked differently; count any public method of a class in the Fuzz project.
        if (file.Contains("Fuzz.Tests")) list.AddRange(Regex.Matches(body, @"public\s+(?:static\s+)?\w+\s+(\w+)\s*\(").Select(m => m.Groups[1].Value));
    }
}
// A class with no tests (helpers, support code) is not a test class.
foreach (var empty in classes.Where(k => k.Value.Count == 0).Select(k => k.Key).ToList()) classes.Remove(empty);
var projects = projectOf.Values.ToHashSet(StringComparer.Ordinal);
var names = new HashSet<string>(classes.Keys.Concat(classes.Values.SelectMany(v => v)), StringComparer.Ordinal);
var repoFiles = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\") && !f.Contains(@"\.git\"))
    .Select(Path.GetFileName).Concat(new[] { "tools", "bench", "apps" }.Where(d => Directory.Exists(Path.Combine(root, d))).SelectMany(d => Directory.GetDirectories(Path.Combine(root, d))).Select(Path.GetFileName)).ToHashSet(StringComparer.OrdinalIgnoreCase);

var passed = new Dictionary<string, string>();
if (trx is not null)
{
    foreach (var f in Directory.GetFiles(trx, "*.trx", SearchOption.AllDirectories))
        foreach (Match m in Regex.Matches(File.ReadAllText(f), @"testName=""([^""]+)""[^>]*outcome=""(\w+)"""))
            passed[m.Groups[1].Value] = m.Groups[2].Value;
}

int untraceable = 0, dangling = 0;
var mentioned = new HashSet<string>();
Console.WriteLine($"{reqs.Count} requirements, {classes.Count} test classes, {names.Count - classes.Count} test methods\n");
foreach (var r in reqs.OrderBy(r => r.Uid, StringComparer.Ordinal))
{
    var tokens = Regex.Matches(r.Verification ?? "", @"[A-Za-z_][A-Za-z0-9_.]*").Select(m => m.Value).Where(t => t.Length > 3).Distinct().ToList();
    var found = new List<string>(); var missing = new List<string>();
    // "dotnet test" runs the whole suite; a test project name stands for every test class in that project.
    if ((r.Verification ?? "").Contains("dotnet test")) found.Add("dotnet test");   // names the suite as a whole; does not count as mentioning any one class
    foreach (var t in tokens)
    {
        if (projects.Contains(t)) { found.Add(t); foreach (var c in classes.Keys.Where(c => projectOf[c] == t)) mentioned.Add(c); continue; }
        // "FuturesTests.SpawnAndGet" names a class and a method; "SpawnAndGet" alone is a method; "stryker-config.json" is a file.
        var parts = t.Split('.');
        bool hit = parts.Any(p => names.Contains(p)) || repoFiles.Contains(t) || repoFiles.Contains(parts[0] + ".json") || parts.Any(p => repoFiles.Contains(p));
        if (hit) { found.Add(t); foreach (var p in parts) mentioned.Add(p); } else if (char.IsUpper(t[0]) && Regex.IsMatch(t, "(Tests|Properties|Test)$|^[A-Z][a-z]+[A-Z]")) missing.Add(t);
    }
    bool implemented = r.Status == "Implemented";
    string verdict = !implemented ? "      " : found.Count == 0 ? "NONE  " : missing.Count > 0 ? "CHECK " : "ok    ";
    if (implemented && found.Count == 0) untraceable++;
    dangling += missing.Count;
    string outcome = "";
    if (trx is not null && found.Count > 0)
    {
        var ran = found.SelectMany(t => passed.Where(p => p.Key.Contains(t.Split('.').Last(), StringComparison.Ordinal))).Select(p => p.Value).ToList();
        outcome = ran.Count == 0 ? "  [no result]" : ran.All(o => o == "Passed") ? $"  [{ran.Count} passed]" : $"  [{ran.Count(o => o != "Passed")} NOT passed]";
    }
    Console.WriteLine($"{verdict}{r.Uid,-14} {r.Status,-11} {Trim(r.Title, 38),-38} {Trim(string.Join(", ", found.Take(3)), 44)}{outcome}");
    if (missing.Count > 0) Console.WriteLine($"                 dangling: {string.Join(", ", missing)}");
}

var untraced = classes.Keys.Where(c => !mentioned.Contains(c)).OrderBy(c => c, StringComparer.Ordinal).ToList();
Console.WriteLine($"\nImplemented but not traceable to any test: {untraceable}");
Console.WriteLine($"Names in VERIFICATION lines that match nothing: {dangling}");
Console.WriteLine($"Test classes no requirement mentions: {untraced.Count} of {classes.Count}");
foreach (var c in untraced) Console.WriteLine($"   {c} ({classes[c].Count} tests)");
return untraceable == 0 ? 0 : 1;

static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "~";

sealed class Req(string file)
{
    public string File { get; } = file;
    public string Uid { get; set; } = "";
    public string Status { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Verification { get; set; }
}
