using Starlark.Syntax;
using static Starlark.Syntax.Tests.TestSupport;

namespace Starlark.Syntax.Tests;

public class GrammarTests
{
    private static void AssertDisplayRoundtrip(string program, string expected)
    {
        string displayed = Parse(program);
        Assert.Equal(expected, displayed);
        Assert.Equal(displayed, Parse(displayed));
    }

    private static void AssertParseFails(params string[] programs)
    {
        foreach (var p in programs)
        {
            Assert.Throws<StarlarkSyntaxException>(() => ParseAst(p));
        }
    }

    private static void AssertErrorSpanSource(string program, string expected)
    {
        var e = ParseErr(program);
        Assert.Equal(expected, e.Location.SourceSpan);
    }

    [Fact]
    public void Empty()
    {
        Assert.Equal("", Parse("\n"));
        Assert.Equal("", Parse(""));
        Assert.Equal("", Parse("    "));
        Assert.Equal("", Parse("    \n"));
        Assert.Equal("x = 1\n", Parse("x = 1\n   "));
        Assert.Equal("x = 1\n", Parse("x = 1\n   \n"));
    }

    [Theory]
    [InlineData("=")]
    [InlineData("+=")]
    [InlineData("-=")]
    [InlineData("*=")]
    [InlineData("/=")]
    [InlineData("//=")]
    [InlineData("%=")]
    [InlineData("&=")]
    [InlineData("|=")]
    [InlineData("^=")]
    [InlineData("<<=")]
    [InlineData(">>=")]
    public void AssignOps(string op) => Assert.Equal($"x {op} 1\n", Parse($"x {op} 1\n"));

    [Fact]
    public void TopLevelComment() => Assert.Equal("", Parse("# Test"));

    [Fact]
    public void TopLevelLoad()
    {
        const string want = "load(\"//top/level/load.bzl\", \"top-level\")\n";
        Assert.Equal(want, Parse("\nload(\"//top/level/load.bzl\", \"top-level\")\n"));
        Assert.Equal(want, Parse("\nload(\"//top/level/load.bzl\", \"top-level\")"));
        Assert.Equal(want, Parse("\nload(\n  \"//top/level/load.bzl\",\n  \"top-level\",\n)\n"));
        Assert.Equal(want, Parse(want));
    }

    [Fact]
    public void TopLevelAssignation() =>
        Assert.Equal("_ASSIGNATION = \"top-level\"\n", Parse("\n_ASSIGNATION = 'top-level'\n"));

    [Fact]
    public void TopLevelDocstring() =>
        Assert.Equal("\"Top-level docstring\"\n", Parse("\n\"\"\"Top-level docstring\"\"\"\n"));

    [Fact]
    public void TopLevelDef()
    {
        Assert.Equal("def toto():\n  pass\n", Parse("def toto():\n  pass\n"));
        ParseFailsWith("top_level_def", All with { EnableDef = false }, "def toto():\n  pass");
        Assert.Equal("def toto():\n  pass\n", Parse("def toto():\n  pass"));
        Assert.Equal("def toto():\n  pass\ndef titi():\n  return 1\n", Parse("def toto():\n  pass\ndef titi(): return 1"));
        Assert.Equal("def toto():\n  pass\ndef titi():\n  return 1\n", Parse("def toto():\n  pass\n\ndef titi(): return 1"));
        Assert.Equal("def t():\n  pass\n", Parse("def t():\n\n  pass"));
    }

    [Fact]
    public void TopLevelStatements()
    {
        ParseFailsWith("top_level_statements", All with { EnableTopLevelStmt = false },
            "x = 1\nif x == 1:\n  x = 2\nx = 3",
            "x = 1\nfor x in []:\n   pass\n");
        Assert.Equal("pass\n", Parse("pass", Dialect.Standard));
        Assert.Equal("if (x == 1):\n  x = 2\n", Parse("if x == 1:\n  x = 2"));
        Assert.Equal("for x in []:\n  pass\n", Parse("for x in []:\n   pass\n"));
        Assert.Equal("pass\n", Parse("pass"));
    }

