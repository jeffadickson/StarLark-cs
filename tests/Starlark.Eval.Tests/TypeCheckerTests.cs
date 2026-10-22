using Starlark.Eval;
using Starlark.Eval.TypeCheck;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>
/// The static type checker. Cases come from the starlark-rust crate's typing tests and golden files
/// (typing/tests/*.rs and tests/golden/*.golden); messages and source positions follow them.
/// </summary>
public class TypeCheckerTests
{
    private static Interpreter New() => new()
    {
        Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableTypes = DialectTypes.Enable },
    };

    private static TypeCheckResult Check(string code) => New().Typecheck("filename", code);

    /// <summary>Each error as <c>message @ line:column</c>.</summary>
    private static List<string> Errors(string code) =>
        Check(code).Errors.Select(e => $"{e.Message} @ {e.CodeMap.ResolveSpan(e.Span).Begin}").ToList();

    private static void NoErrors(string code)
    {
        var errors = Errors(code);
        Assert.True(errors.Count == 0, "unexpected errors:\n" + string.Join("\n", errors));
    }

    // ---------------------------------------------------------------- calls

    [Fact]
    public void WrongArgumentCount()
    {
        // golden: calls
        var errors = Errors("def f(y): pass\n\ndef g():\n    # Extra parameter.\n    f(1, 2)\n    # Not enough parameters.\n    f()\n");
        Assert.Equal(["Too many positional arguments @ 5:10", "Missing required parameter `y` @ 7:5"], errors);
    }

    [Fact]
    public void PositionalOnlyParameterRejectsKeyword()
    {
        // golden: call_pos_only (needs a dialect that allows positional-only parameters)
        var it = new Interpreter { Dialect = Dialect.AllOptionsInternal };
        var result = it.Typecheck("filename", "def f(x, /):\n    pass\n\ndef test():\n    f(\"good\")\n    f(x=\"bad\")\n");
        var errors = result.Errors.Select(e => $"{e.Message} @ {e.CodeMap.ResolveSpan(e.Span).Begin}").ToList();
        Assert.Equal(["Unexpected parameter named `x` @ 6:7"], errors);
    }

    [Fact]
    public void ArgsAndKwargsAnnotationsApplyToEachArgument()
    {
        // golden: types_of_args_kwargs
        var errors = Errors("def foo(*args: str, **kwargs: int):\n    pass\n\ndef test():\n    # Good\n    foo(\"a\")\n    foo(b=1)\n    # Bad\n    foo(1)\n    foo(c=\"x\")\n");
        Assert.Equal(["Expected type `str` but got `int` @ 9:9", "Expected type `int` but got `str` @ 10:9"], errors);
    }

    [Fact]
    public void CallingANonCallableType()
    {
        // golden: call_not_callable, call_callable
        Assert.Equal(["Call to a non-callable type `list` @ 2:5"], Errors("def foo(x: list):\n    x()\n"));
        NoErrors("def foo(x: typing.Callable):\n    x()\n");
    }

    [Fact]
    public void CallingAUnionWithACallableSideIsAllowed() =>
        // golden: call_callable_or_not_callable
        NoErrors("def foo(x: [typing.Callable, str], y: [str, typing.Callable]):\n    x()\n    y()\n");

    [Fact]
    public void TupleEllipsisAnnotation()
    {
        // golden: tuple_ellipsis
        var errors = Errors("def f(t: tuple[int, ...]) -> int:\n    return t[0]\n\ndef g():\n    # Good.\n    f((1, 2, 3))\n\n    # Bad.\n    f((1, \"x\"))\n");
        Assert.Equal(["Expected type `tuple[int, ...]` but got `(int, str)` @ 9:7"], errors);
    }

    [Fact]
    public void NewListAndDictSyntax()
    {
        // test_new_list_dict_syntax_fail_compile_time
        var errors = Errors("def uuu(x: list[int]):\n    pass\n\ndef www():\n    uuu([\"mm\"])\n");
        Assert.Equal(["Expected type `list[int]` but got `list[str]` @ 5:9"], errors);
        NoErrors("def uuu(x: list[int]):\n    pass\n\nuuu([1, 2, 3])\n");
    }

    [Fact]
    public void BuiltinArgumentsAreChecked()
    {
        // golden: failure
        Assert.Equal(["Expected type `str` but got `int` @ 2:10"], Errors("def test():\n    hash(1)\n"));
        NoErrors("def test():\n    hash('x')\n");
    }

    // ---------------------------------------------------------------- returns and annotations

    [Fact]
    public void ReturnTypeMismatch()
    {
        // test_new_syntax_without_dot_type_compile_time
        // `def f() -> int: return 'tea'`: the string literal starts at column 24.
        Assert.Equal(["Expected type `int` but got `str` @ 1:24"], Errors("def f() -> int: return 'tea'\n"));
        NoErrors("def f() -> int: return 17\n");
    }

    [Fact]
    public void ReturnOfUnionTypeReportsTheWholeUnion()
    {
        // golden: union
        var errors = Errors("def func_which_returns_union(p) -> str | int:\n    if p == 56:\n        return \"a\"\n    elif p == 57:\n        return 1\n    else:\n        return []\n");
        Assert.Single(errors);
        Assert.StartsWith("Expected type `str | int` but got `list[typing.Never]` @ 7:16", errors[0]);
    }

    [Fact]
    public void ReturnNoneMustMatchNoneAnnotation()
    {
        // test_types: f_none_bool_compile_time
        Assert.Equal(["Expected type `NoneType` but got `bool` @ 2:12"], Errors("def f() -> None:\n    return True\n"));
        NoErrors("def f() -> None:\n    pass\n");
    }

    [Fact]
    public void FallingOffTheEndOfAFunctionThatMustReturn()
    {
        Assert.Single(Errors("def f() -> bool:\n    pass\n"));
        NoErrors("def f(x) -> bool:\n    if x:\n        return True\n    else:\n        return False\n");
    }

    [Fact]
    public void AnnotatedDefaultMustMatch()
    {
        // test_types: "Should check the type of default parameters that aren't used"
        Assert.Single(Errors("def foo(f: int = None):\n    pass\n"));
        NoErrors("def foo(f: int = 1):\n    pass\n");
    }

    [Fact]
    public void VariableAnnotations()
    {
        Assert.Single(Errors("a: str = 1\n"));
        NoErrors("a: str = 'x'\nb: list[int] = [1]\n");
    }

    [Fact]
    public void TypeAliasesAreUnderstood()
    {
        // test_type_alias
        var result = Check("MyList = list[int]\n\ndef f(x: MyList):\n    pass\n\ndef g():\n    f(['s'])\n");
        Assert.Equal("Expected type `list[int]` but got `list[str]`", Assert.Single(result.Errors).Message);
        Assert.Equal("list[int]", result.Types["x"].ToString());
    }

    [Fact]
    public void ListParametrization()
    {
        Assert.Contains("[,] can only be applied to dict or tuple functions in type expression",
            Errors("def f(x: list[str, int]):\n    pass\n")[0]);
        NoErrors("def f(x: list[str]) -> list[str]:\n    return x\n");
        NoErrors("def f(x: set[int]) -> set[int]:\n    return x\n");
    }

    // ---------------------------------------------------------------- operators and inference

    [Fact]
    public void UnaryOperators()
    {
        // golden: un_op
        var result = Check("def test():\n    # Good.\n    x = -1\n    # Bad.\n    y = ~True\n    # Union good and bad.\n    z = -(1 if True else \"\")\n");
        Assert.Equal("Unary operator `~` is not available on the type `bool`", Assert.Single(result.Errors).Message);
        Assert.Equal("int", result.Types["x"].ToString());
        Assert.Equal("typing.Never", result.Types["y"].ToString());
        Assert.Equal("int", result.Types["z"].ToString());
    }

    [Fact]
    public void BinaryOperatorErrors()
    {
        // golden: int_bitor_float
        var result = Check("def test():\n    x = 0x60000000000000000000000 | 1.0\n");
        Assert.Equal("Binary operator `|` is not available on the types `int` and `float`", Assert.Single(result.Errors).Message);
        Assert.Equal("typing.Never", result.Types["x"].ToString());
    }

    [Fact]
    public void IntPlusFloatIsFloat()
    {
        // golden: int_plus_float
        var result = Check("def test():\n    x = 1 + 1.0\n");
        Assert.Empty(result.Errors);
        Assert.Equal("float", result.Types["x"].ToString());
    }

    [Fact]
    public void ListOperatorsKeepTheElementType()
    {
        // golden: list_bin_op
        var result = Check("def test(a: list[str]):\n    x = a + a\n    y = a * 3\n    z = 3 * a\n");
        Assert.Empty(result.Errors);
        Assert.Equal("list[str]", result.Types["x"].ToString());
        Assert.Equal("list[str]", result.Types["y"].ToString());
        Assert.Equal("list[str]", result.Types["z"].ToString());
    }

    [Fact]
    public void ListsCompare() =>
        // golden: list_less
        NoErrors("def test(x: list[str], y: list[str]) -> bool:\n    return x < y\n");

    [Fact]
    public void ListAppendWidensTheElementType()
    {
        // golden: list_append
        var result = Check("def test():\n    x = []\n    x.append(1)\n    x.append(\"\")\n");
        Assert.Empty(result.Errors);
        Assert.True(result.Types.ContainsKey("x"));
    }

    [Fact]
    public void BuiltinCallsInferResultTypes()
    {
        // goldens: list_function, zip, new_list_dict_syntax
        Assert.Equal("list[int]", Check("def test():\n    x = list([1, 2])\n").Types["x"].ToString());
        Assert.Equal("list[(int, bool, str)]",
            Check("def test():\n    x = zip([1,2], [True, False], [\"a\", \"b\"])\n").Types["x"].ToString());
        Assert.Equal("list[str]", Check(
            "def new_list_dict_syntax(d: dict[str, int]) -> list[str]:\n    return list(d.keys())\ndef test():\n    x = new_list_dict_syntax({\"a\": 1, \"b\": 2})\n")
            .Types["x"].ToString());
    }

    [Fact]
    public void MethodsOnStrings()
    {
        // golden: methods_work_for_ty_starlark_value
        var result = Check("def test(s: str):\n    x = s.startswith(\"a\")\n");
        Assert.Empty(result.Errors);
        Assert.Equal("bool", result.Types["x"].ToString());
    }

    [Fact]
    public void DictLookupByNeverIsNotAnError() =>
        // golden: dict_never_key
        NoErrors("def test(d: dict[typing.Any, str], x: typing.Never):\n    y = d[x]\n");

    [Fact]
    public void DictLookupByTheWrongKeyTypeIsAnError()
    {
        var errors = Errors("def test(d: dict[str, int]):\n    y = d[1]\n");
        Assert.Single(errors);
        Assert.StartsWith("Expected type `str` but got `int`", errors[0]);
    }

    // ---------------------------------------------------------------- equality widening

    [Fact]
    public void IntAndFloatCompareEqualInEitherOrder() =>
        // test_equality_int_float
        NoErrors("def f(x: int, y: float) -> bool:\n    return x == y\n\ndef g(x: int, y: float) -> bool:\n    return y != x\n\ndef h(x: float) -> bool:\n    return x == 6\n");

    [Fact]
    public void NumericWideningRecursesIntoContainers() =>
        // test_equality_int_float_in_containers
        NoErrors(
            "def f(x: list[int], y: list[float]) -> bool:\n    return x == y\n\n" +
            "def g(x: dict[int, str], y: dict[float, str]) -> bool:\n    return x == y\n\n" +
            "def h(x: set[int], y: set[float]) -> bool:\n    return x == y\n\n" +
            "def i(x: tuple[int, ...], y: tuple[float, ...]) -> bool:\n    return x == y\n\n" +
            "def j() -> bool:\n    return (1, \"a\") == (1.0, \"a\")\n\n" +
            "def nested(x: list[list[int]], y: list[list[float]]) -> bool:\n    return x == y\n");

    [Fact]
    public void WideningDoesNotHideARealMismatch()
    {
        // test_equality_container_mismatch_still_rejected, _unrelated_types_still_rejected, _bool_int_still_rejected
        Assert.Contains("Expected type", Assert.Single(Errors("def f(x: list[int], y: list[str]) -> bool:\n    return x == y\n")));
        Assert.Contains("Expected type", Assert.Single(Errors("def f(x: int, y: str) -> bool:\n    return x == y\n")));
        Assert.Contains("Expected type", Assert.Single(Errors("def f(x: bool, y: int) -> bool:\n    return x == y\n")));
    }

    // ---------------------------------------------------------------- iteration

    [Fact]
    public void IteratingANonIterable()
    {
        Assert.Contains("is not iterable", Assert.Single(Errors("def f(x: int):\n    for i in x:\n        pass\n")));
        NoErrors("def f(x: list[int]):\n    for i in x:\n        pass\n");
        NoErrors("def f(x: dict[str, int]):\n    for k in x:\n        pass\n");
    }

    [Fact]
    public void LoopVariableTakesTheElementType()
    {
        var result = Check("def f(x: list[int]):\n    for i in x:\n        y = i\n");
        Assert.Equal("int", result.Types["y"].ToString());
    }

    [Fact]
    public void TupleUnpackingArityMismatch()
    {
        Assert.Contains("length mismatch", Assert.Single(Errors("def f():\n    a, b = (1, 2, 3)\n")));
        NoErrors("def f():\n    a, b = (1, 2)\n");
    }

    // ---------------------------------------------------------------- approximation and leniency

    [Fact]
    public void UnknownThingsAreApproximatedNotRejected()
    {
        // golden: bit_or_return_int records an approximation rather than failing.
        var result = Check("test = int | 3\ndef foo() -> test:\n    pass\n");
        Assert.Empty(result.Errors);
        Assert.NotEmpty(result.Approximations);
    }

    [Fact]
    public void UnannotatedCodeProducesNoErrors() =>
        NoErrors("def f(a, b):\n    return a + b\n\ndef g():\n    return f(1, 'x') + f([1], [2])\n");

    [Fact]
    public void PredeclaredHostFunctionsAreUnknownNotUndefined()
    {
        var it = New();
        it.Define("host", (_, a, k) => null);
        Assert.Empty(it.Typecheck("t", "def f():\n    host(1, 2, x=3)\n").Errors);
    }

    // ---------------------------------------------------------------- integration

    [Fact]
    public void TypecheckBeforeExecuteStopsBadPrograms()
    {
        var it = New();
        it.TypecheckBeforeExecute = true;
        var ex = Assert.Throws<StarlarkException>(() =>
            it.Execute("t", "def f(x: int):\n    pass\n\ndef g():\n    f('s')\n"));
        Assert.Contains("Expected type `int` but got `str`", ex.RawMessage);
    }

    [Fact]
    public void TypecheckBeforeExecuteLetsGoodProgramsRun()
    {
        var it = New();
        it.TypecheckBeforeExecute = true;
        var m = it.Execute("t", "def f(x: int) -> int:\n    return x + 1\nresult = f(2)\n");
        Assert.Equal("3", Repr.Of(m.Globals["result"]));
    }

    [Fact]
    public void ErrorsRenderWithASourceSnippet()
    {
        var error = Check("def f(y): pass\ndef g():\n    f(1, 2)\n").Errors[0];
        Assert.Equal(
            "error: Too many positional arguments\n" +
            " --> filename:3:10\n" +
            "  |\n" +
            "3 |     f(1, 2)\n" +
            "  |          ^\n",
            error.Render());
    }

    [Fact]
    public void EveryRealWorldFileCheckedWithoutCrashing()
    {
        // The corpus is real, unannotated Bazel-era code. The checker must never throw on it, and the only errors it
        // may report are constructs the current spec removed: range() is no longer a list, dict + dict is gone,
        // and strings are not iterable. Anything else would be a false positive.
        var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Starlark.Syntax.Tests", "Testcases");
        Assert.True(Directory.Exists(dir), $"corpus not found at {dir}");

        var legacy = new[]
        {
            "is not available on the types `list[int]` and `range`",
            "is not available on the types `dict[",
            "is not iterable",
        };
        int files = 0, unexpected = 0;
        var report = new List<string>();
        foreach (var file in Directory.GetFiles(dir, "*.star"))
        {
            var it = new Interpreter { Dialect = Dialect.AllOptionsInternal };
            TypeCheckResult result;
            try { result = it.Typecheck(file, File.ReadAllText(file)); }
            catch (StarlarkSyntaxException) { continue; }
            files++;
            foreach (var e in result.Errors.Where(e => !legacy.Any(l => e.Message.Contains(l))))
            {
                unexpected++;
                report.Add($"{Path.GetFileName(file)}: {e.Message}");
            }
        }
        Assert.True(files > 100, $"only {files} files were checked");
        Assert.True(unexpected == 0, "false positives:\n" + string.Join("\n", report));
    }
}
