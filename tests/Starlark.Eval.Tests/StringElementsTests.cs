using System.Numerics;
using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>Both string-element modes: UTF-16 code units (default) and UTF-8 bytes (Go/Rust compatible).</summary>
public class StringElementsTests
{
    private static string Eval(StringElements mode, string expr) =>
        Repr.Of(new Interpreter { StringElements = mode }.Eval(expr));

    private static string U16(string expr) => Eval(StringElements.Utf16, expr);
    private static string U8(string expr) => Eval(StringElements.Utf8, expr);

    [Fact]
    public void DefaultIsUtf16() => Assert.Equal(StringElements.Utf16, new Interpreter().StringElements);

    [Theory]
    [InlineData("len('abc')", "3", "3")]
    [InlineData("len('é')", "1", "2")]
    [InlineData("len('世界')", "2", "6")]
    [InlineData("len('😀')", "2", "4")]
    [InlineData("len('Hello, 世界!')", "10", "14")]
    public void Length(string expr, string utf16, string utf8)
    {
        Assert.Equal(utf16, U16(expr));
        Assert.Equal(utf8, U8(expr));
    }

    [Theory]
    [InlineData("'abc'[1]", "\"b\"")]
    [InlineData("'abc'[1:]", "\"bc\"")]
    [InlineData("'hello'[::-1]", "\"olleh\"")]
    public void AsciiOnlyBehavesIdentically(string expr, string expected)
    {
        Assert.Equal(expected, U16(expr));
        Assert.Equal(expected, U8(expr));
    }

    [Fact]
    public void Utf8SlicingOnCharacterBoundariesWorks()
    {
        Assert.Equal("\"世界\"", U8("'a世界'[1:]"));
        Assert.Equal("\"a\"", U8("'a世界'[:1]"));
        Assert.Equal("\"世\"", U8("'a世界'[1:4]"));
        Assert.Equal("\"界\"", U8("'世界'[3:]"));
        Assert.Equal("\"界世\"", U8("'世界'[3:6] + '世界'[0:3]"));
    }

    [Fact]
    public void Utf8SlicingInsideACharacterIsAnError()
    {
        var it = new Interpreter { StringElements = StringElements.Utf8 };
        Assert.Contains("boundary", Assert.Throws<StarlarkException>(() => it.Eval("'世界'[1:]")).RawMessage);
        Assert.Contains("boundary", Assert.Throws<StarlarkException>(() => it.Eval("'é'[0:1]")).RawMessage);
        Assert.Contains("boundary", Assert.Throws<StarlarkException>(() => it.Eval("'世'[1]")).RawMessage);
    }

    [Fact]
    public void Utf8IndexingAsciiInsideMixedStringWorks()
    {
        Assert.Equal("\"a\"", U8("'aé'[0]"));
        Assert.Equal("\"b\"", U8("'éb'[2]"));
    }

    [Fact]
    public void Utf16SlicingSplitsSurrogatePairsRatherThanFailing()
    {
        // Documented UTF-16 behaviour: the halves are lone surrogates, shown escaped by repr.
        Assert.Equal("\"\\ud83d\"", U16("'😀'[:1]"));
    }

    [Fact]
    public void FindOffsetsAreInElements()
    {
        Assert.Equal("1", U16("'a世界'.find('世')"));
        Assert.Equal("1", U8("'a世界'.find('世')"));
        Assert.Equal("2", U16("'a世界'.find('界')"));
        Assert.Equal("4", U8("'a世界'.find('界')"));
        Assert.Equal("4", U8("'a世界'.rfind('界')"));
        Assert.Equal("-1", U8("'a世界'.find('界', 5)"));
        Assert.Equal("4", U8("'a世界'.index('界', 1)"));
    }

    [Fact]
    public void FindRespectsStartAndEndInElements()
    {
        Assert.Equal("3", U8("'世界世'.find('界', 3, 6)"));
        Assert.Equal("-1", U8("'世界世'.find('界', 4, 6)"));
        Assert.Equal("3", U8("'世界世'.find('界', 3)"));
        Assert.Equal("-1", U8("'世界世'.find('界', 6)"));
        Assert.Equal("6", U8("'世界世'.find('世', 1)"));
    }

    [Fact]
    public void CountStartsEndsUseElementBounds()
    {
        Assert.Equal("2", U8("'世界世'.count('世')"));
        Assert.Equal("1", U8("'世界世'.count('世', 3)"));
        Assert.Equal("True", U8("'世界世'.startswith('界', 3)"));
        Assert.Equal("True", U8("'世界世'.endswith('世', 0, 9)"));
        Assert.Equal("False", U16("'世界世'.startswith('界', 0)"));
        Assert.Equal("True", U16("'世界世'.startswith('界', 1)"));
    }

    [Fact]
    public void LenOfRangeOfOffsetsRoundTripsThroughSlicing()
    {
        const string s = "'añ世😀z'";
        foreach (var mode in new[] { StringElements.Utf16, StringElements.Utf8 })
        {
            var it = new Interpreter { StringElements = mode };
            Assert.Equal(Repr.Of(it.Eval(s)), Repr.Of(it.Eval($"{s}[:len({s})]")));
            Assert.Equal("\"z\"", Repr.Of(it.Eval($"{s}[-1]")));
        }
    }

    [Fact]
    public void ModeIsRestoredAfterEachCall()
    {
        var utf8 = new Interpreter { StringElements = StringElements.Utf8 };
        var utf16 = new Interpreter();
        Assert.Equal("6", Repr.Of(utf8.Eval("len('世界')")));
        Assert.Equal("2", Repr.Of(utf16.Eval("len('世界')")));
        Assert.Equal("6", Repr.Of(utf8.Eval("len('世界')")));
    }

    [Fact]
    public void FunctionsCalledFromHostUseTheInterpretersMode()
    {
        var it = new Interpreter { StringElements = StringElements.Utf8 };
        var m = it.Execute("t", "def n(s):\n  return len(s)\n");
        Assert.Equal("6", Repr.Of(it.CallValue(m.Globals["n"], ["世界"])));
    }

    [Fact]
    public void HashIsIndependentOfMode()
    {
        // The spec fixes hash() as Java's String.hashCode over UTF-16, regardless of element size.
        Assert.Equal(U16("hash('世界')"), U8("hash('世界')"));
        Assert.Equal("99162322", U8("hash('hello')"));
    }

    [Fact]
    public void Utf8ModeMatchesSpecConformanceExamplesFromGo()
    {
        // These are commented out in the shared suite because Java disagrees; Go and Rust expect them.
        Assert.Equal("14", U8("len('Hello, 世界!')"));
        Assert.Equal("4", U8("len('𐐷')"));
    }

    [Fact]
    public void Utf8ModePassesTheWholeConformanceSuiteToo()
    {
        // Re-run every chunk in UTF-8 mode: nothing in the shared suite should depend on the mode.
        var failures = new List<string>();
        foreach (var args in ConformanceTests.AllChunks())
        {
            var chunk = (ConformanceTests.Chunk)args[1];
            var output = ConformanceTests.Run(chunk.Code, StringElements.Utf8);
            bool ok = chunk.Expected.Count == 0
                ? output.Length == 0
                : output.Length > 0 && chunk.Expected.All(e =>
                    output.Contains(e, StringComparison.OrdinalIgnoreCase) ||
                    System.Text.RegularExpressions.Regex.IsMatch(output.ToLowerInvariant(), e.ToLowerInvariant()));
            if (!ok) failures.Add($"{args[0]}: {output}");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