    [Fact]
    public void TopLevelDefWithDocstring() =>
        Assert.Equal("\"Top-level docstring\"\ndef toto():\n  pass\n",
            Parse("\"\"\"Top-level docstring\"\"\"\n\ndef toto():\n  pass\n"));

    [Fact]
    public void IfElse() =>
        Assert.Equal("def d():\n  if True:\n    a\n  else:\n    b\n",
            Parse("def d():\n  if True:\n    a\n  else:\n    b"));

    [Fact]
    public void KwargsPassing() =>
        Assert.Equal("f(x, *a, **b)\nf(x, *a, **{a: b})\nf(x, *[a], **b)\n",
            Parse("f(x, *a, **b); f(x, *a, **{a:b}); f(x, *[a], **b)"));

    [Fact]
    public void UnaryOp()
    {
        Assert.Equal("a = -1\n", Parse("a = -1"));
        Assert.Equal("a = +1\n", Parse("a = +1"));
        Assert.Equal("a = -a\n", Parse("a = -a"));
        Assert.Equal("a = +a\n", Parse("a = +a"));
    }

    [Fact]
    public void Tuples()
    {
        Assert.Equal("a = -1\n", Parse("a = (-1)")); // not a tuple
        Assert.Equal("a = (+1,)\n", Parse("a = (+1,)")); // but this is one
        Assert.Equal("a = ()\n", Parse("a = ()"));
    }

    [Fact]
    public void BytesLiteral()
    {
        Assert.Equal("x = b\"hello\"\n", Parse("x = b\"hello\""));
        Assert.Equal("x = b\"world\"\n", Parse("x = b'world'"));
        Assert.Equal("x = b\"triple\"\n", Parse("x = b\"\"\"triple\"\"\""));
        Assert.Equal("x = b\"\\\\n\"\n", Parse("x = rb\"\\n\""));
        Assert.Equal("print(b\"hi\")\n", Parse("print(b\"hi\")"));
        Assert.Equal("x = (1, b\"\")\n", Parse("x = (1, b\"\")"));
        Assert.Equal("x = (b\"\", 1)\n", Parse("x = (b\"\", 1)"));
        Assert.Equal("x = (1, b\"\", 2)\n", Parse("x = (1, b\"\", 2)"));
    }

    [Fact]
    public void Return()
    {
        Assert.Equal("def fn():\n  return 1\n", Parse("def fn(): return 1"));
        Assert.Equal("def fn():\n  return a()\n", Parse("def fn(): return a()"));
        Assert.Equal("def fn():\n  return\n", Parse("def fn(): return"));
    }

    [Fact]
    public void OptionalWhitespace()
    {
        Assert.Equal("(6 or ())\n", Parse("6 or()"));
        Assert.Equal("(6 or ())\n", Parse("6or()"));
        Assert.Equal("(0 in [1, 2, 3])\n", Parse("0in[1,2,3]"));
    }

    [Fact]
    public void BlankLine() =>
        Assert.Equal("def foo():\n  bar\n  bar\n", Parse("\ndef foo():\n  bar\n\n  bar\n"));

    [Fact]
    public void FnCallSpan()
    {
        var ast = ParseAst("def fn(a):\n  fail(a)\n\nfn(1)\n\nfail(2)\n");
        var xs = Assert.IsType<StatementsStmt>(ast.Statement);
        var lines = xs.Statements.Select(x => ast.CodeMap.ResolveSpan(x.Span).Begin.Line).ToList();
        Assert.Equal([0, 3, 5], lines);
    }

    [Fact]
    public void Comprehension()
    {
        Assert.Equal("[x for x in range(12) if ((x % 2) == 0) if ((x % 3) == 0)]\n",
            Parse("[x for x in range(12) if x % 2 == 0 if x % 3 == 0]"));
        Assert.Equal("[(x + 7) for x in range(12) for y in range(10)]\n",
            Parse("[x + 7 for x in range(12) for y in range(10)]"));
    }

