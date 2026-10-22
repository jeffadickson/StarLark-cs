using System.Diagnostics;
using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Fuzz.Tests;

/// <summary>
/// Fuzz tests. They are deterministic: iteration <c>i</c> of a test always uses seed <c>BaseSeed + i</c>.
/// <list type="bullet">
/// <item>STARLARK_FUZZ_ITERATIONS: iterations per test (default 2000; try 200000 for a soak run).</item>
/// <item>STARLARK_FUZZ_SEED: first seed (default 1). A failure message names the seed that reproduces it.</item>
/// </list>
/// </summary>
public class FuzzTests
{
    private static int Iterations => int.TryParse(Environment.GetEnvironmentVariable("STARLARK_FUZZ_ITERATIONS"), out var n) ? n : 2000;
    private static int BaseSeed => int.TryParse(Environment.GetEnvironmentVariable("STARLARK_FUZZ_SEED"), out var n) ? n : 1;

    private static string Corpus => Path.Combine(AppContext.BaseDirectory, "Testcases");

    private static readonly Lazy<string[]> CorpusSources = new(() =>
        Directory.GetFiles(Corpus, "*.star").OrderBy(f => f, StringComparer.Ordinal).Select(File.ReadAllText).ToArray());

    /// <summary>Run <paramref name="body"/> for every seed in parallel, collecting failures with their seed and input.</summary>
    private static void Fuzz(string what, Func<Fuzzer, string> makeInput, Action<string> check, int? iterations = null)
    {
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        int n = iterations ?? Iterations;
        Parallel.For(0, n, i =>
        {
            int seed = BaseSeed + i;
            string input = "";
            try
            {
                input = makeInput(new Fuzzer(seed));
                var sw = Stopwatch.StartNew();
                check(input);
                if (sw.Elapsed > Oracle.Budget)
                {
                    failures.Add($"seed {seed}: took {sw.Elapsed.TotalSeconds:F1}s (hang?)\n{Show(input)}");
                }
            }
            catch (Exception e)
            {
                failures.Add($"seed {seed}: {e.GetType().Name}: {e.Message}\n{e.StackTrace?.Split('\n').FirstOrDefault()}\n{Show(input)}");
            }
        });
        if (!failures.IsEmpty)
        {
            Assert.Fail($"{what}: {failures.Count} failure(s) in {n} iterations. First:\n{failures.OrderBy(f => f).First()}");
        }
    }

    private static string Show(string s) =>
        "--- input (" + s.Length + " chars) ---\n" + (s.Length > 600 ? s[..600] + "\n...[truncated]" : s) + "\n--- end ---";

    // ---------------------------------------------------------------- lexer & parser

    [Fact]
    public void LexerSurvivesRandomCharacters() =>
        Fuzz("lexer/random chars", f => f.RandomChars(200), Oracle.Lex);

    [Fact]
    public void LexerSurvivesTokenSoup() =>
        Fuzz("lexer/token soup", f => f.TokenSoup(60), Oracle.Lex);

    [Fact]
    public void ParserOnlyThrowsSyntaxErrorsForRandomCharacters() =>
        Fuzz("parser/random chars", f => f.RandomChars(200), s => Oracle.Parse(s));

    [Fact]
    public void ParserOnlyThrowsSyntaxErrorsForTokenSoup() =>
        Fuzz("parser/token soup", f => f.TokenSoup(60), s => Oracle.Parse(s));

    [Fact]
    public void ParserSurvivesMutatedRealPrograms() =>
        Fuzz("parser/mutated corpus", f =>
        {
            var src = CorpusSources.Value[f.Rng.Next(CorpusSources.Value.Length)];
            // Mutate a window, not the whole file: keeps iterations fast and edits dense.
            int start = f.Rng.Next(Math.Max(1, src.Length - 400));
            return f.Mutate(src.Substring(start, Math.Min(400, src.Length - start)));
        }, s => Oracle.Parse(s));

