using System.Text.RegularExpressions;
using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>
/// Runs the official Starlark conformance suite (github.com/bazelbuild/starlark, test_suite/testdata),
/// following the contract of its <c>starlark_test.py</c> harness: files are split into chunks at <c>---</c>;
/// <c>### regex</c> on a line says that chunk must fail with a matching message; a chunk with no
/// expectation must print nothing and not fail. We take the <c>rust</c> expectations where they differ.
/// </summary>
public class ConformanceTests
{
    private const string Impl = "rust";

    private const string Prelude = """

        def assert_eq(x, y):
          if x != y:
            print("%r != %r" % (x, y))
        def assert_ne(x, y):
          if x == y:
            print("%r == %r" % (x, y))
        def assert_(cond, msg="assertion failed"):
          if not cond:
            print(msg)

        """;

    private static readonly Regex Annotation = new("^(.*?) *### *((go|java|rust):)? *(.*)$", RegexOptions.Compiled);

    public sealed record Chunk(string File, int Line, string Code, List<string> Expected);

    private static string Root => Path.Combine(AppContext.BaseDirectory, "Conformance");

    public static IEnumerable<Chunk> ReadChunks(string path)
    {
        var code = new List<string>();
        var expected = new List<string>();
        int lineNo = 0, testLine = 1;
        string rel = Path.GetRelativePath(Root, path).Replace('\\', '/');
        foreach (var raw in File.ReadAllLines(path))
        {
            lineNo++;
            string line = raw.TrimEnd();
            if (line == "---")
            {
                yield return new Chunk(rel, testLine, string.Join("\n", code) + "\n", expected);
                expected = [];
                code = [];
                testLine = lineNo + 1;
                continue;
            }
            var m = Annotation.Match(line);
            if (m.Success)
            {
                string impl = m.Groups[3].Value;
                if (impl.Length == 0 || impl == Impl)
                {
                    expected.Add(m.Groups[4].Value);
                }
                code.Add(m.Groups[1].Value);
            }
            else
            {
                code.Add(line);
            }
        }
        yield return new Chunk(rel, testLine, string.Join("\n", code) + "\n", expected);
    }

    public static IEnumerable<object[]> AllChunks() =>
        Directory.GetFiles(Root, "*.star", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .SelectMany(ReadChunks)
            .Select(c => new object[] { $"{c.File}:{c.Line}", c });

    /// <summary>Runs one chunk and returns what the reference harness would see on stdout+stderr.</summary>
    public static string Run(string code, StringElements elements = StringElements.Utf16)
    {
        var output = new List<string>();
        var it = new Interpreter
        {
            Dialect = Dialect.AllOptionsInternal with { EnableTypes = DialectTypes.Disable },
            Print = output.Add,
            StringElements = elements,
        };
        try
        {
            it.Execute("test.star", Prelude + code);
        }
        catch (StarlarkException e)
        {
            output.Add(e.Message);
        }
        catch (StarlarkSyntaxException e)
        {
            output.Add(e.Message);
        }
        return string.Join("\n", output);
    }

    [Theory]
    [MemberData(nameof(AllChunks))]
    public void Chunk_(string id, Chunk chunk)
    {
        string output = Run(chunk.Code);
        if (chunk.Expected.Count > 0 && output.Length == 0)
        {
            Assert.Fail($"{id}: expected error: {string.Join(" | ", chunk.Expected)}");
        }
        if (chunk.Expected.Count == 0 && output.Length > 0)
        {
            Assert.Fail($"{id}: unexpected output: {output}\n--- code ---\n{chunk.Code}");
        }
        string lower = output.ToLowerInvariant();
        foreach (var exp in chunk.Expected)
        {
            string e = exp.ToLowerInvariant();
            if (!lower.Contains(e) && !Regex.IsMatch(lower, e))
            {
                Assert.Fail($"{id}: error not found: `{exp}`\nGot:\n{output}\n--- code ---\n{chunk.Code}");
            }
        }
    }
}
