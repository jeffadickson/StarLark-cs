using System.Numerics;
using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>
/// Features added to match Starlark-Go: <c>chr</c> and <c>ord</c>, the string iterables <c>codepoints</c>, <c>codepoint_ords</c> and
/// <c>elem_ords</c>, the <c>module</c> type, <c>json.encode_indent</c>, positional-only dictionary methods, the recursion option, and
/// <see cref="ScriptThread"/> (Go's <c>Thread</c>). Expected values and messages are those of Go's own test data.
/// </summary>
public class GoCompatTests
{
    private static Interpreter New(bool recursion = false) => new()
    {
        Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableRecursion = recursion },
        MaxSteps = 20_000_000,
        Print = _ => { },
    };

    private static string R(string expression) => Repr.Of(New().Execute("t.star", "r = " + expression + "\n").Globals["r"]);

    private static string Fails(string expression) =>
        Assert.Throws<StarlarkException>(() => New().Execute("t.star", "r = " + expression + "\n")).RawMessage;

    // ---------------------------------------------------------------- chr and ord

    [Fact]
    public void ChrAndOrdRoundTrip()
    {
        Assert.Equal("\"A\"", R("chr(65)"));
        Assert.Equal("\"Й\"", R("chr(1049)"));
        Assert.Equal("\"😿\"", R("chr(0x1F63F)"));
        Assert.Equal("[65, 1049, 19990, 128575]", R("[ord(\"A\"), ord(\"Й\"), ord(\"世\"), ord(\"😿\")]"));
        Assert.Equal("True", R("all([ord(chr(i)) == i for i in [0, 1, 127, 128, 255, 0x7FF, 0x800, 0xFFFF, 0x10000, 0x10FFFF]])"));
        Assert.Equal("\"\\x00\"", R("chr(0)"));
    }

    [Theory]
    [InlineData("chr(-1)", "Unicode code point -1 out of range \\(<0\\)")]
    [InlineData("chr(0x110000)", "Unicode code point U\\+110000 out of range \\(>0x10FFFF\\)")]
    [InlineData("chr(0xD800)", "surrogate")]
    [InlineData("chr(0xDFFF)", "surrogate")]
    [InlineData("chr('a')", "got string, want int")]
    [InlineData("chr(1.5)", "got float, want int")]
    [InlineData("chr()", "Missing parameter")]
    [InlineData("chr(1, 2)", "Too many positional")]
    [InlineData("ord('abc')", "string encodes 3 Unicode code points, want 1")]
    [InlineData("ord('')", "string encodes 0 Unicode code points, want 1")]
    [InlineData("ord(b'ab')", "ord: bytes has length 2, want 1")]
    [InlineData("ord(b'')", "ord: bytes has length 0, want 1")]
    [InlineData("ord(1)", "got int, want string or bytes")]
    public void ChrAndOrdErrorsUseGosWording(string call, string pattern)
    {
        Assert.Matches(pattern, Fails(call));
    }

    [Fact]
    public void OrdOfBytesIsTheByte() => Assert.Equal("97", R("ord(b\"a\")"));

    [Fact]
    public void OrdAndChrWorkOnUtf16Strings()
    {
        Assert.Equal("128575", R("ord(utf16('😿'))"));
        Assert.Matches("3 Unicode code points", Fails("ord(utf16('abc'))"));
    }

    // ---------------------------------------------------------------- string iterables

    [Fact]
    public void CodepointIterables()
    {
        Assert.Equal("[\"a\", \"b\", \"c\", \"Й\", \"😿\"]", R("list(\"abcЙ😿\".codepoints())"));
        Assert.Equal("[97, 98, 99, 1049, 128575]", R("list(\"abcЙ😿\".codepoint_ords())"));
        Assert.Equal("[]", R("list(\"\".codepoints())"));
        Assert.Equal("[]", R("list(\"\".codepoint_ords())"));
    }

    [Fact]
    public void ElementOrdsAreTheUtf16UnitsByDefault() =>
        Assert.Equal("[97, 98, 99]", R("list(\"abc\".elem_ords())"));

    [Fact]
    public void ElementOrdsAreBytesInUtf8Mode()
    {
        var it = New();
        it.StringElements = StringElements.Utf8;
        var m = it.Execute("t.star", "r = list(\"abcЙ😿\".elem_ords())\nn = len(\"abcЙ😿\".elem_ords())\nlast = \"abcЙ😿\".elem_ords()[-1]\n");
        Assert.Equal("[97, 98, 99, 208, 153, 240, 159, 152, 191]", Repr.Of(m.Globals["r"]));
        Assert.Equal(new BigInteger(9), m.Globals["n"]);
        Assert.Equal(new BigInteger(191), m.Globals["last"]);
    }

    [Fact]
    public void TypeNamesAndReprsMatchGo()
    {
        Assert.Equal("\"string.elems\"", R("type(\"a\".elems())"));
        Assert.Equal("\"string.elems\"", R("type(\"a\".elem_ords())"));
        Assert.Equal("\"string.codepoints\"", R("type(\"a\".codepoints())"));
        Assert.Equal("\"string.codepoints\"", R("type(\"a\".codepoint_ords())"));
        Assert.Equal("\"\\\"abcЙ\\\".codepoint_ords()\"", R("str(\"abcЙ\".codepoint_ords())"));
        Assert.Equal("\"\\\"ab\\\".codepoints()\"", R("str(\"ab\".codepoints())"));
        Assert.Equal("\"\\\"ab\\\".elem_ords()\"", R("str(\"ab\".elem_ords())"));
        Assert.Equal("\"\\\"ab\\\".elems()\"", R("str(\"ab\".elems())"));
    }

    [Fact]
    public void ElementIterablesAreIndexableAndHaveALength()
    {
        Assert.Equal("[\"c\", 99, 3, 3, 99]", R("[\"abc\".elems()[2], \"abc\".elem_ords()[2], len(\"abc\".elems()), len(\"abc\".elem_ords()), \"abc\".elem_ords()[-1]]"));
        Assert.Matches("out of bound", Fails("\"abc\".elems()[5]"));
        Assert.Matches("must be an int", Fails("\"abc\".elems()[\"x\"]"));
    }

    [Fact]
    public void CodepointIterablesAreNeitherIndexableNorSized()
    {
        // A code point's position is not its offset, so Go refuses both.
        Assert.Matches("not indexable", Fails("\"abc\".codepoints()[2]"));
        Assert.Matches("not indexable", Fails("\"abc\".codepoint_ords()[2]"));
        Assert.Matches("has no len", Fails("len(\"abc\".codepoints())"));
        Assert.Matches("has no len", Fails("len(\"abc\".codepoint_ords())"));
    }

    [Fact]
    public void IterablesAreUnhashableAndComparedByIdentity()
    {
        Assert.Matches("unhashable", Fails("{\"a\".codepoints(): 1}"));
        Assert.Equal("False", R("\"a\".codepoints() == \"a\".codepoints()"));
    }

    [Fact]
    public void Utf16Codepoints()
    {
        Assert.Equal("[utf16(\"a\"), utf16(\"😿\")]", R("list(utf16(\"a😿\").codepoints())"));
        Assert.Equal("[97, 128575]", R("list(utf16(\"a😿\").codepoint_ords())"));
        Assert.Equal("[97, 98]", R("list(utf16(\"ab\").elem_ords())"));
    }

    [Fact]
    public void ALoneSurrogateReadsAsTheReplacementCharacter()
    {
        // A string holding half of a surrogate pair cannot be written as a literal, so build one by slicing in UTF-16 mode.
        // In UTF-8 mode (the default) such a slice is an error instead, so this runs in UTF-16 mode.
        var it = New();
        it.StringElements = StringElements.Utf16;
        var m = it.Execute("t.star", "r = list((\"a\" + \"😿\"[1:] + \"Z\").codepoint_ords())\n");
        Assert.Equal("[97, 65533, 90]", Repr.Of(m.Globals["r"]));
    }

    [Fact]
    public void ExistingElemsStillWork()
    {
        Assert.Equal("\"catamaran\"", R("'a'.join('ctmrn'.elems())"));
        Assert.Equal("[65, 66, 67]", R("list(b'ABC'.elems())"));
        Assert.Equal("\"bytes.elems\"", R("type(b'ABC'.elems())"));
    }

    // ---------------------------------------------------------------- module type

    [Fact]
    public void JsonAndTypingAreModules()
    {
        Assert.Equal("\"module\"", R("type(json)"));
        Assert.Equal("\"<module \\\"json\\\">\"", R("str(json)"));
        Assert.Equal("\"module\"", R("type(typing)"));
        Assert.Equal("[\"Any\", \"Callable\", \"Iterable\", \"Never\"]", R("dir(typing)"));
        Assert.Equal("[\"decode\", \"encode\", \"encode_indent\", \"indent\"]", R("dir(json)"));
    }

    [Fact]
    public void ModuleMembersWork()
    {
        Assert.Equal("\"[1,2]\"", R("json.encode([1, 2])"));
        Assert.Equal("True", R("hasattr(json, 'encode')"));
        Assert.Equal("False", R("hasattr(json, 'nonesuch')"));
    }

    [Fact]
    public void ModulesAreUnhashableAndReadOnly()
    {
        Assert.Matches("unhashable: module", Fails("{json: None}"));
        var e = Assert.Throws<StarlarkException>(() => New().Execute("t.star", "def f():\n    json.foo = None\nf()\n"));
        Assert.Matches("can't assign to .foo field of module", e.RawMessage);
        e = Assert.Throws<StarlarkException>(() => New().Execute("t.star", "json.encode = None\n"));
        Assert.Matches("can't assign to .encode field of module", e.RawMessage);
    }

    [Fact]
    public void MissingModuleMembersSuggestTheClosestName()
    {
        Assert.Matches("module has no .nonesuch field or method$", Fails("json.nonesuch"));
        Assert.Matches("module has no .encod field or method; did you mean .encode\\?", Fails("json.encod"));
    }

    [Fact]
    public void AHostCanBuildItsOwnModule()
    {
        var it = New();
        it.DefineValue("geo", new StarModule("geo", [new("pi", 3.14), new("double", new Builtin("double", (_, a, _) => (BigInteger)a[0]! * 2))]));
        var m = it.Execute("t.star", "r = [type(geo), str(geo), geo.pi, geo.double(4), dir(geo)]\n");
        Assert.Equal("[\"module\", \"<module \\\"geo\\\">\", 3.14, 8, [\"double\", \"pi\"]]", Repr.Of(m.Globals["r"]));
    }

    [Fact]
    public void ModulesWorkInsideTasks()
    {
        var it = new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableFutures = true } };
        var m = it.Execute("t.star", "def f(x):\n    return json.encode([x])\nr = parallel_map(f, [1, 2])\n");
        Assert.Equal("[\"[1]\", \"[2]\"]", Repr.Of(m.Globals["r"]));
    }

    [Fact]
    public void LoadedNamesKeepAWorkingModule()
    {
        var it = New();
        it.SourceLoader = id => id == "lib.star" ? "j = json\n" : null;
        Assert.Equal("\"[1]\"", Repr.Of(it.Execute("t.star", "load('lib.star', 'j')\nr = j.encode([1])\n").Globals["r"]));
    }

    // ---------------------------------------------------------------- json.encode_indent

    [Fact]
    public void EncodeIndentUsesTabsByDefault()
    {
        Assert.Equal("\"{\\n\\t\\\"a\\\": [\\n\\t\\t1,\\n\\t\\t2\\n\\t]\\n}\"", R("json.encode_indent({'a': [1, 2]})"));
    }

    [Fact]
    public void EncodeIndentTakesPrefixAndIndentByKeyword()
    {
        Assert.Equal("\"[\\n  1\\n]\"", R("json.encode_indent([1], indent = '  ')"));
        Assert.Equal("\"[\\n>\\t1\\n>]\"", R("json.encode_indent([1], prefix = '>')"));
    }

    [Fact]
    public void EncodeIndentEqualsEncodeThenIndent()
    {
        Assert.Equal("True", R("json.encode_indent({'a': [1, {'b': None}], 'c': 'x'}, indent = '  ') == json.indent(json.encode({'a': [1, {'b': None}], 'c': 'x'}), indent = '  ')"));
    }

    [Theory]
    [InlineData("json.encode_indent([1], 'x')", "got 2 arguments, want 1")]
    [InlineData("json.encode_indent([1], bad = 1)", "Unexpected parameter named `bad`")]
    [InlineData("json.encode_indent()", "Missing parameter")]
    [InlineData("json.encode_indent(float('nan'))", "non-finite")]
    [InlineData("json.encode_indent({1: 2})", "")]
    public void EncodeIndentErrors(string call, string pattern)
    {
        Assert.NotNull(Fails(call));
        if (pattern.Length > 0) Assert.Matches(pattern, Fails(call));
    }

    [Fact]
    public void TheCheckerKnowsEncodeIndent()
    {
        Assert.Empty(New().Typecheck("t.star", "def f():\n    return json.encode_indent([1], indent = ' ')\n").Errors);
    }

    // ---------------------------------------------------------------- positional-only dictionary methods

    [Theory]
    [InlineData("{1: 2}.get(1, default = 0)", "get: unexpected keyword arguments")]
    [InlineData("{1: 2}.get(1, 0, extra = 1)", "get: unexpected keyword arguments")]
    [InlineData("{1: 2}.get(key = 1)", "get: unexpected keyword arguments")]
    [InlineData("{1: 2}.pop(1, default = 0)", "pop: unexpected keyword arguments")]
    [InlineData("{1: 2}.setdefault(1, default = 0)", "setdefault: unexpected keyword arguments")]
    public void DictionaryMethodsRejectKeywordArguments(string call, string pattern) => Assert.Matches(pattern, Fails(call));

    [Fact]
    public void DictionaryMethodsStillWorkPositionally()
    {
        Assert.Equal("[2, 0, 5, 2, 7]", R("[{1: 2}.get(1), {1: 2}.get(3, 0), {1: 2}.get(3, 5), {1: 2}.pop(1), {}.setdefault(1, 7)]"));
        Assert.Equal("9", R("{}.pop(1, 9)"));
        Assert.Equal("{\"e\": 5}", R("{}.update(e = 5) or {'e': 5}"));   // update keeps its keyword form: that is how Go and the spec define it
    }

    // ---------------------------------------------------------------- recursion

    private const string Fib = "def fib(n):\n    if n <= 1:\n        return 1\n    return fib(n - 1) + fib(n - 2)\nr = fib(10)\n";

    [Fact]
    public void RecursionIsAnErrorByDefault()
    {
        var e = Assert.Throws<StarlarkException>(() => New().Execute("t.star", Fib));
        Assert.Matches("called recursively", e.RawMessage);
    }

    [Fact]
    public void RecursionWorksWhenEnabled()
    {
        Assert.Equal(new BigInteger(89), New(recursion: true).Execute("t.star", Fib).Globals["r"]);
    }

    [Fact]
    public void MutualRecursionWorksWhenEnabled()
    {
        const string program = "def even(n):\n    if n == 0:\n        return True\n    return odd(n - 1)\ndef odd(n):\n    if n == 0:\n        return False\n    return even(n - 1)\nr = even(40)\n";
        Assert.Equal(true, New(recursion: true).Execute("t.star", program).Globals["r"]);
        Assert.Throws<StarlarkException>(() => New().Execute("t.star", program));
    }

    [Fact]
    public void RunawayRecursionEndsInAnErrorNotACrash()
    {
        var e = Assert.Throws<StarlarkException>(() => New(recursion: true).Execute("t.star", "def runaway():\n    return runaway()\nrunaway()\n"));
        Assert.Matches("Starlark stack overflow", e.RawMessage);
    }

    [Fact]
    public void RunawayRecursionIsSafeOnASmallStack()
    {
        // Even on a deliberately tiny thread stack the run ends with an error, because the remaining stack is checked on every call.
        string result = "";
        var t = new Thread(() =>
        {
            try { New(recursion: true).Execute("t.star", "def f(n):\n    return f(n + 1)\nf(0)\n"); }
            catch (StarlarkException e) { result = e.RawMessage; }
        }, 256 * 1024);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(30)));
        Assert.Matches("Starlark stack overflow", result);
    }

    [Fact]
    public void TheCallDepthLimitCanBeRaisedOrLowered()
    {
        var it = New(recursion: true);
        it.MaxCallDepth = 20;
        var e = Assert.Throws<StarlarkException>(() => it.Execute("t.star", "def d(n):\n    if n == 0:\n        return 0\n    return 1 + d(n - 1)\nr = d(50)\n"));
        Assert.Matches("Starlark stack overflow", e.RawMessage);
        it.MaxCallDepth = 100;
        Assert.Equal(new BigInteger(50), it.Execute("t.star", "def d(n):\n    if n == 0:\n        return 0\n    return 1 + d(n - 1)\nr = d(50)\n").Globals["r"]);
        Assert.Throws<ArgumentOutOfRangeException>(() => it.MaxCallDepth = 0);
    }

    [Fact]
    public void ErrorsInRecursionCarryTheirCallStack()
    {
        var e = Assert.Throws<StarlarkException>(() =>
            New(recursion: true).Execute("t.star", "def a(n):\n    if n == 0:\n        fail('bottom')\n    return a(n - 1)\na(3)\n"));
        Assert.Equal(["a", "a", "a", "a", "<module>"], e.CallStack.Select(f => f.Function).ToArray());
    }

    [Fact]
    public void RecursionInsideTasksWorksWhenEnabled()
    {
        var it = new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableFutures = true, EnableRecursion = true }, MaxSteps = 50_000_000 };
        var m = it.Execute("t.star", "def fib(n):\n    if n <= 1:\n        return 1\n    return fib(n - 1) + fib(n - 2)\nr = parallel_map(fib, [5, 6, 7])\n");
        Assert.Equal("[8, 13, 21]", Repr.Of(m.Globals["r"]));
    }

    // ---------------------------------------------------------------- ScriptThread (Go's Thread)

    /// <summary>Captures what a host function sees of its thread.</summary>
    private sealed record Snapshot(string Name, long Steps, int Depth, IReadOnlyList<CallFrame> Stack, CallFrame Top, CallFrame Caller);

    private static (Interpreter It, List<Snapshot> Seen) WithProbe(string name = "")
    {
        var it = New();
        it.ThreadName = name;
        var seen = new List<Snapshot>();
        it.Register("probe", (ScriptThread thread) =>
        {
            seen.Add(new Snapshot(thread.Name, thread.Steps, thread.CallStackDepth, thread.CallStack(), thread.CallFrame(0), thread.CallFrame(1)));
            return 0;
        });
        return (it, seen);
    }

    [Fact]
    public void AHostFunctionSeesTheCallStack()
    {
        var (it, seen) = WithProbe();
        it.Execute("main.star", "def inner():\n    return probe()\n\ndef outer():\n    return inner()\n\nr = outer()\n");
        var s = Assert.Single(seen);
        // Go counts the running built-in as frame 0, then the script functions out to the module.
        Assert.Equal(4, s.Depth);
        Assert.Equal(["<module>", "outer", "inner", "probe"], s.Stack.Select(f => f.Name).ToArray());
        Assert.Equal("probe", s.Top.Name);
        Assert.Equal("<builtin>", s.Top.Position);
        Assert.Equal("inner", s.Caller.Name);
        Assert.Equal(2, s.Caller.Line);
        Assert.Equal("main.star:2:12", s.Caller.Position);
    }

    [Fact]
    public void CallFramePositionsPointAtTheCallsInProgress()
    {
        var (it, seen) = WithProbe();
        it.Execute("main.star", "def f():\n    return probe()\n\nr = f()\n");
        var stack = seen.Single().Stack;
        Assert.Equal("main.star:4:5", stack[0].Position);      // <module> is waiting at the call f()
        Assert.Equal("main.star:2:12", stack[1].Position);     // f is waiting at the call probe()
        Assert.Equal("main.star:4:5: in <module>", stack[0].ToString());
    }

    [Fact]
    public void TracebackListsOutermostFirst()
    {
        var (it, seen) = WithProbe();
        var thread = default(ScriptThread);
        it.Register("grab", (ScriptThread t) => { thread = t; return t.Traceback(); });
        var m = it.Execute("main.star", "def f():\n    return grab()\n\nr = f()\n");
        var text = (string)m.Globals["r"]!;
        Assert.StartsWith("Traceback (most recent call last):\n  main.star:4:5: in <module>\n  main.star:2:12: in f\n  <builtin>: in grab", text);
        Assert.NotNull(thread);
        Assert.Empty(seen);
    }

    [Fact]
    public void TopLevelCallHasTwoFrames()
    {
        var (it, seen) = WithProbe();
        it.Execute("main.star", "r = probe()\n");
        Assert.Equal(2, seen.Single().Depth);
        Assert.Equal(["<module>", "probe"], seen.Single().Stack.Select(f => f.Name).ToArray());
    }

    [Fact]
    public void RecursiveCallsAppearAsSeparateFrames()
    {
        var it = New(recursion: true);
        var depths = new List<int>();
        it.Register("depth", (ScriptThread t) => { depths.Add(t.CallStackDepth); return 0; });
        it.Execute("t.star", "def f(n):\n    if n == 0:\n        return depth()\n    return f(n - 1)\nr = f(3)\n");
        Assert.Equal([1 + 4 + 1], depths);   // module + f x4 + depth
    }

    [Fact]
    public void ThreadNameComesFromTheInterpreterAndCanBeChanged()
    {
        var (it, seen) = WithProbe("worker-1");
        it.Execute("t.star", "r = probe()\n");
        Assert.Equal("worker-1", seen.Single().Name);

        var names = new List<string>();
        it.Register("rename", (ScriptThread t) => { t.Name = "renamed"; names.Add(t.Name); return 0; });
        it.Execute("t.star", "r = rename()\n");
        Assert.Equal(["renamed"], names);
        it.Execute("t.star", "r = probe()\n");
        Assert.Equal("worker-1", seen.Last().Name);   // a new run starts with the interpreter's name again
    }

    [Fact]
    public void StepsGrowAsTheScriptRuns()
    {
        var (it, seen) = WithProbe();
        it.Execute("t.star", "def work():\n    x = 0\n    for i in range(100):\n        x += i\n    return x\na = probe()\nw = work()\nb = probe()\n");
        Assert.Equal(2, seen.Count);
        Assert.True(seen[1].Steps - seen[0].Steps >= 200, $"the loop cost only {seen[1].Steps - seen[0].Steps} steps");
    }

    [Fact]
    public void StepsAreCountedEvenWithoutALimit()
    {
        var it = new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true } };
        long steps = -1;
        it.Register("steps", (ScriptThread t) => { steps = t.Steps; return 0; });
        it.Execute("t.star", "x = 1\ny = 2\nr = steps()\n");
        Assert.True(steps > 0);
        Assert.Equal(0, it.MaxSteps);
    }

    [Fact]
    public void ThreadLocalsAreInvisibleToScriptsAndPerRun()
    {
        var it = New();
        it.Register("put", (ScriptThread t, string key, int value) => { t.SetLocal(key, value); return 0; });
        it.Register("get", (ScriptThread t, string key) => t.Local(key) is int v ? v : -1);
        var m = it.Execute("t.star", "a = get('k')\nput('k', 7)\nb = get('k')\n");
        Assert.Equal(new BigInteger(-1), m.Globals["a"]);
        Assert.Equal(new BigInteger(7), m.Globals["b"]);
        // The next run starts empty.
        Assert.Equal(new BigInteger(-1), it.Execute("t.star", "c = get('k')\n").Globals["c"]);
    }

    [Fact]
    public void InterpreterThreadLocalsSeedEveryRun()
    {
        var it = New();
        it.ThreadLocals = new Dictionary<string, object?> { ["tenant"] = "acme" };
        it.Register("tenant", (ScriptThread t) => (string?)t.Local("tenant") ?? "none");
        Assert.Equal("acme", it.Execute("t.star", "r = tenant()\n").Globals["r"]);
        Assert.Equal("acme", it.Execute("u.star", "r = tenant()\n").Globals["r"]);
    }

    [Fact]
    public void LocalsOfARunAreNotSharedWithAnotherRun()
    {
        var it = New();
        it.Register("put", (ScriptThread t) => { t.SetLocal("x", 1); return 0; });
        it.Register("has", (ScriptThread t) => t.Local("x") is not null);
        it.Execute("t.star", "put()\n");
        Assert.Equal(false, it.Execute("u.star", "r = has()\n").Globals["r"]);
    }

    [Fact]
    public void TasksStartWithTheirSpawnersLocalsAndName()
    {
        var it = new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableFutures = true }, ThreadName = "main" };
        it.Register("put", (ScriptThread t) => { t.SetLocal("x", 5); return 0; }, threadSafe: true);
        it.Register("seen", (ScriptThread t, int i) => $"{t.Name}:{t.Local("x")}:{i}", threadSafe: true);
        var m = it.Execute("t.star", "put()\ndef f(i):\n    return seen(i)\nr = parallel_map(f, [1, 2])\n");
        Assert.Equal("[\"main:5:1\", \"main:5:2\"]", Repr.Of(m.Globals["r"]));
    }

    [Fact]
    public void CurrentThreadWorksFromDefinedFunctions()
    {
        var it = New();
        string? name = null;
        int depth = -1;
        it.ThreadName = "defined";
        it.Define("who", (ip, _, _) => { name = ip.CurrentThread!.Name; depth = ip.CurrentThread.CallStackDepth; return null; });
        it.Execute("t.star", "def f():\n    who()\nf()\n");
        Assert.Equal("defined", name);
        Assert.Equal(2, depth);   // <module> and f; a Define'd function is not counted as a frame
        Assert.Null(it.CurrentThread);   // no run in progress here
    }

    [Fact]
    public void AThreadIsOnlyUsableWhileItsRunIsInProgress()
    {
        var it = New();
        ScriptThread? kept = null;
        it.Register("keep", (ScriptThread t) => { kept = t; return 0; });
        it.Execute("t.star", "keep()\n");
        Assert.NotNull(kept);
        Assert.Throws<InvalidOperationException>(() => kept!.CallStackDepth);
        Assert.Throws<InvalidOperationException>(() => kept!.SetLocal("a", 1));
        Assert.Equal("", kept!.Name);   // plain properties of a finished run are harmless
    }

    [Fact]
    public void CallFrameRejectsADepthOutsideTheStack()
    {
        var it = New();
        string? message = null;
        it.Register("bad", (ScriptThread t) =>
        {
            try { t.CallFrame(99); } catch (ArgumentOutOfRangeException e) { message = e.ParamName; }
            try { t.CallFrame(-1); } catch (ArgumentOutOfRangeException) { message += "+neg"; }
            return 0;
        });
        it.Execute("t.star", "r = bad()\n");
        Assert.Equal("depth+neg", message);
    }

    [Fact]
    public void ThreadPrintAndLoadReachTheInterpretersHandlers()
    {
        var it = New();
        var printed = new List<string>();
        it.Print = printed.Add;
        it.SourceLoader = id => id == "m.star" ? "x = 1\n" : null;
        it.Register("show", (ScriptThread t, string s) => { t.Print(s); return t.Load("m.star") ?? "none"; });
        var m = it.Execute("t.star", "r = show('hi')\n");
        Assert.Equal(["hi"], printed);
        Assert.Equal("x = 1\n", m.Globals["r"]);
    }

    [Fact]
    public void AScriptThreadParameterIsInvisibleToTheChecker()
    {
        var it = new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableTypes = DialectTypes.Enable } };
        it.Register("probe2", (ScriptThread t, int n) => n);
        Assert.Empty(it.Typecheck("t.star", "def f():\n    return probe2(3)\n").Errors);
        Assert.NotEmpty(it.Typecheck("t.star", "def f():\n    return probe2('x')\n").Errors);
    }

    [Fact]
    public void FramesAreBalancedAfterErrorsAndNestedRuns()
    {
        var it = New();
        int depth = -1;
        it.Register("depth", (ScriptThread t) => { depth = t.CallStackDepth; return 0; });
        Assert.Throws<StarlarkException>(() => it.Execute("t.star", "def f():\n    fail('x')\nf()\n"));
        it.Execute("u.star", "r = depth()\n");
        Assert.Equal(2, depth);   // nothing left over from the failed run
        var fn = it.Execute("v.star", "def g():\n    return depth()\n").Globals["g"];
        it.CallValue(fn, []);
        Assert.Equal(2, depth);   // g and depth: a call from the host starts a fresh stack
    }
}

