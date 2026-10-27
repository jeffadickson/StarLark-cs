using Starlark.Eval;
using Starlark.Eval.TypeCheck;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>
/// What the checker knows about structs, records, enums and the json module. Before these tests the checker treated
/// all of them as <c>typing.Any</c>.
/// </summary>
public class TypeCheckerPrecisionTests
{
    private static Interpreter New() => new()
    {
        Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableTypes = DialectTypes.Enable },
    };

    private static TypeCheckResult Check(string code) => New().Typecheck("filename", code);

    private static List<string> Errors(string code) =>
        Check(code).Errors.Select(e => $"{e.Message} @ {e.CodeMap.ResolveSpan(e.Span).Begin}").ToList();

    private static string TypeOf(string code, string variable) => Check(code).Types[variable].ToString();

    // ---------------------------------------------------------------- struct

    [Fact]
    public void StructTypeListsItsFields() =>
        // golden: struct
        Assert.Equal("struct(a = int, b = str)", TypeOf("def test():\n    x = struct(a = 1, b = \"test\")\n", "x"));

    [Fact]
    public void StructFieldsHaveTheirTypes()
    {
        const string code = "def test():\n    s = struct(n = 1, name = 'x', items = [1, 2])\n    a = s.n\n    b = s.name\n    c = s.items\n";
        Assert.Equal("int", TypeOf(code, "a"));
        Assert.Equal("str", TypeOf(code, "b"));
        Assert.Equal("list[int]", TypeOf(code, "c"));
    }

    [Fact]
    public void MissingStructFieldIsReportedWhereItIsUsed()
    {
        var errors = Errors("def test():\n    s = struct(a = 1)\n    y = s.b\n");
        Assert.Equal(["The attribute `b` is not available on the type `struct(a = int)` @ 3:9"], errors);
    }

    [Fact]
    public void StructFieldTypesFlowIntoOperators()
    {
        var errors = Errors("def test():\n    s = struct(n = 1, name = 'x')\n    y = s.n + s.name\n");
        Assert.Single(errors);
        Assert.StartsWith("Binary operator `+` is not available on the types `int` and `str`", errors[0]);
    }

    [Fact]
    public void StructTypesCompareFieldByField()
    {
        Assert.Empty(Errors("def f(a, b):\n    return struct(x = 1) == struct(x = 2)\n"));
        Assert.Single(Errors("def f():\n    return struct(x = 1) == struct(x = 'no')\n"));
    }

    [Fact]
    public void StructAnnotationAcceptsAnyStructButNotOtherTypes()
    {
        // An unannotated parameter is typing.Any; only a known non-struct is rejected.
        Assert.Empty(Errors("def f(x):\n    y = x.anything\n"));
        Assert.Empty(Errors("def f(x: typing.Any):\n    y = x.anything\n"));
    }

    // ---------------------------------------------------------------- records

    private const string Rec = "R = record(host = str, port = int)\n";

    [Fact]
    public void RecordFieldsHaveTheirDeclaredTypes()
    {
        const string code = Rec + "def f(r: R):\n    h = r.host\n    p = r.port\n";
        Assert.Equal("str", TypeOf(code, "h"));
        Assert.Equal("int", TypeOf(code, "p"));
    }

    [Fact]
    public void ConstructingARecordGivesTheRecordType() =>
        Assert.Equal("R", TypeOf(Rec + "def f():\n    r = R(host = 'h', port = 1)\n", "r"));

    [Fact]
    public void RecordConstructorChecksFieldTypes()
    {
        var errors = Errors(Rec + "def f():\n    R(host = 1, port = 1)\n");
        // Line 3 is the call; the error points at the offending argument `host = 1`.
        Assert.Equal(["Expected type `str` but got `int` @ 3:7"], errors);
    }

    [Fact]
    public void RecordConstructorReportsMissingAndUnexpectedFields()
    {
        var missing = Errors(Rec + "def f():\n    R(host = 'h')\n");
        Assert.Equal(["Missing required parameter `port` @ 3:5"], missing);

        var unexpected = Errors(Rec + "def f():\n    R(host = 'h', port = 1, extra = 2)\n");
        Assert.Single(unexpected);
        Assert.StartsWith("Unexpected parameter named `extra`", unexpected[0]);

        var positional = Errors(Rec + "def f():\n    R('h', 1)\n");
        Assert.Contains("named arguments only", Assert.Single(positional));
    }

    [Fact]
    public void RecordFieldsWithDefaultsAreOptional()
    {
        const string defs = "R = record(host = str, port = field(int, 80))\n";
        Assert.Empty(Errors(defs + "def f():\n    R(host = 'h')\n"));
        Assert.Empty(Errors(defs + "def f():\n    R(host = 'h', port = 1)\n"));
        Assert.Single(Errors(defs + "def f():\n    R(port = 1)\n"));
    }

    [Fact]
    public void UnknownRecordFieldIsAnAttributeError()
    {
        var errors = Errors(Rec + "def f(r: R):\n    return r.nope\n");
        Assert.Equal(["The attribute `nope` is not available on the type `R` @ 3:12"], errors);
    }

    [Fact]
    public void RecordTypeAnnotationRejectsOtherTypes()
    {
        var errors = Errors(Rec + "def take(r: R):\n    pass\n\ndef g():\n    take(1)\n    take('x')\n");
        Assert.Equal(2, errors.Count);
        Assert.All(errors, e => Assert.StartsWith("Expected type `R` but got", e));
    }

    [Fact]
    public void TwoRecordTypesAreDistinct()
    {
        const string code = "A = record(x = int)\nB = record(x = int)\n\ndef take(a: A):\n    pass\n\ndef g():\n    take(B(x = 1))\n";
        Assert.Contains("Expected type `A` but got `B`", Assert.Single(Errors(code)));
    }

    [Fact]
    public void RecordFieldsFlowThroughTheFunctionsThatReturnThem()
    {
        const string code = Rec + "def make() -> R:\n    return R(host = 'h', port = 1)\n\ndef use():\n    p = make().port\n";
        Assert.Equal("int", TypeOf(code, "p"));
    }

    [Fact]
    public void RecordWithAFunctionTypedFieldStillChecks() =>
        Assert.Empty(Errors("R = record(cb = typing.Callable)\n\ndef f(r: R):\n    r.cb(1, 2)\n"));

    // ---------------------------------------------------------------- enums

    private const string Color = "Color = enum('RED', 'GREEN', 'BLUE')\n";

    [Fact]
    public void EnumConstructionGivesTheEnumType() =>
        Assert.Equal("Color", TypeOf(Color + "def f():\n    c = Color('RED')\n", "c"));

    [Fact]
    public void EnumValueAndIndexHaveTypes()
    {
        const string code = Color + "def f(c: Color):\n    v = c.value\n    i = c.index\n";
        Assert.Equal("str", TypeOf(code, "v"));
        Assert.Equal("int", TypeOf(code, "i"));
    }

    [Fact]
    public void EnumValuesListHasTheValueType() =>
        Assert.Equal("list[str]", TypeOf(Color + "def f():\n    vs = Color.values()\n", "vs"));

    [Fact]
    public void EnumConstructorRejectsAnImpossibleArgument()
    {
        var errors = Errors(Color + "def f():\n    Color(1)\n");
        Assert.Single(errors);
        Assert.StartsWith("Expected type `str` but got `int`", errors[0]);
        Assert.Empty(Errors(Color + "def f():\n    Color('RED')\n"));
    }

    [Fact]
    public void UnknownEnumAttribute() =>
        Assert.Contains("The attribute `colour` is not available on the type `Color`",
            Assert.Single(Errors(Color + "def f(c: Color):\n    return c.colour\n")));

    [Fact]
    public void EnumAnnotationRejectsRawStrings()
    {
        var errors = Errors(Color + "def take(c: Color):\n    pass\n\ndef g():\n    take('RED')\n");
        Assert.Contains("Expected type `Color` but got `str`", Assert.Single(errors));
    }

    [Fact]
    public void EnumsOfNumbersHaveNumericValues() =>
        Assert.Equal("int", TypeOf("N = enum(10, 20, 30)\n\ndef f(n: N):\n    v = n.value\n", "v"));

    // ---------------------------------------------------------------- json

    [Fact]
    public void JsonEncodeGivesAString() =>
        Assert.Equal("str", TypeOf("def f():\n    s = json.encode([1, 2])\n", "s"));

    [Fact]
    public void JsonDecodeGivesAnythingButNeedsAString()
    {
        Assert.Equal("typing.Any", TypeOf("def f():\n    v = json.decode('[1]')\n", "v"));
        var errors = Errors("def f():\n    json.decode(1)\n");
        Assert.Contains("Expected type `str` but got `int`", Assert.Single(errors));
    }

    [Fact]
    public void JsonRejectsMissingAndUnknownMembers()
    {
        Assert.Contains("Missing required parameter `x`", Assert.Single(Errors("def f():\n    json.encode()\n")));
        Assert.Contains("is not available on the type", Assert.Single(Errors("def f():\n    json.nope(1)\n")));
    }

    [Fact]
    public void ALocalNamedJsonShadowsTheModule() =>
        Assert.Empty(Errors("def f(json):\n    json.encode(1, 2, 3)\n"));

    // ---------------------------------------------------------------- shadowing and scope

    [Fact]
    public void ALocalNamedLikeARecordIsNotTreatedAsOne()
    {
        const string code = Rec + "def f(R):\n    return R(1, 2, 3)\n";
        Assert.Empty(Errors(code));
    }

    [Fact]
    public void RecordsDefinedAfterTheirUseAreStillKnown() =>
        // Definitions are collected in a first pass, so function bodies can refer to later globals.
        Assert.Equal("int", TypeOf("def f(r: R):\n    p = r.port\n\nR = record(port = int)\n", "p"));

    // ---------------------------------------------------------------- the checker agrees with the runtime

    [Theory]
    [InlineData("s = struct(a = 1)\nresult = s.a")]
    [InlineData("R = record(x = int)\nresult = R(x = 1).x")]
    [InlineData("E = enum('A', 'B')\nresult = E('B').index")]
    [InlineData("result = json.decode(json.encode([1, 'a']))")]
    public void ProgramsTheCheckerAcceptsAlsoRun(string code)
    {
        var it = New();
        Assert.Empty(it.Typecheck("t", code).Errors);
        it.Execute("t", code);
    }

    [Theory]
    // Type definitions live at module level (as in real programs); the mistake is inside a function.
    [InlineData("", "    s = struct(a = 1)\n    return s.b\n", "no attribute")]
    [InlineData("R = record(x = int)\n", "    return R(x = 'no')\n", "does not match")]
    // A wrong *type* is caught statically. A right type with a wrong *value* (`E('Z')`) is not: types cannot see that.
    [InlineData("E = enum('A')\n", "    return E(5)\n", "not a valid element")]
    [InlineData("R = record(x = int)\n", "    return R()\n", "Missing parameter")]
    [InlineData("R = record(x = int)\n", "    return R(x = 1).y\n", "no attribute")]
    public void ProgramsTheCheckerRejectsAlsoFailAtRunTime(string module, string body, string runtimeMessage)
    {
        var it = New();
        var program = module + "def main():\n" + body + "\nmain()\n";
        Assert.NotEmpty(it.Typecheck("t", program).Errors);
        var ex = Assert.Throws<StarlarkException>(() => it.Execute("t", program));
        Assert.Contains(runtimeMessage, ex.RawMessage);
    }
}
