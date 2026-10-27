using System.Numerics;
using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>
/// Tests written from the survivors of a Stryker.NET run over <c>Ops.cs</c>: exact error messages, boundary values,
/// paths that no other test reached, and the rounding of int to float conversion.
/// </summary>
public class OpsMutationTests
{
    private static Interpreter New() => new()
    {
        Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableTypes = DialectTypes.Enable },
    };

    private static object? Val(string expression) => New().Execute("t", "r = " + expression + "\n").Globals["r"];

    private static string Fails(string statements) =>
        Assert.Throws<StarlarkException>(() => New().Execute("t", statements)).RawMessage;

    // ---------------------------------------------------------------- int to float rounding

    /// <summary>Values at the edge of the 64-bit range take the exact path, not the shifting one.</summary>
    [Fact]
    public void LongBoundariesConvertExactly()
    {
        Assert.Equal(9223372036854775808.0, Ops.BigToDouble(long.MaxValue));
        Assert.Equal(-9223372036854775808.0, Ops.BigToDouble(long.MinValue));
        Assert.Equal(9223372036854775808.0, Ops.BigToDouble(new BigInteger(long.MaxValue) + 1));
        Assert.Equal(-9223372036854777856.0, Ops.BigToDouble(new BigInteger(long.MinValue) - 1025));
    }

    /// <summary>Beyond 64 bits, ties round to even and anything above a tie rounds up (the sticky bit), for either sign.</summary>
    [Fact]
    public void LargeIntsRoundToNearestEven()
    {
        var two64 = BigInteger.One << 64;
        Assert.Equal(18446744073709551616.0, Ops.BigToDouble(two64));
        Assert.Equal(18446744073709551616.0, Ops.BigToDouble(two64 + 2048));          // tie, rounds to even (down)
        Assert.Equal(18446744073709555712.0, Ops.BigToDouble(two64 + 2049));          // just above the tie, rounds up
        Assert.Equal(18446744073709559808.0, Ops.BigToDouble(two64 + 4096 + 2048));   // tie, rounds to even (up)
        Assert.Equal(-18446744073709555712.0, Ops.BigToDouble(-(two64 + 2049)));
        Assert.Equal(-18446744073709551616.0, Ops.BigToDouble(-(two64 + 2048)));
        Assert.Equal(18446744073709551616.0, Ops.BigToDouble(two64 + 2047));          // below the tie, rounds down
    }

    /// <summary>An int too large for a double overflows to infinity and mixed arithmetic reports it.</summary>
    [Fact]
    public void HugeIntsAreTooLargeForFloat()
    {
        Assert.True(double.IsPositiveInfinity(Ops.BigToDouble(BigInteger.One << 1200)));
        Assert.True(double.IsNegativeInfinity(Ops.BigToDouble(-(BigInteger.One << 1200))));
        Assert.Equal("int too large to convert to float", Fails("x = (1 << 2000) + 1.0\n"));
    }

    // ---------------------------------------------------------------- equality and ordering

    /// <summary>Containers of different sizes are unequal, and a dict equals itself even when its values do not.</summary>
    [Fact]
    public void ContainerEqualityEdgeCases()
    {
        Assert.Equal(false, Val("[1] == [1, 2]"));
        Assert.Equal(false, Val("(1,) == (1, 2)"));
        Assert.Equal(false, Val("{1: 2} == {1: 2, 3: 4}"));
        Assert.Equal(true, Val("[1] != [1, 2]"));
        var m = New().Execute("t", "d = {1: 2}\nsame = d == d\nl = [0.0 / 1.0]\nsamel = l == l\n");
        Assert.Equal(true, m.Globals["same"]);
        Assert.Equal(true, m.Globals["samel"]);
    }

    /// <summary>A range equals another with the same elements, and ranges that differ in start or step are unequal.</summary>
    [Fact]
    public void RangeEquality()
    {
        Assert.Equal(true, Val("range(0) == range(5, 5)"));
        Assert.Equal(true, Val("range(1, 2) == range(1, 5, 7)"));
        Assert.Equal(false, Val("range(1, 3) == range(2, 4)"));
        Assert.Equal(false, Val("range(0, 6, 2) == range(0, 6, 3)"));
        Assert.Equal(false, Val("range(3) == range(4)"));
    }

    /// <summary>Strings compare by code point, so a supplementary character sorts after any BMP character.</summary>
    [Fact]
    public void SupplementaryCharactersSortAfterTheBmp()
    {
        const string emoji = "😀";   // U+1F600
        const string fullwidth = "～";      // U+FF5E, above the surrogate range in UTF-16 order
        Assert.True(Ops.Compare(emoji, fullwidth) > 0);
        Assert.True(Ops.Compare(fullwidth, emoji) < 0);
        Assert.True(Ops.Compare("a", "b") < 0);
        Assert.True(Ops.Compare("ab", "a") > 0);
    }

    /// <summary>Unorderable types name the operator that was used.</summary>
    [Theory]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData("<=")]
    [InlineData(">=")]
    public void ComparisonErrorsNameTheOperator(string op)
    {
        string message = Fails($"x = 1 {op} 'a'\n");
        Assert.Contains($"`{op}`", message);
        Assert.Contains("'int'", message);
        Assert.Contains("'string'", message);
    }

    // ---------------------------------------------------------------- host-value dispatch

    /// <summary>A host value that claims every operator, to show which ones are routed to it.</summary>
    private sealed class Greedy : IHostValue
    {
        public List<(BinOp Op, bool Reflected)> Calls { get; } = [];
        public string TypeName => "greedy";
        public bool EqualsValue(object? other) => true;
        public int? CompareTo(object? other) => 0;
        public bool? Contains(object? item) => true;

        public bool TryBinary(BinOp op, object? other, bool reflected, out object? result)
        {
            Calls.Add((op, reflected));
            result = "hit";
            return true;
        }
    }

    /// <summary>Equality, ordering and membership use their own hooks, never <c>TryBinary</c>.</summary>
    [Theory]
    [InlineData(BinOp.Equal)]
    [InlineData(BinOp.NotEqual)]
    [InlineData(BinOp.In)]
    [InlineData(BinOp.NotIn)]
    [InlineData(BinOp.And)]
    [InlineData(BinOp.Or)]
    [InlineData(BinOp.Less)]
    [InlineData(BinOp.Greater)]
    [InlineData(BinOp.LessOrEqual)]
    [InlineData(BinOp.GreaterOrEqual)]
    public void ComparisonOperatorsBypassTryBinary(BinOp op)
    {
        var g = new Greedy();
        try { Ops.Binary(op, g, new StarList([new BigInteger(1)])); } catch (Exception) { /* And and Or are not evaluated here */ }
        try { Ops.Binary(op, new StarList([new BigInteger(1)]), g); } catch (Exception) { }
        Assert.Empty(g.Calls);
    }

    /// <summary>Arithmetic and bitwise operators reach the host value, flagged as reflected when it is on the right.</summary>
    [Theory]
    [InlineData(BinOp.Add)]
    [InlineData(BinOp.Subtract)]
    [InlineData(BinOp.Multiply)]
    [InlineData(BinOp.Percent)]
    [InlineData(BinOp.Divide)]
    [InlineData(BinOp.FloorDivide)]
    [InlineData(BinOp.BitAnd)]
    [InlineData(BinOp.BitOr)]
    [InlineData(BinOp.BitXor)]
    [InlineData(BinOp.LeftShift)]
    [InlineData(BinOp.RightShift)]
    public void ArithmeticOperatorsReachTheHostValue(BinOp op)
    {
        var left = new Greedy();
        Assert.Equal("hit", Ops.Binary(op, left, new BigInteger(1)));
        Assert.Equal([(op, false)], left.Calls);

        var right = new Greedy();
        Assert.Equal("hit", Ops.Binary(op, new BigInteger(1), right));
        Assert.Equal([(op, true)], right.Calls);
    }

    // ---------------------------------------------------------------- type unions

    /// <summary>Only a type may be combined with <c>|</c>, with None on either side.</summary>
    [Fact]
    public void UnionsNeedATypeOnOneSide()
    {
        Assert.Contains("None", Repr.Of(Val("int | None")));
        Assert.Contains("None", Repr.Of(Val("None | int")));
        Assert.Contains("str", Repr.Of(Val("int | str")));
        Assert.Contains("Operation `|` not supported", Fails("x = 1 | int\n"));
        Assert.Contains("Operation `|` not supported", Fails("x = int | 1\n"));
    }

    // ---------------------------------------------------------------- error messages

    /// <summary>Every unsupported operator names itself and both operand types.</summary>
    [Theory]
    [InlineData("1 & 'a'", "Operation `&` not supported on types 'int' and 'string'")]
    [InlineData("1 | 'a'", "Operation `|` not supported on types 'int' and 'string'")]
    [InlineData("1 ^ 'a'", "Operation `^` not supported on types 'int' and 'string'")]
    [InlineData("1 << 'a'", "Operation `<<` not supported on types 'int' and 'string'")]
    [InlineData("1 >> 'a'", "Operation `>>` not supported on types 'int' and 'string'")]
    [InlineData("1 + 'a'", "Operation `+` not supported on types 'int' and 'string'")]
    [InlineData("'a' - 1", "Operation `-` not supported on types 'string' and 'int'")]
    [InlineData("'a' * 'b'", "Operation `*` not supported on types 'string' and 'string'")]
    [InlineData("1 / 'a'", "Operation `/` not supported on types 'int' and 'string'")]
    [InlineData("'a' // 1", "Operation `//` not supported on types 'string' and 'int'")]
    [InlineData("[1] % 2", "Operation `%` not supported on types 'list' and 'int'")]
    [InlineData("-'a'", "Operation `-` not supported on type 'string'")]
    [InlineData("+'a'", "Operation `+` not supported on type 'string'")]
    [InlineData("~1.5", "Operation `~` not supported on type 'float'")]
    public void UnsupportedOperatorMessages(string expression, string message) =>
        Assert.Equal(message, Fails($"x = {expression}\n"));

    /// <summary>Division and modulo by zero have distinct messages for ints and floats.</summary>
    [Theory]
    [InlineData("1 / 0", "Division by zero")]
    [InlineData("1.0 / 0.0", "Division by zero")]
    [InlineData("1 // 0", "Integer division by zero")]
    [InlineData("1.0 // 0", "Floating-point division by zero")]
    [InlineData("1 % 0", "Integer modulo by zero")]
    [InlineData("1.0 % 0", "Floating-point modulo by zero")]
    public void ZeroDivisorMessages(string expression, string message) =>
        Assert.Equal(message, Fails($"x = {expression}\n"));

    /// <summary>Floored division and modulo agree with Python for every sign combination, including exact quotients.</summary>
    [Theory]
    [InlineData("7 // 2", 3)]
    [InlineData("-7 // 2", -4)]
    [InlineData("7 // -2", -4)]
    [InlineData("-7 // -2", 3)]
    [InlineData("6 // -2", -3)]
    [InlineData("-6 // 2", -3)]
    [InlineData("7 % 3", 1)]
    [InlineData("-7 % 3", 2)]
    [InlineData("7 % -3", -2)]
    [InlineData("-7 % -3", -1)]
    [InlineData("-6 % 3", 0)]
    public void FlooredArithmetic(string expression, int expected) =>
        Assert.Equal(new BigInteger(expected), Val(expression));

    /// <summary>The same identities for floats.</summary>
    [Theory]
    [InlineData("7.5 % 2.0", 1.5)]
    [InlineData("-7.5 % 2.0", 0.5)]
    [InlineData("7.5 % -2.0", -0.5)]
    [InlineData("-7.5 % -2.0", -1.5)]
    [InlineData("-7.0 // 2.0", -4.0)]
    public void FlooredFloatArithmetic(string expression, double expected) =>
        Assert.Equal(expected, (double)Val(expression)!);

    // ---------------------------------------------------------------- shifts

    /// <summary>Shift counts are validated, and very large right shifts collapse to 0 or -1.</summary>
    [Fact]
    public void ShiftEdgeCases()
    {
        Assert.Equal("Negative left shift count", Fails("x = 1 << -1\n"));
        Assert.Equal("Integer overflow in left shift", Fails("x = 1 << (1 << 40)\n"));
        Assert.Equal(BigInteger.Zero, Val("0 << 2147483647"));   // the largest count still allowed
        Assert.Equal("Negative right shift count", Fails("x = 1 >> -1\n"));
        Assert.Equal(BigInteger.Zero, Val("5 >> (1 << 40)"));
        Assert.Equal(BigInteger.MinusOne, Val("-5 >> (1 << 40)"));
        Assert.Equal(new BigInteger(2), Val("8 >> 2"));
        Assert.Equal(new BigInteger(-2), Val("-8 >> 2"));
    }

    // ---------------------------------------------------------------- the in operator

    /// <summary>Membership tests of the wrong type name the container and the offending type.</summary>
    [Fact]
    public void MembershipTypeErrors()
    {
        Assert.Equal("'in <string>' requires string as left operand, not 'int'", Fails("x = 1 in 'abc'\n"));
        Assert.Equal("'in <utf16>' requires string or utf16 as left operand, not 'int'", Fails("x = 1 in utf16('abc')\n"));
        Assert.Equal("'in <bytes>' requires bytes or int as left operand, not 'float'", Fails("x = 1.5 in b'abc'\n"));
        Assert.Equal("'in <range>' requires int as left operand, not 'string'", Fails("x = 'a' in range(3)\n"));
        Assert.Equal("int in bytes: value out of range 0..255", Fails("x = 256 in b'abc'\n"));
        Assert.Equal("int in bytes: value out of range 0..255", Fails("x = -1 in b'abc'\n"));
    }

    /// <summary>Byte membership accepts the whole 0..255 range and finds a sub-sequence at the very start.</summary>
    [Fact]
    public void ByteMembership()
    {
        Assert.Equal(true, Val("0 in b'\\x00'"));
        Assert.Equal(true, Val("255 in b'\\xff'"));
        Assert.Equal(false, Val("7 in b'\\x01'"));
        Assert.Equal(true, Val("b'a' in b'abc'"));
        Assert.Equal(true, Val("b'abc' in b'abc'"));
        Assert.Equal(false, Val("b'abcd' in b'abc'"));
    }

    /// <summary>Range membership respects the start, stop (exclusive) and step, in both directions.</summary>
    [Theory]
    [InlineData("0 in range(0)", false)]
    [InlineData("3 in range(3, 10)", true)]
    [InlineData("5 in range(3, 10)", true)]
    [InlineData("2 in range(3, 10)", false)]
    [InlineData("9 in range(3, 10)", true)]
    [InlineData("10 in range(3, 10)", false)]
    [InlineData("0 in range(0, 10)", true)]
    [InlineData("10 in range(0, 10)", false)]
    [InlineData("5 in range(1, 10, 2)", true)]
    [InlineData("4 in range(1, 10, 2)", false)]
    [InlineData("10 in range(10, 0, -1)", true)]
    [InlineData("5 in range(10, 0, -1)", true)]
    [InlineData("1 in range(10, 0, -1)", true)]
    [InlineData("0 in range(10, 0, -1)", false)]
    [InlineData("11 in range(10, 0, -1)", false)]
    [InlineData("-3 in range(-10, 0)", true)]
    [InlineData("7 in range(10, 0, -3)", true)]
    [InlineData("8 in range(10, 0, -3)", false)]
    public void RangeMembership(string expression, bool expected) => Assert.Equal(expected, Val(expression));

    // ---------------------------------------------------------------- repetition and concatenation

    /// <summary>Repetition limits report the specific problem, and the largest representable count is not mistaken for overflow.</summary>
    [Fact]
    public void RepetitionLimits()
    {
        Assert.Equal("Repetition count too large", Fails("x = 'a' * (1 << 40)\n"));
        Assert.Equal("Result of repetition is too large", Fails("x = 'a' * 2147483647\n"));
        Assert.Equal("Result of repetition is too large", Fails("x = 'ab' * 1073741824\n"));
        Assert.Equal("Result of repetition is too large", Fails("x = [0] * 100000001\n"));
        Assert.Equal("Result of repetition is too large", Fails("x = (0,) * 100000001\n"));
        Assert.Equal("", Val("'abc' * 0"));
        Assert.Equal("", Val("'abc' * -5"));
        Assert.Equal("ababab", Val("'ab' * 3"));
    }

    /// <summary>Adding two lists checks the combined size, not the difference, against the memory budget.</summary>
    [Fact]
    public void ListConcatenationCountsBothOperands()
    {
        var it = New();
        it.MaxAllocatedBytes = 400;   // each list is 240 bytes, the sum 480; a difference would be 0
        var e = Assert.Throws<ExecutionLimitException>(() =>
            it.Execute("t", "a = [0] * 30\nb = [0] * 30\nc = a + b\n"));
        Assert.Equal("memory", e.Limit);
    }

    // ---------------------------------------------------------------- indexing and slicing

    /// <summary>Item assignment and slice bounds validate their operand types.</summary>
    [Fact]
    public void IndexAndSliceTypeErrors()
    {
        Assert.Equal("Operation `[]=` not supported on types 'list' and 'float'", Fails("x = [1]\nx[1.5] = 2\n"));
        Assert.Equal("Operation not supported: slice start must be an int (got string, want int)", Fails("x = [1][ 'a':]\n"));
        Assert.Equal("Operation not supported: slice stop must be an int (got string, want int)", Fails("x = [1][:'a']\n"));
        Assert.Equal("Operation not supported: slice step must be an int (got string, want int)", Fails("x = [1][::'a']\n"));
        Assert.Equal("Slice step cannot be zero", Fails("x = [1][::0]\n"));
        Assert.Equal("Type 'int' does not support item assignment", Fails("x = 5\nx[0] = 1\n"));
    }

    /// <summary>Only sequences can be sliced.</summary>
    [Fact]
    public void UnsliceableTypes() =>
        Assert.Equal("Type 'int' is not sliceable",
            Assert.Throws<StarlarkException>(() => Ops.Slice(new BigInteger(5), null, null, null)).RawMessage);

    /// <summary>A range can be sliced up to the largest size an int index allows, and no further.</summary>
    [Fact]
    public void RangeSliceSizeLimit()
    {
        Assert.Equal("range too large to slice", Fails("x = range(1 << 40)[1:3]\n"));
        Assert.Equal("[0, 1]", Repr.Of(Val("range(2147483647)[0:2]")));
        Assert.Equal("[2, 1, 0]", Repr.Of(Val("range(3)[::-1]")));
    }

    /// <summary>The unary plus on numbers returns the number unchanged.</summary>
    [Fact]
    public void UnaryPlus()
    {
        Assert.Equal(new BigInteger(3), Val("+3"));
        Assert.Equal(2.5, Val("+2.5"));
    }
}