    [Fact]
    public void ParserAcceptsEveryTruncationOfARealFileOrRejectsItCleanly()
    {
        // Every prefix of a valid program is either valid or a clean syntax error: this walks all the
        // "unexpected end of file" paths in order.
        var failures = new List<string>();
        foreach (var src in CorpusSources.Value.Take(20))
        {
            int step = Math.Max(1, src.Length / 300);
            for (int cut = 0; cut < src.Length; cut += step)
            {
                try { Oracle.Parse(src[..cut]); }
                catch (Exception e) { failures.Add($"{e.GetType().Name} at cut {cut}: {e.Message}"); }
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(5)));
    }

    [Fact]
    public void GeneratedProgramsParseAndPrintToAFixedPoint() =>
        Fuzz("parser/round trip", f => f.Program(), s =>
        {
            AstModule ast;
            try
            {
                ast = AstModule.Parse("fuzz.star", s, Oracle.Dialect);
            }
            catch (StarlarkSyntaxException e)
            {
                throw new InvalidOperationException("generated program failed to parse:\n" + e.Message);
            }
            Oracle.RoundTrip(ast);
        });

    [Fact]
    public void ParsedMutantsPrintToAFixedPointToo() =>
        Fuzz("parser/mutant round trip", f => f.Mutate(f.Program()), s =>
        {
            if (Oracle.Parse(s) is { } ast) Oracle.RoundTrip(ast);
        });

    [Fact]
    public void ErrorSnippetsRenderForAnyInput() =>
        Fuzz("diagnostics", f => f.TokenSoup(40), s =>
        {
            try { AstModule.Parse("x", s, Dialect.Standard); }
            catch (StarlarkSyntaxException e) { _ = e.ToString(); }
        });

    // ---------------------------------------------------------------- interpreter

    [Fact]
    public void InterpreterOnlyRaisesStarlarkErrorsForGeneratedPrograms() =>
        Fuzz("interpreter/generated", f => f.Program(), s => Oracle.Run(s));

    [Fact]
    public void InterpreterOnlyRaisesStarlarkErrorsForMutatedPrograms() =>
        Fuzz("interpreter/mutated", f => f.Mutate(f.Program()), s => Oracle.Run(s));

    [Fact]
    public void InterpreterOnlyRaisesStarlarkErrorsForTokenSoup() =>
        Fuzz("interpreter/token soup", f => f.TokenSoup(40), s => Oracle.Run(s), iterations: Iterations / 2);

    [Fact]
    public void Utf8ModeIsAsRobustAsUtf16() =>
        Fuzz("interpreter/utf8", f => f.Program(), s => Oracle.Run(s, StringElements.Utf8), iterations: Iterations / 2);

    // ---------------------------------------------------------------- static type checker

    private static void TypecheckNeverCrashes(string src)
    {
        var it = new Interpreter
        {
            Dialect = Dialect.AllOptionsInternal,
            Print = _ => { },
        };
        try
        {
            var result = it.Typecheck("fuzz.star", src);
            foreach (var e in result.Errors) _ = e.Render();   // rendering a diagnostic must not throw either
        }
        catch (StarlarkSyntaxException)
        {
            // Not a program: the checker is never reached.
        }
    }

    [Fact]
    public void TypeCheckerNeverCrashesOnGeneratedPrograms() =>
        Fuzz("typecheck/generated", f => f.Program(), TypecheckNeverCrashes);

    [Fact]
    public void TypeCheckerNeverCrashesOnMutatedPrograms() =>
        Fuzz("typecheck/mutated", f => f.Mutate(f.Program()), TypecheckNeverCrashes);

    [Fact]
    public void TypeCheckerNeverCrashesOnMutatedRealFiles() =>
        Fuzz("typecheck/mutated corpus", f =>
        {
            var src = CorpusSources.Value[f.Rng.Next(CorpusSources.Value.Length)];
            int start = f.Rng.Next(Math.Max(1, src.Length - 400));
            return f.Mutate(src.Substring(start, Math.Min(400, src.Length - start)));
        }, TypecheckNeverCrashes);

