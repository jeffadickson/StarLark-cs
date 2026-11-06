using System.Diagnostics;
using System.Numerics;
using System.Text;
using Starlark.Eval;
using Starlark.Eval.Futures;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>
/// Regressions for the release candidate 1 review: host conversion of hostile values, Eval and Define, the int size cap,
/// unbounded strings and bytes, range slicing and repr, struct and record hashing, code-point string methods, escapes,
/// and the futures differences between inline and threaded runs.
/// </summary>
public class ReleaseCandidate1Tests
{
    private static Interpreter New(long maxBytes = 0) => new()
    {
        Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableTypes = DialectTypes.Enable },
        MaxSteps = 50_000_000,
        Timeout = TimeSpan.FromSeconds(30),
        MaxAllocatedBytes = maxBytes,
    };

    private static object? Val(string expression, Interpreter? it = null) =>
        (it ?? New()).Execute("t", "r = " + expression + "\n").Globals["r"];

    private static string Fails(string statements, Interpreter? it = null) =>
        Assert.Throws<StarlarkException>(() => (it ?? New()).Execute("t", statements)).RawMessage;

    /// <summary>
    /// A Starlark function <c>pow2(k)</c> giving 2**k for any k, built from shifts of at most 511 bits: a left shift count
    /// must be below 512, as in Go. Tests of the int size cap prepend it and write <c>pow2(1048575)</c> where they used to
    /// write <c>1 &lt;&lt; 1048575</c>.
    /// </summary>
    private const string Pow2 =
        "def pow2(k):\n    x = 1\n    for i in range(k // 511):\n        x = x << 511\n    return x << (k % 511)\n";

    // ---------------------------------------------------------------- #1 #3 #4 #35 #39: host conversion never crashes the host

    private static Interpreter WithHostFunctions()
    {
        var it = New();
        it.Register("take", (object o) => o?.GetType().Name ?? "null");
        it.Register("strdict", (Dictionary<string, int> d) => d.Count);
        it.Register("intlist", (List<int> xs) => xs.Count);
        it.Register("dec", (decimal d) => (double)d);
        it.Register("span", (TimeSpan t) => t.TotalSeconds);
        it.Register("when", (DateTime d) => d.Year);
        it.Register("pt", (Point p) => p.X);
        return it;
    }

    public sealed record Point(int X)
    {
        public int X { get; init; } = X >= 0 ? X : throw new ArgumentOutOfRangeException(nameof(X), "X must not be negative");
    }

    /// <summary>A list or dict that contains itself is a Starlark error, for a host parameter and for Call&lt;object&gt;.</summary>
    [Fact]
    public void CyclicValuesConvertToAnError()
    {
        var it = WithHostFunctions();
        Assert.Contains("nesting is too deep", Fails("x = []\nx.append(x)\ntake(x)\n", it));
        Assert.Contains("nesting is too deep", Fails("x = {}\nx['a'] = x\ntake(x)\n", it));
        Assert.Contains("Cannot convert a value of type 'list'", Fails("x = []\nx.append(x)\nintlist(x)\n", it));
        var m = New().Execute("t", "def f():\n    x = []\n    x.append(x)\n    return x\ndef g():\n    d = {}\n    d[1] = d\n    return d\n");
        Assert.Contains("nesting is too deep", Assert.Throws<StarlarkException>(() => New().Call<object>(m, "f")).RawMessage);
        Assert.Contains("nesting is too deep", Assert.Throws<StarlarkException>(() => New().Call<object>(m, "g")).RawMessage);
    }

    /// <summary>A value nested 200,000 deep (built in a few steps) is an error, not a stack overflow.</summary>
    [Fact]
    public void DeeplyNestedValuesConvertToAnError()
    {
        var it = WithHostFunctions();
        Assert.Contains("nesting is too deep",
            Fails("def b():\n    x = []\n    for i in range(200000):\n        x = [x]\n    return x\ntake(b())\n", it));
        // A moderately nested value still converts.
        Assert.Equal("List`1", Val("take([[[[[1]]]]])", it));
    }

    /// <summary>A None dict key is a Starlark error, not ArgumentNullException.</summary>
    [Fact]
    public void NoneDictKeysAreAStarlarkError()
    {
        var it = WithHostFunctions();
        Assert.Contains("None key", Fails("take({None: 1})\n", it));
        Assert.Contains("None key", Fails("strdict({None: 1})\n", it));
        var m = New().Execute("t", "def g():\n    return {None: 1}\n");
        Assert.Contains("None key", Assert.Throws<StarlarkException>(() => New().Call<object>(m, "g")).RawMessage);
    }

    /// <summary>Floats out of range for decimal or TimeSpan, and unparsable dates, are Starlark errors.</summary>
    [Theory]
    [InlineData("dec(float('inf'))", "does not fit in Decimal")]
    [InlineData("dec(float('nan'))", "does not fit in Decimal")]
    [InlineData("dec(1e300)", "does not fit in Decimal")]
    [InlineData("span(1e300)", "does not fit in TimeSpan")]
    [InlineData("span(float('nan'))", "does not fit in TimeSpan")]
    [InlineData("span(1 << 200)", "does not fit in TimeSpan")]
    [InlineData("when('nope')", "DateTime")]
    public void OutOfRangeConversionsAreStarlarkErrors(string call, string message) =>
        Assert.Contains(message, Fails(call + "\n", WithHostFunctions()));

    /// <summary>Values in range still convert.</summary>
    [Fact]
    public void InRangeConversionsStillWork()
    {
        var it = WithHostFunctions();
        Assert.Equal(1.5, Val("dec(1.5)", it));
        Assert.Equal(90.0, Val("span(90)", it));
        Assert.Equal(new BigInteger(2024), Val("when('2024-01-02T03:04:05Z')", it));
        Assert.Equal(new BigInteger(3), Val("pt(struct(x = 3))", it));
    }

    /// <summary>A constructor that throws while binding a record is reported as a Starlark error.</summary>
    [Fact]
    public void AThrowingConstructorIsAStarlarkError() =>
        Assert.Contains("X must not be negative", Fails("pt(struct(x = -1))\n", WithHostFunctions()));

    /// <summary>Exhaustively, small hostile values passed to every typed parameter end in a Starlark error or a result.</summary>
    [Fact]
    public void HostileArgumentsNeverEscapeAsOtherExceptions()
    {
        string[] values = ["None", "1e300", "-1e300", "float('nan')", "float('inf')", "1 << 100", "-1", "'x'", "'2024-13-45'",
            "{None: None}", "{1: 2}", "[None]", "struct(x = None)", "struct()", "(1,)", "b'x'", "{'x': [1]}"];
        string[] functions = ["take", "strdict", "intlist", "dec", "span", "when", "pt"];
        var it = WithHostFunctions();
        foreach (var f in functions)
        {
            foreach (var v in values)
            {
                try
                {
                    it.Execute("t", $"r = {f}({v})\n");
                }
                catch (StarlarkException)
                {
                }
            }
        }
    }

    // ---------------------------------------------------------------- #5 Eval, #6 Define

    /// <summary>Text that closes Eval's wrapper and adds statements is a syntax error, and runs nothing.</summary>
    [Fact]
    public void EvalRejectsInjectedStatements()
    {
        var printed = new List<string>();
        var it = New();
        it.Print = printed.Add;
        var e = Assert.Throws<StarlarkSyntaxException>(() => it.Eval("1)\nprint('injected')\n_ = (2"));
        Assert.Contains("single expression", e.RawMessage);
        Assert.Empty(printed);
        Assert.Throws<StarlarkSyntaxException>(() => it.Eval("1)\n_: int = (2"));
        Assert.Equal(new BigInteger(3), it.Eval("1 + 2"));
    }

    /// <summary>Redefining a name with Define drops the C# signature Register recorded, so the checker does not use it.</summary>
    [Fact]
    public void DefineReplacesARegisteredSignature()
    {
        var it = New();
        it.Register("f", (int x) => x);
        Assert.NotEmpty(it.Typecheck("t", "f('hello')\n").Errors);
        it.Define("f", (interp, args, kwargs) => args[0]);
        Assert.Empty(it.Typecheck("t", "f('hello')\n").Errors);
        it.DefineValue("g", 42);
        it.Define("g", "now a string");
        Assert.Empty(it.Typecheck("t", "x = g + 'x'\n").Errors);
    }

    // ---------------------------------------------------------------- #40 #41 host tasks and huge timeouts

    /// <summary>A host task that faults with several exceptions is a Starlark error naming the first.</summary>
    [Fact]
    public void AHostTaskWithSeveralFaultsIsAStarlarkError()
    {
        var it = New();
        it.Register("two", () => Task.WhenAll(
            Task.Run(() => throw new InvalidOperationException("first")),
            Task.Run(async () => { await Task.Delay(20); throw new InvalidOperationException("second"); })));
        Assert.Contains("Host function failed", Fails("two()\n", it));
    }

    /// <summary>A timeout of 30 days or TimeSpan.MaxValue does not make a wait throw ArgumentOutOfRangeException.</summary>
    [Theory]
    [InlineData(30)]
    [InlineData(-1)]
    public void HugeTimeoutsDoNotThrow(int days)
    {
        foreach (bool inline in new[] { false, true })
        {
            var it = new Interpreter
            {
                Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableFutures = true },
                Timeout = days < 0 ? TimeSpan.MaxValue : TimeSpan.FromDays(days),
                FutureLimits = new FutureLimits { RunInline = inline },
            };
            it.Register("slow", async () => { await Task.Delay(10); return 1; });
            var m = it.Execute("t", "def w():\n    return 2\ndef run():\n    return spawn(w).get() + slow()\nr = run()\n");
            Assert.Equal(new BigInteger(3), m.Globals["r"]);
        }
    }

    // ---------------------------------------------------------------- #10 #42 steps of every run

    /// <summary>LastRunSteps is set for every outermost run, with or without tasks; LastRunStats stays null without tasks.</summary>
    [Fact]
    public void LastRunStepsIsRecordedForEveryRun()
    {
        var it = New();
        Assert.Equal(0, it.LastRunSteps);
        it.Execute("t", "def f():\n    x = 0\n    for i in range(100):\n        x += i\n    return x\nf()\n");
        Assert.True(it.LastRunSteps >= 200, $"only {it.LastRunSteps} steps");
        Assert.Null(it.LastRunStats);
        long loop = it.LastRunSteps;
        Assert.Throws<StarlarkException>(() => it.Execute("t", "fail('x')\n"));
        Assert.InRange(it.LastRunSteps, 1, loop - 1);   // a failed run records its steps too
        it.Eval("1 + 2");
        Assert.InRange(it.LastRunSteps, 1, 10);
    }

    // ---------------------------------------------------------------- #12 #24 #33 the int size cap

    /// <summary>Every way of making an int past the cap fails at once with the same message, under any limits.</summary>
    [Theory]
    [InlineData("x = pow2(1048575) << 1\n", "left shift")]
    [InlineData("x = pow2(1048100) << 511\n", "left shift")]   // 1,048,100 + 511 bits is past 2^20
    [InlineData("x = pow2(1000000) * pow2(1000000)\n", "multiplication")]
    [InlineData("x = pow2(1048575)\ny = x + x\n", "addition")]
    [InlineData("x = -pow2(1048575)\ny = x - pow2(1048575) - 1\n", "subtraction")]
    [InlineData("x = -pow2(1048575) - pow2(1048575)\ny = -x\n", "negation")]
    [InlineData("def f():\n    x = 3\n    for i in range(30):\n        x = x * x\nf()\n", "multiplication")]
    public void IntsPastTheCapAreAnError(string program, string operation)
    {
        var sw = Stopwatch.StartNew();
        string message = Fails(Pow2 + program, New(maxBytes: 64L << 20));
        Assert.Contains("Integer overflow in " + operation, message);
        Assert.Contains($"{Ops.MaxIntBits} bits", message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
    }

    /// <summary>The cap is exactly 2**20 bits: the largest and smallest ints are allowed, one more bit is not.</summary>
    [Fact]
    public void TheCapIsExact()
    {
        Assert.Equal(1 << 20, Ops.MaxIntBits);
        var max = (BigInteger.One << Ops.MaxIntBits) - 1;
        Assert.Equal(max, New().Execute("t", Pow2 + "r = pow2(1048575) - 1 + pow2(1048575)\n").Globals["r"]);
        Assert.Equal(-max, New().Execute("t", Pow2 + "r = -pow2(1048575) - pow2(1048575) + 1\n").Globals["r"]);
        Assert.Contains("Integer overflow", Fails(Pow2 + "x = (pow2(1048575) - 1 + pow2(1048575)) + 1\n"));
        Assert.Contains("Integer overflow", Fails(Pow2 + "x = (-pow2(1048575) - pow2(1048575)) // -1\n"));
    }

    /// <summary>A huge int literal is a syntax error, found quickly.</summary>
    [Fact]
    public void HugeIntLiteralsAreASyntaxError()
    {
        var sw = Stopwatch.StartNew();
        var e = Assert.Throws<StarlarkSyntaxException>(() => New().Execute("t", "x = " + new string('9', 400_000) + "\n"));
        Assert.Contains("too large", e.RawMessage);
        Assert.True(e.RawMessage.Length < 200);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
        Assert.Equal(BigInteger.Parse(new string('7', 3000)), Val(new string('7', 3000)));
    }

    /// <summary>int() of too many digits is an error at once; up to the cap it parses quickly and exactly, in every base.</summary>
    [Fact]
    public void IntParsingIsBoundedAndFast()
    {
        var sw = Stopwatch.StartNew();
        Assert.Contains("too many digits", Fails("x = int('7' * 400000)\n"));
        Assert.Contains("too many digits", Fails("x = int('1' * 1048577, 2)\n"));
        Assert.Equal(new BigInteger(300_000), Val("len(str(int('7' * 300000)))"));
        Assert.Equal(true, New().Execute("t", Pow2 + "r = int('f' * 200000, 16) == pow2(800000) - 1\n").Globals["r"]);
        Assert.Equal(true, New().Execute("t", Pow2 + "r = int('-0x' + 'f' * 1000, 0) == -(pow2(4000) - 1)\n").Globals["r"]);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
    }

    /// <summary>The largest ints print, in every base, quickly and in agreement with BigInteger.</summary>
    [Fact]
    public void IntPrintingIsFastAndExact()
    {
        var sw = Stopwatch.StartNew();
        var max = (BigInteger.One << (Ops.MaxIntBits - 1)) * 2 - 1;
        var m = New().Execute("t", Pow2 + "x = pow2(1048575) - 1 + pow2(1048575)\ns = str(x)\nh = '%x' % x\no = '%o' % x\nd = '{:d}'.format(-x)\nj = json.encode(x)\n");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        Assert.Equal(max.ToString(), m.Globals["s"]);
        Assert.Equal(max.ToString(), m.Globals["j"]);
        Assert.Equal("-" + max.ToString(), m.Globals["d"]);
        Assert.Equal(new string('f', Ops.MaxIntBits / 4), m.Globals["h"]);
        Assert.Equal(Ops.MaxIntBits / 3 + 1, ((string)m.Globals["o"]!).Length);
    }

    /// <summary>Digit conversion agrees with BigInteger exactly, around chunk and power boundaries, in every base used.</summary>
    [Fact]
    public void DigitConversionAgreesWithBigInteger()
    {
        var rng = new Random(7);
        foreach (int bits in new[] { 0, 1, 63, 64, 65, 127, 128, 1000, 19_999, 20_000, 20_001, 66_000, 200_000 })
        {
            for (int k = 0; k < 3; k++)
            {
                var bytes = new byte[bits / 8 + 1];
                rng.NextBytes(bytes);
                bytes[^1] = 0;
                var v = new BigInteger(bytes) >> (bytes.Length * 8 - bits);
                if (k == 1) v = (BigInteger.One << bits) - 1;
                if (k == 2) v = BigInteger.Pow(10, bits / 4);
                var it = New();
                it.DefineValue("v", v);
                var m = it.Execute("t", "d = str(v)\nback = int(d) == v\nhx = int('%x' % v, 16) == v\nbn = int('{:b}'.format(v), 2) == v\noc = int('%o' % v, 8) == v\n");
                Assert.Equal(v.ToString(), m.Globals["d"]);
                Assert.Equal(true, m.Globals["back"]);
                Assert.Equal(true, m.Globals["hx"]);
                Assert.Equal(true, m.Globals["bn"]);
                Assert.Equal(true, m.Globals["oc"]);
            }
        }
    }

    /// <summary>A loop of multiplications near the cap is bounded by the step limit and the deadline.</summary>
    [Fact]
    public void BigIntWorkIsChargedToTheBudgets()
    {
        var it = New();
        it.MaxSteps = 200_000;
        var sw = Stopwatch.StartNew();
        var e = Assert.Throws<ExecutionLimitException>(() =>
            it.Execute("t", Pow2 + "x = pow2(500000)\nfor i in range(1000000):\n    y = x * x\n"));
        Assert.Equal("steps", e.Limit);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"took {sw.Elapsed}");
    }

    /// <summary>A shift's result counts against the memory budget (a 10 KB budget is passed on the way to 2**1000000).</summary>
    [Fact]
    public void BigIntsCountAgainstTheMemoryBudget() =>
        Assert.Equal("memory", Assert.Throws<ExecutionLimitException>(() => New(maxBytes: 10_000).Execute("t", Pow2 + "x = pow2(1000000)\n")).Limit);

    /// <summary>json.decode of a number past the cap is an error.</summary>
    [Fact]
    public void JsonNumbersPastTheCapAreAnError() =>
        Assert.Contains("out of range", Fails("x = json.decode('1' + '0' * 400000)\n"));

    // ---------------------------------------------------------------- #25 #28 strings and bytes that grow too large

    /// <summary>Bytes repetition has the same ceiling as strings with no memory limit set, and is exact below it.</summary>
    [Fact]
    public void BytesRepetitionIsBoundedWithNoLimit()
    {
        Assert.Equal("excessive repeat (2 * 1500000000 elements)", Fails("x = b'ab' * 1500000000\n"));
        Assert.Equal("b\"ababab\"", Repr.Of(Val("b'ab' * 3")));
        Assert.Equal("b\"\"", Repr.Of(Val("b'ab' * -2")));
        Assert.Equal(new BigInteger(3_000_000), Val("len(b'abc' * 1000000)"));
    }

    /// <summary>replace that multiplies a string's size is refused before passing the memory limit.</summary>
    [Fact]
    public void ReplaceIsBounded()
    {
        var e = Assert.Throws<ExecutionLimitException>(() =>
            New(maxBytes: 64L << 20).Execute("t", "x = 'a' * 1000\ny = x.replace('a', x)\nz = y.replace('a', x)\n"));
        Assert.Equal("memory", e.Limit);
        Assert.Equal("Xb", Val("'ab'.replace('a', 'X')", New(maxBytes: 100)));
    }

    /// <summary>str of a container with many references to one big string is refused past the memory limit.</summary>
    [Fact]
    public void ReprOfContainersIsBounded()
    {
        Assert.Equal("memory", Assert.Throws<ExecutionLimitException>(() =>
            New(maxBytes: 64L << 20).Execute("t", "r = str(['a' * 10000000] * 5)\n")).Limit);
        Assert.Equal("memory", Assert.Throws<ExecutionLimitException>(() =>
            New(maxBytes: 64L << 20).Execute("t", "r = str({1: 'a' * 10000000, 2: 'b' * 10000000, 3: 'c' * 10000000, 4: 'd' * 10000000})\n")).Limit);
        Assert.Equal("memory", Assert.Throws<ExecutionLimitException>(() =>
            New(maxBytes: 64L << 20).Execute("t", "r = json.encode(['a' * 10000000] * 5)\n")).Limit);
    }

    // ---------------------------------------------------------------- #26 #31 ranges

    /// <summary>A slice of a range is a range, equal element for element to slicing the list, for many bounds and steps.</summary>
    [Fact]
    public void RangeSlicesMatchListSlices()
    {
        var sb = new StringBuilder("bad = []\n");
        sb.Append("for r in [range(0), range(7), range(2, 11, 3), range(10, -4, -2), range(-5, 5)]:\n");
        sb.Append("    l = list(r)\n");
        sb.Append("    for a in [None, -12, -3, -1, 0, 1, 2, 5, 12]:\n");
        sb.Append("        for b in [None, -12, -3, -1, 0, 1, 4, 12]:\n");
        sb.Append("            for c in [None, -3, -2, -1, 1, 2, 3]:\n");
        sb.Append("                s = r[a:b:c]\n");
        sb.Append("                if type(s) != 'range' or list(s) != l[a:b:c] or len(s) != len(l[a:b:c]):\n");
        sb.Append("                    bad.append((r, a, b, c))\n");
        var m = New().Execute("t", sb.ToString());
        Assert.Equal("[]", Repr.Of(m.Globals["bad"]));
    }

    /// <summary>Slicing a huge range costs nothing, whatever the limits.</summary>
    [Fact]
    public void SlicingAHugeRangeIsFree()
    {
        var it = New(maxBytes: 1 << 20);
        it.MaxSteps = 1000;
        Assert.Equal("range(400000000)", Repr.Of(Val("range(400000000)[:]", it)));
        Assert.Equal("range(1, 1099511627776, 2)", Repr.Of(Val("range(1 << 40)[1::2]", it)));
    }

    /// <summary>A range prints as the spec says: no start when it is 0, no step when it is 1.</summary>
    [Theory]
    [InlineData("range(10)", "range(10)")]
    [InlineData("range(0, 10)", "range(10)")]
    [InlineData("range(1, 10)", "range(1, 10)")]
    [InlineData("range(1, 10, 2)", "range(1, 10, 2)")]
    [InlineData("range(0, 10, 2)", "range(0, 10, 2)")]
    [InlineData("range(-1)", "range(-1)")]
    public void RangeReprFollowsTheSpec(string expr, string repr) => Assert.Equal(repr, Val("str(" + expr + ")"));

    // ---------------------------------------------------------------- #27 struct and record hashing

    /// <summary>Equal structs and records are the same dict key and set element.</summary>
    [Fact]
    public void StructsAndRecordsHashLikeTheyCompare()
    {
        Assert.Equal(true, Val("struct(a = 1) in {struct(a = 1): 'x'}"));
        Assert.Equal(new BigInteger(1), Val("len(dict([(struct(a = 1, b = 'x'), 1), (struct(b = 'x', a = 1), 2)]))"));
        // A literal with two equal keys is now reported as a duplicate, as for any other equal keys.
        Assert.Contains("Duplicate dictionary key", Fails("x = {struct(a = 1, b = 'x'): 1, struct(b = 'x', a = 1): 2}\n"));
        Assert.Equal(new BigInteger(1), Val("len(set([struct(a = 1), struct(a = 1)]))"));
        Assert.Equal(new BigInteger(2), Val("len(set([struct(a = 1), struct(a = 2)]))"));
        var m = New().Execute("t", "R = record(a = int, b = str)\nS = record(a = int, b = str)\nd = {R(a = 1, b = 'x'): 1}\nr = [R(a = 1, b = 'x') in d, S(a = 1, b = 'x') in d, len(set([R(a = 1, b = 'x'), R(a = 1, b = 'x')]))]\n");
        Assert.Equal("[True, False, 1]", Repr.Of(m.Globals["r"]));
    }

    /// <summary>A struct or record holding a mutable list cannot be a key.</summary>
    [Fact]
    public void StructsWithMutableFieldsAreUnhashable()
    {
        Assert.Contains("unhashable", Fails("x = {struct(a = [1]): 1}\n"));
        Assert.Contains("unhashable", Fails("R = record(a = list)\nx = {R(a = [1]): 1}\n"));
        Assert.Contains("unhashable", Fails("x = set([struct(a = struct(b = {}))])\n"));
    }

    /// <summary>Equal values have equal hashes, for every hashable kind of value, compared pairwise.</summary>
    [Fact]
    public void EqualValuesHaveEqualHashes()
    {
        const string program = """
            R = record(a = int)
            E = enum("x", "y")
            vals = [None, True, False, 0, 1, -1, 1.0, 0.0, -0.0, 2.5, "", "a", b"a", (), (1,), (1, 2), (1.0, 2),
                    struct(), struct(a = 1), struct(a = 1.0), struct(a = 1, b = 2), struct(b = 2, a = 1),
                    R(a = 1), R(a = 2), E("x"), E("y"), E[0], frozen]
            r = vals
            """;
        var it = New();
        it.DefineValue("frozen", new List<int> { 1, 2 });
        var vals = (StarList)it.Execute("t", program).Globals["r"]!;
        foreach (var a in vals.Items)
        {
            foreach (var b in vals.Items)
            {
                if (Ops.Equal(a, b)) Assert.True(Hashing.Hash(a) == Hashing.Hash(b), $"{Repr.Of(a)} == {Repr.Of(b)} but their hashes differ");
            }
        }
    }

    // ---------------------------------------------------------------- #29 code points in strip and replace

    /// <summary>
    /// Exhaustively over strings of up to three characters from {a, é, 😀, 😁} and cutsets of up to two: strip, lstrip, rstrip
    /// and replace('', '-') never leave an unpaired surrogate, and strip removes exactly the code points in the cutset.
    /// </summary>
    [Fact]
    public void StripAndReplaceWorkInCodePoints()
    {
        string[] alphabet = ["a", "é", "😀", "😁"];
        var strings = new List<string> { "" };
        for (int len = 1; len <= 3; len++)
        {
            foreach (var s in strings.Where(s => CodePoints(s).Count == len - 1).ToList())
            {
                foreach (var c in alphabet) strings.Add(s + c);
            }
        }
        var cutsets = strings.Where(s => CodePoints(s).Count is >= 1 and <= 2).ToList();
        var it = New();
        it.DefineValue("strings", strings);
        it.DefineValue("cutsets", cutsets);
        var m = it.Execute("t", """
            out = []
            for s in strings:
                out.append(s.replace("", "-"))
                for c in cutsets:
                    out.append([s, c, s.strip(c), s.lstrip(c), s.rstrip(c)])
            r = out
            """);
        foreach (var item in ((StarList)m.Globals["r"]!).Items)
        {
            if (item is string replaced)
            {
                Assert.False(HasLoneSurrogate(replaced), replaced);
                continue;
            }
            var row = ((StarList)item!).Items.Cast<string>().ToArray();
            var (s, c) = (row[0], row[1]);
            var cut = CodePoints(c).ToHashSet();
            var cps = CodePoints(s);
            int lo = 0, hi = cps.Count;
            while (lo < hi && cut.Contains(cps[lo])) lo++;
            while (hi > lo && cut.Contains(cps[hi - 1])) hi--;
            int l2 = 0;
            while (l2 < cps.Count && cut.Contains(cps[l2])) l2++;
            int r2 = cps.Count;
            while (r2 > 0 && cut.Contains(cps[r2 - 1])) r2--;
            Assert.Equal(Join(cps[lo..hi]), row[2]);
            Assert.Equal(Join(cps[l2..]), row[3]);
            Assert.Equal(Join(cps[..r2]), row[4]);
            foreach (var x in row) Assert.False(HasLoneSurrogate(x), $"{s} / {c}: {x}");
        }
        Assert.Equal("-a-😀-", Val("'a😀'.replace('', '-')"));
    }

    private static List<int> CodePoints(string s)
    {
        var list = new List<int>();
        foreach (var r in s.EnumerateRunes()) list.Add(r.Value);
        return list;
    }

    private static string Join(List<int> cps) => string.Concat(cps.Select(char.ConvertFromUtf32));

    private static bool HasLoneSurrogate(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
            else if (char.IsSurrogate(s[i])) return true;
        }
        return false;
    }

    /// <summary>In UTF-16 mode, replace('', ...) still inserts between UTF-16 elements, as before.</summary>
    [Fact]
    public void Utf16ReplaceStillUsesCodeUnits()
    {
        var it = New();
        it.StringElements = StringElements.Utf16;
        Assert.Equal(new BigInteger(5), Val("len('😀'.replace('', '-'))", it));   // -, high, -, low, -
    }

    // ---------------------------------------------------------------- #34 #36 #37 #38 futures

    private static Interpreter Futures(bool inline, Action<Interpreter>? configure = null)
    {
        var it = new Interpreter
        {
            Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableFutures = true, EnableRecursion = true },
            FutureLimits = new FutureLimits { RunInline = inline },
            MaxSteps = 20_000_000,
        };
        configure?.Invoke(it);
        return it;
    }

    private static (string Output, string Error) RunBoth(string program, bool inline, Action<Interpreter>? configure = null)
    {
        var printed = new List<string>();
        var it = Futures(inline, configure);
        it.Print = printed.Add;
        string error = "";
        try
        {
            it.Execute("t", program);
        }
        catch (Exception e) when (e is StarlarkException or ExecutionLimitException or OperationCanceledException)
        {
            error = e.GetType().Name + ": " + (e is StarlarkException s ? s.RawMessage : e.Message);
        }
        return (string.Join("\n", printed), error);
    }

    private static void AgreeInBothModes(string program, string expectedError, Action<Interpreter>? configure = null)
    {
        var threaded = RunBoth(program, false, configure);
        var inline = RunBoth(program, true, configure);
        Assert.Equal(threaded, inline);
        Assert.Contains(expectedError, threaded.Error);
    }

    /// <summary>Deep recursion in a task fails at the same depth with the same error threaded and inline.</summary>
    [Fact]
    public void TaskRecursionDepthIsTheSameInBothModes()
    {
        const string rec = "def rec(n):\n    if n == 0:\n        return 0\n    return 1 + rec(n - 1)\n";
        AgreeInBothModes(rec + "def run():\n    return spawn(rec, 900).get()\nprint(run())\n", "Task call depth exceeded");
        var ok = RunBoth(rec + "def run():\n    return spawn(rec, 90).get()\nprint(run())\n", inline: true);
        Assert.Equal(("90", ""), ok);
        Assert.Equal(ok, RunBoth(rec + "def run():\n    return spawn(rec, 90).get()\nprint(run())\n", inline: false));
        // The cap counts the spawner's calls too, so the result does not depend on how deep the spawn happened.
        AgreeInBothModes(rec + "def deep(n):\n    if n == 0:\n        return spawn(rec, 60).get()\n    return deep(n - 1)\nprint(deep(50))\n",
            "Task call depth exceeded");
        // On a 1 MB thread (a console host's main thread) the cap is reached before the stack check, inline.
        (string, string) small = default;
        var t = new Thread(() => small = RunBoth(rec + "def run():\n    return spawn(rec, 900).get()\nprint(run())\n", inline: true), 1024 * 1024);
        t.Start();
        t.Join();
        Assert.Contains("Task call depth exceeded", small.Item2);
    }

    /// <summary>After a task hits a limit, a later spawn and get report that limit (not cancellation), in both modes.</summary>
    [Fact]
    public void ALimitInATaskIsReportedTheSameInBothModes()
    {
        const string burn = "def burn():\n    for i in range(100000000):\n        pass\ndef ok():\n    print('ok ran')\n    return 1\n";
        // pause() gives a worker time to run burn to its limit, as inline mode does at the spawn itself.
        static void Pause(Interpreter it) => it.Register("pause", () => { Thread.Sleep(300); return 0; });
        const string limit = "ExecutionLimitException: Script exceeded the limit of 20000000 steps";
        AgreeInBothModes(burn + "def run():\n    f = spawn(burn)\n    pause()\n    g = spawn(ok)\n    print('got', g.get())\n    f.get()\nrun()\n", limit, Pause);
        AgreeInBothModes(burn + "def run():\n    f = spawn(burn)\n    pause()\n    f.get()\nrun()\n", limit, Pause);
        AgreeInBothModes(burn + "def run():\n    f = spawn(burn)\n    pause()\n    return parallel_map(abs, [1, 2])\nrun()\n", limit, Pause);
        // A future that finished before the limit was hit still reports the limit: the run is over.
        AgreeInBothModes(burn + "def run():\n    g = spawn(ok)\n    f = spawn(burn)\n    pause()\n    print('got', g.get())\nrun()\n", limit, Pause);
    }

    /// <summary>Output held by uncollected tasks is capped for the whole run, not only per task.</summary>
    [Fact]
    public void BufferedTaskOutputIsCappedForTheRun()
    {
        const string program = "def p(i):\n    s = 'x' * 1000000\n    for k in range(4):\n        print(s)\n    return i\ndef run():\n    fs = [spawn(p, i) for i in range(150)]\n    return len(all_of(fs))\nprint(run())\n";
        AgreeInBothModes(program, "Tasks printed more than", it => it.MaxAllocatedBytes = 256L << 20);
        // Collecting tasks as they finish frees their share, so the same total output in a loop of spawn/get is fine.
        var (output, error) = RunBoth("def p(i):\n    print('x' * 1000000)\n    return i\ndef run():\n    n = 0\n    for i in range(40):\n        n += spawn(p, i).get()\n    return n\nprint(len(str(run())))\n", inline: false);
        Assert.Equal("", error);
        Assert.EndsWith("3", output);
    }

    /// <summary>A chain of 20,000 module functions can be frozen and passed to a task without overflowing the stack.</summary>
    [Fact]
    public void ALongChainOfGlobalFunctionsCanCrossToATask()
    {
        var sb = new StringBuilder("def f0():\n    return 0\n");
        for (int i = 1; i <= 20000; i++) sb.Append($"def f{i}():\n    return f{i - 1}\n");
        var plain = sb.ToString();
        New().Execute("t", plain);   // freezing the module
        sb.Append("def run():\n    return spawn(f20000).get()\nprint(run())\n");
        foreach (bool inline in new[] { false, true })
        {
            var printed = new List<string>();
            var it = Futures(inline);
            it.Print = printed.Add;
            it.Execute("t", sb.ToString());
            Assert.Equal(["<function f19999>"], printed);
        }
    }
}