    [Fact]
    public void Lambda()
    {
        Assert.Equal("x = (lambda y: (y + 1))\n", Parse("x = lambda y: y + 1"));
        ParseFailsWith("lambda", All with { EnableLambda = false }, "x = lambda y: y + 1");
        Assert.Equal("(lambda y: (x == 1))(1)\n", Parse("(lambda y: x == 1)(1)"));
        Assert.Equal("(lambda x: (x or 1))(1)\n", Parse("(lambda x: x or 1)(1)"));
        Assert.Equal("f = (lambda x, y: (x * y))\n", Parse("f = lambda x, y: x * y"));
        Assert.Equal("(lambda x: True)\n", Parse("lambda x: True"));
        Assert.Equal("(lambda : True)\n", Parse("lambda: True"));
        Assert.Equal("f((lambda x, y = 1, *args, **kwargs: ((x + y) + z)))\n",
            Parse("f(lambda x, y=1, *args, **kwargs: x + y + z)"));
        Assert.Equal("[x for x in [1, 2] if (lambda : (3 if True else 4))]\n",
            Parse("[x for x in [1, 2] if (lambda : 3 if True else 4)]"));
    }

    [Fact]
    public void Ellipsis()
    {
        ParseFailsWith("ellipsis", All with { EnableTypes = DialectTypes.Disable }, "x = ...");
        Assert.Equal("x = ...\n", Parse("x = ..."));
    }

    [Fact]
    public void NestedDef() =>
        Assert.Equal("def foo(x):\n  def bar(y):\n    return y\n  return bar(x)\n",
            Parse("def foo(x):\n  def bar(y): return y\n  return bar(x)"));

    [Fact]
    public void OpAssociativity()
    {
        Assert.Equal("(((1 or 2) or 3) or 4)\n", Parse("1 or 2 or 3 or 4"));
        Assert.Equal("(((1 and 2) and 3) and 4)\n", Parse("1 and 2 and 3 and 4"));
        Assert.Equal("((1 | 2) | 3)\n", Parse("1 | 2 | 3"));
        Assert.Equal("((1 + 2) + 3)\n", Parse("1 + 2 + 3"));
        Assert.Equal("((1 * 2) * 3)\n", Parse("1 * 2 * 3"));
        // Comparisons are not associative.
        Assert.Contains("Parse error", ParseErr("0 <= 1 < 2").Message);
    }

    [Fact]
    public void BadAssignment() => ParseFails("bad_assignment", "[x or y] = 1", "[x] += 1");

    [Fact]
    public void AssignmentTypeAnnotation()
    {
        // The same annotation syntax as Python.
        Parse("x: int = 1");
        Parse("x.y: int = 1");
        Parse("x[1]: int = 1");
        ParseFails("assignment_type_annotation", "(x, y): int = foo");
    }

    [Fact]
    public void TestListInIndexExpr()
    {
        Assert.Equal("x[1, 2]\n", Parse("x[1, 2]"));
        ParseFails("list_in_index_expr", "x[1, 2] = 3");
    }

    [Fact]
    public void ErrorUnmatchedParens() =>
        ParseFails("error_unmatched_parens", "(1 + 2", "f(x, y", "[1, 2, 3", "{1: 2", "x = (1 +\n  2 +\n  3");

    [Fact]
    public void ErrorUnexpectedToken() =>
        ParseFails("error_unexpected_token",
            "x = = 1", "_ = *x", "def foo(,):\n  pass", "f(a=1, *, b=2)", "x = [1, , 2]", "_ = a + b not c",
            "f(1+2 = 3)", "x = {,}", "print 1 2", "x[1, 2,:]", "a = max(range(10)))", "def foo(x,,y):\n  pass");

    [Fact]
    public void ErrorUnexpectedEof() =>
        ParseFails("error_unexpected_eof",
            "def f():", "if True:", "if True:\n  pass\nelse:", "if True:\n  pass\nelif x:", "for x in xs:");

    [Fact]
    public void ErrorMissingColon() =>
        ParseFails("error_missing_colon", "if True\n  pass", "def foo()\n  pass", "for x in []\n  pass");

    [Fact]
    public void ErrorMissingExpression() =>
        ParseFails("error_missing_expression", "x =", "x +=", "x = 1 +\n2", "return\n1 +");

