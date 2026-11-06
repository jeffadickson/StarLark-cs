using System.Diagnostics;
using System.Numerics;
using System.Text;
using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>
/// The safety limits adopted from Starlark-Go (QA-ROB-001), and the walkers that must survive the deepest tree the parser
/// allows. Deeply nested source text used to end the process with an uncatchable stack overflow, in the parser for nested
/// lists, unary operators and <c>not</c>, and in the evaluator for a 1000-term sum (a left-deep tree evaluated recursively).
/// Each case here runs on a thread with an ordinary 1 MB stack (4 MB for a Debug build of the library, whose unoptimized
/// frames are about twice as large), so it proves what a host thread can do, not what the test runner's thread can do.
/// </summary>
public class GoLimitsTests
{
    private const int Limit = SyntaxLimits.MaxNesting;

    private static Interpreter New(bool recursion = false) => new()
    {
        Dialect = Dialect.Standard with
        {
            EnableTopLevelStmt = true, EnableFStrings = true, EnableTypes = DialectTypes.Enable, EnableRecursion = recursion,
        },
        Print = _ => { },
        MaxSteps = 50_000_000,
        Timeout = TimeSpan.FromSeconds(60),
    };

    private static object? Val(string expression) => New().Execute("t", "r = " + expression + "\n").Globals["r"];

    private static string Fails(string statements, Interpreter? it = null) =>
        Assert.Throws<StarlarkException>(() => (it ?? New()).Execute("t", statements)).RawMessage;

    private static string Repeat(string s, int n) => new StringBuilder(s.Length * n).Insert(0, s, n).ToString();