    [Fact]
    public void ProgramsThatPassTheCheckerDoNotFailWithTypeErrorsWhenRun() =>
        // Soundness check in the lenient direction: a program the checker accepts may still fail at run time
        // (the checker only rejects what it is sure of), but it must never fail with an *annotation* error that
        // the checker could have seen. Generated programs carry no annotations, so none may appear.
        Fuzz("typecheck/soundness", f => f.Program(), s =>
        {
            var it = new Interpreter
            {
                Dialect = Dialect.AllOptionsInternal with { EnableTypes = DialectTypes.Enable },
                AllowGlobalReassign = true,
                Print = _ => { },
            };
            try { it.Execute("fuzz.star", s); }
            catch (StarlarkException e) when (e.RawMessage.Contains("does not match the type annotation"))
            {
                throw new InvalidOperationException("annotation error in an unannotated program: " + e.RawMessage);
            }
            catch (Exception e) when (e is StarlarkException or StarlarkSyntaxException)
            {
            }
        });

    [Fact]
    public void ExpressionsNeverCrashTheEvaluator() =>
        Fuzz("interpreter/expressions", f => "result = " + f.Expr(4) + "\n", s => Oracle.Run(s));

    // ---------------------------------------------------------------- targeted properties

    [Fact]
    public void SliceAndIndexAgreeWithAnIndexListModel()
    {
        // For random bounds, `xs[a:b:c]` must equal picking list(range(len))[a:b:c] elements by index.
        var rng = new Random(BaseSeed);
        var it = new Interpreter();
        for (int i = 0; i < Iterations; i++)
        {
            int len = rng.Next(0, 12);
            string Bound() => rng.Next(4) == 0 ? "None" : rng.Next(-15, 16).ToString();
            string step = rng.Next(4) == 0 ? "None" : (rng.Next(-4, 5) is var s && s == 0 ? "1" : s.ToString());
            string a = Bound(), b = Bound();
            string expr = $"[list(range({len}))[{a}:{b}:{step}], [list(range({len}))[j] for j in range({len})][{a}:{b}:{step}]]";
            var r = (StarList)it.Eval(expr)!;
            Assert.True(Ops.Equal(r[0], r[1]), $"slice mismatch for {expr}");
        }
    }

    [Fact]
    public void ArithmeticMatchesBigIntegerModel()
    {
        var rng = new Random(BaseSeed);
        var it = new Interpreter();
        for (int i = 0; i < Iterations; i++)
        {
            long x = rng.NextInt64(-1_000_000, 1_000_000), y = rng.NextInt64(-1000, 1000);
            if (y == 0) y = 7;
            // Floor division and modulo identities from the spec: (x // y) * y + (x % y) == x, sign of x % y follows y.
            var r = (StarList)it.Eval($"[({x}) // ({y}), ({x}) % ({y}), (({x}) // ({y})) * ({y}) + (({x}) % ({y}))]")!;
            Assert.Equal(new System.Numerics.BigInteger(x), (System.Numerics.BigInteger)r[2]!);
            var m = (System.Numerics.BigInteger)r[1]!;
            Assert.True(m.IsZero || (m.Sign < 0) == (y < 0), $"sign of {x} % {y} = {m}");
        }
    }

    [Fact]
    public void JsonRoundTripsGeneratedData()
    {
        var rng = new Random(BaseSeed);
        var it = new Interpreter();
        it.Define("json", JsonModule.Create());
        for (int i = 0; i < Iterations; i++)
        {
            string value = JsonValue(rng, 3);
            var r = (StarList)it.Eval($"(lambda v: [json.decode(json.encode(v)), v])({value})")!;
            Assert.True(Ops.Equal(r[0], r[1]), $"round trip failed for {value}");
        }
    }

    private static string JsonValue(Random rng, int depth)
    {
        if (depth <= 0) return Scalar(rng);
        return rng.Next(5) switch
        {
            0 => Scalar(rng),
            1 => "[" + string.Join(", ", Enumerable.Range(0, rng.Next(4)).Select(_ => JsonValue(rng, depth - 1))) + "]",
            2 => "{" + string.Join(", ", Enumerable.Range(0, rng.Next(4)).Select(j => $"\"k{j}\": {JsonValue(rng, depth - 1)}")) + "}",
            _ => Scalar(rng),
        };
    }

    private static string Scalar(Random rng) => rng.Next(7) switch
    {
        0 => "None",
        1 => "True",
        2 => rng.Next(-1000, 1000).ToString(),
        3 => "123456789012345678901234567890",
        4 => (rng.NextDouble() * 1000 - 500).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        5 => "\"a\\\"b\\\\c\\n\\té\"",
        _ => "\"\"",
    };
}