    [Fact]
    public void AssignmentRegressionsAreRejected() =>
        AssertParseFails("x, y\n", "x,\n", "x: int y\n", "x: int\n", "x: int += 1\n");

    [Fact]
    public void ErrorBadDef() =>
        ParseFails("error_bad_def",
            "def :\n  pass", "def pass():\n  pass", "def load():\n  pass", "def f(load):\n  pass",
            "def f :\n  pass", "def foo(x y):\n  pass", "def f(a, *-b, c):\n  pass", "def foo(**x, y):\n  pass");

    [Fact]
    public void ErrorBadLoad() =>
        ParseFails("error_bad_load",
            "load()", "load(123)", "load(\"foo.bzl\")", "load(\"\", 1)", "load(1, 2)", "load(\"a\", x)", "load(\"a\", x2=x)");

    [Fact]
    public void ErrorBadFor() => ParseFails("error_bad_for", "for x + y in []:\n  pass");

    [Fact]
    public void ErrorChainedComparison() =>
        ParseFails("error_chained_comparison",
            "0 <= 1 < 2", "0 == 1 == 2", "a == b != c", "a < b > c", "a in b in c", "a in b not in c");

    [Fact]
    public void ErrorNotWithoutIn() => ParseFails("error_rd_not_without_in", "_ = a not b");

    [Fact]
    public void ErrorStringUnterminated() =>
        ParseFails("error_string_unterminated", "x = \"hello", "x = 'hello");

    [Fact]
    public void ErrorIndentation() =>
        ParseFails("error_indentation", "if True:\n    x = 1\n  y = 2", "def foo():\n  x = 1\n    y = 2");

    [Fact]
    public void ErrorReservedKeyword() =>
        ParseFails("error_reserved_keyword", "class Foo:\n  pass", "import os", "raise ValueError", "load = 1", "f(load())");

    [Fact]
    public void ErrorBadComprehension() =>
        ParseFails("error_bad_comprehension",
            "_ = {x for y in z}", "_ = [x for x in 1, 2, 3]", "_ = [a for b in c if 1, 2]",
            "_ = [a for b in lambda: c]", "_ = [x for x in a if b else c]", "[a for b in c else d]");

    [Fact]
    public void ErrorMissingElse() => ParseFails("error_missing_else", "_ = a if b");

    [Fact]
    public void ErrorStringEscape() => ParseFails("error_string_escape", "raw = r'a\nb'", "s = \"\\x-0\"");

    [Fact]
    public void ErrorCallArguments() =>
        ParseFails("error_call_arguments",
            "f(x = 1, 2)", "f(x = 1, y = 2, 3)", "f(x = 1, x = 2)", "f(x = 1, x = 2, 3)",
            "f(*args, x = 1)", "f(*args, *more_args)", "f(**kwargs, **more_kwargs)");

    [Fact]
    public void ErrorPrecedence() =>
        ParseFails("error_precedence", "f(x = 1, 2", "def f(**x, y", "return 1\n)", "def f():\n  load(\"m.bzl\", \"x\")\n)");

    [Fact]
    public void ErrorStatementPlacement() =>
        ParseFails("error_statement_placement", "return 1", "break", "continue", "def f():\n  load(\"m.bzl\", \"x\")");

    [Fact]
    public void ErrorIntegerLiteral() => ParseFails("error_integer_literal", "x = 01", "x = 0123");

    [Fact]
    public void ErrorTupleTrailingComma() =>
        ParseFails("error_tuple_trailing_comma",
            "_ = 1,", "for k, v, in dict.items():\n  pass", "_ = [(v, k) for k, v, in dict.items()]",
            "a, b, = 1, 2", "a, b = 1, 2,");

    [Fact]
    public void NonAscii() => ParseFails("error_non_ascii", "(\"☃\", 1 +, 1)", "[\"☃\"] += 1");

    [Fact]
    public void LexerErrorsPropagate()
    {
        Assert.Contains("reserved keyword", ParseErr("import os\n").Message);
        AssertErrorSpanSource("import os\n", "import");
        Assert.Contains("unfinished string literal", ParseErr("x = \"hello").Message);
        AssertErrorSpanSource("x = \"hello", "\"hello");
    }

