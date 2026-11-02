using System.Numerics;
using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>
/// The formatting extension. Expected values follow Python's documented behaviour for <c>%</c> and
/// <c>format</c>, which the extension deliberately mirrors; plain conversions must behave exactly as before.
/// </summary>
public class FormatSpecTests
{
    private static Interpreter New() => new()
    {
        Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableFStrings = true },
    };

    private static object? Val(string expression) => New().Execute("t", "r = " + expression + "\n").Globals["r"];

    private static string Fails(string expression) =>
        Assert.Throws<StarlarkException>(() => New().Execute("t", "r = " + expression + "\n")).RawMessage;

    // ---------------------------------------------------------------- % with flags, width and precision

    /// <summary>Width and the left-justify flag for strings.</summary>
    [Theory]
    [InlineData("'%5s' % 'ab'", "   ab")]
    [InlineData("'%-5s|' % 'ab'", "ab   |")]
    [InlineData("'%.2s' % 'abcdef'", "ab")]
    [InlineData("'%5.1s|' % 'abc'", "    a|")]
    [InlineData("'%-6r|' % 'x'", "\"x\"   |")]
    [InlineData("'%3c' % 65", "  A")]
    public void PercentStrings(string expression, string expected) => Assert.Equal(expected, Val(expression));

    /// <summary>Integers: zero padding, sign flags, minimum digits and alternate prefixes.</summary>
    [Theory]
    [InlineData("'%05d' % 42", "00042")]
    [InlineData("'%05d' % -42", "-0042")]
    [InlineData("'%+d' % 5", "+5")]
    [InlineData("'% d' % 5", " 5")]
    [InlineData("'%+d' % -5", "-5")]
    [InlineData("'%5d|' % 42", "   42|")]
    [InlineData("'%-5d|' % 42", "42   |")]
    [InlineData("'%.3d' % 5", "005")]
    [InlineData("'%x' % 255", "ff")]
    [InlineData("'%#x' % 255", "0xff")]
    [InlineData("'%#X' % 255", "0XFF")]
    [InlineData("'%#o' % 8", "0o10")]
    [InlineData("'%08x' % 255", "000000ff")]
    [InlineData("'%#06x' % 255", "0x00ff")]
    public void PercentIntegers(string expression, string expected) => Assert.Equal(expected, Val(expression));

    /// <summary>Floats: precision, sign, width, and the e and g forms.</summary>
    [Theory]
    [InlineData("'%.2f' % 3.14159", "3.14")]
    [InlineData("'%8.3f' % 3.14159", "   3.142")]
    [InlineData("'%-8.1f|' % 2.25", "2.2     |")]
    [InlineData("'%+.1f' % 2.0", "+2.0")]
    [InlineData("'%08.2f' % -3.5", "-0003.50")]
    [InlineData("'%.0f' % 2.5", "2")]
    [InlineData("'%.0f' % 3.5", "4")]
    [InlineData("'%f' % 1.5", "1.500000")]
    [InlineData("'%.3e' % 12345.678", "1.235e+04")]
    [InlineData("'%.3g' % 0.0001234", "0.000123")]
    [InlineData("'%10g|' % 100000", "    100000|")]
    [InlineData("'%g' % 1000000", "1e+06")]
    public void PercentFloats(string expression, string expected) => Assert.Equal(expected, Val(expression));

    /// <summary>A star takes its width or precision from the argument list.</summary>
    [Theory]
    [InlineData("'%*d' % (5, 42)", "   42")]
    [InlineData("'%-*d|' % (5, 42)", "42   |")]
    [InlineData("'%*d|' % (-5, 42)", "42   |")]
    [InlineData("'%.*f' % (2, 3.14159)", "3.14")]
    [InlineData("'%*.*f' % (8, 2, 3.14159)", "    3.14")]
    public void PercentStar(string expression, string expected) => Assert.Equal(expected, Val(expression));

    /// <summary>Plain conversions are exactly what they were, and a literal percent still works.</summary>
    [Theory]
    [InlineData("'%d items' % 3", "3 items")]
    [InlineData("'%s and %s' % ('a', 'b')", "a and b")]
    [InlineData("'100%%' % ()", "100%")]
    [InlineData("'%(n)d-%(m)s' % {'n': 1, 'm': 'x'}", "1-x")]
    public void PlainConversionsAreUnchanged(string expression, string expected) => Assert.Equal(expected, Val(expression));

    /// <summary>A huge width is refused rather than allocating gigabytes.</summary>
    [Fact]
    public void HugeWidthsAreRefused()
    {
        Assert.Contains("larger than", Fails("'%99999999d' % 1"));
        Assert.Contains("larger than", Fails("'%.99999999f' % 1.0"));
        Assert.Contains("must be an int", Fails("'%*d' % ('x', 1)"));
    }

    /// <summary>Errors for a bad conversion name the specifier.</summary>
    [Fact]
    public void BadSpecifiers()
    {
        Assert.Contains("Unknown format specifier '%z'", Fails("'%5z' % 1"));
        Assert.Contains("Unexpected end of format string", Fails("'%5' % 1"));
        Assert.Contains("Not enough arguments", Fails("'%5d %d' % 1"));
    }

    // ---------------------------------------------------------------- {value:spec}

    /// <summary>Width, alignment and fill for strings.</summary>
    [Theory]
    [InlineData("'{:5}'.format('ab')", "ab   ")]
    [InlineData("'{:>5}'.format('ab')", "   ab")]
    [InlineData("'{:<5}|'.format('ab')", "ab   |")]
    [InlineData("'{:^6}|'.format('ab')", "  ab  |")]
    [InlineData("'{:^5}|'.format('ab')", " ab  |")]
    [InlineData("'{:*^7}'.format('ab')", "**ab***")]
    [InlineData("'{:-<5}'.format('ab')", "ab---")]
    [InlineData("'{:.2}'.format('abcdef')", "ab")]
    [InlineData("'{:>8.3}'.format('abcdef')", "     abc")]
    public void SpecStrings(string expression, string expected) => Assert.Equal(expected, Val(expression));

    /// <summary>Integers: default right alignment, sign, zero padding, bases and grouping.</summary>
    [Theory]
    [InlineData("'{:5}'.format(42)", "   42")]
    [InlineData("'{:<5}|'.format(42)", "42   |")]
    [InlineData("'{:05}'.format(42)", "00042")]
    [InlineData("'{:05}'.format(-42)", "-0042")]
    [InlineData("'{:+}'.format(42)", "+42")]
    [InlineData("'{: }'.format(42)", " 42")]
    [InlineData("'{:x}'.format(255)", "ff")]
    [InlineData("'{:X}'.format(255)", "FF")]
    [InlineData("'{:#x}'.format(255)", "0xff")]
    [InlineData("'{:#o}'.format(8)", "0o10")]
    [InlineData("'{:b}'.format(5)", "101")]
    [InlineData("'{:#b}'.format(5)", "0b101")]
    [InlineData("'{:08b}'.format(5)", "00000101")]
    [InlineData("'{:,}'.format(1234567)", "1,234,567")]
    [InlineData("'{:_}'.format(1234567)", "1_234_567")]
    [InlineData("'{:,}'.format(-1234)", "-1,234")]
    [InlineData("'{:*>8,}'.format(1234)", "***1,234")]
    [InlineData("'{:=+6}'.format(5)", "+    5")]
    public void SpecIntegers(string expression, string expected) => Assert.Equal(expected, Val(expression));

    /// <summary>Floats: fixed, exponent and general forms, with precision, grouping and percent.</summary>
    [Theory]
    [InlineData("'{:.2f}'.format(3.14159)", "3.14")]
    [InlineData("'{:8.2f}'.format(3.14159)", "    3.14")]
    [InlineData("'{:<8.2f}|'.format(3.14159)", "3.14    |")]
    [InlineData("'{:08.2f}'.format(-3.14159)", "-0003.14")]
    [InlineData("'{:+.1f}'.format(2.0)", "+2.0")]
    [InlineData("'{:,.2f}'.format(1234567.891)", "1,234,567.89")]
    [InlineData("'{:e}'.format(1234.5)", "1.234500e+03")]
    [InlineData("'{:.2e}'.format(1234.5)", "1.23e+03")]
    [InlineData("'{:g}'.format(0.00001234)", "1.234e-05")]
    [InlineData("'{:g}'.format(1234.5)", "1234.5")]
    [InlineData("'{}'.format(1234.5)", "1234.5")]
    [InlineData("'{:.1%}'.format(0.256)", "25.6%")]
    [InlineData("'{:10.1%}'.format(0.5)", "     50.0%")]
    public void SpecFloats(string expression, string expected) => Assert.Equal(expected, Val(expression));

    /// <summary>A spec combines with a name, an index or a conversion.</summary>
    [Theory]
    [InlineData("'{0:>4}{1:<4}|'.format('a', 'b')", "   ab   |")]
    [InlineData("'{x:05d}'.format(x = 7)", "00007")]
    [InlineData("'{name:^7}'.format(name = 'hi')", "  hi   ")]
    [InlineData("'{!r:>6}'.format('a')", "   \"a\"")]
    [InlineData("'{:>6}'.format(True)", "  True")]
    [InlineData("'{:6}|'.format(True)", "True  |")]
    public void SpecWithFields(string expression, string expected) => Assert.Equal(expected, Val(expression));

    /// <summary>f-strings take the same specs.</summary>
    [Fact]
    public void FStringsAcceptSpecs()
    {
        var m = New().Execute("t", "n = 42\nx = 3.14159\ns = 'ab'\nr = [f'{n:05d}', f'{x:.2f}', f'{s:>5}', f'{n:#x}', f'{x:8.3f}']\n");
        Assert.Equal("[\"00042\", \"3.14\", \"   ab\", \"0x2a\", \"   3.142\"]", Repr.Of(m.Globals["r"]));
    }

    /// <summary>Plain fields without a spec behave exactly as before.</summary>
    [Theory]
    [InlineData("'{} {}'.format(1, 'a')", "1 a")]
    [InlineData("'{!r}'.format('a')", "\"a\"")]
    [InlineData("'{{}} {}'.format(3)", "{} 3")]
    public void PlainFieldsAreUnchanged(string expression, string expected) => Assert.Equal(expected, Val(expression));

    /// <summary>A spec that does not fit the value is an error that names the problem.</summary>
    [Fact]
    public void SpecErrors()
    {
        Assert.Contains("Unknown format code 'd' for a string", Fails("'{:d}'.format('x')"));
        Assert.Contains("Unknown format code 'x' for a float", Fails("'{:x}'.format(1.5)"));
        Assert.Contains("Unknown format code 'q' for an int", Fails("'{:q}'.format(1)"));
        Assert.Contains("Invalid format specifier", Fails("'{:5dd}'.format(1)"));
        Assert.Contains("missing precision", Fails("'{:.}'.format(1.5)"));
        Assert.Contains("larger than", Fails("'{:99999999}'.format(1)"));
    }

    /// <summary>Special float values are never zero padded and keep their sign.</summary>
    [Theory]
    [InlineData("'{:08.2f}'.format(float('inf'))", "     inf")]
    [InlineData("'{:08.2f}'.format(float('-inf'))", "    -inf")]
    [InlineData("'{:.2f}'.format(float('nan'))", "nan")]
    [InlineData("'{:+.1f}'.format(float('inf'))", "+inf")]
    public void SpecialFloats(string expression, string expected) => Assert.Equal(expected, Val(expression));

    /// <summary>Big integers format exactly.</summary>
    [Fact]
    public void BigIntegers()
    {
        Assert.Equal("1,267,650,600,228,229,401,496,703,205,376", Val("'{:,}'.format(1 << 100)"));
        Assert.Equal("10000000000000000000000000", Val("'{:x}'.format(1 << 100)"));
        Assert.Equal("-1,267,650,600,228,229,401,496,703,205,376", Val("'{:,}'.format(-(1 << 100))"));
    }
}
