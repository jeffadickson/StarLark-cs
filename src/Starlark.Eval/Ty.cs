using System.Numerics;
using System.Text;

namespace Starlark.Eval;

/// <summary>
/// A Starlark type, as used by annotations (<c>x: list[int]</c>), <c>isinstance</c>, and the static checker.
/// Types are immutable values; build them with the static members.
/// </summary>
public abstract record Ty
{
    // ---------------------------------------------------------------- the lattice

    /// <summary><c>typing.Any</c>: matches every value.</summary>
    public sealed record AnyTy : Ty;

    /// <summary><c>typing.Never</c>: no value matches (the type of <c>fail()</c>).</summary>
    public sealed record NeverTy : Ty;

    /// <summary>A named built-in type: <c>int</c>, <c>str</c>, <c>bool</c>, <c>float</c>, <c>NoneType</c>, <c>bytes</c>, <c>range</c>, ...</summary>
    public sealed record Name(string TypeName) : Ty;

    /// <summary><c>list[T]</c>.</summary>
    public sealed record ListTy(Ty Item) : Ty;

    /// <summary><c>set[T]</c>.</summary>
    public sealed record SetTy(Ty Item) : Ty;

    /// <summary><c>dict[K, V]</c>.</summary>
    public sealed record DictTy(Ty Key, Ty Value) : Ty;

    /// <summary><c>tuple[A, B, C]</c>: a fixed arity.</summary>
    public sealed record TupleTy(IReadOnlyList<Ty> Items) : Ty
    {
        public bool Equals(TupleTy? o) => o is not null && Items.SequenceEqual(o.Items);
        public override int GetHashCode() => Items.Aggregate(7, (h, x) => HashCode.Combine(h, x));
    }

    /// <summary><c>tuple[T, ...]</c>: any arity.</summary>
    public sealed record TupleOfTy(Ty Item) : Ty;

    /// <summary><c>A | B</c>.</summary>
    public sealed record UnionTy(IReadOnlyList<Ty> Options) : Ty
    {
        public bool Equals(UnionTy? o) => o is not null && Options.SequenceEqual(o.Options);
        public override int GetHashCode() => Options.Aggregate(11, (h, x) => HashCode.Combine(h, x));
    }

    /// <summary><c>typing.Callable</c>, optionally with a parameter and result description.</summary>
    public sealed record CallableTy(IReadOnlyList<Ty>? Params, Ty? Result) : Ty
    {
        public bool Equals(CallableTy? o) =>
            o is not null && Result == o.Result && (Params is null ? o.Params is null : o.Params is not null && Params.SequenceEqual(o.Params));
        public override int GetHashCode() => HashCode.Combine(Result, Params?.Count);
    }

    /// <summary><c>typing.Iterable</c> / <c>typing.Iterable[T]</c>.</summary>
    public sealed record IterableTy(Ty Item) : Ty;

