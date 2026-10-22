using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>The built-in <c>json</c> module, reached the way a Starlark program reaches it (no host wiring).</summary>
public class JsonTests
{
    private const string Q = "\"";

    private static Interpreter New() => new() { Dialect = Dialect.Standard with { EnableTopLevelStmt = true } };

    /// <summary>The repr of the result.</summary>
    private static string R(string expr) => Repr.Of(New().Eval(expr));

    /// <summary>The result, which must be a string, as a plain .NET string.</summary>
    private static string S(string expr) => (string)New().Eval(expr)!;

    private static StarlarkException Fails(string expr) => Assert.Throws<StarlarkException>(() => New().Eval(expr));

    [Fact]
    public void IsAvailableWithoutHostWiring()
    {
        Assert.Equal("{" + Q + "a" + Q + ":1}", S("json.encode({'a': 1})"));
        Assert.Equal("{" + Q + "a" + Q + ": 1}", R("json.decode('{\"a\": 1}')"));
        Assert.Equal("struct", S("type(json)"));
    }

    [Theory]
    [InlineData("None", "null")]
    [InlineData("True", "true")]
    [InlineData("42", "42")]
    [InlineData("-7", "-7")]
    [InlineData("123456789012345678901234567890", "123456789012345678901234567890")]
    [InlineData("[1, [2], ()]", "[1,[2],[]]")]
    public void Encode_Basics(string value, string expected) => Assert.Equal(expected, S("json.encode(" + value + ")"));

    [Fact]
    public void Encode_StringsAndContainers()
    {
        Assert.Equal(Q + "hi" + Q, S("json.encode('hi')"));
        Assert.Equal("[1," + Q + "a" + Q + "]", S("json.encode((1, 'a'))"));
        Assert.Equal("{" + Q + "k" + Q + ":[null]}", S("json.encode({'k': [None]})"));
    }

    [Fact]
    public void Encode_EscapesQuoteBackslashAndControlCharacters()
    {
        // Starlark source:  json.encode('q"\n\t\\')   ->  JSON text:  "q\"\n\t\\"  (with the escapes spelled out)
        string source = "json.encode('q" + Q + "\\n\\t\\\\')";
        string expected = Q + "q\\" + Q + "\\n\\t\\\\" + Q;
        Assert.Equal(expected, S(source));
        Assert.Equal(Q + "\\u0001" + Q, S("json.encode('\\x01')"));
    }

