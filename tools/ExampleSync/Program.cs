using System.Text;
using System.Text.RegularExpressions;
using Starlark.Eval;
using Starlark.Syntax;

// Runs each example program and prints what its '# output:' lines should be, so a new example can be written
// first and its expectation reviewed against the spec, rather than hand-typed.
//
//   ExampleSync <examples-dir> [--write] [file ...]
//
// Without --write it only reports differences. With --write it rewrites the '# output:' block of mismatching files.
// A human still reads every change: the point is to avoid transcription mistakes, not to bless whatever we print.

string dir = args.Length > 0 ? args[0] : "examples/programs";
bool write = args.Contains("--write");
var only = args.Skip(1).Where(a => !a.StartsWith("--")).ToHashSet();

int different = 0;
foreach (var path in Directory.GetFiles(dir, "*.star").OrderBy(p => p, StringComparer.Ordinal))
{
    string name = Path.GetFileName(path);
    if (name.StartsWith('_') || (only.Count > 0 && !only.Contains(name))) continue;

    string text = File.ReadAllText(path).Replace("\r\n", "\n");
    var header = Regex.Matches(text, @"^#\s*([a-z]+):\s?(.*)$", RegexOptions.Multiline)
        .GroupBy(m => m.Groups[1].Value)
        .ToDictionary(g => g.Key, g => g.Select(m => m.Groups[2].Value.TrimEnd()).ToList());
    if (header.ContainsKey("error") || header.ContainsKey("typecheck")) continue;   // nothing printed to compare

    var dialect = Dialect.Standard with { EnableTopLevelStmt = true };
    foreach (var f in (header.GetValueOrDefault("dialect")?.FirstOrDefault() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        dialect = f switch
        {
            "types" => dialect with { EnableTypes = DialectTypes.Enable },
            "fstrings" => dialect with { EnableFStrings = true },
            "positional-only" => dialect with { EnablePositionalOnlyArguments = true },
            "keyword-only" => dialect with { EnableKeywordOnlyArguments = true },
            _ => dialect,
        };
    }

    var printed = new List<string>();
    var it = new Interpreter
    {
        Dialect = dialect,
        Print = printed.Add,
        AllowGlobalReassign = header.GetValueOrDefault("reassign")?.FirstOrDefault() == "yes",
        StringElements = header.GetValueOrDefault("elements")?.FirstOrDefault() == "utf8" ? StringElements.Utf8 : StringElements.Utf16,
    };
    var modules = (header.GetValueOrDefault("load") ?? []).ToDictionary(n => n, n => File.ReadAllText(Path.Combine(dir, n)));
    it.SourceLoader = id => modules.GetValueOrDefault(id);

    try { it.Execute(name, text); }
    catch (Exception e) when (e is StarlarkException or StarlarkSyntaxException)
    {
        Console.WriteLine($"{name}: ERROR {(e as StarlarkException)?.RawMessage ?? (e as StarlarkSyntaxException)?.RawMessage}");
        different++;
        continue;
    }

    var expected = header.GetValueOrDefault("output") ?? [];
    if (expected.SequenceEqual(printed)) continue;

    different++;
    Console.WriteLine($"{name}: output differs");
    foreach (var line in printed) Console.WriteLine($"    {line}");

    if (!write) continue;
    var lines = text.Split('\n').ToList();
    int first = lines.FindIndex(l => l.StartsWith("# output:"));
    lines.RemoveAll(l => l.StartsWith("# output:"));
    var block = printed.Select(p => p.Length == 0 ? "# output:" : "# output: " + p).ToList();
    if (first < 0) first = Math.Max(1, lines.FindIndex(l => l.StartsWith("# title:")) + 1);
    lines.InsertRange(first, block);
    File.WriteAllText(path, string.Join("\n", lines));
}
Console.WriteLine(different == 0 ? "all outputs match" : $"{different} file(s) differ");
return different == 0 ? 0 : 1;