/// <summary>Grammar corner cases from Go's own list.star: what may follow <c>if</c> in a comprehension.</summary>
public class ComprehensionConditionTests
{
    private static string R(string expression) =>
        Repr.Of(new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true } }.Execute("t.star", "r = " + expression + "\n").Globals["r"]);

    [Fact]
    public void ALambdaIsAValidCondition() =>
        Assert.Equal("[1, 2]", R("[x for x in [1, 2] if lambda: None]"));   // a lambda is a function, which is always true

    [Fact]
    public void AParenthesisedTernaryLambdaIsAValidCondition() =>
        Assert.Equal("[1, 2]", R("[x for x in [1, 2] if (lambda: 3 if True else 4)]"));

    [Fact]
    public void ALambdaConditionStopsBeforeTheNextIf() =>
        Assert.Equal("[2]", R("[x for x in [1, 2] if lambda: None if x == 2]"));   // the second `if` is a clause, not part of the lambda body

    [Fact]
    public void SeveralIfClausesStillWork() =>
        Assert.Equal("[0, 6]", R("[x for x in range(12) if x % 2 == 0 if x % 3 == 0]"));

    [Fact]
    public void LambdasKeepTheirTernaryBodiesElsewhere() =>
        Assert.Equal("[3, 4]", R("[(lambda a: 3 if a else 4)(True), (lambda a: 3 if a else 4)(False)]"));

    [Fact]
    public void ADictComprehensionTakesTheSameConditions() =>
        Assert.Equal("{1: 1}", R("{x: x for x in [1] if lambda: 0}"));
}