    /// <summary>The stack an ordinary host thread has: 1 MB, or 4 MB for an unoptimized (Debug) build of the library.</summary>
    private static int OrdinaryStack { get; } =
        typeof(Interpreter).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false)
            .Cast<DebuggableAttribute>().Any(d => d.IsJITOptimizerDisabled) ? 4 << 20 : 1 << 20;

    /// <summary>Runs <paramref name="body"/> on a new thread with <paramref name="stack"/> bytes; returns what it threw, or null.</summary>
    private static Exception? OnThread(Action body, int stack)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { failure = e; }
        }, stack);
        thread.Start();
        thread.Join();
        return failure;
    }

    // ---------------------------------------------------------------- every walker survives the deepest tree

    /// <summary>Programs nested <c>n</c> levels deep, by construct; each evaluates to something small.</summary>
    private static string Nested(string construct, int n) => construct switch
    {
        "parens" => "x = " + new string('(', n) + "1" + new string(')', n) + "\n",
        "lists" => "x = len(" + new string('[', n - 1) + new string(']', n - 1) + ")\n",
        "dicts" => "x = len(" + Repeat("{1: ", n - 1) + "1" + new string('}', n - 1) + ")\n",
        "tuples" => "x = len(" + Repeat("(1, ", n - 1) + "1" + new string(')', n - 1) + ")\n",
        "minus" => "x = " + new string('-', n) + "1\n",
        "tilde" => "x = " + new string('~', n) + "1\n",
        "not" => "x = " + Repeat("not ", n) + "True\n",
        "lambda" => "x = (" + Repeat("lambda: ", n - 1) + "1)" + Repeat("()", n - 1) + "\n",
        "ternary" => "x = " + Repeat("0 if False else ", n) + "1\n",
        "right-operand" => "x = " + Repeat("1 + (", n / 2) + "1" + new string(')', n / 2) + "\n",
        "calls" => "def f(v):\n    return v\nx = " + Repeat("f(", n - 2) + "1" + new string(')', n - 2) + "\n",
        "builtins" => "x = " + Repeat("str(", n) + "1" + new string(')', n) + "\n",
        "attributes" => "x = 'a'" + Repeat(".upper()", n / 2) + "\n",
        "comprehension" => "x = len(" + Repeat("[y for y in ", n - 1) + "[1]" + new string(']', n - 1) + ")\n",
        "fstring" => "x = " + Repeat("f'{", n) + "1" + Repeat("}'", n) + "\n",
        "if" => Blocks("if True:", n) + new string(' ', n) + "x = 1\n",
        "for" => ForBlocks(n) + new string(' ', n) + "x = 1\n",
        "def" => Blocks("def f():", n - 1) + new string(' ', n - 1) + "return 1\n" + "x = 1\n",
        "elif" => "if False:\n  pass\n" + Repeat("elif False:\n  pass\n", n - 2) + "else:\n  x = 1\n",
        "annotation" => "x: " + Repeat("list[", n - 1) + "int" + new string(']', n - 1) + " = []\n",
        _ => throw new ArgumentException(construct),
    };

    /// <summary>Nested <c>for</c> loops, each with its own variable (a global may be bound only once).</summary>
    private static string ForBlocks(int n)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++) sb.Append(' ', i).Append($"for v{i} in [1]:\n");
        return sb.ToString();
    }

    private static string Blocks(string header, int n)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++) sb.Append(' ', i).Append(header).Append('\n');
        return sb.ToString();
    }

    public static readonly TheoryData<string> Constructs = new()
    {
        "parens", "lists", "dicts", "tuples", "minus", "tilde", "not", "lambda", "ternary", "right-operand", "calls", "builtins",
        "attributes", "comprehension", "fstring", "if", "for", "def", "elif", "annotation",
    };

    /// <summary>
    /// The deepest program the parser accepts parses, resolves, runs and type checks on an ordinary stack, the first time it
    /// is seen. (The walkers on the recursive paths are compiled optimized at once for this reason: unoptimized first-call
    /// code has frames several times larger.)
    /// </summary>
    [Theory]
    [MemberData(nameof(Constructs))]
    public void TheDeepestAllowedProgramRunsAndChecksOnAnOrdinaryStack(string construct)
    {
        string source = Nested(construct, Limit - 1);
        object? x = null;
        Assert.Null(OnThread(() =>
        {
            var it = New();
            var ast = AstModule.Parse("t.star", source, it.Dialect);
            x = it.Execute(ast).Globals["x"];
            _ = it.Typecheck(ast);
            _ = ast.Statement.ToString();
        }, OrdinaryStack));
        Assert.NotNull(x);
    }

    /// <summary>One level deeper and every construct is a syntax error, before anything runs.</summary>
    [Theory]
    [MemberData(nameof(Constructs))]
    public void OneLevelTooDeepIsASyntaxError(string construct)
    {
        var e = OnThread(() => New().Execute("t.star", Nested(construct, Limit + 3)), OrdinaryStack);
        Assert.Contains("excessive nesting", Assert.IsType<StarlarkSyntaxException>(e).RawMessage);
    }

    /// <summary>A function body nests inside its caller's evaluation: the stack guard ends a too-deep combination cleanly.</summary>
    [Fact]
    public void NestingAcrossCallsEndsInAnErrorNotACrash()
    {
        // Each call evaluates an expression nested 900 deep that calls the next function: nesting adds up across calls.
        var sb = new StringBuilder();
        for (int i = 0; i < 40; i++)
        {
            sb.Append($"def f{i}():\n    return ").Append(new string('-', 900)).Append(i == 39 ? "1" : $"f{i + 1}()").Append('\n');
        }
        sb.Append("x = f0()\n");
        var e = OnThread(() => New().Execute("t.star", sb.ToString()), OrdinaryStack);
        var se = Assert.IsType<StarlarkException>(e);
        Assert.True(se.RawMessage.Contains("stack overflow") || se.RawMessage.Contains("nested too deeply"), se.RawMessage);
    }

    /// <summary>On a tiny thread every walker stops with an ordinary error before the stack runs out.</summary>
    [Theory]
    [InlineData("lists")]
    [InlineData("calls")]
    [InlineData("if")]
    [InlineData("minus")]
    [InlineData("annotation")]
    public void ATinyStackEndsInAnErrorNotACrash(string construct)
    {
        string source = Nested(construct, Limit - 1);
        var e = OnThread(() =>
        {
            var it = New();
            var ast = AstModule.Parse("t.star", source, it.Dialect);
            it.Execute(ast);
        }, 256 * 1024);
        Assert.True(e is StarlarkException or StarlarkSyntaxException, e?.ToString());
        e = OnThread(() =>
        {
            var it = New();
            it.Typecheck(AstModule.Parse("t.star", source, it.Dialect));
        }, 256 * 1024);
        Assert.True(e is null or StarlarkException or StarlarkSyntaxException, e?.ToString());
    }

    // ---------------------------------------------------------------- long chains of one operator

    /// <summary>
    /// A 1000-term sum is ordinary generated configuration, not an attack. It is a left-deep tree 1000 deep (as in Go), and
    /// it must work on an ordinary stack; so must much longer chains, which every walker visits with a loop.
    /// </summary>
    [Theory]
    [InlineData(1_000)]
    [InlineData(100_000)]
    public void ALongSumWorksOnAnOrdinaryStack(int terms)
    {
        object? x = null;
        Assert.Null(OnThread(() =>
        {
            var it = New();
            var ast = AstModule.Parse("t.star", "x = 1" + Repeat(" + 1", terms - 1) + "\n", it.Dialect);
            x = it.Execute(ast).Globals["x"];
            Assert.Empty(it.Typecheck(ast).Errors);
        }, OrdinaryStack));
        Assert.Equal(new BigInteger(terms), x);
    }

    /// <summary>Mixed operators of one precedence, string concatenation, and/or chains and a long union type annotation.</summary>
    [Fact]
    public void OtherLongChainsWork()
    {
        Assert.Null(OnThread(() =>
        {
            Assert.Equal(new BigInteger(1), Val("1" + Repeat(" + 2 - 2", 20_000)));
            Assert.Equal(new string('a', 10_000), Val("''" + Repeat(" + 'a'", 10_000)));
            Assert.Equal(true, Val("False" + Repeat(" or False", 20_000) + " or True"));
            Assert.Equal(false, Val("True" + Repeat(" and True", 20_000) + " and False"));
            Assert.Equal(new BigInteger(42), Val("2 * 3 * 7" + Repeat(" * 1", 20_000)));
            var it = New();
            var ast = AstModule.Parse("t.star", "def f(x: int" + Repeat(" | str", 5_000) + ") -> None:\n    pass\nf(1)\n", it.Dialect);
            it.Execute(ast);
            Assert.Empty(it.Typecheck(ast).Errors);
        }, OrdinaryStack));
    }

    /// <summary>An error inside a long chain still points at the operator that failed, and each operator still costs a step.</summary>
    [Fact]
    public void AnErrorInsideALongChainIsReportedAtItsOperator()
    {
        var e = Assert.Throws<StarlarkException>(() => New().Execute("t.star", "x = 1" + Repeat(" + 1", 500) + " + 'a'" + Repeat(" + 1", 500) + "\n"));
        Assert.Contains("+", e.RawMessage);
        Assert.Equal(0, e.CodeMap!.ResolveSpan(e.Span!.Value).Begin.Line);
        Assert.Equal(4, e.CodeMap.ResolveSpan(e.Span.Value).Begin.Column);   // the chain up to the bad operand starts at `1`
        var it = New();
        it.Execute("t.star", "x = 1" + Repeat(" + 1", 999) + "\n");
        Assert.True(it.LastRunSteps >= 2000, $"{it.LastRunSteps} steps");   // an operator and an operand per term
        it.MaxSteps = 500;
        Assert.Equal("steps", Assert.Throws<ExecutionLimitException>(() => it.Execute("t.star", "x = 1" + Repeat(" + 1", 999) + "\n")).Limit);
    }

    /// <summary>A long chain short-circuits exactly as the nested form does.</summary>
    [Fact]
    public void ShortCircuitInALongChain()
    {
        var printed = new List<string>();
        var it = New();
        it.Print = printed.Add;
        it.Execute("t.star", "def p(v):\n    print(v)\n    return v\nx = p(0) or p(0) or p(1) or p(2) or p(3)\ny = p(1) and p(0) and p(9)\n");
        Assert.Equal(["0", "0", "1", "1", "0"], printed);
    }

    // ---------------------------------------------------------------- repetition

    /// <summary>Go's repetition rules, for all four sequence types: the count must fit in an int32.</summary>
    [Theory]
    [InlineData("'ab'")]
    [InlineData("b'ab'")]
    [InlineData("[1, 2]")]
    [InlineData("(1, 2)")]
    public void ARepeatCountPastInt32IsTooLarge(string seq)
    {
        Assert.Equal("repeat count 2147483648 too large", Fails($"x = {seq} * 2147483648\n"));
        Assert.Equal("repeat count 1000000000000 too large", Fails($"x = {seq} * (1000000 * 1000000)\n"));
        Assert.Equal("repeat count -2147483649 too large", Fails($"x = -2147483649 * {seq}\n"));
    }

    /// <summary>
    /// The cases from Starlark-Go's own test data (starlark/testdata/string.star, tuple.star, int.star), which our copies
    /// of the shared conformance files leave commented out, with Go's patterns.
    /// </summary>
    [Theory]
    [InlineData("\"abc\" * (1000000 * 1000000)", "repeat count 1000000000000 too large")]
    [InlineData("\"abc\" * 1000000 * 1000000", @"excessive repeat \(3000000 \* 1000000 elements")]
    [InlineData("(\"a\", \"b\", \"c\") * (1000000 * 1000000)", "repeat count 1000000000000 too large")]
    [InlineData("(\"a\", \"b\", \"c\") * 1000000 * 1000000", @"excessive repeat \(3000000 \* 1000000 elements")]
    [InlineData("2 << -1", "negative shift count")]
    [InlineData("1 << 512", "shift count too large")]
    public void GosOwnTestData(string expression, string pattern) =>
        Assert.Matches(pattern, Fails($"x = {expression}\n"));

    /// <summary>The result must have fewer than 2^30 elements: chars, bytes or items.</summary>
    [Theory]
    [InlineData("'ab'", "2")]
    [InlineData("b'ab'", "2")]
    [InlineData("[1, 2]", "2")]
    [InlineData("(1, 2)", "2")]
    [InlineData("'abc'", "3")]
    public void AResultOf2To30ElementsIsExcessive(string seq, string length)
    {
        Assert.Equal($"excessive repeat ({length} * 1073741824 elements)", Fails($"x = {seq} * 1073741824\n"));
        Assert.Equal($"excessive repeat ({length} * 2147483647 elements)", Fails($"x = {seq} * 2147483647\n"));
    }

    /// <summary>Exactly 2^30 - 1 elements is under the ceiling; the memory budget then decides, and reports a memory error.</summary>
    [Fact]
    public void JustUnderTheCeilingIsLeftToTheMemoryBudget()
    {
        var it = New();
        it.MaxAllocatedBytes = 64L << 20;
        Assert.Equal("memory", Assert.Throws<ExecutionLimitException>(() => it.Execute("t", "x = 'a' * 1073741823\n")).Limit);
        Assert.Equal("memory", Assert.Throws<ExecutionLimitException>(() => it.Execute("t", "x = [0] * 1073741823\n")).Limit);
        Assert.Equal("memory", Assert.Throws<ExecutionLimitException>(() => it.Execute("t", "x = b'a' * 1073741823\n")).Limit);
        Assert.Equal("memory", Assert.Throws<ExecutionLimitException>(() => it.Execute("t", "x = (0,) * 1073741823\n")).Limit);
        Assert.Equal(Ops.MaxRepeatElements - 1, MemoryBudget.MaxStringChars);
    }

    /// <summary>Counts below one, empty sequences and ordinary repetition work as in Go (an empty sequence accepts any count).</summary>
    [Fact]
    public void OrdinaryRepetition()
    {
        Assert.Equal("ababab", Val("'ab' * 3"));
        Assert.Equal("ababab", Val("3 * 'ab'"));
        Assert.Equal("", Val("'ab' * 0"));
        Assert.Equal("", Val("'ab' * -5"));
        Assert.Equal("", Val("'' * (1 << 100)"));
        Assert.Equal(new BigInteger(0), Val("len([] * (1 << 100))"));
        Assert.Equal(new BigInteger(0), Val("len(() * -(1 << 100))"));
        Assert.Equal("[1, 2, 1, 2]", Repr.Of(Val("[1, 2] * 2")));
        Assert.Equal("(1, 2, 1, 2, 1, 2)", Repr.Of(Val("(1, 2) * 3")));
        Assert.Equal("b\"xyxy\"", Repr.Of(Val("b'xy' * 2")));
        Assert.Equal(new BigInteger(3_000_007), Val("len([1, 2, 3, 4, 5, 6, 7] * 428572 + [1, 2, 3])"));
        Assert.Equal(new BigInteger(1_000_000), Val("len((1,) * 1000000)"));
        Assert.Equal(new BigInteger(7), Val("((1, 2, 3, 4, 5, 6, 7) * 1000)[6999]"));
        var m = New().Execute("t", "a = [[]] * 3\na[0].append(1)\nr = a\n");   // the items are shared, as in Python and Go
        Assert.Equal("[[1], [1], [1]]", Repr.Of(m.Globals["r"]));
    }

    /// <summary>A utf16 string repeats under the same ceiling, counted in UTF-16 code units.</summary>
    [Fact]
    public void Utf16Repetition()
    {
        Assert.Equal("excessive repeat (2 * 1073741824 elements)", Fails("x = utf16('ab') * 1073741824\n"));
        Assert.Equal("abab", ((StarUtf16)Val("utf16('ab') * 2")!).Value);
    }

    // ---------------------------------------------------------------- shifts

    /// <summary>Go's shift rules: a left shift count below 512, and no negative count either way.</summary>
    [Fact]
    public void ShiftCounts()
    {
        Assert.Equal(BigInteger.One << 511, Val("1 << 511"));
        Assert.Equal("shift count too large: 512", Fails("x = 1 << 512\n"));
        Assert.Equal("shift count too large: 512", Fails("x = 0 << 512\n"));
        Assert.Equal("shift count too large: 1099511627776", Fails("x = 1 << (1 << 40)\n"));
        Assert.Equal("negative shift count: -1", Fails("x = 2 << -1\n"));
        Assert.Equal("negative shift count: -1", Fails("x = 2 >> -1\n"));
        Assert.Equal(BigInteger.Zero, Val("5 >> 100000"));
        Assert.Equal(BigInteger.MinusOne, Val("-5 >> (1 << 40)"));
        Assert.Equal("shift count too large: 600", Fails("def f():\n    x = 1\n    x <<= 600\nf()\n"));
    }

    /// <summary>The int size cap (2^20 bits) still holds and is still reached, by repeated shifts or by multiplication.</summary>
    [Fact]
    public void TheIntSizeCapIsStillReachable()
    {
        // 2052 shifts of 511 bits pass 2^20 bits; the largest int (2^1048576 - 1) is still reachable by shifts and sums.
        Assert.Contains("Integer overflow in left shift", Fails("def f():\n    x = 1\n    for i in range(2100):\n        x = x << 511\nf()\n"));
        Assert.Contains("Integer overflow in multiplication", Fails("def f():\n    y = 3\n    for i in range(30):\n        y = y * y\nf()\n"));
        // 2052 * 511 + 3 = 2^20 - 1: top() is 2^1048575, the largest power of two allowed, and 2 * top() - 1 the largest int.
        var top = "def top():\n    x = 1\n    for i in range(2052):\n        x = x << 511\n    return x << 3\n";
        Assert.Equal(new BigInteger(315653), New().Execute("t", top + "r = len(str(top() - 1 + top()))\n").Globals["r"]);
    }

    // ---------------------------------------------------------------- recursion and ScriptStackSize

    private const string Countdown = "def d(n):\n    if n == 0:\n        return 0\n    return 1 + d(n - 1)\n";

    /// <summary>
    /// On an ordinary 1 MB thread a simple recursive function gets at least 600 calls deep (about 1.3 KB of stack per call,
    /// and the runtime keeps back about 180 KB), up from about 180 before the evaluator's frames were made smaller. The
    /// default cap of 1000 is therefore reached only on a bigger stack (see <see cref="Interpreter.ScriptStackSize"/>); on 1 MB
    /// the run ends sooner with the same "Starlark stack overflow" error, never a crash.
    /// </summary>
    [Fact]
    public void RecursionOnAnOrdinaryStack()
    {
        object? r = null;
        Assert.Null(OnThread(() => r = New(recursion: true).Execute("t", Countdown + "r = d(600)\n").Globals["r"], OrdinaryStack));
        Assert.Equal(new BigInteger(600), r);
        var e = OnThread(() => New(recursion: true).Execute("t", Countdown + "r = d(5000)\n"), OrdinaryStack);
        Assert.Matches("Starlark stack overflow", Assert.IsType<StarlarkException>(e).RawMessage);
    }

    /// <summary>With a 4 MB script stack the default cap of 1000 is the limit that ends a runaway recursion.</summary>
    [Fact]
    public void TheDefaultCapIsReachedWithAModestScriptStack()
    {
        var it = New(recursion: true);
        it.ScriptStackSize = 4L << 20;
        if (OrdinaryStack > 1 << 20) it.ScriptStackSize = 16L << 20;   // unoptimized Debug frames are larger
        Assert.Equal(new BigInteger(998), it.Execute("t", Countdown + "r = d(998)\n").Globals["r"]);
        Assert.Matches("Starlark stack overflow: more than 1000 nested calls", Fails(Countdown + "r = d(1000)\n", it));
    }

    /// <summary>Raising MaxCallDepth past what the stack holds ends in "Starlark stack overflow", not a crash.</summary>
    [Fact]
    public void ARaisedCapIsStillBoundedByTheStack()
    {
        var it = New(recursion: true);
        it.MaxCallDepth = 1_000_000;
        var e = OnThread(() => it.Execute("t", Countdown + "r = d(500000)\n"), OrdinaryStack);
        Assert.Matches("Starlark stack overflow", Assert.IsType<StarlarkException>(e).RawMessage);
    }

    /// <summary>
    /// Go allows 100,000 nested calls. With a 256 MB script stack and the cap raised, so do we (99,998 calls of a simple
    /// function, plus the module and the call itself), and the next call past the cap is "Starlark stack overflow". A Debug
    /// build of the library needs about three times the stack per call, so there the script gets 1 GB.
    /// </summary>
    [Fact]
    public void GosRecursionDepthWithABigScriptStack()
    {
        bool debug = OrdinaryStack > 1 << 20;
        var it = New(recursion: true);
        it.MaxCallDepth = 100_000;
        it.ScriptStackSize = debug ? 1L << 30 : 256L << 20;
        var sw = Stopwatch.StartNew();
        Assert.Equal(new BigInteger(99_999), it.Execute("t", Countdown + "r = d(99999)\n").Globals["r"]);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"took {sw.Elapsed}");
        Assert.Matches("Starlark stack overflow: more than 100000 nested calls", Fails(Countdown + "r = d(100000)\n", it));
    }

    /// <summary>The same program fails on the caller's stack and works with a script stack; nothing else changes.</summary>
    [Fact]
    public void AScriptStackRunsWhatTheCallersStackCannot()
    {
        var it = New(recursion: true);
        it.MaxCallDepth = 20_000;
        var e = OnThread(() => it.Execute("t", Countdown + "r = d(10000)\n"), OrdinaryStack);
        Assert.Matches("Starlark stack overflow", Assert.IsType<StarlarkException>(e).RawMessage);
        it.ScriptStackSize = 64L << 20;
        object? r = null;
        Assert.Null(OnThread(() => r = it.Execute("t", Countdown + "r = d(10000)\n").Globals["r"], OrdinaryStack));
        Assert.Equal(new BigInteger(10000), r);
    }

    /// <summary>Results, errors (with their stack trace and call stack), Call, Eval and CallValue all come back unchanged.</summary>
    [Fact]
    public void AScriptStackIsTransparentToTheHost()
    {
        var it = New(recursion: true);
        it.ScriptStackSize = 16L << 20;
        int callerThread = Environment.CurrentManagedThreadId;
        int hostThread = 0;
        it.Register("where", () => hostThread = Environment.CurrentManagedThreadId);
        var m = it.Execute("t.star", Countdown + "where()\ndef boom(n):\n    if n == 0:\n        fail('deep')\n    return boom(n - 1)\n");
        Assert.NotEqual(callerThread, hostThread);   // host functions run on the script's thread
        Assert.Equal(new BigInteger(50), it.Call<BigInteger>(m, "d", 50));
        Assert.Equal(new BigInteger(7), it.Eval("3 + 4"));
        Assert.Equal(new BigInteger(5), it.CallValue(m.Globals["d"], [new BigInteger(5)]));

        var e = Assert.Throws<StarlarkException>(() => it.Call<object>(m, "boom", 3));
        Assert.Contains("deep", e.RawMessage);
        Assert.Equal(["boom", "boom", "boom", "boom"], e.CallStack.Select(f => f.Function).ToArray());
        // Exactly what the same call reports on the caller's thread.
        var plain = New(recursion: true);
        plain.Register("where", () => 0);
        var m0 = plain.Execute("t.star", Countdown + "where()\ndef boom(n):\n    if n == 0:\n        fail('deep')\n    return boom(n - 1)\n");
        var e0 = Assert.Throws<StarlarkException>(() => plain.Call<object>(m0, "boom", 3));
        Assert.Equal(e0.Message, e.Message);
        Assert.Equal(e0.CallStack.Select(f => f.ToString()), e.CallStack.Select(f => f.ToString()));
        // The original .NET stack trace survives the hop between threads: it still shows where the script failed.
        Assert.Contains("CallFunction", e.StackTrace);
        Assert.Throws<StarlarkSyntaxException>(() => it.Execute("t.star", "x = (\n"));
        Assert.Throws<ExecutionLimitException>(() => { it.MaxSteps = 100; it.Execute("t.star", Countdown + "r = d(500)\n"); });
    }

    /// <summary>Cancellation and Timeout end a run on a script stack as they do on the caller's.</summary>
    [Fact]
    public void AScriptStackHonoursCancellationAndTimeout()
    {
        const string spin = "def spin():\n    for i in range(1000000000):\n        pass\nspin()\n";
        var it = New();
        it.MaxSteps = 0;
        it.ScriptStackSize = 16L << 20;
        it.Timeout = TimeSpan.FromMilliseconds(200);
        var sw = Stopwatch.StartNew();
        Assert.Equal("time", Assert.Throws<ExecutionLimitException>(() => it.Execute("t", spin)).Limit);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");

        it.Timeout = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        it.Cancellation = cts.Token;
        Assert.ThrowsAny<OperationCanceledException>(() => it.Execute("t", spin));
    }

    /// <summary>A host function calling back into the script stays on the script's thread rather than starting another.</summary>
    [Fact]
    public void ACallBackFromAHostFunctionStaysOnTheScriptThread()
    {
        var it = New();
        it.ScriptStackSize = 16L << 20;
        var threads = new List<int>();
        Module? module = null;
        it.Define("record_thread", (_, _, _) => { threads.Add(Environment.CurrentManagedThreadId); return null; });
        it.Register("again", () =>
        {
            threads.Add(Environment.CurrentManagedThreadId);
            return it.Call<BigInteger>(module!, "inner");   // a call into the script from inside a running script
        });
        module = it.Execute("lib", "def inner():\n    record_thread()\n    return 3\n");
        var m = it.Execute("main", "r = again()\n");
        Assert.Equal(new BigInteger(3), m.Globals["r"]);
        Assert.Equal(2, threads.Count);
        Assert.Equal(threads[0], threads[1]);   // the call back ran on the script's thread, not on a third one
        Assert.NotEqual(Environment.CurrentManagedThreadId, threads[0]);
    }

    /// <summary>Values outside the accepted range are refused; 0 means the caller's thread, and is the default.</summary>
    [Fact]
    public void ScriptStackSizeIsValidated()
    {
        var it = new Interpreter();
        Assert.Equal(0, it.ScriptStackSize);
        Assert.Throws<ArgumentOutOfRangeException>(() => it.ScriptStackSize = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => it.ScriptStackSize = 1000);
        Assert.Throws<ArgumentOutOfRangeException>(() => it.ScriptStackSize = 1L << 40);
        it.ScriptStackSize = Interpreter.MinScriptStackSize;
        it.ScriptStackSize = 0;
        int caller = Environment.CurrentManagedThreadId, ran = 0;
        it.Register("where", () => ran = Environment.CurrentManagedThreadId);
        it.Execute("t", "where()\n");
        Assert.Equal(caller, ran);   // by default the script runs on the caller's thread
    }

    // ---------------------------------------------------------------- crashes found on the way

    /// <summary>
    /// <c>Span.ToString()</c> used to end the process with a stack overflow: the compiler-generated record ToString printed
    /// <c>EndSpan</c>, itself a Span with an EndSpan, without end. Any record holding a span (StackFrame, TypeDiagnostic,
    /// a debugger's watch window) hit it too.
    /// </summary>
    [Fact]
    public void SpansAndStackFramesPrint()
    {
        Assert.Null(OnThread(() =>
        {
            Assert.Equal("3..7", new Span(3, 7).ToString());
            var e = Assert.Throws<StarlarkException>(() => New().Execute("t.star", "def f():\n    fail('x')\nf()\n"));
            Assert.Equal(["t.star:2:5: in f", "t.star:3:1: in <module>"], e.CallStack.Select(f => f.ToString()).ToArray());
            Assert.Equal("in g", new StackFrame("g", null, null).ToString());
            var diag = New().Typecheck("t.star", "x = 1 + 'a'\n").Errors.Single();
            Assert.Contains("Span", diag.ToString() + " Span " + new Spanned<int>(1, new Span(0, 1)));
        }, OrdinaryStack));
    }

    // ---------------------------------------------------------------- deliberate deviations

    /// <summary>
    /// Go caps comparison depth at 10 (CompareLimit), which rejects ordinary deep data; that is not adopted. Comparing deep
    /// values is bounded by the stack guard instead (StackDepthTests covers values nested 200,000 deep).
    /// </summary>
    [Fact]
    public void ComparingDeepValuesIsNotCappedAtGosTen()
    {
        const string build = "def build(n):\n    a = []\n    for i in range(n):\n        a = [a]\n    return a\n";
        Assert.Equal(true, New().Execute("t", build + "r = build(50) == build(50)\n").Globals["r"]);
        Assert.Equal(true, New().Execute("t", build + "r = build(50) < build(51)\n").Globals["r"]);
    }
}
