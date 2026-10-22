using System.Numerics;

namespace Starlark.Eval;

/// <summary>
/// A type used as a value, e.g. the result of <c>list[int]</c> or <c>int | str</c>, or the globals
/// <c>typing.Any</c>. It can be parameterised with <c>[...]</c> and combined with <c>|</c>.
/// </summary>
public sealed class TypeValue(Ty ty) : ICallable
{
    public Ty Ty { get; } = ty;
    public string Name => Ty.ToString();
    public string TypeName => "type";

    /// <summary>Calling a type value (other than a constructor builtin) is an error.</summary>
    public object? Call(Interpreter interp, IReadOnlyList<object?> args, IReadOnlyList<KeyValuePair<string, object?>> kwargs) =>
        throw new StarlarkException($"Type `{Ty}` is not callable");

    public override string ToString() => Ty.ToString();
}

/// <summary>Converts runtime values appearing in type position into <see cref="Ty"/>.</summary>
public static class TypeEval
{
    /// <summary>Convert a value (a type, <c>None</c>, a builtin like <c>int</c>, a record/enum type...) to a type.</summary>
    public static Ty ToTy(object? v)
    {
        switch (v)
        {
            case null: return Ty.None;
            case TypeValue t: return t.Ty;
            case StarRecordType r: return r.Ty;
            case StarEnumType e: return e.Ty;
            case Builtin b:
                var named = ConstructorType(b.Name);
                if (named is not null) return named;
                break;
            case StarList:
                throw new StarlarkException("`[X]` cannot be used as type, perhaps you meant `list[X]`");
            case StarDict:
                throw new StarlarkException("`{A: B}` cannot be used as type, perhaps you meant `dict[A, B]`");
            case string s:
                throw new StarlarkException($"String literals are not allowed in type expressions: `{s}`");
            case StarTuple t:
                return new Ty.TupleTy(t.Items.Select(ToTy).ToList());
        }
        throw new StarlarkException($"Type `{Repr.Of(v)}` is not a valid type annotation");
    }

    /// <summary>The type produced by a builtin constructor used as a type, such as <c>int</c> or <c>list</c>.</summary>
    public static Ty? ConstructorType(string builtin) => builtin switch
    {
        "int" => Ty.Int,
        "float" => Ty.Float,
        "bool" => Ty.Bool,
        "str" => Ty.Str,
        "bytes" => Ty.Bytes,
        "list" => Ty.List,
        "dict" => Ty.Dict,
        "set" => Ty.Set,
        "tuple" => Ty.Tuple,
        "range" => Ty.Range,
        _ => null,
    };

    /// <summary>Evaluate <c>base[args]</c> where <c>base</c> is a type.</summary>
    public static object? Parametrize(object? target, object? index)
    {
        var ty = ToTy(target);
        object?[] args = index is StarTuple t ? t.Items : [index];

        switch (ty)
        {
            case Ty.ListTy { Item: Ty.AnyTy }:
                if (args.Length != 1) throw TooMany();
                return new TypeValue(new Ty.ListTy(ToTy(args[0])));
            case Ty.SetTy { Item: Ty.AnyTy }:
                if (args.Length != 1) throw TooMany();
                return new TypeValue(new Ty.SetTy(ToTy(args[0])));
            case Ty.DictTy { Key: Ty.AnyTy, Value: Ty.AnyTy }:
                if (args.Length != 2) throw new StarlarkException("dict[K, V] takes exactly two type parameters");
                return new TypeValue(new Ty.DictTy(ToTy(args[0]), ToTy(args[1])));
            case Ty.TupleOfTy { Item: Ty.AnyTy }:
                if (args.Length == 2 && args[1] is EllipsisMarker)
                {
                    return new TypeValue(new Ty.TupleOfTy(ToTy(args[0])));
                }
                return new TypeValue(new Ty.TupleTy(args.Select(ToTy).ToList()));
            case Ty.IterableTy { Item: Ty.AnyTy }:
                if (args.Length != 1) throw TooMany();
                return new TypeValue(new Ty.IterableTy(ToTy(args[0])));
            case Ty.CallableTy { Params: null, Result: null }:
                if (args.Length != 2) throw new StarlarkException("typing.Callable[[params], result] takes two parameters");
                if (args[0] is not StarList ps && args[0] is not EllipsisMarker)
                {
                    throw new StarlarkException("The first parameter of typing.Callable must be a list of parameter types or `...`");
                }
                return new TypeValue(new Ty.CallableTy(
                    args[0] is StarList l ? l.Items.Select(ToTy).ToList() : null, ToTy(args[1])));
        }
        if (args.Length > 0 && ty is Ty.ListTy or Ty.SetTy)
        {
            throw new StarlarkException("[,] can only be applied to dict or tuple functions in type expression");
        }
        throw new StarlarkException($"Type `{ty}` does not support type parameters: `[...]` is not supported");
    }

    private static StarlarkException TooMany() =>
        new("[,] can only be applied to dict or tuple functions in type expression");

    /// <summary><c>A | B</c> where at least one side is a type.</summary>
    public static object? Union(object? a, object? b) =>
        new TypeValue(Ty.Union([ToTy(a), ToTy(b)]));

    /// <summary>Whether a value is usable as a type operand of <c>|</c>, <c>[]</c> or <c>isinstance</c>.</summary>
    public static bool IsTypeLike(object? v) =>
        v is TypeValue or StarRecordType or StarEnumType || v is Builtin b && ConstructorType(b.Name) is not null;
}

/// <summary>The value of the literal <c>...</c> when it appears in <c>tuple[int, ...]</c>.</summary>
public sealed class EllipsisMarker
{
    public static readonly EllipsisMarker Instance = new();
    public override string ToString() => "...";
}

/// <summary>The <c>typing</c> module.</summary>
public static class TypingModule
{
    public static StarStruct Create() => new(
    [
        new("Any", new TypeValue(Ty.Any)),
        new("Never", new TypeValue(Ty.Never)),
        new("Callable", new TypeValue(Ty.Callable)),
        new("Iterable", new TypeValue(Ty.Iterable)),
    ]);
}
