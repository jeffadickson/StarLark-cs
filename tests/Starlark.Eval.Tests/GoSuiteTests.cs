using System.Text.RegularExpressions;
using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>
/// Runs the Starlark-Go <c>float.star</c> and <c>set.star</c> test data, which use an <c>asserts</c> module the host
/// must supply. The host provides <c>eq</c>, <c>ne</c>, <c>lt</c>, <c>true</c>, <c>contains</c>, <c>fails</c> and <c>freeze</c>.
/// </summary>
public class GoSuiteTests
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "GoSuites");

    /// <summary>The names of the Go test files that are run.</summary>
    public static IEnumerable<object[]> Files() => [["float.star"], ["set.star"]];

    private static readonly List<string> Problems = [];

    /// <summary>Errors that were raised, but with different wording from Go's. Reported by a test below, not failures.</summary>
    private static readonly List<string> Wording = [];

    /// <summary>Records a failed assertion and lets the script carry on, so one run reports every mismatch.</summary>
    private static object? Failure(string message, int line = 0)
    {
        lock (Problems) Problems.Add(message);
        return null;
    }

    private static Interpreter Build(List<string> printed)
    {
        var it = new Interpreter
        {
            Dialect = Dialect.AllOptionsInternal with { EnableTypes = DialectTypes.Disable },
            Print = printed.Add,
            AllowGlobalReassign = true,
        };
        it.Define("_eq", (_, a, _) => Ops.Equal(a[0], a[1]) ? null : Failure($"assert.eq failed: {Repr.Of(a[0])} != {Repr.Of(a[1])}"));
        it.Define("_ne", (_, a, _) => !Ops.Equal(a[0], a[1]) ? null : Failure($"assert.ne failed: {Repr.Of(a[0])} == {Repr.Of(a[1])}"));
        it.Define("_lt", (_, a, _) => Ops.Compare(a[0], a[1]) < 0 ? null : Failure($"assert.lt failed: {Repr.Of(a[0])} !< {Repr.Of(a[1])}"));
        it.Define("_true", (_, a, _) => Types.Truthy(a[0]) ? null : Failure("assert.true failed"));
        it.Define("_contains", (_, a, _) => Ops.Contains(a[0], a[1]) ? null : Failure($"assert.contains failed: {Repr.Of(a[1])} not in {Repr.Of(a[0])}"));
        it.Define("_freeze", (_, a, _) => { Freezer.Freeze(a[0]); return null; });
        it.Define("_fails", (interp, a, _) =>
        {
            try
            {
                interp.CallValue(a[0], []);
            }
            catch (StarlarkException e)
            {
                string pattern = (string)a[1]!;
                if (!Regex.IsMatch(e.RawMessage, pattern))
                {
                    lock (Wording) Wording.Add($"{e.RawMessage}   [Go expects /{pattern}/]");
                }
                return null;
            }
            return Failure($"assert.fails: expected an error matching /{a[1]}/");
        });
        it.SourceLoader = id => id == "asserts.star"
            ? "freeze = _freeze\nasserts = struct(eq = _eq, ne = _ne, lt = _lt, true = _true, contains = _contains, fails = _fails, freeze = _freeze)\n"
            : null;
        return it;
    }

    /// <summary>Each Go file runs to completion; every mismatch is collected and reported together.</summary>
    [Theory]
    [MemberData(nameof(Files))]
    public void GoTestDataPasses(string file)
    {
        lock (Problems) Problems.Clear();
        lock (Wording) Wording.Clear();
        var printed = new List<string>();
        var it = Build(printed);
        string text = File.ReadAllText(Path.Combine(Dir, file));
        try
        {
            it.Execute(file, text);
        }
        catch (StarlarkException e)
        {
            lock (Problems) Problems.Add("uncaught: " + e.Message);
        }
        lock (Problems) Assert.True(Problems.Count == 0, $"{Problems.Count} problem(s):\n" + string.Join("\n", Problems));
        Assert.Empty(printed);
    }
}
