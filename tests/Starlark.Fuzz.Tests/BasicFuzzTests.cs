using System.Text;
using Basic;
using Starlark.Eval;

namespace Starlark.Fuzz.Tests;

/// <summary>
/// Fuzzing the Level II BASIC interpreter, which is itself a Starlark program that takes free-typed input (the no-crash policy,
/// QA-ROB-001). The samples are the bundled BASIC programs: they are mutated, typed in a line at a time, run, and driven with random
/// <c>EDIT</c> keys. Whatever is typed, the machine must answer with text, never throw, never hang, and stay usable afterwards.
/// Iterations and seed come from the same environment variables as the other fuzz tests.
/// </summary>
public class BasicFuzzTests
{
    private static int Iterations =>
        (int.TryParse(Environment.GetEnvironmentVariable("STARLARK_FUZZ_ITERATIONS"), out var n) ? n : 2000) / 20 + 20;

    private static int BaseSeed
    {
        get
        {
            var text = Environment.GetEnvironmentVariable("STARLARK_FUZZ_SEED");
            return string.IsNullOrEmpty(text) ? 1 : int.TryParse(text, out var n) && n >= 0 ? n : throw new InvalidOperationException($"STARLARK_FUZZ_SEED={text} is unusable");
        }
    }

    private static readonly string[] Samples = Scripts.ProgramNames().Select(n => Scripts.ReadProgram(n)!).ToArray();

    private static readonly string[] Words =
    [
        "PRINT", "?", "FOR", "TO", "STEP", "NEXT", "IF", "THEN", "ELSE", "GOTO", "GOSUB", "RETURN", "ON", "INPUT", "DATA", "READ",
        "RESTORE", "DIM", "DEF", "FN", "LET", "REM", "END", "STOP", "CONT", "RUN", "LIST", "NEW", "DELETE", "CLEAR", "CLS", "SET",
        "RESET", "POINT", "POKE", "PEEK", "RANDOM", "TRON", "TROFF", "DEFINT", "DEFSTR", "ERROR", "RESUME", "EDIT", "USING", "TAB(",
        "SPC(", "@", "AND", "OR", "NOT", "SIN(", "COS(", "LOG(", "EXP(", "SQR(", "RND(", "INT(", "ABS(", "LEFT$(", "MID$(", "RIGHT$(",
        "LEN(", "ASC(", "CHR$(", "STR$(", "VAL(", "INSTR(", "STRING$(", "&H", "&O", "1E38", "1E-38", "32767", "-32768", "A$", "B", "C(",
        "\"", "(", ")", ",", ";", ":", "=", "<", ">", "+", "-", "*", "/", "^", "#", "%", "!", "$", "'", " ", "10", "20", "0", "65535",
        "é", "世", "😀", "\t",
    ];

    private static string Mutate(Random rng, string source)
    {
        var sb = new StringBuilder(source);
        int edits = 1 + rng.Next(5);
        for (int e = 0; e < edits && sb.Length > 0; e++)
        {
            int at = rng.Next(sb.Length);
            switch (rng.Next(6))
            {
                case 0: sb.Remove(at, Math.Min(sb.Length - at, 1 + rng.Next(10))); break;
                case 1: sb.Insert(at, Words[rng.Next(Words.Length)]); break;
                case 2: sb[at] = (char)rng.Next(32, 127); break;
                case 3: sb.Insert(at, Words[rng.Next(Words.Length)] + Words[rng.Next(Words.Length)]); break;
                case 4:
                {
                    int len = Math.Min(sb.Length - at, 1 + rng.Next(30));
                    sb.Insert(rng.Next(sb.Length), sb.ToString(at, len));
                    break;
                }
                default: sb.Length = at; break;
            }
        }
        return sb.ToString();
    }

    private static string RandomLine(Random rng)
    {
        var sb = new StringBuilder();
        if (rng.Next(3) != 0) sb.Append((rng.Next(1, 40) * 10).ToString()).Append(' ');
        int n = 1 + rng.Next(10);
        for (int i = 0; i < n; i++) sb.Append(Words[rng.Next(Words.Length)]).Append(rng.Next(3) == 0 ? " " : "");
        return sb.ToString();
    }

