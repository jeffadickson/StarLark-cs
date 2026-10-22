using System.Numerics;
using System.Text;

namespace Starlark.Eval;

/// <summary>
/// A string whose elements are always UTF-16 code units, whatever <see cref="Interpreter.StringElements"/> is set to.
/// This is a Starlark extension (the spec has one string type) for programs that must see the same lengths, indices
/// and slices as a .NET or Java string even when the interpreter counts UTF-8 bytes for plain strings.
/// <para>
/// It is a distinct type: <c>utf16("a") == "a"</c> is false, and <c>+</c>, ordering and dict-key lookup do not mix the
/// two. Conversions are explicit: <c>utf16(s)</c> and <c>str(u)</c>. Methods and <c>in</c> accept either kind of argument.
/// </para>
/// </summary>
public sealed class StarUtf16(string value)
{
    public string Value { get; } = value;

    public override bool Equals(object? obj) => obj is StarUtf16 u && string.Equals(Value, u.Value, StringComparison.Ordinal);
    public override int GetHashCode() => Hashing.JavaStringHash(Value);
    public override string ToString() => Value;
}

/// <summary>Helpers that move values between the <c>string</c> and <c>utf16</c> worlds.</summary>
internal static class Utf16
{
    /// <summary>Plain strings become utf16, recursively through lists and tuples (the results of string methods).</summary>
    public static object? Wrap(object? v)
    {
        switch (v)
        {
            case string s:
                return new StarUtf16(s);
            case StarList l:
                return new StarList(l.Items.Select(Wrap));
            case StarTuple t:
                return new StarTuple(t.Items.Select(Wrap).ToArray());
            case StarElems { Source: string src }:
                return new StarElems(new StarUtf16(src));
            default:
                return v;
        }
    }

    /// <summary>utf16 values become plain strings, recursively through lists and tuples (method arguments).</summary>
    public static object? Unwrap(object? v)
    {
        switch (v)
        {
            case StarUtf16 u:
                return u.Value;
            case StarList l when l.Items.Any(NeedsUnwrap):
                return new StarList(l.Items.Select(Unwrap));
            case StarTuple t when t.Items.Any(NeedsUnwrap):
                return new StarTuple(t.Items.Select(Unwrap).ToArray());
            default:
                return v;
        }
    }

    private static bool NeedsUnwrap(object? v) => v is StarUtf16 or StarList or StarTuple;

    /// <summary>The <c>utf16(x)</c> constructor.</summary>
    public static StarUtf16 Convert(object? x) => x switch
    {
        null => throw new StarlarkException("utf16() cannot convert NoneType"),
        StarUtf16 u => u,
        string s => new StarUtf16(s),
        StarBytes b => new StarUtf16(Encoding.UTF8.GetString(b.Value)),
        _ => throw new StarlarkException($"utf16() cannot convert value of type '{Types.Name(x)}': expected string, utf16 or bytes"),
    };
}
