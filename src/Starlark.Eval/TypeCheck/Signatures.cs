namespace Starlark.Eval.TypeCheck;

/// <summary>How a parameter may be supplied.</summary>
public enum ParamMode
{
    /// <summary>Positional only (before a <c>/</c>).</summary>
    PosOnly,

    /// <summary>By position or by name.</summary>
    PosOrName,

    /// <summary>Only by name (after <c>*</c> or <c>*args</c>).</summary>
    NameOnly,

    /// <summary>The <c>*args</c> collector.</summary>
    Args,

    /// <summary>The <c>**kwargs</c> collector.</summary>
    Kwargs,
}

public sealed record SigParam(string Name, ParamMode Mode, Ty Type, bool Required)
{
    /// <summary>A second accepted spelling of <see cref="Name"/> (the C# parameter name of a host function), or null.</summary>
    public string? Alias { get; init; }
}

/// <summary>A function's type: its parameters and its result.</summary>
public sealed record Signature(IReadOnlyList<SigParam> Params, Ty Result, string? Display = null)
{
    public override string ToString() => Display ?? "def(" + string.Join(", ", Params.Select(p => p.Name + ": " + p.Type)) + ") -> " + Result;
}

/// <summary>Type signatures of the predeclared functions, as they appear in the spec.</summary>
public static class BuiltinSignatures
{
    private static SigParam P(string name, Ty type, bool required = true) => new(name, ParamMode.PosOnly, type, required);
    private static SigParam N(string name, Ty type, bool required = false) => new(name, ParamMode.NameOnly, type, required);
    private static SigParam Rest(Ty type) => new("args", ParamMode.Args, type, false);

    private static readonly Ty Num = Ty.Union([Ty.Int, Ty.Float]);
    private static readonly Ty Str = Ty.Str;
    private static readonly Ty Collection = Ty.Union([Ty.List, Ty.Tuple, Ty.Dict, Ty.Set, Ty.Range, Ty.Str, Ty.Utf16, Ty.Bytes]);
    private static readonly Ty Anything = Ty.Any;
    private static readonly Ty IterAny = Ty.Iterable;

    private static Signature S(Ty result, params SigParam[] ps) => new(ps, result);

    public static readonly IReadOnlyDictionary<string, Signature> Functions = new Dictionary<string, Signature>
    {
        ["abs"] = S(Num, P("x", Num)),
        ["parallel_map"] = S(new Ty.ListTy(Ty.Any), P("function", Ty.Callable), P("iterable", IterAny)),
        ["all_of"] = S(new Ty.ListTy(Ty.Any), P("futures", IterAny)),
        ["spawn"] = S(new Ty.Name("future"), P("function", Ty.Callable), Rest(Anything), new SigParam("kwargs", ParamMode.Kwargs, Anything, false)),
        ["all"] = S(Ty.Bool, P("x", IterAny)),
        ["any"] = S(Ty.Bool, P("x", IterAny)),
        ["bool"] = S(Ty.Bool, P("x", Anything, false)),
        ["bytes"] = S(Ty.Bytes, P("x", Anything)),
        ["dir"] = S(new Ty.ListTy(Str), P("x", Anything)),
        // The Java implementation also accepts msg= and attr=..., which the spec notes; accept any keywords.
        ["fail"] = S(Ty.Never, Rest(Anything), new SigParam("kwargs", ParamMode.Kwargs, Anything, false)),
        ["float"] = S(Ty.Float, P("x", Anything, false)),
        ["getattr"] = S(Anything, P("x", Anything), P("name", Str), P("default", Anything, false)),
        ["hasattr"] = S(Ty.Bool, P("x", Anything), P("name", Str)),
        ["hash"] = S(Ty.Int, P("x", Ty.Union([Str, Ty.Utf16, Ty.Bytes]))),
        ["int"] = S(Ty.Int, P("x", Anything, false), new SigParam("base", ParamMode.PosOrName, Ty.Int, false)),
        ["len"] = S(Ty.Int, P("x", Collection)),
        ["print"] = S(Ty.None, Rest(Anything), N("sep", Str)),
        ["repr"] = S(Str, P("x", Anything)),
        ["str"] = S(Str, P("x", Anything)),
        ["utf16"] = S(Ty.Utf16, P("x", Ty.Union([Ty.Str, Ty.Utf16, Ty.Bytes]))),
        ["type"] = S(Str, P("x", Anything)),
    };

