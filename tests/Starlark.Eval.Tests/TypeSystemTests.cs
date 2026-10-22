using System.Numerics;
using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>
/// Types, annotations, <c>isinstance</c>, records, enums and structs. Cases follow the expectations in the
/// starlark-rust crate (values/typing/type_compiled/tests.rs, record/globals.rs, enumeration/enum_type.rs).
/// </summary>
public class TypeSystemTests
{
    private static Interpreter New() => new()
    {
        Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableTypes = DialectTypes.Enable },
        Print = _ => { },
        AllowGlobalReassign = true,
    };

    private static object? Eval(string expr) => New().Eval(expr);
    private static string R(string expr) => Repr.Of(Eval(expr));

    private static object? Run(string src, string name = "result") => New().Execute("t", src).Globals[name];

    private static StarlarkException Fails(string src) =>
        Assert.Throws<StarlarkException>(() => New().Execute("t", src));

    // ---------------------------------------------------------------- isinstance and type expressions

    [Theory]
    [InlineData("isinstance(1, int)")]
    [InlineData("isinstance(True, bool)")]
    [InlineData("isinstance(True, typing.Any)")]
    [InlineData("isinstance(None, None)")]
    [InlineData("isinstance(len, typing.Callable)")]
    [InlineData("isinstance([], list[int])")]
    [InlineData("isinstance([], list[typing.Any])")]
    [InlineData("isinstance([1, 2, 3], list[int])")]
    [InlineData("isinstance('test', int | str)")]
    [InlineData("isinstance(('test', None), (str, None))")]
    [InlineData("isinstance({'test': 1, 'more': 2}, dict[str, int])")]
    [InlineData("isinstance({1: 1, 2: 2}, dict[int, int])")]
    [InlineData("isinstance([1, 2, 'test'], list)")]
    [InlineData("isinstance((1, 2, 3), tuple[int, ...])")]
    [InlineData("isinstance((1, 'a'), tuple[int, str])")]
    [InlineData("isinstance(1.5, float)")]
    [InlineData("isinstance(set([1]), set[int])")]
    [InlineData("isinstance([1], typing.Iterable)")]
    [InlineData("isinstance(range(3), typing.Iterable[int])")]
    [InlineData("isinstance(None, list[int] | None)")]
    public void IsInstanceTrue(string expr) => Assert.True((bool)Eval(expr)!, expr);

    [Theory]
    [InlineData("isinstance(1, None)")]
    [InlineData("isinstance((1, 1), str)")]
    [InlineData("isinstance('test', int | bool)")]
    [InlineData("isinstance([1, 2, None], list[int])")]
    [InlineData("isinstance({'test': 1, 8: 2}, dict[str, int])")]
    [InlineData("isinstance({'test': 1, 'more': None}, dict[str, int])")]
    [InlineData("isinstance(1, typing.Never)")]
    [InlineData("isinstance((1, 2), tuple[int, str])")]
    [InlineData("isinstance((1, 'a', 3), tuple[int, str])")]
    [InlineData("isinstance(1, str)")]
    [InlineData("isinstance(True, int)")]
    [InlineData("isinstance('x', typing.Iterable)")]
    [InlineData("isinstance(5, typing.Callable)")]
    public void IsInstanceFalse(string expr) => Assert.False((bool)Eval(expr)!, expr);

    [Theory]
    [InlineData("isinstance(None, isinstance)", "not a valid type")]
    [InlineData("isinstance(None, [])", "cannot be used as type")]
    [InlineData("isinstance(None, {'1': '', '2': ''})", "cannot be used as type")]
    [InlineData("isinstance(1, 'int')", "String literals are not allowed")]
    public void InvalidTypesAreRejected(string expr, string message) =>
        Assert.Contains(message, Assert.Throws<StarlarkException>(() => Eval(expr)).RawMessage);

    [Theory]
    [InlineData("list[int]", "list[int]")]
    [InlineData("list[typing.Any]", "list")]
    [InlineData("dict[str, int]", "dict[str, int]")]
    [InlineData("int | str", "int | str")]
    [InlineData("int | None", "int | NoneType")]
    [InlineData("tuple[int, ...]", "tuple[int, ...]")]
    [InlineData("tuple[int, str]", "(int, str)")]
    [InlineData("typing.Callable", "typing.Callable")]
    [InlineData("typing.Callable[[int], str]", "typing.Callable[[int], str]")]
    [InlineData("typing.Iterable[int]", "typing.Iterable[int]")]
    [InlineData("typing.Any", "typing.Any")]
    [InlineData("typing.Never", "typing.Never")]
    [InlineData("set[int]", "set[int]")]
    public void TypesPrintAsTheyAreWritten(string expr, string printed) =>
        Assert.Equal(printed, ((string)Eval($"repr(eval_type({expr}))")!).Trim('"').Replace("\\\"", "\""));

    [Fact]
    public void EvalTypeGivesFirstClassTypes()
    {
        Assert.True((bool)Eval("isinstance(1, eval_type(eval_type(int)))")!);
        Assert.True((bool)Eval("(lambda t: isinstance(1, t))(eval_type(int))")!);
        Assert.Equal("\"int | str\"", R("repr(eval_type(int | str))"));
    }

    [Fact]
    public void UnionIsFlattenedAndDeduplicated() =>
        Assert.Equal("\"int | str\"", R("repr(eval_type(int | str | int | (str | int)))"));

    [Fact]
    public void BadTypeParametersAreErrors()
    {
        Assert.Contains("[,] can only be applied to dict or tuple", Fails("x = list[str, int]\n").RawMessage);
        Assert.Contains("does not support type parameters", Fails("x = str[int]\n").RawMessage);
    }

    // ---------------------------------------------------------------- annotations on functions

    [Fact]
    public void ArgumentsAreCheckedAgainstAnnotations()
    {
        Assert.Equal("False", Repr.Of(Run("def f(i: int) -> bool:\n  return i == 3\nresult = f(8)\n")));
        var ex = Fails("def f(i: bool):\n  pass\nf(1)\n");
        Assert.Contains("Value `1` of type `int` does not match the type annotation `bool` for argument `i`", ex.RawMessage);
    }

    [Fact]
    public void ReturnValuesAreCheckedAgainstAnnotations()
    {
        var ex = Fails("def f() -> bool:\n  return 1\nf()\n");
        Assert.Contains("Value `1` of type `int` does not match the type annotation `bool` for return type", ex.RawMessage);
    }

    [Fact]
    public void FallingOffTheEndReturnsNoneWhichMustMatch()
    {
        var ex = Fails("def f() -> bool:\n  pass\nf()\n");
        Assert.Contains("Value `None` of type `NoneType` does not match the type annotation `bool` for return type", ex.RawMessage);
        New().Execute("t", "def g() -> None:\n  pass\ng()\n");
    }

    [Fact]
    public void NoneAsReturnAnnotationMeansNoneType()
    {
        New().Execute("t", "def f() -> None:\n  return None\nf()\n");
        Assert.Contains("does not match", Fails("def f() -> None:\n  return True\nf()\n").RawMessage);
    }

    [Fact]
    public void DefaultsAreCheckedEvenWhenNeverUsed()
    {
        var ex = Fails("def foo(f: int = None):\n  pass\n");
        Assert.Contains("`None` of type `NoneType` does not match the type annotation `int`", ex.RawMessage);
    }

    [Fact]
    public void UnannotatedFunctionsAreUnchecked() =>
        Assert.Equal("\"x\"", Repr.Of(Run("def f(a, b=1):\n  return a\nresult = f('x')\n")));

    [Fact]
    public void ContainerAnnotationsCheckElements()
    {
        New().Execute("t", "def f(x: list[int]):\n  pass\nf([1, 2, 3])\n");
        var ex = Fails("def f(x: list[int]):\n  pass\nf(['mm'])\n");
        Assert.Contains("Value `[\"mm\"]` of type `list` does not match", ex.RawMessage);
    }

    [Fact]
    public void VarargsAndKwargsAnnotationsApplyToEachElement()
    {
        const string def = "def foo(*args: str, **kwargs: int):\n  pass\n";
        New().Execute("t", def + "foo('a')\nfoo(b=1)\n");
        Assert.Contains("does not match", Fails(def + "foo(1)\n").RawMessage);
        Assert.Contains("does not match", Fails(def + "foo(c='x')\n").RawMessage);
    }

    [Fact]
    public void UnionAnnotationsAcceptEitherSide()
    {
        const string def = "def f(x: int | str) -> str | int:\n  return x\n";
        Assert.Equal("1", Repr.Of(Run(def + "result = f(1)\n")));
        Assert.Equal("\"a\"", Repr.Of(Run(def + "result = f('a')\n")));
        Assert.Contains("does not match", Fails(def + "f([])\n").RawMessage);
    }

    [Fact]
    public void AnnotationsMayUseAliasesAndCompiledTypes()
    {
        Assert.Equal("1", Repr.Of(Run("MyList = list[int]\ndef f(x: MyList):\n  return len(x)\nresult = f([5])\n")));
        New().Execute("t", "ty = eval_type(int)\ndef f(x: ty):\n  pass\nf(1)\n");
        Assert.Contains("does not match the type annotation `int` for argument `x`",
            Fails("ty = eval_type(int)\ndef f(x: ty):\n  pass\nf('x')\n").RawMessage);
    }

    [Fact]
    public void AnnotationsReferringToUnknownNamesAreErrors()
    {
        Assert.Contains("made_up", Fails("def f(i: made_up):\n  pass\n").RawMessage);

        // A call is rejected by the parser itself, before anything runs.
        var syntax = Assert.Throws<StarlarkSyntaxException>(() => New().Execute("t", "def f(i: fail('bad')):\n  pass\n"));
        Assert.Contains("call expression is not allowed in type expression", syntax.RawMessage);
    }

    [Fact]
    public void AnnotatedVariableAssignmentsAreChecked()
    {
        New().Execute("t", "x: int = 1\ny: list[str] = ['a']\n");
        Assert.Contains("does not match the type annotation `str`", Fails("a: str = 1\n").RawMessage);
    }

    [Fact]
    public void TypesDisabledByDialectRejectAnnotations()
    {
        var it = new Interpreter { Dialect = Dialect.Standard };
        Assert.Throws<StarlarkSyntaxException>(() => it.Execute("t", "def f(x: int):\n  pass\n"));
    }

    // ---------------------------------------------------------------- records

    private const string Rec = "R = record(host=str, port=int)\n";

    [Fact]
    public void RecordsHoldTypedFields()
    {
        const string src = Rec + "a = R(host='test', port=80)\nb = R(host='test', port=90)\n" +
                           "result = [a.host, a.port, a == a, a == b, dir(a)]\n";
        Assert.Equal("[\"test\", 80, True, False, [\"host\", \"port\"]]", Repr.Of(Run(src)));
    }

    [Fact]
    public void RecordFieldTypesAreEnforced()
    {
        var ex = Fails(Rec + "R(host=1, port=80)\n");
        Assert.Contains("Value `1` of type `int` does not match the type annotation `str` for argument `host`", ex.RawMessage);
    }

    [Fact]
    public void RecordConstructionErrors()
    {
        Assert.Contains("Missing parameter `port`", Fails(Rec + "R(host='x')\n").RawMessage);
        Assert.Contains("Unexpected parameter named `extra`", Fails(Rec + "R(host='x', port=1, extra=2)\n").RawMessage);
        Assert.Contains("named arguments only", Fails(Rec + "R('x', 1)\n").RawMessage);
    }

    [Fact]
    public void RecordFieldDefaults()
    {
        const string src = "R = record(host=str, port=field(int, 80))\nresult = R(host='h').port\n";
        Assert.Equal("80", Repr.Of(Run(src)));
        Assert.Equal("[]", Repr.Of(Run("R = record(xs = field(list[int], []))\nresult = R().xs\n")));
        Assert.Contains("does not match", Fails("R = record(xs = field(int, 'no'))\n").RawMessage);
    }

    [Fact]
    public void RecordTypeIsUsableAsAnAnnotation()
    {
        const string src = Rec + "def f(v: R) -> R:\n  return v\nresult = f(R(host='h', port=1)).host\n";
        Assert.Equal("\"h\"", Repr.Of(Run(src)));
        Assert.Contains("does not match the type annotation `record`", Fails(Rec + "def f(v: R):\n  pass\nf(1)\n").RawMessage);
    }

    [Fact]
    public void RecordsOfDifferentTypesAreDistinct()
    {
        const string src = "A = record(x=int)\nB = record(x=int)\nresult = [isinstance(A(x=1), A), isinstance(A(x=1), B)]\n";
        Assert.Equal("[True, False]", Repr.Of(Run(src)));
    }

    [Fact]
    public void RecordsAreFrozenWithTheirModule()
    {
        var m = New().Execute("t", Rec + "v = R(host='h', port=1)\n");
        Assert.NotNull(m.Globals["v"]);
    }

    // ---------------------------------------------------------------- enums

    private const string Color = "Color = enum('RED', 'GREEN', 'BLUE')\n";

    [Fact]
    public void EnumValuesHaveValueAndIndex()
    {
        const string src = Color + "g = Color('GREEN')\nresult = [g.value, g.index, Color.values()]\n";
        Assert.Equal("[\"GREEN\", 1, [\"RED\", \"GREEN\", \"BLUE\"]]", Repr.Of(Run(src)));
    }

    [Fact]
    public void EnumTypesBehaveLikeSequences()
    {
        Assert.Equal("3", Repr.Of(Run(Color + "result = len(Color)\n")));
        Assert.Equal("\"BLUE\"", Repr.Of(Run(Color + "result = Color[2].value\n")));
        Assert.Equal("\"RED\"", Repr.Of(Run(Color + "result = Color[-3].value\n")));
        Assert.Equal("[\"RED\", \"GREEN\", \"BLUE\"]", Repr.Of(Run(Color + "result = [x.value for x in Color]\n")));
    }

    [Fact]
    public void EnumValuesCompareByIdentityOfTheirOption()
    {
        const string src = Color + "result = [Color('RED') == Color('RED'), Color('RED') == Color('BLUE'), Color('RED') == Color[0]]\n";
        Assert.Equal("[True, False, True]", Repr.Of(Run(src)));
    }

    [Fact]
    public void EnumConstructionRejectsUnknownValues() =>
        Assert.Contains("not a valid element", Fails(Color + "Color('PURPLE')\n").RawMessage);

    [Fact]
    public void EnumRejectsDuplicates() =>
        Assert.Contains("Duplicate enum value", Fails("E = enum('A', 'A')\n").RawMessage);

    [Fact]
    public void EnumTypeIsUsableAsAnAnnotation()
    {
        const string def = Color + "def f(x: Color) -> Color:\n  return x\n";
        New().Execute("t", def + "f(Color('RED'))\n");
        Assert.Contains("does not match the type annotation `enum`", Fails(def + "f('RED')\n").RawMessage);
    }

    [Fact]
    public void EnumsMayHoldAnyHashableValue()
    {
        Assert.Equal("1", Repr.Of(Run("N = enum(10, 20, 30)\nresult = N(20).index\n")));
    }

    // ---------------------------------------------------------------- struct

    [Fact]
    public void StructsHaveNamedFields()
    {
        Assert.Equal("[1, \"x\"]", Repr.Of(Run("s = struct(a=1, b='x')\nresult = [s.a, s.b]\n")));
        Assert.Equal("[\"a\", \"b\"]", Repr.Of(Run("result = dir(struct(a=1, b=2))\n")));
        Assert.Equal("\"struct(a = 1, b = [])\"", Repr.Of(Run("result = repr(struct(a=1, b=[]))\n")));
    }

    [Fact]
    public void StructsCompareByFields()
    {
        Assert.Equal("[True, False]", Repr.Of(Run(
            "result = [struct(a=1, b=2) == struct(a=1, b=2), struct(a=1) == struct(a=2)]\n")));
    }

    [Fact]
    public void MissingStructFieldIsAnError() =>
        Assert.Contains("no attribute `c`", Fails("struct(a=1).c\n").RawMessage);

    [Fact]
    public void StructHasAttrAndGetAttr() =>
        Assert.Equal("[True, False, 7]", Repr.Of(Run(
            "s = struct(a=7)\nresult = [hasattr(s, 'a'), hasattr(s, 'z'), getattr(s, 'a')]\n")));

    // ---------------------------------------------------------------- json and typing modules

    [Fact]
    public void JsonEncodesStructsAndRecords()
    {
        Assert.Equal("\"{\\\"foo\\\":42,\\\"bar\\\":\\\"some\\\"}\"", R("json.encode(struct(foo=42, bar='some'))"));
        Assert.Equal("\"{\\\"host\\\":\\\"h\\\",\\\"port\\\":1}\"",
            Repr.Of(Run(Rec + "result = json.encode(R(host='h', port=1))\n")));
    }

    [Fact]
    public void TypingModuleExposesItsNames()
    {
        Assert.Equal("[\"Any\", \"Callable\", \"Iterable\", \"Never\"]", R("dir(typing)"));
    }
}
