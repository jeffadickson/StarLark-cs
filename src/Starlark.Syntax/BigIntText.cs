using System.Globalization;
using System.Numerics;
using System.Text;

namespace Starlark.Syntax;

/// <summary>
/// The size cap on Starlark ints, and conversion between ints and digit strings in time well below quadratic, so that
/// parsing a literal or <c>int(s)</c> and printing an int stay fast up to the cap. This file is compiled into both the
/// Starlark.Syntax and Starlark.Eval assemblies (as an internal class of each), so the lexer and the evaluator agree.
/// </summary>
internal static class BigIntText
{
    /// <summary>
    /// The largest int, in bits: every int <c>x</c> satisfies <c>-2**MaxBits &lt;= x &lt; 2**MaxBits</c>. That is 1,048,576
    /// bits (2**20, 128 KB, about 315,653 decimal digits). An operation, <c>int()</c> call or literal whose result would be
    /// larger is an error, checked before the result is built, so no int operation can take more than a fraction of a second
    /// or allocate more than a few hundred KB.
    /// </summary>
    public const int MaxBits = 1 << 20;

    /// <summary>
    /// Parses unsigned <paramref name="digits"/> in <paramref name="radix"/> (2 to 36; letters in either case). Returns false
    /// if a character is not a digit of the radix (<paramref name="tooLarge"/> false) or the value would have more than
    /// <see cref="MaxBits"/> bits (<paramref name="tooLarge"/> true). An empty string is not a number.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> digits, int radix, out BigInteger value, out bool tooLarge)
    {
        value = BigInteger.Zero;
        tooLarge = false;
        if (digits.Length == 0) return false;
        // The common case, a short literal: up to 15 digits fit a ulong in any radix up to 16 (16^15 < 2^64); no allocation.
        if (digits.Length <= 15 && radix <= 16)
        {
            ulong small = 0;
            foreach (char c in digits)
            {
                int d = DigitValue(c);
                if (d >= radix) return false;
                small = small * (ulong)radix + (ulong)d;
            }
            value = small;
            return true;
        }
        foreach (char c in digits)
        {
            if (DigitValue(c) >= radix) return false;
        }
        int start = 0;
        while (start < digits.Length - 1 && digits[start] == '0') start++;
        digits = digits[start..];
        // The value is at least radix^(n-1); refuse at once anything that is surely too large, before any arithmetic.
        if ((digits.Length - 1) * Math.Log2(radix) > MaxBits + 1)
        {
            tooLarge = true;
            return false;
        }

        var (k, chunkBase) = Chunk(radix);
        // Chunks of k digits from the least significant end, then combined pairwise: value = sum chunk[i] * chunkBase^i.
        int count = (digits.Length + k - 1) / k;
        var parts = new BigInteger[count];
        int end = digits.Length;
        for (int i = 0; i < count; i++)
        {
            int begin = Math.Max(0, end - k);
            ulong acc = 0;
            for (int j = begin; j < end; j++) acc = acc * (ulong)radix + (ulong)DigitValue(digits[j]);
            parts[i] = acc;
            end = begin;
        }
        BigInteger mult = chunkBase;
        while (count > 1)
        {
            int half = (count + 1) / 2;
            for (int i = 0; i < half; i++)
            {
                parts[i] = 2 * i + 1 < count ? parts[2 * i] + parts[2 * i + 1] * mult : parts[2 * i];
            }
            count = half;
            if (count > 1) mult *= mult;
        }
        value = parts[0];
        if (value.GetBitLength() > MaxBits)
        {
            value = BigInteger.Zero;
            tooLarge = true;
            return false;
        }
        return true;
    }

    /// <summary>
    /// The digits of <paramref name="v"/> in <paramref name="radix"/> (2 to 36), with a leading '-' if it is negative and no
    /// prefix. Divide and conquer, so a large value is not printed in quadratic time as <see cref="BigInteger.ToString()"/> does.
    /// </summary>
    public static string ToString(BigInteger v, int radix, bool upper = false)
    {
        if (v.Sign < 0) return "-" + ToString(-v, radix, upper);
        if (radix == 10 && v.GetBitLength() < 20_000) return v.ToString(CultureInfo.InvariantCulture);
        var (k, chunkBase) = Chunk(radix);
        string alphabet = upper ? "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ" : "0123456789abcdefghijklmnopqrstuvwxyz";
        // pows[i] = chunkBase^(2^i): k * 2^i digits. Build until one exceeds v.
        var pows = new List<BigInteger> { chunkBase };
        while (pows[^1] <= v) pows.Add(pows[^1] * pows[^1]);
        var sb = new StringBuilder();
        Span<char> leaf = stackalloc char[64];

        // Writes x (< pows[level]) using exactly k * 2^level digits if pad, otherwise without leading zeros.
        void Write(BigInteger x, int level, bool pad, Span<char> buf)
        {
            if (level == 0)
            {
                ulong u = (ulong)x;
                int n = 0;
                do
                {
                    buf[n++] = alphabet[(int)(u % (ulong)radix)];
                    u /= (ulong)radix;
                } while (u != 0);
                if (pad) sb.Append('0', k - n);
                for (int i = n - 1; i >= 0; i--) sb.Append(buf[i]);
                return;
            }
            var q = BigInteger.DivRem(x, pows[level - 1], out var rem);
            if (!pad && q.IsZero)
            {
                Write(rem, level - 1, false, buf);
                return;
            }
            Write(q, level - 1, pad, buf);
            Write(rem, level - 1, true, buf);
        }

        Write(v, pows.Count - 1, false, leaf);
        return sb.ToString();
    }

    /// <summary>The number of digits k that fit in one ulong chunk for <paramref name="radix"/>, and radix^k.</summary>
    private static (int K, ulong Base) Chunk(int radix)
    {
        int k = 0;
        ulong b = 1;
        while (b <= ulong.MaxValue / (ulong)radix / (ulong)radix)
        {
            b *= (ulong)radix;
            k++;
        }
        return (k, b);
    }

    /// <summary>The value of a digit character in radices up to 36, or 99 if it is not one.</summary>
    private static int DigitValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'z' => c - 'a' + 10,
        >= 'A' and <= 'Z' => c - 'A' + 10,
        _ => 99,
    };
}
