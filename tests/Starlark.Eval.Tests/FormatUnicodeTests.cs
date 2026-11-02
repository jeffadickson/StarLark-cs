using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>
/// The formatting extension on text that is not plain ASCII, in both string element modes and with the separate
/// <c>utf16</c> type. Width and precision count Unicode code points (as Python does), so an emoji is one column and
/// is never cut in half; <c>len()</c> keeps counting the mode's own elements.
/// </summary>
public class FormatUnicodeTests
{
    private static Interpreter New(StringElements elements) => new()
    {
        Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableFStrings = true },
        StringElements = elements,
    };

    private static object? Val(StringElements elements, string expression) =>
        New(elements).Execute("t", "r = " + expression + "\n").Globals["r"];

    public static IEnumerable<object[]> Modes() => [[StringElements.Utf16], [StringElements.Utf8]];

    /// <summary>Padding treats each code point as one column, whether it takes 1, 2, 3 or 4 bytes.</summary>
    [Theory]
    [MemberData(nameof(Modes))]
    public void WidthCountsCodePoints(StringElements mode)
    {
        Assert.Equal("    é", Val(mode, "'{:>5}'.format('é')"));          // 2 bytes in UTF-8
        Assert.Equal("世    ", Val(mode, "'{:5}'.format('世')"));          // 3 bytes
        Assert.Equal("😀    ", Val(mode, "'{:5}'.format('😀')"));          // 4 bytes, a surrogate pair in UTF-16
        Assert.Equal("    😀", Val(mode, "'%5s' % '😀'"));
        Assert.Equal("é世😀  |", Val(mode, "'%-5s|' % 'é世😀'"));
        Assert.Equal("  é世  ", Val(mode, "'{:^6}'.format('é世')"));
        Assert.Equal("**😀😀**", Val(mode, "'{:*^6}'.format('😀😀')"));
    }

    /// <summary>Precision truncates by code points and never leaves half of a surrogate pair.</summary>
    [Theory]
    [MemberData(nameof(Modes))]
    public void PrecisionNeverSplitsASurrogatePair(StringElements mode)
    {
        Assert.Equal("😀", Val(mode, "'%.1s' % '😀x'"));
        Assert.Equal("😀a", Val(mode, "'{:.2}'.format('😀ab')"));
        Assert.Equal("é世", Val(mode, "'%.2s' % 'é世😀'"));
        Assert.Equal("", Val(mode, "'%.0s' % '😀'"));
        Assert.Equal("😀😀", Val(mode, "'{:.5}'.format('😀😀')"));
        Assert.Equal("    😀", Val(mode, "'%5.1s' % '😀x'"));   // width 5, one character kept: four spaces
    }

    /// <summary>len() still counts the mode's own elements, so formatting does not change what len() means.</summary>
    [Fact]
    public void LenKeepsItsOwnUnit()
    {
        // One code point padded to width 5 gets four spaces; len() then counts the emoji as 4 bytes or 2 UTF-16 units.
        Assert.Equal(new System.Numerics.BigInteger(8), Val(StringElements.Utf8, "len('{:5}'.format('😀'))"));
        Assert.Equal(new System.Numerics.BigInteger(6), Val(StringElements.Utf16, "len('{:5}'.format('😀'))"));
    }

    /// <summary>The separate utf16 type formats the same way and stays a string for width purposes.</summary>
    [Theory]
    [MemberData(nameof(Modes))]
    public void Utf16TypeFormatsLikeAString(StringElements mode)
    {
        Assert.Equal("****é", Val(mode, "'{:*>5}'.format(utf16('é'))"));
        Assert.Equal("   😀|", Val(mode, "'%4s|' % utf16('😀')"));
        Assert.Equal("😀", Val(mode, "'%.1s' % utf16('😀x')"));
        Assert.Equal("ab  |", Val(mode, "'{:4}|'.format(utf16('ab'))"));
    }

    /// <summary>Combining marks and zero-width characters count as separate code points, as in Python.</summary>
    [Theory]
    [MemberData(nameof(Modes))]
    public void CombiningMarksAreSeparateCodePoints(StringElements mode)
    {
        // "e" + COMBINING ACUTE ACCENT (U+0301) is two code points, so it takes two columns and gets three spaces.
        // The precomposed U+00E9 is one code point and gets four. Both are written as escapes so the forms stay distinct.
        string decomposed = "é";
        string precomposed = "é";
        Assert.Equal(decomposed + "   |", Val(mode, "'{:5}|'.format('e\u0301')"));
        Assert.Equal(precomposed + "    |", Val(mode, "'{:5}|'.format('\u00e9')"));
    }

    /// <summary>f-strings take the same Unicode-aware specs.</summary>
    [Theory]
    [MemberData(nameof(Modes))]
    public void FStringsCountCodePoints(StringElements mode)
    {
        var m = New(mode).Execute("t", "s = '😀é'\nr = [f'{s:>4}|', f'{s:<4}|', f'{s:.1}', f'{s:^6}|']\n");
        Assert.Equal("[\"  😀é|\", \"😀é  |\", \"😀\", \"  😀é  |\"]", Repr.Of(m.Globals["r"]));
    }

    /// <summary>Numbers are ASCII, so only their fill character can be non-ASCII; it still counts as one column.</summary>
    [Theory]
    [MemberData(nameof(Modes))]
    public void NonAsciiFillCharacters(StringElements mode)
    {
        Assert.Equal("世世世42", Val(mode, "'{:世>5}'.format(42)"));
        Assert.Equal("42😀😀😀", Val(mode, "'{:😀<5}'.format(42)"));
        Assert.Equal("ééé42", Val(mode, "'{:é>5d}'.format(42)"));
    }

    /// <summary>A fill character outside the basic plane is a surrogate pair in UTF-16; it must be taken whole.</summary>
    [Fact]
    public void AstralFillCharacterIsTakenWhole()
    {
        // The spec parser reads the first code point as the fill, so a 4-byte character works like any other.
        Assert.Equal("😀😀42", Val(StringElements.Utf16, "'{:😀>4}'.format(42)"));
    }
}
