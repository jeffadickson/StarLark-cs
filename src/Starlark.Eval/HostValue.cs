using System.Numerics;

namespace Starlark.Eval;

/// <summary>
/// Implemented by a .NET class to make its instances first-class Starlark values with their own type name,
/// attributes, operators, equality, hashing, truthiness, indexing, iteration and length. Every member has a default
/// that behaves like an opaque object, so a class overrides only what it needs. This is the counterpart of the Rust
/// <c>StarlarkValue</c> trait.
/// </summary>
/// <remarks>
/// Register an instance with <see cref="Interpreter.DefineValue"/> or return it from a registered function; values
/// that implement this interface pass through <see cref="Interop.ToStar"/> unchanged.
/// </remarks>
public interface IHostValue
{
    /// <summary>The name returned by <c>type()</c> and used in error messages.</summary>
    string TypeName { get; }

    /// <summary>The text produced by <c>repr()</c> and <c>str()</c>.</summary>
    string Repr() => $"<{TypeName}>";

    /// <summary>The truth value, used by <c>if</c>, <c>not</c>, <c>and</c> and <c>or</c>.</summary>
    bool Truthy() => true;

    /// <summary>Looks up a field or method; return false if there is none.</summary>
    bool TryGetAttribute(string name, out object? value)
    {
        value = null;
        return false;
    }

    /// <summary>The names listed by <c>dir()</c>.</summary>
    IEnumerable<string> Attributes() => [];

    /// <summary>Handles a binary operator; return false for "not supported". <paramref name="reflected"/> is true when this value is the right-hand operand.</summary>
    bool TryBinary(Syntax.BinOp op, object? other, bool reflected, out object? result)
    {
        result = null;
        return false;
    }

    /// <summary>Handles unary minus; return false for "not supported".</summary>
    bool TryNegate(out object? result)
    {
        result = null;
        return false;
    }

    /// <summary>Equality. The default is reference equality.</summary>
    bool EqualsValue(object? other) => ReferenceEquals(this, other);

    /// <summary>Hash code, consistent with <see cref="EqualsValue"/>; throw to make the value unhashable.</summary>
    int HashValue() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    /// <summary>Three-way comparison, or null if the value is not orderable with <paramref name="other"/>.</summary>
    int? CompareTo(object? other) => null;

    /// <summary>The length, or null if <c>len()</c> is not supported.</summary>
    BigInteger? Length() => null;

    /// <summary>Handles <c>x[index]</c>; return false for "not indexable".</summary>
    bool TryIndex(object? index, out object? result)
    {
        result = null;
        return false;
    }

    /// <summary>The elements for <c>for</c> loops and <c>list()</c>, or null if the value is not iterable.</summary>
    IEnumerable<object?>? Iterate() => null;

    /// <summary>Handles <c>item in x</c>; return null if not supported.</summary>
    bool? Contains(object? item) => null;

    /// <summary>Called when the module that holds this value is frozen. Make the value immutable here.</summary>
    void Freeze() { }
}

/// <summary>
/// Marks a host value as safe to use from several threads at once. Only such values may be handed to a task or sit in
/// the universe of an interpreter that runs tasks; the implementer promises immutability or its own synchronisation.
/// </summary>
public interface IThreadSafeHostValue : IHostValue
{
}
