using System.Numerics;
using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>
/// The <c>utf16</c> extension type: a string whose elements are always UTF-16 code units, whatever the interpreter's
/// <see cref="StringElements"/> mode counts for plain strings.
/// </summary>
public class Utf16TypeTests
{
    private static Interpreter Utf8() => new()
    {
        StringElements = StringElements.Utf8,
        Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableTypes = DialectTypes.Enable },
    };

    private static Interpreter Utf16Mode() => new()
    {
        StringElements = StringElements.Utf16,
        Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableTypes = DialectTypes.Enable },
    };

    private static string R(Interpreter it, string expr) => Repr.Of(it.Eval(expr));

    // ---------------------------------------------------------------- the point of the type

    [Theory]
    [InlineData("len('é')", "2", "1")]
    [InlineData("len('世界')", "6", "2")]
    [InlineData("len('😀')", "4", "2")]
    [InlineData("len('Hello, 世界!')", "14", "10")]
    public void PlainStringsFollowTheInterpreterModeButUtf16DoesNot(string plain, string utf8Len, string utf16Len)
    {
        Assert.Equal(utf8Len, R(Utf8(), plain));
        Assert.Equal(utf16Len, R(Utf16Mode(), plain));

        var asUtf16 = plain.Replace("len('", "len(utf16('");
        asUtf16 = asUtf16.Substring(0, asUtf16.Length - 1) + "))";
        Assert.Equal(utf16Len, R(Utf8(), asUtf16));
        Assert.Equal(utf16Len, R(Utf16Mode(), asUtf16));
    }

    [Fact]
    public void IndexingAndSlicingAreInCodeUnitsEvenInUtf8Mode()
    {
        var it = Utf8();
        Assert.Equal("utf16(\"世\")", R(it, "utf16('世界')[0]"));
        Assert.Equal("utf16(\"界\")", R(it, "utf16('世界')[1]"));
        Assert.Equal("utf16(\"界\")", R(it, "utf16('世界')[1:]"));
        Assert.Equal("utf16(\"界世\")", R(it, "utf16('世界')[::-1]"));
        // Cutting through a surrogate pair is legal for utf16 (it would be an error for a UTF-8 string).
        Assert.Equal("2", R(it, "len(utf16('😀'))"));
        Assert.Equal("1", R(it, "len(utf16('😀')[:1])"));
    }

    [Fact]
    public void MethodOffsetsAreInCodeUnits()
    {
        var it = Utf8();
        Assert.Equal("1", R(it, "utf16('a世界').find(utf16('世'))"));
        Assert.Equal("2", R(it, "utf16('a世界').find('界')"));
        Assert.Equal("4", R(it, "'a世界'.find('界')"));   // the plain string counts bytes
        Assert.Equal("2", R(it, "utf16('世界世').count('世')"));
        Assert.Equal("True", R(it, "utf16('世界').startswith('界', 1)"));
        Assert.Equal("[utf16(\"a\"), utf16(\"b\")]", R(it, "utf16('a,b').split(',')"));
    }

    // ---------------------------------------------------------------- it is a distinct type

    [Fact]
    public void ItIsADifferentTypeFromString()
    {
        var it = Utf16Mode();
        Assert.Equal("\"utf16\"", R(it, "type(utf16('x'))"));
        Assert.Equal("\"string\"", R(it, "type('x')"));
        Assert.Equal("False", R(it, "utf16('x') == 'x'"));
        Assert.Equal("True", R(it, "utf16('x') != 'x'"));
        Assert.Equal("True", R(it, "utf16('x') == utf16('x')"));
        Assert.Equal("False", R(it, "isinstance(utf16('x'), str)"));
        Assert.Equal("True", R(it, "isinstance(utf16('x'), utf16)"));
        Assert.Equal("True", R(it, "isinstance('x', str)"));
    }

    [Fact]
    public void TheTwoKindsDoNotMixInOperators()
    {
        var it = Utf16Mode();
        Assert.Throws<StarlarkException>(() => it.Eval("utf16('a') + 'b'"));
        Assert.Throws<StarlarkException>(() => it.Eval("'a' + utf16('b')"));
        Assert.Throws<StarlarkException>(() => it.Eval("utf16('a') < 'b'"));
        Assert.Equal("utf16(\"ab\")", R(it, "utf16('a') + utf16('b')"));
        Assert.Equal("True", R(it, "utf16('a') < utf16('b')"));
    }

    [Fact]
    public void ExplicitConversionsAreLossless()
    {
        var it = Utf8();
        Assert.Equal("\"世界\"", R(it, "str(utf16('世界'))"));
        Assert.Equal("utf16(\"世界\")", R(it, "utf16(str(utf16('世界')))"));
        Assert.Equal("utf16(\"x\")", R(it, "utf16(utf16('x'))"));
        Assert.Equal("utf16(\"hi\")", R(it, "utf16(b'hi')"));
        Assert.Equal("b\"hi\"", R(it, "bytes(utf16('hi'))"));
        Assert.Throws<StarlarkException>(() => it.Eval("utf16(1)"));
        Assert.Throws<StarlarkException>(() => it.Eval("utf16(None)"));
    }

    [Fact]
    public void MethodsAcceptEitherKindOfArgument()
    {
        var it = Utf16Mode();
        Assert.Equal("utf16(\"a-b\")", R(it, "utf16('-').join([utf16('a'), utf16('b')])"));
        Assert.Equal("utf16(\"a-b\")", R(it, "utf16('-').join(['a', 'b'])"));
        Assert.Equal("utf16(\"xbc\")", R(it, "utf16('abc').replace('a', utf16('x'))"));
        Assert.Equal("True", R(it, "utf16('abc').startswith(('z', utf16('a')))"));
        Assert.Equal("True", R(it, "'b' in utf16('abc')"));
        Assert.Equal("True", R(it, "utf16('b') in utf16('abc')"));
        Assert.Equal("True", R(it, "utf16('b') in 'abc'"));
    }

    [Fact]
    public void FormatAndInterpolation()
    {
        var it = Utf16Mode();
        Assert.Equal("utf16(\"a=1\")", R(it, "utf16('a={}').format(1)"));
        Assert.Equal("utf16(\"x-y\")", R(it, "utf16('%s-%s') % ('x', 'y')"));
    }

    // ---------------------------------------------------------------- behaves like the other values

    [Fact]
    public void ReprStrAndTruthiness()
    {
        var it = Utf16Mode();
        Assert.Equal("\"utf16(\\\"é\\\")\"", R(it, "repr(utf16('é'))"));
        Assert.Equal("\"é\"", R(it, "str(utf16('é'))"));
        Assert.Equal("False", R(it, "bool(utf16(''))"));
        Assert.Equal("True", R(it, "bool(utf16('x'))"));
        Assert.Equal("[False, True]", R(it, "[not utf16('x'), not utf16('')]"));
    }

    [Fact]
    public void HashableAndUsableAsDictKeysDistinctFromStrings()
    {
        var it = Utf16Mode();
        Assert.Equal("2", R(it, "len({'a': 1, utf16('a'): 2})"));
        Assert.Equal("2", R(it, "{'a': 1, utf16('a'): 2}[utf16('a')]"));
        Assert.Equal("1", R(it, "{'a': 1, utf16('a'): 2}['a']"));
        Assert.Equal("1", R(it, "len(set([utf16('a'), utf16('a')]))"));
        // hash follows the spec for strings: Java's hashCode over UTF-16.
        Assert.Equal("99162322", R(it, "hash(utf16('hello'))"));
        Assert.Equal(R(it, "hash('héllo')"), R(it, "hash(utf16('héllo'))"));
    }

    [Fact]
    public void RepetitionAndComparison()
    {
        var it = Utf16Mode();
        Assert.Equal("utf16(\"abab\")", R(it, "utf16('ab') * 2"));
        Assert.Equal("utf16(\"\")", R(it, "utf16('ab') * -1"));
        Assert.Equal("utf16(\"ababab\")", R(it, "3 * utf16('ab')"));
        Assert.Equal("[utf16(\"a\"), utf16(\"b\"), utf16(\"c\")]", R(it, "sorted([utf16('c'), utf16('a'), utf16('b')])"));
    }

    [Fact]
    public void ElemsAndIndexErrors()
    {
        var it = Utf16Mode();
        Assert.Equal("[utf16(\"a\"), utf16(\"b\")]", R(it, "list(utf16('ab').elems())"));
        Assert.Throws<StarlarkException>(() => it.Eval("utf16('ab')[2]"));
        Assert.Throws<StarlarkException>(() => it.Eval("utf16('ab')['x']"));
        Assert.Throws<StarlarkException>(() => it.Eval("[x for x in utf16('ab')]"));   // strings are not iterable
    }

    [Fact]
    public void JsonEncodesItAsAString() =>
        Assert.Equal("\"\\\"hi\\\"\"", R(Utf16Mode(), "json.encode(utf16('hi'))"));

    // ---------------------------------------------------------------- types and the checker

    [Fact]
    public void UsableAsAnAnnotation()
    {
        var it = Utf16Mode();
        const string def = "def f(x: utf16) -> utf16:\n  return x\n";
        it.Execute("t", def + "f(utf16('a'))\n");
        var ex = Assert.Throws<StarlarkException>(() => it.Execute("t", def + "f('a')\n"));
        Assert.Contains("does not match the type annotation `utf16`", ex.RawMessage);
    }

    [Fact]
    public void CheckerKeepsTheTwoKindsApart()
    {
        var it = Utf16Mode();
        var bad = it.Typecheck("t", "def f(x: utf16):\n  pass\n\ndef g():\n  f('plain')\n");
        Assert.Equal("Expected type `utf16` but got `str`", Assert.Single(bad.Errors).Message);

        var good = it.Typecheck("t", "def f(x: utf16):\n  pass\n\ndef g():\n  f(utf16('ok'))\n");
        Assert.Empty(good.Errors);

        var mixed = it.Typecheck("t", "def g(a: utf16, b: str):\n  c = a + b\n");
        Assert.Contains("Binary operator `+` is not available on the types `utf16` and `str`", Assert.Single(mixed.Errors).Message);
    }

    [Fact]
    public void CheckerInfersMethodResultsAsTheSameKindOfString()
    {
        var it = Utf16Mode();
        var r = it.Typecheck("t", "def g(a: utf16):\n  u = a.upper()\n  parts = a.split(',')\n  n = a.find('x')\n");
        Assert.Empty(r.Errors);
        Assert.Equal("utf16", r.Types["u"].ToString());
        Assert.Equal("list[utf16]", r.Types["parts"].ToString());
        Assert.Equal("int", r.Types["n"].ToString());
    }

    [Fact]
    public void LenAndHashAcceptIt()
    {
        var it = Utf16Mode();
        Assert.Empty(it.Typecheck("t", "def g(a: utf16):\n  n = len(a)\n  h = hash(a)\n").Errors);
    }
}