    [Fact]
    public void Encode_Refuses_WhatJsonCannotRepresent()
    {
        Assert.Contains("non-finite", Fails("json.encode(float('inf'))").Message, StringComparison.Ordinal);
        Assert.Contains("non-finite", Fails("json.encode(float('nan'))").Message, StringComparison.Ordinal);
        Assert.Contains("keys must be strings", Fails("json.encode({1: 2})").Message, StringComparison.Ordinal);
        Assert.Contains("cannot be serialized", Fails("json.encode(len)").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Encode_DetectsCycles_AndExcessiveDepth()
    {
        var ex = Assert.Throws<StarlarkException>(() => New().Execute("c.star", "a = []\na.append(a)\nr = json.encode(a)\n"));
        Assert.Contains("Cycle", ex.Message, StringComparison.Ordinal);
        Assert.Contains("nested too deeply", Fails("json.encode(" + new string('[', 300) + new string(']', 300) + ")").Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("null", "None")]
    [InlineData("true", "True")]
    [InlineData("[]", "[]")]
    [InlineData("  [1 , 2 ]  ", "[1, 2]")]
    [InlineData("1.5", "1.5")]
    [InlineData("-0", "0")]
    [InlineData("1e3", "1000.0")]
    [InlineData("12345678901234567890123", "12345678901234567890123")]
    public void Decode_Basics(string json, string expectedRepr) => Assert.Equal(expectedRepr, R("json.decode('" + json + "')"));

    [Fact]
    public void Decode_ObjectsAndEscapes()
    {
        Assert.Equal("{" + Q + "a" + Q + ": {" + Q + "b" + Q + ": [False]}}", R("json.decode('{\"a\":{\"b\":[false]}}')"));
        Assert.Equal("é\n", S("json.decode('\"\\\\u00e9\\\\n\"')"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[")]
    [InlineData("{")]
    [InlineData("{\"a\"}")]
    [InlineData("{\"a\":1,}")]
    [InlineData("[1,]")]
    [InlineData("[1 2]")]
    [InlineData("nul")]
    [InlineData("01")]
    [InlineData("1.")]
    [InlineData(".5")]
    [InlineData("\"unterminated")]
    [InlineData("[1] trailing")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void Decode_RejectsMalformedJson_WithAStarlarkError(string json)
    {
        string source = "json.decode('" + json.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "')";
        Fails(source);
    }

    [Theory]
    [InlineData("\"bad \\\\q escape\"")]
    [InlineData("\"\\\\ud800\"")]
    public void Decode_RejectsBadEscapes(string json) => Fails("json.decode('" + json + "')");

    [Fact]
    public void Decode_DefaultSuppressesTheError_ButOnlyWhenGiven()
    {
        Assert.Equal(Q + "fallback" + Q, R("json.decode('{oops', default='fallback')"));
        Assert.Equal("None", R("json.decode('{oops', default=None)"));
        Fails("json.decode('{oops')");
    }

    [Fact]
    public void Decode_IsDepthLimited_NotStackOverflowing()
    {
        Fails("json.decode('" + new string('[', 100_000) + "')");
        Fails("json.decode('" + string.Concat(Enumerable.Repeat("{\"a\":", 100_000)).Replace("\"", "\\\"", StringComparison.Ordinal) + "')");
    }

    [Fact]
    public void Decode_ResultIsOrdinaryMutableData()
    {
        Assert.Equal("[1, 2, 3]", R("(lambda v: (v.append(3), v)[1])(json.decode('[1,2]'))"));
        Assert.Equal("{" + Q + "a" + Q + ": 1, " + Q + "b" + Q + ": 2}", R("(lambda d: (d.update({'b': 2}), d)[1])(json.decode('{\"a\":1}'))"));
    }

    [Fact]
    public void Indent_Reformats_AndRejectsInvalidInput()
    {
        string expected = "{\n  " + Q + "a" + Q + ": [\n    1,\n    2\n  ]\n}";
        Assert.Equal(expected, S("json.indent('{\"a\":[1,2]}', indent='  ')"));
        Fails("json.indent('{nope')");
    }

    [Fact]
    public void ModuleMembers_AreChecked()
    {
        Fails("json.nothing");
        Fails("json.encode()");
        Fails("json.encode(1, 2)");
        Fails("json.decode(5)");
    }

    [Fact]
    public void SpecialCharacters_RoundTrip()
        => Assert.Equal("True", R("(lambda s: json.decode(json.encode(s)) == s)('tab\\there \"quote\" back\\\\slash \\x00 \\u00e9 \\U0001F600')"));

    [Fact]
    public void ResultsSurviveModuleFreezing()
    {
        var m = New().Execute("t.star", "x = json.encode([1, 2])\ny = json.decode(x)\n");
        Assert.True(m.TryGet("y", out var y));
        Assert.Equal("[1, 2]", Repr.Of(y));
    }
}

public class JsonSurrogateTests
{
    // Inside the Starlark source the JSON text sits in a '...' literal, so each JSON backslash must itself be written as two.
    private const string U = "\\\\u";

    private static Interpreter New() => new() { Dialect = Dialect.Standard with { EnableTopLevelStmt = true } };

    private static object? Decode(string jsonBody) => New().Eval("json.decode('" + (char)34 + jsonBody + (char)34 + "')");

    [Fact]
    public void ProperSurrogatePair_DecodesToOneCodePoint()
        => Assert.Equal("😀", (string)Decode(U + "d83d" + U + "de00")!);

    [Theory]
    [InlineData("d83d")]
    [InlineData("de00")]
    [InlineData("d83dx")]
    [InlineData("d83d|u0041")]
    [InlineData("de00|ud83d")]
    public void LoneOrMisorderedSurrogates_AreRejected(string spec)
    {
        // "|" stands for the start of a second escape so the data row stays readable.
        string body = U + spec.Replace("|", U, StringComparison.Ordinal);
        Assert.Throws<StarlarkException>(() => Decode(body));
    }
}