    [Fact]
    public void ParseErrorsPointAtOffendingToken()
    {
        AssertErrorSpanSource("x = = 1\n", "=");
        AssertErrorSpanSource("def foo(,):\n  pass\n", ",");
        AssertErrorSpanSource("load(123)\n", "123");
    }

    [Fact]
    public void ChainedComparisonInCallArgumentIsRejected()
    {
        AssertErrorSpanSource("f(x < y < z)\n", "<");
        AssertErrorSpanSource("f(a in b in c)\n", "in");
    }

    [Fact]
    public void NotAndNotInPrecedence()
    {
        Assert.Equal("(x not in y)\n", Parse("x not in y"));
        Assert.Equal("(not (x in y))\n", Parse("not x in y"));
    }

    [Fact]
    public void IdentifierLedCallArgumentsContinueParsing() =>
        Assert.Equal("f(x.y, x[0], g(x), (x if y else z), (x not in y))\n",
            Parse("f(x.y, x[0], g(x), x if y else z, x not in y)"));

    [Fact]
    public void LoadAliasForms()
    {
        AssertDisplayRoundtrip("load(\"m.bzl\", alias = \"real\", \"plain\",)", "load(\"m.bzl\", alias = \"real\", \"plain\")\n");
        AssertDisplayRoundtrip("load(\"m.bzl\", \"if\")", "load(\"m.bzl\", \"if\")\n");
    }

    private static void AssertSlice(string program, string baseText, string? start, string? stop, string? step)
    {
        var assign = Assert.IsType<AssignStmt>(ParseAst(program).Statement);
        var slice = Assert.IsType<SliceExpr>(assign.Value);
        Assert.Equal(baseText, slice.Target.ToString());
        Assert.Equal(start, slice.Start?.ToString());
        Assert.Equal(stop, slice.Stop?.ToString());
        Assert.Equal(step, slice.Step?.ToString());
    }

    [Fact]
    public void SliceFormsCoverAllComponents()
    {
        AssertSlice("x = a[:2]", "a", null, "2", null);
        AssertSlice("x = a[1:]", "a", "1", null, null);
        AssertSlice("x = a[1:2:3]", "a", "1", "2", "3");
        AssertSlice("x = a[::2]", "a", null, null, "2");
        AssertDisplayRoundtrip("a[1:2]", "a[1:2]\n");
    }

    [Fact]
    public void FStringDisplayRoundtrip() => AssertDisplayRoundtrip("f\"a{x}b\"", "\"a{}b\".format(x)\n");

    [Fact]
    public void NulDisplayRoundtrip()
    {
        AssertDisplayRoundtrip("x = \"\\x001\"", "x = \"\\x001\"\n");
        AssertDisplayRoundtrip("x = f\"\\x001{y}\"", "x = \"\\x001{}\".format(y)\n");
        AssertDisplayRoundtrip("load(\"m\", symbol = \"\\x001\")", "load(\"m\", symbol = \"\\x001\")\n");
    }

    [Fact]
    public void IntegerDotDisplayRoundtrip()
    {
        AssertDisplayRoundtrip("(1).imag", "(1).imag\n");
        AssertDisplayRoundtrip("(1).field = value", "(1).field = value\n");
    }

    [Fact]
    public void UnaryReceiverDisplayRoundtrip()
    {
        AssertDisplayRoundtrip("(-x).field = value", "(-x).field = value\n");
        AssertDisplayRoundtrip("(+x)[0] = value", "(+x)[0] = value\n");
        AssertDisplayRoundtrip("(~x).field += value", "(~x).field += value\n");
        AssertDisplayRoundtrip("(-x).field", "(-x).field\n");
        AssertDisplayRoundtrip("(+f)()", "(+f)()\n");
        AssertDisplayRoundtrip("(~x)[i]", "(~x)[i]\n");
        AssertDisplayRoundtrip("(-x)[i, j]", "(-x)[i, j]\n");
        AssertDisplayRoundtrip("(-x)[a:b:c]", "(-x)[a:b:c]\n");
    }

