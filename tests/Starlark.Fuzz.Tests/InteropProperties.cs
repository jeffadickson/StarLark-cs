using System.Numerics;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Fuzz.Tests;

/// <summary>FsCheck properties for the value model and the host conversions.</summary>
public class InteropProperties
{
    private static Interpreter New() => new() { Dialect = Dialect.Standard with { EnableTopLevelStmt = true } };

    /// <summary>Any 64-bit integer survives a trip to Starlark and back.</summary>
    [Property]
    public bool LongsRoundTrip(long n) => (long)Interop.FromStar(Interop.ToStar(n), typeof(long))! == n;

    /// <summary>Any string survives a trip to Starlark and back.</summary>
    [Property]
    public bool StringsRoundTrip(NonNull<string> s) => (string)Interop.FromStar(Interop.ToStar(s.Get), typeof(string))! == s.Get;

    /// <summary>Lists of integers survive a trip to Starlark and back.</summary>
    [Property]
    public bool ListsRoundTrip(int[] xs) => ((List<int>)Interop.FromStar(Interop.ToStar(xs), typeof(List<int>))!).SequenceEqual(xs);

    /// <summary>Dictionaries keyed by strings survive a trip to Starlark and back.</summary>
    [Property]
    public bool DictionariesRoundTrip(Dictionary<string, int> map)
    {
        var back = (Dictionary<string, int>)Interop.FromStar(Interop.ToStar(map), typeof(Dictionary<string, int>))!;
        return back.Count == map.Count && map.All(kv => back[kv.Key] == kv.Value);
    }

    /// <summary>Converting a Starlark int to a narrower type either gives the same value or fails with a Starlark error, never a CLR exception.</summary>
    [Property]
    public bool NarrowingNeverThrowsAClrException(long n)
    {
        try
        {
            return (int)Interop.FromStar(new BigInteger(n), typeof(int))! == n;
        }
        catch (StarlarkException)
        {
            return n < int.MinValue || n > int.MaxValue;
        }
    }

    /// <summary>The script's integer arithmetic agrees with BigInteger for any operands.</summary>
    [Property]
    public bool AdditionAgreesWithBigInteger(long a, long b)
    {
        var it = New();
        it.DefineValue("a", a);
        it.DefineValue("b", b);
        var m = it.Execute("t", "r = a + b\n");
        return (BigInteger)m.Globals["r"]! == (BigInteger)a + b;
    }

    /// <summary>Floored division and modulo satisfy <c>a == (a // b) * b + a % b</c> whenever b is not zero.</summary>
    [Property]
    public bool DivisionIdentity(long a, NonZeroInt b)
    {
        var it = New();
        it.DefineValue("a", a);
        it.DefineValue("b", (long)b.Get);
        var m = it.Execute("t", "ok = a == (a // b) * b + a % b\nsign = a % b == 0 or (a % b < 0) == (b < 0)\n");
        return (bool)m.Globals["ok"]! && (bool)m.Globals["sign"]!;
    }

    /// <summary>Equal values hash equally, for ints and floats that are numerically equal.</summary>
    [Property]
    public bool EqualNumbersHashEqually(int n) => Hashing.Hash(new BigInteger(n)) == Hashing.Hash((double)n);

    /// <summary>The snake_case conversion is idempotent and never produces upper case letters.</summary>
    [Property]
    public bool SnakeCaseIsIdempotent(NonNull<string> s)
    {
        string once = Interop.SnakeCase(s.Get);
        return Interop.SnakeCase(once) == once && !once.Any(char.IsUpper);
    }

    /// <summary>int to float conversion is monotonic: a larger int never gives a smaller float.</summary>
    [Property]
    public bool IntToFloatIsMonotonic(long a, long b)
    {
        double x = Ops.BigToDouble(a), y = Ops.BigToDouble(b);
        return a <= b ? x <= y : x >= y;
    }

    /// <summary>For values a double can hold exactly, the conversion is exact.</summary>
    [Property]
    public bool IntToFloatIsExactBelow2To53(int n) => Ops.BigToDouble(n) == n;
}