    /// <summary>Runs one machine through <paramref name="lines"/>, giving each a bounded number of slices, and checks it stayed usable.</summary>
    private static void Drive(IEnumerable<string> lines, Random rng)
    {
        // A tight step budget turns a runaway program into an error instead of a long wait; the host loop below does the same with slices.
        var m = new Machine(maxSteps: 2_000_000);
        m.TakeOutput();
        var typed = new List<string>();
        foreach (var line in lines)
        {
            typed.Add(line);
            if (m.Editing)
            {
                // Typed text while the editor is open arrives as keys, as it does on a real keyboard.
                foreach (var ch in line) m.Key(ch.ToString());
                m.Key("\r");
                continue;
            }
            string status;
            try
            {
                status = m.Type(line);
                for (int slice = 0; slice < 20 && status == "budget"; slice++) status = m.Continue();
            }
            catch (ExecutionLimitException)
            {
                // A runaway program using up its budget is the designed outcome, as BREAK is: the host stops it and carries on.
                m.Recover();
                status = "done";
            }
            if (status == "budget") m.Break();
            if (m.Editing && rng.Next(2) == 0) m.Key("Q");
            m.TakeOutput();
        }
        // Whatever happened, it must be possible to get back to a working prompt and run a plain line.
        while (m.Editing) m.Key("Q");
        m.Recover();
        m.TakeOutput();
        m.Enter("PRINT 1+1");
        Assert.Contains(" 2 ", m.TakeOutput());
    }

    private static void Fuzz(string what, Action<Random> one)
    {
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        int n = Iterations;
        Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, i =>
        {
            int seed = BaseSeed + i;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                one(new Random(seed));
                if (sw.Elapsed > TimeSpan.FromSeconds(30)) failures.Add($"seed {seed}: took {sw.Elapsed.TotalSeconds:F1}s (hang?)");
            }
            catch (Exception e)
            {
                // The first line goes in the message; the rest (the program that was typed) goes to a file named after the seed.
                var first = e.Message.Split('\n')[0];
                string kept = "";
                if (e.Message.Contains('\n'))
                {
                    kept = Path.Combine(Path.GetTempPath(), $"basic-fuzz-seed-{seed}.txt");
                    File.WriteAllText(kept, e.Message);
                    kept = $" (program kept in {kept})";
                }
                failures.Add($"seed {seed}: {e.GetType().Name}: {first}{kept}");
            }
        });
        if (!failures.IsEmpty) Assert.Fail($"{what}: {failures.Count} failure(s) in {n} iterations. First:\n{failures.OrderBy(f => f).First()}");
    }

    [Fact]
    public void MutatedSampleProgramsNeverBreakTheMachine() => Fuzz("basic/mutated samples", rng =>
    {
        var program = Mutate(rng, Samples[rng.Next(Samples.Length)]);
        var lines = program.Replace("\r\n", "\n").Split('\n').ToList();
        lines.Add("RUN");
        lines.Add("LIST");
        Drive(lines, rng);
    });

    [Fact]
    public void RandomTypedLinesNeverBreakTheMachine() => Fuzz("basic/random lines", rng =>
        Drive(Enumerable.Range(0, 12).Select(_ => RandomLine(rng)).Append("RUN").Append("LIST"), rng));

    [Fact]
    public void RandomEditKeysNeverBreakTheMachine() => Fuzz("basic/edit keys", rng =>
    {
        var m = new Machine(maxSteps: 2_000_000);
        m.TakeOutput();
        m.Load(Samples[rng.Next(Samples.Length)]);
        string alphabet = "0123456789 DICXHSKLQAE\"\\,:()+*$é😀\b\x1b\r";
        for (int round = 0; round < 6; round++)
        {
            m.Enter("EDIT 10");
            for (int k = 0; k < 40 && m.Editing; k++)
            {
                var runes = alphabet.EnumerateRunes().ToArray();
                m.Key(runes[rng.Next(runes.Length)].ToString());
            }
            while (m.Editing) m.Key("\x1b".Length > 0 && rng.Next(2) == 0 ? "Q" : "\r");
            m.TakeOutput();
        }
        m.Enter("LIST");
        Assert.Contains("READY", m.TakeOutput());
    });

    [Fact]
    public void ControlAndAstralCharactersInProgramTextAreSafe() => Fuzz("basic/odd characters", rng =>
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 60; i++)
        {
            int kind = rng.Next(5);
            sb.Append(kind switch
            {
                0 => (char)rng.Next(0, 32),
                1 => (char)rng.Next(0xD800, 0xE000),   // lone surrogates
                2 => char.ConvertFromUtf32(rng.Next(0x10000, 0x10FFFF)),
                3 => (char)rng.Next(0x80, 0x800),
                _ => (char)rng.Next(32, 127),
            });
        }
        Drive(["10 PRINT \"" + sb.ToString().Replace("\"", "") + "\"", "20 A$=\"" + sb.ToString().Replace("\"", "") + "\":PRINT LEN(A$);LEFT$(A$,3);ASC(A$)", "RUN", "LIST", "EDIT 10"], rng);
    });
}
