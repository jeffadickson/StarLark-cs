using System.Diagnostics;
using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>
/// Regression tests for the crash and railway-oriented-error audit (QA-ROB-001): no script input may crash, hang or exhaust
/// memory in the host, and only <see cref="StarlarkSyntaxException"/>, <see cref="StarlarkException"/>,
/// <see cref="ExecutionLimitException"/> and <see cref="OperationCanceledException"/> may reach it. Every interpreter here has
/// step, time and (where it matters) memory limits, so a regression fails fast instead of eating the machine; work that the
/// old code could not bound at all runs under <see cref="Within"/>, which fails the test after a wall-clock limit.
/// </summary>
public class AuditRegressionTests
{
    private static Interpreter New(long maxBytes = 0, bool types = false, double seconds = 20) => new()
    {
        Dialect = Dialect.Standard with
        {
            EnableTopLevelStmt = true, EnableFStrings = true,
            EnableTypes = types ? DialectTypes.Enable : DialectTypes.Disable,
        },
        Print = _ => { },
        MaxSteps = 100_000_000,
        Timeout = TimeSpan.FromSeconds(seconds),
        MaxAllocatedBytes = maxBytes,
        AllowGlobalReassign = true,
    };

    /// <summary>
    /// Runs <paramref name="body"/> on its own thread and fails if it has not finished within <paramref name="seconds"/>.
    /// Returns what it threw, or null.
    /// </summary>
    private static Exception? Within(double seconds, Action body, int stackBytes = 16 << 20)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { failure = e; }
        }, stackBytes) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(seconds)), $"did not finish within {seconds} s");
        return failure;
    }

    /// <summary>Asserts that only an exception type the host is promised can come out of <paramref name="e"/>.</summary>
    private static void Documented(Exception? e) =>
        Assert.True(e is null or StarlarkException or ExecutionLimitException or OperationCanceledException or StarlarkSyntaxException,
            $"undocumented exception reached the host: {e}");

    // ---------------------------------------------------------------- types

    // C6: Ty.Display recursed without a stack guard.
    [Fact]
    public void PrintingADeepRuntimeTypeIsAnErrorNotAStackOverflow()
    {
        var e = Assert.Throws<StarlarkException>(() => New(types: true).Execute("t",
            "t = list\nfor i in range(200000):\n    t = list[t]\ns = str(t)\n"));
        Assert.Contains("nested too deeply", e.RawMessage);
    }

    // C6: the static checker formats the type of a deep chain of variables in its error message.
    [Fact]
    public void TypecheckingADeepChainOfVariablesDoesNotOverflowTheStack()
    {
        var sb = new System.Text.StringBuilder("x0 = 0\n");
        for (int i = 1; i <= 100_000; i++) sb.Append($"x{i} = [x{i - 1}]\n");
        sb.Append("y = x100000 + 1\n");
        var e = Within(60, () => New(types: true).Typecheck("t", sb.ToString()));
        Documented(e);
    }

    // C7: Ty.Matches recursed without a stack guard.
    [Fact]
    public void IsinstanceOfADeepValueAgainstADeepTypeIsAnErrorNotAStackOverflow()
    {
        var e = Assert.Throws<StarlarkException>(() => New(types: true).Execute("t",
            "t = list\nx = []\nfor i in range(200000):\n    t = list[t]\n    x = [x]\nr = isinstance(x, t)\n"));
        Assert.Contains("nested too deeply", e.RawMessage);
    }

    // C7: a union of two equal deep types compares them structurally (synthesized record Equals).
    [Fact]
    public void UnionOfTwoDeepTypesIsAnErrorNotAStackOverflow()
    {
        var e = Assert.Throws<StarlarkException>(() => New(types: true).Execute("t",
            "t = list\nu = list\nfor i in range(200000):\n    t = list[t]\n    u = list[u]\nv = t | u\n"));
        Assert.Contains("nested too deeply", e.RawMessage);
    }

    // C9: the checker's cost grew about cubically with `x = [x]` lines.
    [Fact]
    public void TypecheckingManyRebindingsOfOneVariableIsFast()
    {
        var sb = new System.Text.StringBuilder("x = 0\n");
        for (int i = 0; i < 3000; i++) sb.Append("x = [x]\n");
        sb.Append("y = (x, x)\n");
        for (int i = 0; i < 3000; i++) sb.Append("y = (y, y)\n");
        var watch = Stopwatch.StartNew();
        Documented(Within(30, () => New(types: true).Typecheck("t", sb.ToString())));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    // C10: Ty.Matches on a shared (DAG) tuple was exponential and ignored the deadline.
    [Fact]
    public void IsinstanceOnASharedTupleIsLinear()
    {
        var it = New(types: true, seconds: 5);
        var m = Within(10, () => it.Execute("t",
            "t = int\nx = 1\nfor i in range(60):\n    t = tuple[t, ...]\n    x = (x, x)\nr = isinstance(x, t)\n"));
        Assert.Null(m);
    }

    // C10: the memo that makes the DAG case linear must not change answers: a cyclic list is a list of lists all the way
    // down, but not a list of lists of ints, and a failed branch of a union must not be remembered as a success.
    [Fact]
    public void IsinstanceWithTheMemoGivesTheSameAnswers()
    {
        var m = New(types: true).Execute("t",
            "x = []\nx.append(x)\n" +
            "a = isinstance(x, list[list[list]])\n" +
            "b = isinstance(x, list[list[list[int]]])\n" +
            "s = (1, 2)\nd = (s, s)\n" +
            "c = isinstance(d, tuple[tuple[int, str] | tuple[int, int], ...])\n" +
            "e = isinstance(d, tuple[tuple[int, int], tuple[int, str]])\n" +
            "f = isinstance([[1], [1, 'a']], list[list[int]] | list[list[int | str]])\n");
        Assert.Equal(true, m.Globals["a"]);
        Assert.Equal(false, m.Globals["b"]);
        Assert.Equal(true, m.Globals["c"]);
        Assert.Equal(false, m.Globals["e"]);
        Assert.Equal(true, m.Globals["f"]);
    }

    // C9: the checker obeys the interpreter's cancellation and time limit, with the exceptions a run gives.
    [Fact]
    public void TypecheckingObeysCancellationAndTimeout()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 5000; i++) sb.Append($"x{i} = {i} + 1\n");
        var it = New(types: true);
        it.Cancellation = new CancellationToken(true);
        Assert.ThrowsAny<OperationCanceledException>(() => it.Typecheck("t", sb.ToString()));
        it.TypecheckBeforeExecute = true;
        Assert.ThrowsAny<OperationCanceledException>(() => it.Execute("t", sb.ToString()));
        var late = New(types: true);
        late.Timeout = TimeSpan.Zero;
        var e = Assert.Throws<ExecutionLimitException>(() => late.Typecheck("t", sb.ToString()));
        Assert.Equal("time", e.Limit);
    }

    // ---------------------------------------------------------------- load

    // C8: a deep load() chain overflowed the stack in LoadModule.
    [Fact]
    public void ADeepLoadChainIsAnErrorNotAStackOverflow()
    {
        var it = New();
        it.SourceLoader = id => int.Parse(id) < 5000 ? $"load(\"{int.Parse(id) + 1}\", \"x\")\ny = x\n" : "x = 1\n";
        // An ordinary 1 MB thread: the limit must hold where hosts actually run scripts.
        var e = Within(60, () => it.Execute("m", "load(\"0\", \"x\")\n"), stackBytes: 1 << 20);
        var se = Assert.IsType<StarlarkException>(e);
        Assert.Contains("load() nesting", se.RawMessage);
    }

    // C6, C7: a host can build a deep type through the public API; printing, matching and comparing it are errors, not crashes.
    [Fact]
    public void ADeepTypeBuiltByTheHostIsAnErrorNotAStackOverflow()
    {
        var e = Within(60, () =>
        {
            Ty a = Ty.Int, b = Ty.Int;
            object? v = System.Numerics.BigInteger.One;
            for (int i = 0; i < 200_000; i++)
            {
                a = new Ty.ListTy(a);
                b = new Ty.ListTy(b);
                v = new StarList([v]);
            }
            Assert.Throws<StarlarkException>(() => a.ToString());
            Assert.Throws<StarlarkException>(() => a.Matches(v));
            Assert.Throws<StarlarkException>(() => a.Equals(b));
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
        }, stackBytes: 1 << 20);
        Assert.Null(e);
    }

    // C8: a load chain within the limit still works, and a failed chain leaves no half-loaded module behind.
    [Fact]
    public void AModerateLoadChainLoadsAndAFailedOneIsNotCached()
    {
        var it = New();
        it.SourceLoader = id => int.Parse(id) < 50 ? $"load(\"{int.Parse(id) + 1}\", \"x\")\ny = x\n" : "x = 1\n";
        Assert.Equal(System.Numerics.BigInteger.One, it.Execute("m", "load(\"0\", \"x\")\n").Globals["x"]);
        int calls = 0;
        var it2 = New();
        it2.SourceLoader = id => { calls++; return "fail('no')\n"; };
        Assert.Throws<StarlarkException>(() => it2.Execute("m", "load(\"a\", \"x\")\n"));
        Assert.Throws<StarlarkException>(() => it2.Execute("m", "load(\"a\", \"x\")\n"));
        Assert.Equal(2, calls);
    }

    // ---------------------------------------------------------------- strings

    // C12: rsplit was quadratic (List.Insert(0)).
    [Fact]
    public void RsplitIsLinear()
    {
        Assert.Null(Within(10, () => New().Execute("t",
            "s = 'a,' * 1000000\nr = s.rsplit(',')\nw = ('a ' * 1000000).rsplit()\nn = s.rsplit(',', 3)\n")));
        var m = New().Execute("t", "r = 'a,b,c'.rsplit(',', 1)\nw = ' a b  c '.rsplit(None, 1)\nz = 'a b c'.rsplit()\n");
        Assert.Equal("[\"a,b\", \"c\"]", Repr.Of(m.Globals["r"]));
        Assert.Equal("[\" a b\", \"c\"]", Repr.Of(m.Globals["w"]));
        Assert.Equal("[\"a\", \"b\", \"c\"]", Repr.Of(m.Globals["z"]));
    }

    // C27: the not-found message embedded the whole haystack.
    [Fact]
    public void IndexNotFoundMessageIsClipped()
    {
        var e = Assert.Throws<StarlarkException>(() => New().Execute("t", "s = 'a' * 100000\ns.index('b')\n"));
        Assert.True(e.RawMessage.Length < 300, $"message of {e.RawMessage.Length} chars");
        var l = Assert.Throws<StarlarkException>(() => New().Execute("t", "l = [1]\nl.remove('a' * 100000)\n"));
        Assert.True(l.RawMessage.Length < 300, $"message of {l.RawMessage.Length} chars");
        var d = Assert.Throws<StarlarkException>(() => New().Execute("t", "d = {}\nd['a' * 100000]\n"));
        Assert.True(d.RawMessage.Length < 300, $"message of {d.RawMessage.Length} chars");
    }

    // C40: '{:%}' of a huge float printed .NET's "Infinity%".
    [Fact]
    public void PercentFormatOfAHugeFloatPrintsInf() =>
        Assert.Equal("inf%", New().Eval("'{:%}'.format(1e307)"));

    // C26: str.format fields may each be under the limit, but together they must not be.
    [Fact]
    public void FormatResultIsCheckedAgainstTheMemoryLimit()
    {
        var e = Assert.Throws<ExecutionLimitException>(() => New(1_000_000).Execute("t", "s = ('{:400000}' * 20).format(*([1] * 20))\n"));
        Assert.Equal("memory", e.Limit);
        Assert.Throws<ExecutionLimitException>(() => New(1_000_000).Execute("t", "s = '%.1000000f' % 1e300\n"));
        Assert.Throws<ExecutionLimitException>(() => New(1_000_000).Execute("t", "s = ('%400000d' * 20) % tuple([1] * 20)\n"));
    }

    // C16: fail() joined a message of any size. The separator itself (600 KB) is within the 1 MB limit; the message is not.
    [Fact]
    public void FailMessageIsCheckedAgainstTheMemoryLimit()
    {
        var e = Assert.Throws<ExecutionLimitException>(() => New(1_000_000).Execute("t", "s = 'x' * 300000\nfail(1, 2, 3, sep = s)\n"));
        Assert.Equal("memory", e.Limit);
    }

    // C16: print() joined a line of any size.
    [Fact]
    public void PrintLineIsCheckedAgainstTheMemoryLimit()
    {
        var e = Assert.Throws<ExecutionLimitException>(() => New(1_000_000).Execute("t", "s = 'x' * 300000\nprint(1, 2, 3, sep = s)\n"));
        Assert.Equal("memory", e.Limit);
    }

    // C18: json.indent output was unbounded.
    [Fact]
    public void JsonIndentIsCheckedAgainstTheMemoryLimit()
    {
        var e = Assert.Throws<ExecutionLimitException>(() => New(1_000_000).Execute("t",
            "s = json.indent('[' * 127 + ']' * 127, indent = 'x' * 1000)\n"));
        Assert.Equal("memory", e.Limit);
        Assert.Equal("[\n\t1\n]", New().Eval("json.indent('[1]')"));   // Go's default indent is a tab
    }

    // ---------------------------------------------------------------- concatenation and growth

    // C15: bytes + bytes, tuple + tuple and utf16 + utf16 skipped the memory budget.
    [Theory]
    [InlineData("b = b'x' * 300000\nb = b + b + b + b\n")]
    [InlineData("t = (1,) * 100000\nt = t + t\n")]
    [InlineData("u = utf16('x' * 300000)\nu = u + u\n")]
    public void ConcatenationOfBytesTuplesAndUtf16IsBounded(string code)
    {
        var e = Assert.Throws<ExecutionLimitException>(() => New(1_000_000).Execute("t", code));
        Assert.Equal("memory", e.Limit);
    }

    // C17: list.extend and += grew a list with no memory check on the result.
    [Theory]
    [InlineData("b = [1] * 100000\nb.extend(b)\n")]
    [InlineData("def f():\n    b = [1] * 100000\n    b += b\nf()\n")]   // `+=` on a global is a static error
    public void ListGrowthIsBounded(string code)
    {
        var e = Assert.Throws<ExecutionLimitException>(() => New(1_000_000).Execute("t", code));
        Assert.Equal("memory", e.Limit);
    }

    // C15, C17: with no memory limit, + and extend stop at the element ceilings instead of hanging or overflowing.
    [Theory]
    [InlineData("def f():\n    b = b'x' * 540000000\n    b = b + b\nf()\n")]
    [InlineData("def f():\n    b = [1] * 60000000\n    b.extend(b)\nf()\n")]
    public void ConcatenationWithoutAMemoryLimitStopsAtTheCeiling(string code)
    {
        var it = New();
        it.MaxAllocatedBytes = 0;
        var e = Within(60, () => it.Execute("t", code));
        Assert.IsType<StarlarkException>(e);
    }

    // C17: append is checked too.
    [Fact]
    public void AppendIsBounded()
    {
        // Appending one at a time: the list passes the 1 MB budget (125,000 items) part way through.
        var e = Assert.Throws<ExecutionLimitException>(() => New(1_000_000).Execute("t", "def f():\n    b = []\n    for i in range(200000):\n        b.append(i)\nf()\n"));
        Assert.Equal("memory", e.Limit);
    }

    // ---------------------------------------------------------------- iteration and shared values

    // C14: all/any iterated a huge range without counting steps.
    [Theory]
    [InlineData("x = all(range(1, 1 << 60))\n")]
    [InlineData("x = any([0] * 5000000)\n")]
    [InlineData("x = bytes([1] * 5000000)\n")]
    [InlineData("x = ''.join(['x'] * 5000000)\n")]
    public void HugeIterationsInOneCallCountSteps(string code)
    {
        var it = New(seconds: 5);
        it.MaxSteps = 1_000_000;
        var e = Within(15, () => it.Execute("t", code));
        Assert.IsType<ExecutionLimitException>(e);
    }

    // C29: all(), any() and join over elems() and codepoints() of a big string count steps, and join checks its size as it goes.
    [Theory]
    [InlineData("x = all(('x' * 5000000).elems())\n")]
    [InlineData("x = any((b'\\0' * 5000000).elems())\n")]
    [InlineData("x = ''.join(('x' * 5000000).codepoints())\n")]
    public void IterationOverElemsCountsSteps(string code)
    {
        var it = New(seconds: 5);
        it.MaxSteps = 1_000_000;
        var e = Within(15, () => it.Execute("t", code));
        Assert.IsType<ExecutionLimitException>(e);
    }

    // C13: freezing a module holding a shared tuple walked every path through it.
    [Fact]
    public void FreezingASharedTupleIsLinear() =>
        Assert.Null(Within(10, () => New(seconds: 5).Execute("t", "def f():\n    t = ()\n    for i in range(60):\n        t = (t, t)\n    return t\nx = f()\n")));

    // C13: hashing, comparing, printing and encoding a shared tuple obey the time limit (or are linear).
    [Theory]
    [InlineData("d = {t: 1}\n")]
    [InlineData("r = [t, t] == [t, t]\n")]
    [InlineData("r = str(t)\n")]
    [InlineData("r = json.encode(t)\n")]
    public void WalkingASharedTupleObeysTheBudget(string code)
    {
        var e = Within(15, () => New(seconds: 2).Execute("t",
            "def f():\n    t = ()\n    for i in range(60):\n        t = (t, t)\n    return t\nt = f()\n" + code));
        Documented(e);
    }

    // ---------------------------------------------------------------- host boundary

    // C24: a function registered with Define let a raw CLR exception reach the host.
    [Fact]
    public void ADefinedFunctionThatThrowsGivesAStarlarkException()
    {
        var it = New();
        it.Define("boom", (_, _, _) => throw new InvalidOperationException("host"));
        var e = Assert.Throws<StarlarkException>(() => it.Execute("t", "boom()\n"));
        Assert.Contains("host", e.RawMessage);
        Assert.NotNull(e.Span);
        Assert.IsType<InvalidOperationException>(e.InnerException);
    }

    // C24: the Print sink is host code too.
    [Fact]
    public void APrintSinkThatThrowsGivesAStarlarkException()
    {
        var it = New();
        it.Print = _ => throw new InvalidOperationException("print sink");
        var e = Assert.Throws<StarlarkException>(() => it.Execute("t", "print(1)\n"));
        Assert.Contains("print sink", e.RawMessage);
    }

    // C24: a limit or a cancellation raised by host code keeps its type.
    [Fact]
    public void ADefinedFunctionKeepsLimitAndCancellationExceptions()
    {
        var it = New();
        it.Define("lim", (_, _, _) => throw new ExecutionLimitException("steps", "x"));
        it.Define("can", (_, _, _) => throw new OperationCanceledException());
        Assert.Throws<ExecutionLimitException>(() => it.Execute("t", "lim()\n"));
        Assert.Throws<OperationCanceledException>(() => it.Execute("t", "can()\n"));
    }

    private sealed class BadOrder(int n, Exception? fail) : IHostValue
    {
        public string TypeName => "bad";
        public int? CompareTo(object? other) =>
            fail is not null ? throw fail : other is BadOrder b ? n.CompareTo(b.N) : null;
        public int N => n;
    }

    // C28: sorted() let comparer exceptions other than StarlarkException escape wrapped in InvalidOperationException.
    [Fact]
    public void SortedKeepsLimitExceptionsAndWrapsOthers()
    {
        var it = New();
        it.DefineValue("a", new BadOrder(1, null));
        it.DefineValue("lim", new BadOrder(2, new ExecutionLimitException("steps", "x")));
        it.DefineValue("can", new BadOrder(2, new OperationCanceledException()));
        it.DefineValue("bad", new BadOrder(2, new FormatException("host compare")));
        Assert.Throws<ExecutionLimitException>(() => it.Execute("t", "sorted([lim, a])\n"));
        Assert.Throws<OperationCanceledException>(() => it.Execute("t", "sorted([can, a])\n"));
        var e = Assert.Throws<StarlarkException>(() => it.Execute("t", "sorted([bad, a])\n"));
        Assert.Contains("host compare", e.RawMessage);
    }

    // C36: Eval("") and Eval(null) returned an empty tuple.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# just a comment")]
    public void EvalOfNoExpressionIsASyntaxError(string text) =>
        Assert.Throws<StarlarkSyntaxException>(() => New().Eval(text));

    // C36: an explicit empty tuple is still an expression.
    [Fact]
    public void EvalOfAnEmptyTupleStillWorks()
    {
        Assert.IsType<StarTuple>(New().Eval("()"));
        Assert.IsType<StarTuple>(New().Eval(" ( ) "));
        Assert.Throws<ArgumentNullException>(() => New().Eval(null!));
    }
}