    /// <summary>A user-defined type (<c>record</c>, <c>enum</c>, <c>struct</c>), identified by reference.</summary>
    public sealed record Custom(string TypeName, object Identity) : Ty
    {
        public bool Equals(Custom? o) => o is not null && ReferenceEquals(Identity, o.Identity);
        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Identity);
    }

    // ---------------------------------------------------------------- common instances

    public static readonly Ty Any = new AnyTy();
    public static readonly Ty Never = new NeverTy();
    public static readonly Ty None = new Name("NoneType");
    public static readonly Ty Bool = new Name("bool");
    public static readonly Ty Int = new Name("int");
    public static readonly Ty Float = new Name("float");
    public static readonly Ty Str = new Name("str");
    public static readonly Ty Bytes = new Name("bytes");
    public static readonly Ty Range = new Name("range");
    public static readonly Ty List = new ListTy(Any);
    public static readonly Ty Dict = new DictTy(Any, Any);
    public static readonly Ty Set = new SetTy(Any);
    public static readonly Ty Tuple = new TupleOfTy(Any);
    public static readonly Ty Callable = new CallableTy(null, null);
    public static readonly Ty Iterable = new IterableTy(Any);

    /// <summary>The type name as Starlark's <c>type()</c> reports it, for named types.</summary>
    public static string StarlarkName(string tyName) => tyName == "str" ? "string" : tyName;

    // ---------------------------------------------------------------- construction

    /// <summary>Make a union, flattening nested unions, removing duplicates and absorbing <c>Never</c>/<c>Any</c>.</summary>
    public static Ty Union(IEnumerable<Ty> options)
    {
        var flat = new List<Ty>();
        foreach (var o in options)
        {
            switch (o)
            {
                case NeverTy:
                    break;
                case AnyTy:
                    return Any;
                case UnionTy u:
                    foreach (var x in u.Options) if (!flat.Contains(x)) flat.Add(x);
                    break;
                default:
                    if (!flat.Contains(o)) flat.Add(o);
                    break;
            }
        }
        return flat.Count switch
        {
            0 => Never,
            1 => flat[0],
            _ => new UnionTy(flat),
        };
    }

    public static Ty Of(Ty a, Ty b) => Union([a, b]);

    // ---------------------------------------------------------------- display

    public override string ToString() => Display(this);

    private static string Display(Ty t) => t switch
    {
        AnyTy => "typing.Any",
        NeverTy => "typing.Never",
        Name n => n.TypeName,
        ListTy { Item: AnyTy } => "list",
        ListTy l => $"list[{Display(l.Item)}]",
        SetTy { Item: AnyTy } => "set",
        SetTy s => $"set[{Display(s.Item)}]",
        DictTy { Key: AnyTy, Value: AnyTy } => "dict",
        DictTy d => $"dict[{Display(d.Key)}, {Display(d.Value)}]",
        TupleOfTy { Item: AnyTy } => "tuple",
        TupleOfTy t2 => $"tuple[{Display(t2.Item)}, ...]",
        TupleTy { Items.Count: 0 } => "()",
        TupleTy tt => "(" + string.Join(", ", tt.Items.Select(Display)) + ")",
        UnionTy u => string.Join(" | ", u.Options.Select(Display)),
        CallableTy { Params: null, Result: null } => "typing.Callable",
        CallableTy c => $"typing.Callable[[{string.Join(", ", (c.Params ?? []).Select(Display))}], {Display(c.Result ?? Any)}]",
        IterableTy { Item: AnyTy } => "typing.Iterable",
        IterableTy i => $"typing.Iterable[{Display(i.Item)}]",
        Custom c => c.TypeName,
        _ => t.GetType().Name,
    };

    // ---------------------------------------------------------------- runtime matching

    /// <summary>Whether <paramref name="v"/> is a member of this type. Containers are checked element by element.</summary>
    public bool Matches(object? v) => this switch
    {
        AnyTy => true,
        NeverTy => false,
        UnionTy u => u.Options.Any(o => o.Matches(v)),
        Name n => MatchesName(n.TypeName, v),
        ListTy l => v is StarList list && (l.Item is AnyTy || list.Items.All(l.Item.Matches)),
        SetTy s => v is StarSet set && (s.Item is AnyTy || set.Items.All(s.Item.Matches)),
        DictTy d => v is StarDict dict && (d is { Key: AnyTy, Value: AnyTy } ||
            dict.Entries.All(kv => d.Key.Matches(kv.Key) && d.Value.Matches(kv.Value))),
        TupleOfTy t => v is StarTuple tup && (t.Item is AnyTy || tup.Items.All(t.Item.Matches)),
        TupleTy tt => v is StarTuple tup && tup.Items.Length == tt.Items.Count &&
            tup.Items.Zip(tt.Items).All(p => p.Second.Matches(p.First)),
        CallableTy => v is ICallable,
        IterableTy i => IsIterable(v) && (i.Item is AnyTy || SafeElements(v).All(i.Item.Matches)),
        Custom c => c.Identity is IUserType ut && ut.IsInstance(v),
        _ => false,
    };

    private static bool IsIterable(object? v) => v is StarList or StarTuple or StarDict or StarSet or StarRange or StarElems;

    private static IEnumerable<object?> SafeElements(object? v) => v switch
    {
        StarList l => l.Items,
        StarTuple t => t.Items,
        StarDict d => d.Keys,
        StarSet s => s.Items,
        StarElems e => e.Items,
        StarRange r => Enumerable.Range(0, (int)BigInteger.Min(r.Count, 1_000_000)).Select(i => (object?)r[i]),
        _ => [],
    };

    private static bool MatchesName(string name, object? v) => name switch
    {
        "NoneType" => v is null,
        "bool" => v is bool,
        "int" => v is BigInteger,
        "float" => v is double,
        "str" => v is string,
        "bytes" => v is StarBytes,
        "range" => v is StarRange,
        "function" => v is StarFunction,
        _ => Types.Name(v) == name,
    };
}

/// <summary>Implemented by user-defined types (records, enums) so they can answer <c>isinstance</c>.</summary>
public interface IUserType
{
    bool IsInstance(object? v);
}
