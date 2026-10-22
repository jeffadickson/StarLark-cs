using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>Examples copied from the Starlark spec (spec.md), each with the result the spec states.</summary>
public class SpecExampleTests
{
    private static Interpreter New() =>
        new() { Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableKeywordOnlyArguments = true } };

    private static string R(string expr) => Repr.Of(New().Eval(expr));

    private static string Global(string src, string name = "result") => Repr.Of(New().Execute("t", src).Globals[name]);

    [Theory]
    [InlineData("100 // 5 * 9 + 32", "212")]
    [InlineData("111111111 * 111111111", "12345678987654321")]
    [InlineData("int('0xffff', 16)", "65535")]
    [InlineData("1.23e45 * 1.23e45", "1.5129e+90")]
    [InlineData("3 / 2.0", "1.5")]
    [InlineData("3.0 // 2.0", "1.0")]
    [InlineData("'hello'[-1000:1000]", "\"hello\"")]
    [InlineData("'banana'[1::2]", "\"aaa\"")]
    [InlineData("'banana'[4::-2]", "\"nnb\"")]
    [InlineData("'abc'[1:-1]", "\"b\"")]
    [InlineData("0x12345678 & 0xFF", "120")]
    [InlineData("-1 >> 100", "-1")]
    [InlineData("0b01011101 << 2", "372")]
    [InlineData("3 * (True, 'a')", "(True, \"a\", True, \"a\", True, \"a\")")]
    [InlineData("'mur' * 2", "\"murmur\"")]
    [InlineData("~-1", "0")]
    [InlineData("0 and 'hello'", "0")]
    [InlineData("1 or 'hello'", "1")]
    [InlineData("'nasty' in 'dynasty'", "True")]
    [InlineData("97 in b'abc'", "True")]
    [InlineData("'Hello %s' % 'Bob'", "\"Hello Bob\"")]
    [InlineData("'Hello %s, your score is %d' % ('Bob', 75)", "\"Hello Bob, your score is 75\"")]
    [InlineData("'coordinates=%s' % ((40, -74),)", "\"coordinates=(40, -74)\"")]
    [InlineData("[x*y+z for (x, y), z in [((2, 3), 5), (('o', 2), '!')]]", "[11, \"oo!\"]")]
    [InlineData("[(x, y) for x in range(5) if x%2 == 0 for y in range(5) if y > x]",
        "[(0, 1), (0, 2), (0, 3), (0, 4), (2, 3), (2, 4)]")]
    [InlineData("max('two', 'three', 'four', key=len)", "\"three\"")]
    [InlineData("min('two', 'three', 'four', key=len)", "\"two\"")]
    [InlineData("sorted(['two', 'three', 'four'], key=len, reverse=True)", "[\"three\", \"four\", \"two\"]")]
    [InlineData("enumerate(['one', 'two'], 1)", "[(1, \"one\"), (2, \"two\")]")]
    [InlineData("zip(range(10), ['a', 'b', 'c'])", "[(0, \"a\"), (1, \"b\"), (2, \"c\")]")]
    [InlineData("reversed({'one': 1, 'two': 2}.keys())", "[\"two\", \"one\"]")]
    [InlineData("dict([(1, 2), ['a', 'b']])", "{1: 2, \"a\": \"b\"}")]
    [InlineData("int('0b111', 0)", "7")]
    [InlineData("int('0b0', 16)", "176")]
    [InlineData("float('-inf')", "-inf")]
    [InlineData("getattr('banana', 'split')('a')", "[\"b\", \"n\", \"n\", \"\"]")]
    [InlineData("'banana'.count('a')", "3")]
    [InlineData("'bonbon'.find('on', 2)", "4")]
    [InlineData("'filename.star'.startswith('name', 4, 7)", "False")]
    [InlineData("'foo.cc'.endswith(('.cc', '.h'))", "True")]
    [InlineData("'  hello   '.strip('h o')", "\"ell\"")]
    [InlineData("'banana'.replace('a', 'o', 2)", "\"bonona\"")]
    [InlineData("'one/two/three'.rpartition('/')", "(\"one/two\", \"/\", \"three\")")]
    [InlineData("'hello, world!'.count('o', 7, 12)", "1")]
    public void SpecExample(string expr, string expected) => Assert.Equal(expected, R(expr));

    [Fact]
    public void ClosureSeesLaterAssignmentInEnclosingFunction() =>
        Assert.Equal("[1, 2]", Global(
            "def f(x):\n  res = []\n  def get_x():\n    res.append(x)\n  get_x()\n  x = 2\n  get_x()\n  return res\nresult = f(1)\n"));

    [Fact]
    public void LocalShadowsGlobalThroughoutFunction()
    {
        // Spec: the binding of `y` makes it local to `hello`, so the earlier use also refers to the local.
        // The second iteration sees "hello" (the local), never the global "goodbye".
        const string src =
            "y = 'goodbye'\ndef hello():\n  for x in (1, 2):\n    if x == 2:\n      return y\n    if x == 1:\n      y = 'hello'\nresult = hello()\n";
        Assert.Equal("\"hello\"", Global(src));

        // And reading it before any assignment is a dynamic error, even though a global `y` exists.
        var ex = Assert.Throws<StarlarkException>(() => New().Execute("t",
            "y = 'goodbye'\ndef hello():\n  z = y\n  y = 'hello'\n  return z\nhello()\n"));
        Assert.Contains("Local variable `y` referenced before assignment", ex.RawMessage);
    }

    [Fact]
    public void GlobalReferencedBeforeAssignment()
    {
        var ex = Assert.Throws<StarlarkException>(() => New().Execute("t", "print(x)\nx = 'hello'\n"));
        Assert.Contains("Global variable `x` referenced before assignment", ex.RawMessage);
    }

    [Fact]
    public void ComprehensionVariablesDoNotLeak() =>
        Assert.Equal("1", Global("x = 1\n_ = [x for x in [2]]\nresult = x\n"));

    [Fact]
    public void ComprehensionLaterLoopVariableIsStaticallyInScope()
    {
        Assert.Equal("[]", R("[1//0 for x in [] for y in z for z in ()]"));
        var ex = Assert.Throws<StarlarkException>(() => New().Eval("[1//0 for x in [1] for y in z for z in ()]"));
        Assert.Contains("referenced before assignment", ex.RawMessage);
    }

    [Fact]
    public void KeywordOnlyAndVarargsExamples()
    {
        const string defs = "def g(a, *args, b=2, c):\n  return (a, b, c, args)\n";
        Assert.Equal("(1, 2, 3, (4,))", Global(defs + "result = g(1, 4, c=3)\n"));
        Assert.Equal("(1, 2, 3, (4, 5))", Global(defs + "result = g(1, c=3, *[4, 5])\n"));
        Assert.Contains("c", Assert.Throws<StarlarkException>(() => New().Execute("t", defs + "g(1, 3)\n")).RawMessage);

        const string star = "def f(a, *, b=2, c):\n  return (a, b, c)\n";
        Assert.Equal("(1, 2, 3)", Global(star + "result = f(1, c=3)\n"));
        Assert.Throws<StarlarkException>(() => New().Execute("t", star + "f(1, 3)\n"));
    }

    [Fact]
    public void PositionalAndNamedMix()
    {
        const string d = "def idiv(x, y):\n  return x // y\n";
        Assert.Equal("2", Global(d + "result = idiv(y=3, x=6)\n"));
        Assert.Equal("2", Global(d + "result = idiv(6, y=3)\n"));
    }

    [Fact]
    public void KwargsDuplicateIsDynamicError() =>
        Assert.Throws<StarlarkException>(() => New().Execute("t", "def f(x):\n  return x\nf(x=1, **dict(x=2))\n"));

    [Fact]
    public void ForLoopTargetsMayBeSubscripts() =>
        Assert.Equal("{\"a\": 1, \"b\": 2}", Global(
            "def list_to_dict(items):\n  m = {}\n  for k, m[k] in items:\n    pass\n  return m\nresult = list_to_dict([('a', 1), ('b', 2)])\n"));

    [Fact]
    public void ForMutationErrorAndWorkaround()
    {
        Assert.Throws<StarlarkException>(() => New().Execute("t",
            "a = [5, 11, 12, 13, 8]\nfor x in a:\n  if x > 10:\n    a.remove(x)\n"));
        Assert.Equal("[5, 8]", Global(
            "def f():\n  a = [5, 11, 12, 13, 8]\n  for x in list(a):\n    if x > 10:\n      a.remove(x)\n  return a\nresult = f()\n"));
    }

    [Fact]
    public void EnumerateReturnsListSoLoopsDoNotLockTheSource() =>
        Assert.Equal("[1, 2, 1, 2]", Global(
            "def f():\n  xs = [1, 2]\n  for i, x in enumerate(xs):\n    xs.append(x)\n  return xs\nresult = f()\n"));

    [Fact]
    public void LoadedNamesStartingWithUnderscoreAreNotExported()
    {
        var it = New();
        it.SourceLoader = _ => "_private = 1\npublic = 2\n";
        Assert.Throws<StarlarkException>(() => it.Execute("t", "load('m', '_private')\n"));
        Assert.Equal("2", Repr.Of(it.Execute("t", "load('m', y='public')\nresult = y\n").Globals["result"]));
    }

    [Fact]
    public void ReturnWithMultipleValuesIsTuple() =>
        Assert.Equal("(1, 2)", Global("def f():\n  return 1, 2\nresult = f()\n"));

    [Fact]
    public void AugmentedAssignmentEvaluatesTargetOnce() =>
        Assert.Equal("([20], 1)", Global(
            "calls = []\ndef idx():\n  calls.append(1)\n  return 0\ndef f():\n  a = [10]\n  a[idx()] *= 2\n  return (a, len(calls))\nresult = f()\n"));

    [Fact]
    public void DictUnionIsInsertionOrdered() =>
        Assert.Equal("{\"a\": 3, \"b\": 2, \"c\": 4}", R("{'a': 1, 'b': 2} | {'a': 3, 'c': 4}"));

    [Fact]
    public void FrozenGlobalsRejectMutationFromFunctions()
    {
        var it = New();
        var m = it.Execute("t", "xs = [1]\ndef add():\n  xs.append(2)\n");
        var ex = Assert.Throws<StarlarkException>(() => it.CallValue(m.Globals["add"], []));
        Assert.Contains("frozen", ex.RawMessage);
    }
}
