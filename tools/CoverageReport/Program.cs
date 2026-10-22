using System.Collections.Concurrent;
using System.Text;
using System.Xml.Linq;

// Merge Cobertura files (from `dotnet test --collect:"XPlat Code Coverage"`) into one per-file summary.
//
//   CoverageReport <results-dir> [--out report.md] [--html report.html] [--min-line 80]
//
// A line counts as covered if ANY input file hit it, so running the Syntax and Eval test projects separately
// and merging them gives the combined picture. Exits 1 if total line coverage is below --min-line.

string? dir = null, mdOut = null, htmlOut = null;
double minLine = 0;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--out": mdOut = args[++i]; break;
        case "--html": htmlOut = args[++i]; break;
        case "--min-line": minLine = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        default: dir = args[i]; break;
    }
}
if (dir is null || !Directory.Exists(dir))
{
    Console.Error.WriteLine("usage: CoverageReport <results-dir> [--out report.md] [--html report.html] [--min-line N]");
    return 2;
}

// file -> line -> (hits, branches covered, branches total)
var files = new ConcurrentDictionary<string, ConcurrentDictionary<int, (int Hits, int BrCov, int BrTotal)>>();

var inputs = Directory.GetFiles(dir, "coverage.cobertura.xml", SearchOption.AllDirectories);
Parallel.ForEach(inputs, path =>
{
    var doc = XDocument.Load(path);
    // Cobertura gives filenames relative to a <source> root; qualify them so they are unique across projects.
    var root = (doc.Descendants("source").FirstOrDefault()?.Value ?? "").Replace('\\', '/').TrimEnd('/');
    foreach (var cls in doc.Descendants("class"))
    {
        string rel = (cls.Attribute("filename")?.Value ?? "").Replace('\\', '/');
        string file = Path.IsPathRooted(rel) ? rel : root + "/" + rel;
        var lines = files.GetOrAdd(file, _ => new());
        foreach (var line in cls.Elements("lines").Elements("line"))
        {
            int n = int.Parse(line.Attribute("number")!.Value);
            int hits = int.Parse(line.Attribute("hits")!.Value);
            int cov = 0, total = 0;
            var cc = line.Attribute("condition-coverage")?.Value;
            if (cc is not null)
            {
                // "50% (1/2)"
                int open = cc.IndexOf('(');
                var parts = cc[(open + 1)..^1].Split('/');
                cov = int.Parse(parts[0]);
                total = int.Parse(parts[1]);
            }
            lines.AddOrUpdate(n, (hits, cov, total),
                (_, old) => (old.Hits + hits, Math.Max(old.BrCov, cov), Math.Max(old.BrTotal, total)));
        }
    }
});

// Several methods can report the same file; show paths relative to the repo's src/.
string Short(string f)
{
    int i = f.IndexOf("/src/", StringComparison.Ordinal);
    return i >= 0 ? f[(i + 1)..] : f;
}

var rows = files
    .Where(kv => kv.Key.Contains("/src/", StringComparison.Ordinal))
    .Select(kv =>
    {
        int total = kv.Value.Count;
        int covered = kv.Value.Count(l => l.Value.Hits > 0);
        int bt = kv.Value.Sum(l => l.Value.BrTotal);
        int bc = kv.Value.Sum(l => l.Value.BrCov);
        return (File: Short(kv.Key), Total: total, Covered: covered, BrTotal: bt, BrCov: bc,
                Missed: kv.Value.Where(l => l.Value.Hits == 0).Select(l => l.Key).OrderBy(x => x).ToList());
    })
    .OrderBy(r => r.File, StringComparer.Ordinal)
    .ToList();

int tl = rows.Sum(r => r.Total), tc = rows.Sum(r => r.Covered);
int tbt = rows.Sum(r => r.BrTotal), tbc = rows.Sum(r => r.BrCov);
double Pct(int a, int b) => b == 0 ? 100.0 : 100.0 * a / b;

static string Ranges(List<int> xs)
{
    var sb = new StringBuilder();
    for (int i = 0; i < xs.Count; i++)
    {
        int j = i;
        while (j + 1 < xs.Count && xs[j + 1] == xs[j] + 1) j++;
        if (sb.Length > 0) sb.Append(", ");
        sb.Append(i == j ? xs[i].ToString() : $"{xs[i]}-{xs[j]}");
        i = j;
    }
    return sb.ToString();
}

var md = new StringBuilder();
md.AppendLine("# Code coverage");
md.AppendLine();
md.AppendLine($"Line coverage **{Pct(tc, tl):F1}%** ({tc}/{tl}), branch coverage **{Pct(tbc, tbt):F1}%** ({tbc}/{tbt}). " +
              $"Merged from {inputs.Length} report(s).");
md.AppendLine();
md.AppendLine("| File | Lines | Branches | Uncovered lines |");
md.AppendLine("|---|---:|---:|---|");
foreach (var r in rows)
{
    string missed = r.Missed.Count == 0 ? "" : Ranges(r.Missed);
    if (missed.Length > 80) missed = missed[..77] + "...";
    md.AppendLine($"| {r.File} | {Pct(r.Covered, r.Total):F1}% ({r.Covered}/{r.Total}) | " +
                  $"{(r.BrTotal == 0 ? "-" : $"{Pct(r.BrCov, r.BrTotal):F1}%")} | {missed} |");
}
Console.Write(md.ToString());
if (mdOut is not null) File.WriteAllText(mdOut, md.ToString());

if (htmlOut is not null)
{
    var h = new StringBuilder();
    h.AppendLine("<!doctype html><meta charset=utf-8><title>Coverage</title>");
    h.AppendLine("<style>body{font:14px system-ui;margin:2em}table{border-collapse:collapse}td,th{padding:4px 10px;border-bottom:1px solid #ddd;text-align:left}td.n{text-align:right}.bar{display:inline-block;height:10px;background:#c33;width:100px}.bar i{display:block;height:10px;background:#3a3}</style>");
    h.AppendLine($"<h1>Code coverage</h1><p>Lines <b>{Pct(tc, tl):F1}%</b> ({tc}/{tl}) &middot; branches <b>{Pct(tbc, tbt):F1}%</b> ({tbc}/{tbt})</p>");
    h.AppendLine("<table><tr><th>File<th>Lines<th><th>Uncovered");
    foreach (var r in rows)
    {
        double p = Pct(r.Covered, r.Total);
        h.AppendLine($"<tr><td>{System.Net.WebUtility.HtmlEncode(r.File)}<td class=n>{p:F1}%<td><span class=bar><i style='width:{p:F0}px'></i></span><td>{Ranges(r.Missed)}");
    }
    h.AppendLine("</table>");
    File.WriteAllText(htmlOut, h.ToString());
}

if (Pct(tc, tl) < minLine)
{
    Console.Error.WriteLine($"FAIL: line coverage {Pct(tc, tl):F1}% is below the required {minLine:F1}%");
    return 1;
}
return 0;
