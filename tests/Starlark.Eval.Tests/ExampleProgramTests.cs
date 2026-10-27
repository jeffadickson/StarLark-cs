using System.Text.RegularExpressions;
using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>
/// Runs every program in <c>examples/programs</c>. Each is a normal Starlark file that doubles as documentation and
/// as a test: header comments say how to run it, and trailing comments say what it must print or how it must fail.
/// <code>
/// # title: what the program shows
/// # dialect: types, fstrings, positional-only, keyword-only   (optional; features to enable)
/// # elements: utf8                                              (optional; default utf16)
/// # reassign: yes                                               (optional; allow rebinding globals)
/// # typecheck: errors N                                         (optional; also run the static checker)
/// # output: line 1                                              (one per printed line, in order)
/// # error: regex                                                (the program must fail with a matching message)
/// # load: name.star                                             (a sibling file made available to load())
/// </code>
/// </summary>
public class ExampleProgramTests
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "Examples");

    public sealed record Example(string Name, string Source, Dictionary<string, List<string>> Header)
    {
        public override string ToString() => Name;
        public string? One(string key) => Header.TryGetValue(key, out var v) ? v[0] : null;
        public List<string> All(string key) => Header.TryGetValue(key, out var v) ? v : [];
    }

    private static readonly Regex HeaderLine = new(@"^#\s*([a-z]+):\s?(.*)$", RegexOptions.Compiled);

    public static Example Parse(string path)
    {
        string text = File.ReadAllText(path).Replace("\r\n", "\n");
        var header = new Dictionary<string, List<string>>();
        foreach (var line in text.Split('\n'))
        {
            var m = HeaderLine.Match(line);
            if (!m.Success) continue;
            if (!header.TryGetValue(m.Groups[1].Value, out var list)) header[m.Groups[1].Value] = list = [];
            list.Add(m.Groups[2].Value.TrimEnd());
        }
        return new Example(Path.GetFileName(path), text, header);
    }

    public static IEnumerable<object[]> All() =>
        Directory.GetFiles(Dir, "*.star")
            .Where(f => !Path.GetFileName(f).StartsWith('_'))   // `_lib_*.star` are modules for load(), not examples
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => new object[] { Parse(f) });

    private static Dialect DialectFor(Example ex)
    {
        var d = Dialect.Standard with { EnableTopLevelStmt = true };
        foreach (var feature in (ex.One("dialect") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            d = feature switch
            {
                "types" => d with { EnableTypes = DialectTypes.Enable },
                "fstrings" => d with { EnableFStrings = true },
                "positional-only" => d with { EnablePositionalOnlyArguments = true },
                "keyword-only" => d with { EnableKeywordOnlyArguments = true },
                _ => throw new InvalidOperationException($"{ex.Name}: unknown dialect feature '{feature}'"),
            };
        }
        return d;
    }

    private static Interpreter Build(Example ex, List<string> printed)
    {
        var it = new Interpreter
        {
            Dialect = DialectFor(ex),
            Print = printed.Add,
            AllowGlobalReassign = ex.One("reassign") == "yes",
            StringElements = ex.One("elements") == "utf8" ? StringElements.Utf8 : StringElements.Utf16,
        };
        var modules = ex.All("load").ToDictionary(n => n, n => File.ReadAllText(Path.Combine(Dir, n)));
        it.SourceLoader = id => modules.GetValueOrDefault(id);
        return it;
    }

    [Fact]
    public void ThereAreFiftyOrMoreExamples() => Assert.True(All().Count() >= 50, $"found {All().Count()}");

    [Fact]
    public void EveryExampleDeclaresAnExpectation()
    {
        foreach (var args in All())
        {
            var ex = (Example)args[0];
            Assert.True(ex.One("title") is not null, $"{ex.Name}: missing '# title:'");
            Assert.True(ex.Header.ContainsKey("output") || ex.Header.ContainsKey("error") || ex.Header.ContainsKey("typecheck"),
                $"{ex.Name}: needs '# output:', '# error:' or '# typecheck:'");
        }
    }

    [Theory]
    [MemberData(nameof(All))]
    public void ProgramBehavesAsItsCommentsSay(Example ex)
    {
        var printed = new List<string>();
        var it = Build(ex, printed);

        if (ex.One("typecheck") is { } tc)
        {
            // "errors N" = the checker must report exactly N errors; "errors 0" = clean.
            var m = Regex.Match(tc, @"errors (\d+)");
            Assert.True(m.Success, $"{ex.Name}: bad typecheck directive '{tc}'");
            var result = it.Typecheck(ex.Name, ex.Source);
            Assert.True(int.Parse(m.Groups[1].Value) == result.Errors.Count,
                $"{ex.Name}: expected {m.Groups[1].Value} type errors, got {result.Errors.Count}:\n" +
                string.Join("\n", result.Errors.Select(e => e.Message)));
            foreach (var expected in ex.All("typeerror"))
            {
                Assert.Contains(result.Errors, e => Regex.IsMatch(e.Message, expected));
            }
            if (!ex.Header.ContainsKey("output") && !ex.Header.ContainsKey("error")) return;
        }

        string? error = null;
        try
        {
            it.Execute(ex.Name, ex.Source);
        }
        catch (StarlarkException e)
        {
            error = e.RawMessage;
        }
        catch (StarlarkSyntaxException e)
        {
            error = e.RawMessage;
        }

        if (ex.One("error") is { } pattern)
        {
            Assert.True(error is not null, $"{ex.Name}: expected an error matching /{pattern}/ but it ran to completion");
            Assert.True(Regex.IsMatch(error!, pattern, RegexOptions.IgnoreCase),
                $"{ex.Name}: error did not match /{pattern}/:\n{error}");
        }
        else
        {
            Assert.True(error is null, $"{ex.Name}: unexpected error: {error}");
        }

        // Output expected before a failure still has to have been printed.
        var expectedOutput = ex.All("output");
        Assert.True(expectedOutput.SequenceEqual(printed),
            $"{ex.Name}: output differs.\n--- expected ---\n{string.Join("\n", expectedOutput)}\n--- actual ---\n{string.Join("\n", printed)}");
    }
}
