using Starlark.Syntax;

namespace Starlark.Syntax.Tests;

/// <summary>
/// What may appear in a type position. The parser validates annotations (the grammar's <c>Test</c> is far wider
/// than the set of legal types), so each construct that is not a type must be rejected with a precise message.
/// </summary>
public class TypeAnnotationTests
{
    private static string Annotated(string type) => $"def f(x: {type}):\n    pass\n";

    private static void Accepts(string type) =>
        AstModule.Parse("t", Annotated(type), Dialect.AllOptionsInternal);

    private static string Rejects(string type) =>
        Assert.Throws<StarlarkSyntaxException>(() => AstModule.Parse("t", Annotated(type), Dialect.AllOptionsInternal)).RawMessage;

    [Theory]
    [InlineData("int")]
    [InlineData("list")]
    [InlineData("list[int]")]
    [InlineData("dict[str, int]")]
    [InlineData("dict[str, list[int]]")]
    [InlineData("tuple[int, ...]")]
    [InlineData("tuple[int, str]")]
    [InlineData("int | str")]
    [InlineData("int | str | None")]
    [InlineData("[int, str]")]
    [InlineData("(int, str)")]
    [InlineData("typing.Any")]
    [InlineData("typing.Callable[[int, str], bool]")]
    [InlineData("typing.Callable[..., int]")]
    [InlineData("ns.sub.Type")]
    [InlineData("ns.Generic[str]")]
    [InlineData("ns.Generic[str, int]")]
    [InlineData("...")]
    [InlineData("None")]
    public void ValidTypesAreAccepted(string type) => Accepts(type);

    [Theory]
    [InlineData("f(x)", "call expression is not allowed in type expression")]
    [InlineData("x[1:2]", "slice expression is not allowed in type expression")]
    [InlineData("lambda: 1", "lambda expression is not allowed in type expression")]
    [InlineData("'literal'", "string literal expression is not allowed in type expression")]
    [InlineData("1", "int expression is not allowed in type expression")]
    [InlineData("1.5", "float expression is not allowed in type expression")]
    [InlineData("b'x'", "bytes literal expression is not allowed in type expression")]
    [InlineData("not x", "not expression is not allowed in type expression")]
    [InlineData("-x", "minus expression is not allowed in type expression")]
    [InlineData("+x", "plus expression is not allowed in type expression")]
    [InlineData("~x", "bit not expression is not allowed in type expression")]
    [InlineData("a + b", "bin op except `|` expression is not allowed in type expression")]
    [InlineData("a if b else c", "if expression is not allowed in type expression")]
    [InlineData("{1: 2}", "dict expression is not allowed in type expression")]
    [InlineData("[x for x in y]", "list comprehension expression is not allowed in type expression")]
    [InlineData("{x: 1 for x in y}", "dict comprehension expression is not allowed in type expression")]
    [InlineData("f'{x}'", "f-string expression is not allowed in type expression")]
    [InlineData("[int]", "list of 1 element expression is not allowed in type expression")]
    [InlineData("[]", "Empty list is not allowed in type expression")]
    public void InvalidTypesAreRejectedWithTheConstructNamed(string type, string message) =>
        Assert.Contains(message, Rejects(type));

    [Fact]
    public void OnlyIdentDotIdentIsAllowedAsAPath()
    {
        Assert.Contains("Only dot expression of form `ident.ident` is allowed in type expression", Rejects("f().x"));
        Assert.Contains("Only dot expression of form `ident.ident` is allowed in type expression", Rejects("(a + b).x"));
        Assert.Contains("Expecting path like `a.b.c`", Rejects("f()[int]"));
        Assert.Contains("Expecting path like `a.b.c`", Rejects("(a | b)[int]"));
    }

    [Fact]
    public void DotTypeSuffixIsBannedInFavourOfTheBareName()
    {
        Assert.Contains("`ns.Thing.type` is not allowed in type expression, use `ns.Thing` instead", Rejects("ns.Thing.type"));
        Assert.Contains("`a.type` is not allowed in type expression, use `a` instead", Rejects("a.type"));
        Assert.Contains("`type` is not allowed", Rejects("x.y.z.type").Replace("`x.y.z.type`", "`type`"));
    }

    [Fact]
    public void ErrorsInsideNestedTypesAreReported()
    {
        Assert.Contains("call expression", Rejects("list[f(x)]"));
        Assert.Contains("string literal", Rejects("dict[str, 'int']"));
        Assert.Contains("int expression", Rejects("tuple[int, 1]"));
        Assert.Contains("call expression", Rejects("[int, f(x)]"));
        Assert.Contains("call expression", Rejects("int | f(x)"));
    }

    [Fact]
    public void ListArgumentsAreAllowedInsideTwoParameterIndexing()
    {
        // `typing.Callable[[int], str]`: the first parameter is a list of types, not a union.
        Accepts("typing.Callable[[int, str], bool]");
        Accepts("typing.Callable[[], bool]");
        Assert.Contains("call expression", Rejects("typing.Callable[[f(x)], bool]"));
    }

    [Fact]
    public void EveryAnnotationPositionIsValidated()
    {
        var d = Dialect.AllOptionsInternal;
        foreach (var src in new[]
        {
            "def f() -> f(x):\n    pass\n",
            "def f(*a: f(x)):\n    pass\n",
            "def f(**k: f(x)):\n    pass\n",
            "x: f(y) = 1\n",
            "def f(a: int, b: f(x) = 1):\n    pass\n",
        })
        {
            var e = Assert.Throws<StarlarkSyntaxException>(() => AstModule.Parse("t", src, d));
            Assert.Contains("call expression is not allowed in type expression", e.RawMessage);
        }
    }

    [Fact]
    public void DisabledTypesRejectAnyAnnotation()
    {
        foreach (var src in new[] { "def f(x: int):\n    pass\n", "def f() -> int:\n    pass\n", "x: int = 1\n" })
        {
            var e = Assert.Throws<StarlarkSyntaxException>(() => AstModule.Parse("t", src, Dialect.Standard));
            Assert.Contains("type annotations are not allowed in this dialect", e.RawMessage);
        }
    }

    [Fact]
    public void ParseOnlyDialectAllowsAnnotations() =>
        AstModule.Parse("t", Annotated("list[int]"), Dialect.Standard with { EnableTypes = DialectTypes.ParseOnly });
}