    [Fact]
    public void FloatDisplayRoundtrip()
    {
        AssertDisplayRoundtrip("1.0", "1.0\n");
        AssertDisplayRoundtrip("1e400", "1e400\n");
        AssertDisplayRoundtrip("-1e400", "-1e400\n");
    }

    [Fact]
    public void DictComprehensionParses() =>
        Assert.Equal("x = {k: v for k in xs if pred(k)}\n", Parse("x = {k: v for k in xs if pred(k)}"));

    [Fact]
    public void IfElifSpanCoversFullChain()
    {
        var ast = ParseAst("if True:\n  a\nelif False:\n  b\nelse:\n  c\n");
        Assert.Equal("if True:\n  a\nelif False:\n  b\nelse:\n  c",
            ast.CodeMap.SourceSpan(ast.Statement.Span).TrimEnd('\n'));
    }

    // ---------------------------------------------------------------- module-level APIs

    [Fact]
    public void Locations()
    {
        string Get(string code) => string.Join(" ", ParseAst(code).StmtLocations().Select(x => x.ResolveSpan().ToString()));
        Assert.Equal("1:1-4", Get("foo"));
        Assert.Equal("1:1-4 2:1-3:8 3:4-8", Get("foo\ndef x():\n   pass"));
    }

    [Fact]
    public void Comments()
    {
        Assert.Empty(ParseAst("x = 1").Comments);

        var m = ParseAst("# a\n# b\nx = 1 # inline\n# c");
        var texts = m.Comments.Select(c => m.FileSpan(c).SourceSpan).ToList();
        Assert.Equal(["# a", "# b", "# inline", "# c"], texts);
        Assert.Equal("1:1-4", m.FileSpan(m.Comments[0]).ResolveSpan().ToString());
    }

    [Fact]
    public void Loads()
    {
        var m = ParseAst("load(\"a.bzl\", \"x\", y = \"z\")\nload(\"b.bzl\", \"w\")\nq = 1\n");
        var loads = m.Loads();
        Assert.Equal(2, loads.Count);
        Assert.Equal("a.bzl", loads[0].ModuleId);
        Assert.Equal([("x", "x"), ("y", "z")], loads[0].Symbols);
        Assert.Equal("b.bzl", loads[1].ModuleId);
    }

    [Fact]
    public void LintSuppression()
    {
        var m = ParseAst("# starlark-lint-disable foo, bar\nx = 1\ny = 2\n");
        var line2 = m.CodeMap.LineSpan(1);
        var line3 = m.CodeMap.LineSpan(3);
        Assert.True(m.IsSuppressed("foo", line2));
        Assert.True(m.IsSuppressed("bar", line2));
        Assert.False(m.IsSuppressed("foo", line3));
        Assert.False(m.IsSuppressed("baz", line2));
    }

    [Fact]
    public void ErrorSnippetRendering()
    {
        var e = ParseErr("x = = 1");
        Assert.Equal(
            "error: Parse error: unexpected symbol '=', expected expression\n" +
            " --> assert.bzl:1:5\n" +
            "  |\n" +
            "1 | x = = 1\n" +
            "  |     ^\n",
            e.Message);
    }

    // ---------------------------------------------------------------- f-strings

    [Fact]
    public void FStringExpressionsAndConversions()
    {
        Assert.Equal("\"a{}b{}\".format(x, (y + 1))\n", Parse("f\"a{x}b{y + 1}\""));
        Assert.Equal("\"{!r}\".format(x)\n", Parse("f\"{x!r}\""));
        Assert.Equal("\"{}\".format(x)\n", Parse("f\"{x!s}\""));
        Assert.Equal("\"{{literal}}\".format()\n", Parse("f\"{{literal}}\""));
        Assert.Contains("invalid f-string conversion", ParseErr("f\"{x!q}\"").Message);
    }

    [Fact]
    public void FStringsRequireDialectSupport() =>
        Assert.Contains("must enable f-strings", ParseErr("x = f\"{y}\"", Dialect.Standard).Message);
}