/// <summary><c>x[a, b] = v</c>: Go stores the key (a, b); Rust rejects the statement. This library follows Go.</summary>
public class TupleIndexAssignmentTests
{
    private static Interpreter New() => new() { Dialect = Dialect.Standard with { EnableTopLevelStmt = true }, MaxSteps = 1_000_000 };

    private static string R(string program) => Repr.Of(New().Execute("t.star", program).Globals["r"]);

    [Fact]
    public void AssigningStoresTheTupleKey() =>
        Assert.Equal("{(1, 2): 3}", R("d = {}\nd[1, 2] = 3\nr = d\n"));

    [Fact]
    public void ReadingTheSameKeyWorksBothWays() =>
        Assert.Equal("[3, 3]", R("d = {}\nd[1, 2] = 3\nr = [d[1, 2], d[(1, 2)]]\n"));

    [Fact]
    public void ThePairIsAnOrdinaryTupleKey() =>
        Assert.Equal("[(1, 2), True]", R("d = {}\nd[1, 2] = 3\nr = [d.keys()[0], (1, 2) in d]\n"));

    [Fact]
    public void AugmentedAssignmentUsesTheTupleKey() =>
        Assert.Equal("{(1, 2): 10}", R("d = {(1, 2): 4}\nd[1, 2] += 6\nr = d\n"));