    /// <summary>
    /// The result type of builtins whose result depends on their arguments. Returns null to fall back to
    /// <see cref="Functions"/>. Argument types are the checker's inferred types.
    /// </summary>
    public static Ty? Result(string name, IReadOnlyList<Ty> args) => (name, args.Count) switch
    {
        ("list", 0) => new Ty.ListTy(Ty.Never),
        ("list", 1) => new Ty.ListTy(TypeRelations.IterableItem(args[0]) ?? Ty.Any),
        ("tuple", 1) => new Ty.TupleOfTy(TypeRelations.IterableItem(args[0]) ?? Ty.Any),
        ("set", 0) => new Ty.SetTy(Ty.Never),
        ("set", 1) => new Ty.SetTy(TypeRelations.IterableItem(args[0]) ?? Ty.Any),
        ("sorted", 1) => new Ty.ListTy(TypeRelations.IterableItem(args[0]) ?? Ty.Any),
        // The futures extension: a map over a function returns a list of its results; a future's result is unknown here.
        ("parallel_map", 2) => new Ty.ListTy(args[0] is Ty.CallableTy { Result: { } r } ? r : Ty.Any),
        ("all_of", 1) => new Ty.ListTy(Ty.Any),
        ("spawn", >= 1) => new Ty.Name("future"),
        ("reversed", 1) => new Ty.ListTy(TypeRelations.IterableItem(args[0]) ?? Ty.Any),
        ("range", _) => Ty.Range,
        ("dict", 0) => new Ty.DictTy(Ty.Never, Ty.Never),
        ("enumerate", >= 1) => new Ty.ListTy(new Ty.TupleTy([Ty.Int, TypeRelations.IterableItem(args[0]) ?? Ty.Any])),
        ("zip", _) => new Ty.ListTy(new Ty.TupleTy(args.Select(a => TypeRelations.IterableItem(a) ?? Ty.Any).ToList())),
        ("min" or "max", 1) => TypeRelations.IterableItem(args[0]) ?? Ty.Any,
        ("min" or "max", > 1) => Ty.Union(args),
        _ => null,
    };

    /// <summary>Result types of methods on built-in types, keyed by receiver kind and method name.</summary>
    public static Ty? MethodResult(Ty receiver, string method, IReadOnlyList<Ty> args)
    {
        switch (receiver)
        {
            case Ty.Name { TypeName: "str" or "utf16" } own:
                // Results are the same kind of string as the receiver: `utf16` methods return `utf16`.
                return method switch
                {
                    "upper" or "lower" or "capitalize" or "title" or "strip" or "lstrip" or "rstrip" or "replace" or "join"
                        or "format" or "removeprefix" or "removesuffix" => own,
                    "startswith" or "endswith" or "isalpha" or "isdigit" or "isalnum" or "isspace" or "islower" or "isupper"
                        or "istitle" => Ty.Bool,
                    "find" or "rfind" or "index" or "rindex" or "count" => Ty.Int,
                    "split" or "rsplit" or "splitlines" => new Ty.ListTy(own),
                    "partition" or "rpartition" => new Ty.TupleTy([own, own, own]),
                    "elems" => Ty.Iterable,
                    _ => null,
                };
            case Ty.ListTy l:
                return method switch
                {
                    "append" or "extend" or "insert" or "remove" or "clear" => Ty.None,
                    "pop" => l.Item,
                    "index" => Ty.Int,
                    _ => null,
                };
            case Ty.DictTy d:
                return method switch
                {
                    "keys" => new Ty.ListTy(d.Key),
                    "values" => new Ty.ListTy(d.Value),
                    "items" => new Ty.ListTy(new Ty.TupleTy([d.Key, d.Value])),
                    "get" => args.Count > 1 ? Ty.Union([d.Value, args[1]]) : Ty.Union([d.Value, Ty.None]),
                    "pop" or "setdefault" => d.Value,
                    "popitem" => new Ty.TupleTy([d.Key, d.Value]),
                    "update" or "clear" => Ty.None,
                    _ => null,
                };
            case Ty.SetTy s:
                return method switch
                {
                    "add" or "discard" or "remove" or "update" or "clear" => Ty.None,
                    "pop" => s.Item,
                    "union" or "intersection" or "difference" or "symmetric_difference" => s,
                    "issubset" or "issuperset" or "isdisjoint" => Ty.Bool,
                    _ => null,
                };
        }
        return null;
    }
}
