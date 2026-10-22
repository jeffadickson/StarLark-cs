using System.Numerics;
using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

public class EvalTests
{
    private static Interpreter New(List<string>? output = null)
    {
        var it = new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true } };
        if (output is not null) it.Print = output.Add;
        return it;
    }

    private static object? Eval(string expr) => New().Eval(expr);

    private static object? Run(string src, string name = "result")
    {
        var m = New().Execute("t.star", src);
        Assert.True(m.TryGet(name, out var v), $"no global `{name}`");
        return v;
    }

    private static string R(object? v) => Repr.Of(v);

    private static StarlarkException Fails(string src) =>
        Assert.Throws<StarlarkException>(() => New().Execute("t.star", src));

    // ---------------------------------------------------------------- arithmetic

    [Theory]
    [InlineData("1 + 2", "3")]
    [InlineData("7 // 2", "3")]
    [InlineData("-7 // 2", "-4")]
    [InlineData("7 // -2", "-4")]
    [InlineData("-7 % 3", "2")]
    [InlineData("7 % -3", "-2")]
    [InlineData("7 / 2", "3.5")]
    [InlineData("1 << 10", "1024")]
    [InlineData("-1 >> 1", "-1")]
    [InlineData("~5", "-6")]
    [InlineData("5 & 3 | 8 ^ 1", "9")]
    [InlineData("1 + 2 * 3 - 4", "3")]
    [InlineData("1.0 + 2", "3.0")]
    [InlineData("1e100", "1e+100")]
    [InlineData("1.5e-7", "1.5e-07")]
    [InlineData("100000000000000000000 * 100000000000000000000", "10000000000000000000000000000000000000000")]
    public void Arithmetic(string expr, string expected) => Assert.Equal(expected, R(Eval(expr)));

    [Fact]
    public void DivisionByZeroFails()
    {
        Assert.Throws<StarlarkException>(() => Eval("1 // 0"));
        Assert.Throws<StarlarkException>(() => Eval("1 / 0"));
        Assert.Throws<StarlarkException>(() => Eval("1 % 0"));
    }

    [Fact]
    public void ComparisonsAndEquality()
    {
        Assert.Equal("True", R(Eval("1 == 1.0")));
        Assert.Equal("True", R(Eval("[1, 2] < [1, 3]")));
        Assert.Equal("True", R(Eval("(1, 'a') == (1, 'a')")));
        Assert.Equal("True", R(Eval("'abc' < 'abd'")));
        Assert.Equal("False", R(Eval("None == 0")));
        Assert.Equal("True", R(Eval("{1: 2} == {1: 2}")));
        Assert.Throws<StarlarkException>(() => Eval("1 < 'a'"));
    }

    [Fact]
    public void ShortCircuitReturnsOperand()
    {
        Assert.Equal("5", R(Eval("0 or 5")));
        Assert.Equal("0", R(Eval("0 and 5")));
        Assert.Equal("\"x\"", R(Eval("'' or 'x'")));
        Assert.Equal("True", R(Eval("not []")));
    }

    // ---------------------------------------------------------------- strings

    [Theory]
    [InlineData("'a,b,c'.split(',')", "[\"a\", \"b\", \"c\"]")]
    [InlineData("'  x  y '.split()", "[\"x\", \"y\"]")]
    [InlineData("'abc'.upper()", "\"ABC\"")]
    [InlineData("'-'.join(['a', 'b'])", "\"a-b\"")]
    [InlineData("'hello'.replace('l', 'L')", "\"heLLo\"")]
    [InlineData("'hello'.find('l')", "2")]
    [InlineData("'hello'.rfind('l')", "3")]
    [InlineData("'hello'.find('z')", "-1")]
    [InlineData("'  hi '.strip()", "\"hi\"")]
    [InlineData("'xxhixx'.strip('x')", "\"hi\"")]
    [InlineData("'hello'.startswith('he')", "True")]
    [InlineData("'hello'.endswith(('x', 'lo'))", "True")]
    [InlineData("'a b'.partition(' ')", "(\"a\", \" \", \"b\")")]
    [InlineData("'hello'[1:3]", "\"el\"")]
    [InlineData("'hello'[::-1]", "\"olleh\"")]
    [InlineData("'hello'[-1]", "\"o\"")]
    [InlineData("'a{}b{}'.format(1, 'x')", "\"a1bx\"")]
    [InlineData("'{0}{0}{n}'.format('a', n=2)", "\"aa2\"")]
    [InlineData("'%d-%s-%r' % (3, 'a', 'b')", "\"3-a-\\\"b\\\"\"")]
    [InlineData("'%5.2f' % 1.0 if False else '%x' % 255", "\"ff\"")]
    [InlineData("'100%%' % ()", "\"100%\"")]
    [InlineData("'ab' * 3", "\"ababab\"")]
    [InlineData("'b' in 'abc'", "True")]
    [InlineData("len('héllo')", "5")]
    [InlineData("'one\\ntwo\\n'.splitlines()", "[\"one\", \"two\"]")]
    [InlineData("'a,b,c'.rsplit(',', 1)", "[\"a,b\", \"c\"]")]
    [InlineData("'a,b,c'.split(',', 1)", "[\"a\", \"b,c\"]")]
    public void Strings(string expr, string expected) => Assert.Equal(expected, R(Eval(expr)));

    [Fact]
    public void StringsAreSequencesOfUtf16Elements()
    {
        // The spec lets an implementation choose K = 8 or K = 16; this one uses 16-bit elements (like Java).
        Assert.Equal("1", R(Eval("len('é')")));
        Assert.Equal("2", R(Eval("len('😀')")));
        Assert.Equal("\"b\"", R(Eval("'a😀b'[3]")));
    }

    [Fact]
    public void StringsAreNotIterable() => Assert.Throws<StarlarkException>(() => Eval("[x for x in 'abc']"));

    [Fact]
    public void FStrings()
    {
        var it = new Interpreter { Dialect = Dialect.Standard with { EnableFStrings = true } };
        Assert.Equal("a=3 b=\"x\"", it.Execute("t", "n = 3\ns = 'x'\nr = f\"a={n} b={s!r}\"\n").Globals["r"]);
    }

    // ---------------------------------------------------------------- containers

    [Fact]
    public void ListOperations()
    {
        Assert.Equal("[1, 2, 3]", R(Run("result = [1]\nresult.append(2)\nresult.extend([3])\n")));
        Assert.Equal("[1, 9, 2]", R(Run("result = [1, 2]\nresult.insert(1, 9)\n")));
        Assert.Equal("[2]", R(Run("result = [1, 2]\nresult.remove(1)\n")));
        Assert.Equal("2", R(Run("result = [1, 2].pop()")));
        Assert.Equal("[1, 2, 3, 4]", R(Eval("[1, 2] + [3, 4]")));
        Assert.Equal("[3, 2, 1]", R(Eval("[1, 2, 3][::-1]")));
        Assert.Equal("[1, 1, 1]", R(Eval("[1] * 3")));
    }

    [Fact]
    public void AugmentedAssignmentOnListMutatesInPlace() =>
        Assert.Equal("[1, 2, 3]", R(Run(
            "def f():\n  a = [1]\n  b = a\n  a += [2, 3]\n  return b\nresult = f()\n")));

    [Fact]
    public void ListPlusIsNewList() =>
        Assert.Equal("[1]", R(Run(
            "def f():\n  a = [1]\n  b = a\n  a = a + [2]\n  return b\nresult = f()\n")));

    [Fact]
    public void AugmentedAssignmentToGlobalIsStaticError() =>
        Assert.Contains("augmented assignment on global", Fails("a = [1]\na += [2]\n").RawMessage);

    [Fact]
    public void ReassigningGlobalIsStaticError() =>
        Assert.Contains("cannot reassign global `x` declared on line 1", Fails("x = 1\nx = 2\n").RawMessage);

    [Fact]
    public void UndefinedNameIsStaticErrorEvenInUnexecutedCode() =>
        Assert.Contains("`g` not found", Fails("def f():\n  if False:\n    g()\n").RawMessage);

    [Fact]
    public void LoadCannotBindExistingGlobal()
    {
        var it = New();
        it.SourceLoader = _ => "x = 1\n";
        Assert.Throws<StarlarkException>(() => it.Execute("t", "x = 2\nload('m', 'x')\n"));
    }

    [Fact]
    public void SetOperations()
    {
        Assert.Equal("set([1, 2, 3])", R(Eval("set([1, 2]) | set([3, 2])")));
        Assert.Equal("set([2])", R(Eval("set([1, 2]) & set([2, 3])")));
        Assert.Equal("set([1])", R(Eval("set([1, 2]) - set([2, 3])")));
        Assert.Equal("set([1, 3])", R(Eval("set([1, 2]) ^ set([2, 3])")));
        Assert.Equal("set()", R(Eval("set()")));
        Assert.Equal("True", R(Eval("set([1, 2]) == set([2, 1])")));
        Assert.Equal("True", R(Eval("set() != []")));
        Assert.Equal("[3, 1, 2]", R(Eval("list(set([3, 1, 1, 2]))")));
        Assert.Equal("set([1, 2, 3, 4])", R(Eval("set([1, 2]).union([2, 3], {3: 'a', 4: 'b'})")));
        Assert.Equal("set([2])", R(Eval("set([1, 2, 3]).difference([0, 1], [3, 4])")));
        Assert.Equal("set([1])", R(Eval("set([1, 2, 3]).intersection([0, 1], [1, 2])")));
        Assert.Equal("True", R(Eval("set([1]).issubset([1, 2])")));
        Assert.Equal("True", R(Eval("set([1, 2]).isdisjoint([3])")));
        Assert.Throws<StarlarkException>(() => Eval("set([1]) < set([2])"));
        Assert.Throws<StarlarkException>(() => Eval("set([[1]])"));
    }

    [Fact]
    public void SetMutationInFunction() =>
        Assert.Equal("[2, 4]", R(Run(
            "def f():\n  s = set([1, 2])\n  s |= set([2, 3, 4])\n  s &= set([0, 2, 3, 4])\n  s -= set([0, 3])\n  return list(s)\nresult = f()\n")));

    [Fact]
    public void FloatSemantics()
    {
        Assert.Equal("True", R(Eval("float('nan') == float('nan')")));
        Assert.Equal("True", R(Eval("float('inf') < float('nan')")));
        Assert.Equal("True", R(Eval("0.0 == -0.0")));
        Assert.Equal("False", R(Eval("(1 << 53) + 1 == ((1 << 53) + 1) + 0.0")));
        Assert.Equal("1.2345678e+07", Repr.Str(Eval("12345678.0")));
        Assert.Equal("0.00001", Repr.Str(Eval("1e-5")));
        Assert.Equal("9e-06", Repr.Str(Eval("9e-6")));
        Assert.Equal("1e+06", Repr.Str(Eval("1e6")));
        Assert.Equal("100000.0", Repr.Str(Eval("1e5")));
        Assert.Equal("1.0", Repr.Str(Eval("1.0")));
        Assert.Equal("+inf", Repr.Str(Eval("float('+Infinity')")));
        Assert.Throws<StarlarkException>(() => Eval("1.0 // 0.0"));
    }

    [Fact]
    public void PercentFormatting()
    {
        Assert.Equal("\"1.230000e+12\"", R(Eval("'%e' % 1.23e12")));
        Assert.Equal("\"1230000000000.000000\"", R(Eval("'%f' % 1.23e12")));
        Assert.Equal("\"1.5\"", R(Eval("'%g' % 1.5")));
        Assert.Equal("\"1e+45\"", R(Eval("'%g' % 1e45")));
        Assert.Equal("\"ff FF 17\"", R(Eval("'%x %X %o' % (255, 255, 15)")));
        Assert.Equal("\"3\"", R(Eval("'%d' % 3.9")));
        Assert.Equal("\"A 123 hi Z\"", R(Eval("'A %(foo)d %(bar)s Z' % {'foo': 123, 'bar': 'hi'}")));
        Assert.Equal("\"(40, -74)\"", R(Eval("'%s' % ((40, -74),)")));
        Assert.Throws<StarlarkException>(() => Eval("'%s' % (40, -74)"));
        Assert.Throws<StarlarkException>(() => Eval("'%d' % True"));
        Assert.Throws<StarlarkException>(() => Eval("'%d' % 'x'"));
    }

    [Fact]
    public void StringMethodSpecExamples()
    {
        Assert.Equal("\"a2b3c1\"", R(Eval("'a{x}b{y}c{}'.format(1, x=2, y=3)")));
        Assert.Equal("[\"one two\", \"three\"]", R(Eval("'one two  three'.rsplit(None, 1)")));
        Assert.Equal("[\"one\", \"two  three\"]", R(Eval("'one two  three'.split(None, 1)")));
        Assert.Equal("[\"one\", \"two\", \"\", \"three\"]", R(Eval("'one two  three'.split(' ')")));
        Assert.Equal("[\"\"]", R(Eval("''.split('o')")));
        Assert.Equal("[]", R(Eval("'   '.split()")));
        Assert.Equal("\"ello  \"", R(Eval("'   hello  '.lstrip('h o')")));
        Assert.Equal("-1", R(Eval("'bonbon'.find('on', 2, 5)")));
        Assert.Equal("1", R(Eval("'bonbon'.rfind('on', None, 5)")));
        Assert.Equal("True", R(Eval("'Catch-22'.istitle()")));
        Assert.Equal("False", R(Eval("'HAL-9000'.istitle()")));
        Assert.Equal("\"Hello, World!\"", R(Eval("'hElLo, WoRlD!'.title()")));
        Assert.Equal("\"catamaran\"", R(Eval("'a'.join('ctmrn'.elems())")));
        Assert.Equal("\"string.elems\"", R(Eval("type('abc'.elems())")));
        Assert.Equal("[\"A\", \"B\", \"C\", \"D\"]", R(Eval("'A\\nB\\rC\\r\\nD'.splitlines()")));
        Assert.Equal("\"ban\"", R(Eval("'banana'.removesuffix('ana')")));
    }

    [Fact]
    public void BytesBehaviour()
    {
        Assert.Equal("b\"ABC\"", R(Eval("bytes([65, 66, 67])")));
        Assert.Equal("[65, 66, 67]", R(Eval("list(b'ABC'.elems())")));
        Assert.Equal("\"bytes.elems\"", R(Eval("type(b'ABC'.elems())")));
        Assert.Equal("True", R(Eval("97 in b'abc'")));
        Assert.Equal("True", R(Eval("b'nasty' in b'dynasty'")));
        Assert.Equal("65", R(Eval("b'ABC'[0]")));
        Assert.Equal("b\"BC\"", R(Eval("b'ABC'[1:]")));
        Assert.Equal("\"ABC\"", R(Eval("str(b'ABC')")));
        Assert.Throws<StarlarkException>(() => Eval("bytes(65)"));
        Assert.Throws<StarlarkException>(() => Eval("bytes([256])"));
    }

    [Fact]
    public void HashFollowsSpec()
    {
        Assert.Equal("99162322", R(Eval("hash('hello')")));
        Assert.Equal("113318802", R(Eval("hash('world')")));
        Assert.Throws<StarlarkException>(() => Eval("hash(1)"));
    }

    [Fact]
    public void RangeBehaviour()
    {
        Assert.Equal("range(10)".Replace("range(10)", "range(0, 10)"), Repr.Of(Eval("range(10)")));
        Assert.Equal("True", R(Eval("range(0, 10, 2) == range(0, 9, 2)")));
        Assert.Equal("True", R(Eval("4 in range(0, 10, 2)")));
        Assert.Equal("False", R(Eval("5 in range(0, 10, 2)")));
        Assert.Equal("[4, 3, 2, 1, 0]", R(Eval("reversed(range(5))")));
        Assert.Equal("2", R(Eval("range(10)[-8]")));
        Assert.Throws<StarlarkException>(() => Eval("{range(3): 1}"));
    }

    [Fact]
    public void FrozenFunctionDefaultsBecomeImmutable()
    {
        var it = New();
        var m = it.Execute("t", "def f(x=[]):\n  return x\n");
        var f = (ICallable)m.Globals["f"]!;
        var dflt = (StarList)it.CallValue(f, [])!;
        Assert.True(dflt.Frozen);
    }

    [Fact]
    public void DictOperations()
    {
        Assert.Equal("{\"a\": 1, \"b\": 2}", R(Eval("{'a': 1, 'b': 2}")));
        Assert.Equal("[\"a\", \"b\"]", R(Eval("{'a': 1, 'b': 2}.keys()")));
        Assert.Equal("[(\"a\", 1)]", R(Eval("{'a': 1}.items()")));
        Assert.Equal("3", R(Eval("{'a': 1}.get('z', 3)")));
        Assert.Equal("{\"a\": 1, \"b\": 2}", R(Run("result = {'a': 1}\nresult['b'] = 2\n")));
        Assert.Equal("{\"a\": 1, \"b\": 2}", R(Run("result = {'a': 1}\nresult.update({'b': 2})\n")));
        Assert.Equal("{\"x\": 1}", R(Eval("{k: 1 for k in ['x']}")));
        Assert.Equal("{\"a\": 1, \"b\": 2}", R(Eval("{'a': 1} | {'b': 2}")));
    }

    [Fact]
    public void DictPreservesInsertionOrder() =>
        Assert.Equal("[\"z\", \"a\", \"m\"]", R(Eval("list({'z': 1, 'a': 2, 'm': 3})")));

    [Fact]
    public void DictKeyLookupAndErrors()
    {
        Assert.Throws<StarlarkException>(() => Eval("{}['x']"));
        Assert.Throws<StarlarkException>(() => Eval("{[1]: 2}"));
        Assert.Throws<StarlarkException>(() => Eval("{'a': 1, 'a': 2}"));
        Assert.Equal("2", R(Eval("{(1, 2): 2}[(1, 2)]")));
        Assert.Equal("1", R(Eval("{1: 1}[1.0]")));
        Assert.Equal("5", R(Eval("{None: 5}[None]")));
    }

    [Fact]
    public void TuplesAndUnpacking()
    {
        Assert.Equal("(1, 2)", R(Eval("(1, 2)")));
        Assert.Equal("(1,)", R(Eval("(1,)")));
        Assert.Equal("3", R(Run("a, b = 1, 2\nresult = a + b\n")));
        Assert.Equal("2", R(Run("[a, (b, c)] = [1, (2, 3)]\nresult = b\n")));
        Assert.Contains("Too many values", Fails("a, b = [1, 2, 3]").RawMessage);
        Assert.Contains("Too few values", Fails("a, b, c = [1, 2]").RawMessage);
    }

    [Fact]
    public void SlicesFollowPythonRules()
    {
        Assert.Equal("[2, 3]", R(Eval("[1, 2, 3, 4][1:3]")));
        Assert.Equal("[1, 3]", R(Eval("[1, 2, 3, 4][:3:2]")));
        Assert.Equal("[4, 3, 2, 1]", R(Eval("[1, 2, 3, 4][::-1]")));
        Assert.Equal("[3, 2]", R(Eval("[1, 2, 3, 4][2:0:-1]")));
        Assert.Equal("[]", R(Eval("[1, 2, 3][5:10]")));
        Assert.Equal("[2, 3]", R(Eval("[1, 2, 3][-2:]")));
    }

    [Fact]
    public void IndexOutOfBounds() => Assert.Throws<StarlarkException>(() => Eval("[1][1]"));

    // ---------------------------------------------------------------- control flow & functions

    [Fact]
    public void ForLoopBreakContinue() =>
        Assert.Equal("[0, 1, 3, 4]", R(Run(
            "result = []\nfor i in range(10):\n  if i == 2:\n    continue\n  if i == 5:\n    break\n  result.append(i)\n")));

    [Fact]
    public void FunctionsDefaultsKeywordsVarargs()
    {
        Assert.Equal("6", R(Run("def f(a, b=2, *rest, **kw):\n  return a * b\nresult = f(3)\n")));
        Assert.Equal("(1, (2, 3))", R(Run("def f(a, *rest):\n  return (a, rest)\nresult = f(1, 2, 3)\n")));
        Assert.Equal("{\"x\": 1}", R(Run("def f(**kw):\n  return kw\nresult = f(x=1)\n")));
        Assert.Equal("3", R(Run("def f(a, b):\n  return a - b\nresult = f(b=1, a=4)\n")));
        Assert.Equal("5", R(Run("def f(a, b):\n  return a + b\nargs = [2]\nkw = {'b': 3}\nresult = f(*args, **kw)\n")));
    }

    [Fact]
    public void KeywordOnlyParameters()
    {
        var it = new Interpreter { Dialect = Dialect.Standard with { EnableKeywordOnlyArguments = true } };
        var m = it.Execute("t", "def f(a, *, b):\n  return a + b\nresult = f(1, b=2)\n");
        Assert.Equal("3", Repr.Of(m.Globals["result"]));
        Assert.Throws<StarlarkException>(() => it.Execute("t", "def f(a, *, b):\n  return a\nx = f(1, 2)\n"));
    }

    [Fact]
    public void ArgumentErrors()
    {
        Assert.Contains("Missing parameter", Fails("def f(a):\n  pass\nf()\n").RawMessage);
        Assert.Contains("Too many positional", Fails("def f(a):\n  pass\nf(1, 2)\n").RawMessage);
        Assert.Contains("Unexpected parameter", Fails("def f(a):\n  pass\nf(1, z=2)\n").RawMessage);
        Assert.Contains("more than once", Fails("def f(a):\n  pass\nf(1, a=2)\n").RawMessage);
    }

    [Fact]
    public void ClosuresCaptureEnclosingScope() =>
        Assert.Equal("[11, 12]", R(Run(
            "def make(n):\n  def add(x):\n    return x + n\n  return add\nf = make(10)\nresult = [f(1), f(2)]\n")));

    [Fact]
    public void LambdaAndHigherOrder() =>
        Assert.Equal("[2, 4, 6]", R(Run("result = [(lambda x: x * 2)(i) for i in [1, 2, 3]]\n")));

    [Fact]
    public void DefaultValuesEvaluatedOnce() =>
        Assert.Equal("[1, 2]", R(Run(
            "def f(x=[]):\n  x.append(1)\n  return len(x)\nresult = [f(), f()]\n")));

    [Fact]
    public void LocalsAreResolvedStatically() =>
        Assert.Contains("referenced before assignment", Fails("x = 1\ndef f():\n  y = x\n  x = 2\n  return y\nf()\n").RawMessage);

    [Fact]
    public void GlobalsReadableFromFunctions() =>
        Assert.Equal("11", R(Run("g = 10\ndef f():\n  return g + 1\nresult = f()\n")));

    [Fact]
    public void RecursionIsRejected() =>
        Assert.Contains("recursively", Fails("def f(n):\n  return f(n + 1)\nf(0)\n").RawMessage);

    [Fact]
    public void ComprehensionsHaveOwnScope() =>
        Assert.Equal("5", R(Run("i = 5\nxs = [i for i in range(3)]\nresult = i\n")));

    [Fact]
    public void NestedComprehension() =>
        Assert.Equal("[(0, 1), (0, 2), (1, 2)]", R(Run(
            "result = [(a, b) for a in range(3) for b in range(3) if a < b]\n")));

    // ---------------------------------------------------------------- mutation & freezing

    [Fact]
    public void GlobalsAreFrozenAfterExecution()
    {
        var it = New();
        var m = it.Execute("t", "xs = [1, 2]\nd = {'a': [1]}\n");
        var xs = (StarList)m.Globals["xs"]!;
        Assert.True(xs.Frozen);
        Assert.Throws<StarlarkException>(() => xs.Add(3));
        var inner = (StarList)((StarDict)m.Globals["d"]!).Entries.First().Value!;
        Assert.True(inner.Frozen);
    }

    [Fact]
    public void FrozenValuesFromLoadedModulesCannotBeMutated()
    {
        var it = New();
        it.SourceLoader = id => id == "lib.star" ? "shared = [1]\n" : null;
        var ex = Assert.Throws<StarlarkException>(() => it.Execute("t", "load('lib.star', 'shared')\nshared.append(2)\n"));
        Assert.Contains("frozen", ex.RawMessage);
    }

    [Fact]
    public void CannotMutateWhileIterating() =>
        Assert.Contains("while iterating", Fails("xs = [1, 2]\nfor x in xs:\n  xs.append(x)\n").RawMessage);

    [Fact]
    public void MutatingAfterLoopIsFine() =>
        Assert.Equal("[1, 2, 3]", R(Run("result = [1, 2]\nfor x in result:\n  pass\nresult.append(3)\n")));

    // ---------------------------------------------------------------- load

    [Fact]
    public void LoadImportsAndAliases()
    {
        var it = New();
        it.SourceLoader = id => id switch
        {
            "a.star" => "x = 1\ny = 2\n",
            "b.star" => "load('a.star', 'x', yy = 'y')\nz = x + yy\n",
            _ => null,
        };
        var m = it.Execute("t", "load('b.star', 'z')\nresult = z\n");
        Assert.Equal("3", R(m.Globals["result"]));
    }

    [Fact]
    public void LoadErrors()
    {
        var it = New();
        it.SourceLoader = _ => "x = 1\n";
        Assert.Throws<StarlarkException>(() => it.Execute("t", "load('a', 'missing')\n"));
        it.SourceLoader = _ => null;
        Assert.Throws<StarlarkException>(() => new Interpreter().Execute("t", "load('nope', 'x')\n"));
    }

    // ---------------------------------------------------------------- builtins

    [Theory]
    [InlineData("len([1, 2, 3])", "3")]
    [InlineData("type(1)", "\"int\"")]
    [InlineData("type(1.0)", "\"float\"")]
    [InlineData("type(None)", "\"NoneType\"")]
    [InlineData("type([])", "\"list\"")]
    [InlineData("type(len)", "\"builtin_function_or_method\"")]
    [InlineData("type(lambda: 1)", "\"function\"")]
    [InlineData("str(1)", "\"1\"")]
    [InlineData("str('a')", "\"a\"")]
    [InlineData("repr('a')", "\"\\\"a\\\"\"")]
    [InlineData("int('42')", "42")]
    [InlineData("int('ff', 16)", "255")]
    [InlineData("int('0x1f', 16)", "31")]
    [InlineData("int(3.9)", "3")]
    [InlineData("int(-3.9)", "-3")]
    [InlineData("float('1.5')", "1.5")]
    [InlineData("bool([])", "False")]
    [InlineData("abs(-3)", "3")]
    [InlineData("min([3, 1, 2])", "1")]
    [InlineData("max(1, 5, 2)", "5")]
    [InlineData("sorted([3, 1, 2])", "[1, 2, 3]")]
    [InlineData("sorted([3, 1, 2], reverse=True)", "[3, 2, 1]")]
    [InlineData("sorted(['bb', 'a', 'ccc'], key=len)", "[\"a\", \"bb\", \"ccc\"]")]
    [InlineData("reversed([1, 2, 3])", "[3, 2, 1]")]
    [InlineData("list(range(3))", "[0, 1, 2]")]
    [InlineData("list(range(5, 0, -2))", "[5, 3, 1]")]
    [InlineData("enumerate(['a', 'b'])", "[(0, \"a\"), (1, \"b\")]")]
    [InlineData("zip([1, 2], ['a', 'b', 'c'])", "[(1, \"a\"), (2, \"b\")]")]
    [InlineData("all([1, 2])", "True")]
    [InlineData("any([0, ''])", "False")]
    [InlineData("dict([('a', 1)], b=2)", "{\"a\": 1, \"b\": 2}")]
    [InlineData("tuple([1, 2])", "(1, 2)")]
    [InlineData("hasattr('a', 'upper')", "True")]
    [InlineData("getattr('a', 'nope', 7)", "7")]
    [InlineData("range(3)[1]", "1")]
    [InlineData("len(range(10, 0, -3))", "4")]
    public void BuiltinFunctions(string expr, string expected) => Assert.Equal(expected, R(Eval(expr)));

    [Fact]
    public void SortIsStable() =>
        Assert.Equal("[(1, \"b\"), (1, \"a\"), (2, \"c\")]", R(Eval(
            "sorted([(2, 'c'), (1, 'b'), (1, 'a')], key=lambda p: p[0])")));

    [Fact]
    public void SortReverseKeepsStableTies() =>
        Assert.Equal("[(2, \"c\"), (1, \"b\"), (1, \"a\")]", R(Eval(
            "sorted([(1, 'b'), (2, 'c'), (1, 'a')], key=lambda p: p[0], reverse=True)")));

    [Fact]
    public void PrintAndFail()
    {
        var output = new List<string>();
        New(output).Execute("t", "print('a', 1, [2])\n");
        Assert.Equal(["a 1 [2]"], output);
        Assert.Contains("fail: boom", Fails("fail('boom')").RawMessage);
    }

    [Fact]
    public void HostCanDefineGlobalsAndFunctions()
    {
        var it = New();
        it.Define("answer", new BigInteger(42));
        it.Define("twice", (_, a, _) => (BigInteger)a[0]! * 2);
        var m = it.Execute("t", "result = twice(answer)\n");
        Assert.Equal("84", R(m.Globals["result"]));
    }

    [Fact]
    public void HostCanCallStarlarkFunctions()
    {
        var it = New();
        var m = it.Execute("t", "def add(a, b):\n  return a + b\n");
        Assert.Equal("5", R(it.CallValue(m.Globals["add"], [new BigInteger(2), new BigInteger(3)])));
    }

    [Fact]
    public void ErrorsCarrySourceLocation()
    {
        var ex = Fails("x = 1\ny = x + 'a'\n");
        Assert.Contains("--> t.star:2:", ex.Message);
        Assert.Contains("y = x + 'a'", ex.Message);
    }

    [Fact]
    public void SpecExampleFromReadme()
    {
        const string src = """
            def fib(n):
              a, b = 0, 1
              out = []
              for _ in range(n):
                out.append(a)
                a, b = b, a + b
              return out

            result = fib(10)
            """;
        Assert.Equal("[0, 1, 1, 2, 3, 5, 8, 13, 21, 34]", R(Run(src)));
    }
}