    [Fact]
    public void KeysAreEvaluatedOnce()
    {
        var it = New();
        int calls = 0;
        it.Define("tick", (_, _, _) => { calls++; return new System.Numerics.BigInteger(calls); });
        var m = it.Execute("t.star", "d = {}\nd[tick(), 7] = 1\nr = d\n");
        Assert.Equal("{(1, 7): 1}", Repr.Of(m.Globals["r"]));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ListsStillTakeOnlyIntegerIndexes()
    {
        var e = Assert.Throws<StarlarkException>(() => New().Execute("t.star", "l = [1, 2]\nl[0, 1] = 3\n"));
        Assert.Matches("not supported on types 'list' and 'tuple'", e.RawMessage);
    }

    [Fact]
    public void AnUnhashableElementIsAnError()
    {
        var e = Assert.Throws<StarlarkException>(() => New().Execute("t.star", "d = {}\nd[1, []] = 3\n"));
        Assert.Matches("unhashable|not hashable", e.RawMessage);
    }

    [Fact]
    public void AFrozenDictRefusesIt()
    {
        // A module's globals are frozen once it has run, so a function it exported cannot add to its own dict afterwards.
        var it = New();
        var m = it.Execute("t.star", "d = {}\ndef add():\n    d[3, 4] = 5\n");
        var e = Assert.Throws<StarlarkException>(() => it.CallValue(m.Globals["add"], []));
        Assert.Matches("frozen|Immutable", e.RawMessage);
    }

    [Fact]
    public void ThreeOrMoreElementsAreStillNotAnIndex()
    {
        // The grammar has a two-element subscript only; a longer one is a parse error whether read or written.
        Assert.Throws<StarlarkSyntaxException>(() => New().Execute("t.star", "d = {}\nd[1, 2, 3] = 4\n"));
    }

    [Fact]
    public void NonIndexTargetsAreStillRejected() =>
        Assert.Throws<StarlarkSyntaxException>(() => New().Execute("t.star", "f()[1, 2].x = 3\n[1][0, 1] += 1\n(1 + 2) = 3\n"));
}
