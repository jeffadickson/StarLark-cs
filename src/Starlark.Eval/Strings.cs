using System.Text;

namespace Starlark.Eval;

/// <summary>
/// What a string "element" is. The spec lets an implementation choose; this selects the unit used by
/// <c>len</c>, indexing, slicing and every offset taken or returned by string methods.
/// </summary>
public enum StringElements
{
    /// <summary>UTF-16 code units, as in the Java implementation. The default; maps directly onto .NET strings.</summary>
    Utf16,

    /// <summary>UTF-8 bytes, as in the Go and Rust implementations. Slicing inside a character is an error.</summary>
    Utf8,
}

/// <summary>Element-aware string primitives. The mode is ambient per thread, set while an interpreter runs.</summary>
internal static class Strings
{
    private static readonly UTF8Encoding Strict = new(false, throwOnInvalidBytes: true);

    [ThreadStatic] private static StringElements _mode;

    public static StringElements Mode => _mode;

    /// <summary>Make <paramref name="mode"/> current until the returned scope is disposed.</summary>
    public static Scope Enter(StringElements mode)
    {
        var scope = new Scope(_mode);
        _mode = mode;
        return scope;
    }

    internal readonly struct Scope(StringElements previous) : IDisposable
    {
        public void Dispose() => _mode = previous;
    }

    private static bool IsAscii(string s)
    {
        foreach (char c in s)
        {
            if (c >= 0x80) return false;
        }
        return true;
    }

    public static int Len(string s) =>
        _mode == StringElements.Utf16 || IsAscii(s) ? s.Length : Encoding.UTF8.GetByteCount(s);

    /// <summary>The elements in <c>s[start:end]</c> (element offsets already clamped to the string).</summary>
    public static string Sub(string s, int start, int end)
    {
        if (start >= end) return "";
        if (_mode == StringElements.Utf16 || IsAscii(s)) return s[start..end];
        var bytes = Encoding.UTF8.GetBytes(s);
        return DecodeStrict(bytes.AsSpan(start, end - start));
    }

    /// <summary>
    /// Search for <paramref name="sub"/> in elements <c>[start, end)</c> and return its element offset from the start
    /// of <paramref name="s"/>, or -1. In UTF-8 mode this is a byte search, so a window that begins or ends inside a
    /// character is legal (and simply cannot match across the cut), as in Go and Rust.
    /// </summary>
    public static int Search(string s, string sub, int start, int end, bool last, out int count)
    {
        count = 0;
        if (start > end) return -1;
        if (_mode == StringElements.Utf16 || IsAscii(s))
        {
            string region = s[start..end];
            int i = last ? region.LastIndexOf(sub, StringComparison.Ordinal) : region.IndexOf(sub, StringComparison.Ordinal);
            return i < 0 ? -1 : start + i;
        }
        var hay = Encoding.UTF8.GetBytes(s).AsSpan(start, end - start);
        var needle = Encoding.UTF8.GetBytes(sub);
        int j = last ? hay.LastIndexOf(needle) : hay.IndexOf(needle);
        return j < 0 ? -1 : start + j;
    }

    /// <summary>Count non-overlapping occurrences of <paramref name="sub"/> in elements <c>[start, end)</c>.</summary>
    public static int CountIn(string s, string sub, int start, int end)
    {
        if (start > end) return 0;
        if (sub.Length == 0) return end - start + 1;
        int n = 0;
        if (_mode == StringElements.Utf16 || IsAscii(s))
        {
            string region = s[start..end];
            int pos = 0;
            while ((pos = region.IndexOf(sub, pos, StringComparison.Ordinal)) >= 0)
            {
                n++;
                pos += sub.Length;
            }
            return n;
        }
        var hay = Encoding.UTF8.GetBytes(s).AsSpan(start, end - start);
        var needle = Encoding.UTF8.GetBytes(sub);
        while (true)
        {
            int j = hay.IndexOf(needle);
            if (j < 0) return n;
            n++;
            hay = hay[(j + needle.Length)..];
        }
    }

    /// <summary>Whether elements <c>[start, end)</c> begin (or end) with <paramref name="affix"/>.</summary>
    public static bool HasAffix(string s, string affix, int start, int end, bool prefix)
    {
        if (start > end) return false;
        if (_mode == StringElements.Utf16 || IsAscii(s))
        {
            string region = s[start..end];
            return prefix ? region.StartsWith(affix, StringComparison.Ordinal) : region.EndsWith(affix, StringComparison.Ordinal);
        }
        var hay = Encoding.UTF8.GetBytes(s).AsSpan(start, end - start);
        var needle = Encoding.UTF8.GetBytes(affix);
        return prefix ? hay.StartsWith(needle) : hay.EndsWith(needle);
    }

    /// <summary>The element at index <paramref name="i"/> (already normalised to be in range), as a 1-element string.</summary>
    public static string At(string s, int i)
    {
        if (_mode == StringElements.Utf16 || IsAscii(s)) return s[i].ToString();
        var b = Encoding.UTF8.GetBytes(s)[i];
        if (b < 0x80) return ((char)b).ToString();
        throw new StarlarkException(
            "String index does not fall on a UTF-8 character boundary: a single non-ASCII byte is not valid text");
    }

    /// <summary>Pick the elements at the given indices, in order.</summary>
    public static string Pick(string s, IReadOnlyList<int> indices)
    {
        if (_mode == StringElements.Utf16 || IsAscii(s))
        {
            return new string(indices.Select(i => s[i]).ToArray());
        }
        var bytes = Encoding.UTF8.GetBytes(s);
        return DecodeStrict(indices.Select(i => bytes[i]).ToArray());
    }

    private static string DecodeStrict(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return Strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new StarlarkException("String slice does not fall on a UTF-8 character boundary");
        }
    }
}
