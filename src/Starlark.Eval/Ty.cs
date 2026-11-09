using System.Numerics;
using System.Text;

namespace Starlark.Eval;

/// <summary>
/// A Starlark type, as used by annotations (<c>x: list[int]</c>), <c>isinstance</c>, and the static checker.
/// Types are immutable values; build them with the static members.
/// </summary>
public abstract record Ty
{
    // ---------------------------------------------------------------- shape

    // A derived record's properties are initialised before this constructor runs, so its children are already measured
    // and measuring this node costs O(children), never a walk of the whole type.
    // Records are not rebuilt with `with` anywhere (that would copy a stale shape), and a structural hash is cached the same
    // way, so equality can reject most unequal types in O(1) and hashing never walks the type.
    protected Ty() => (Depth, Size, _hash) = Measure(this);

    protected Ty(Ty original)
    {
        Depth = original.Depth;
        Size = original.Size;
        _hash = original._hash;
    }

    private readonly int _hash;

    /// <summary>How many levels of type parameters this type has: 0 for <c>int</c>, 1 for <c>list[int]</c>.</summary>
    public int Depth { get; }

    /// <summary>The number of nodes in this type written out as a tree (saturating at <see cref="int.MaxValue"/>).</summary>
    public int Size { get; }

    /// <summary>The deepest type a script may build at runtime (with <c>list[...]</c>, <c>|</c> and so on).</summary>
    public const int MaxRuntimeDepth = 1024;

    /// <summary>The largest type, in nodes written out as a tree, a script may build at runtime.</summary>
    public const int MaxRuntimeSize = 1 << 20;

    /// <summary>Throws a <see cref="StarlarkException"/> if a type built by a script is too deep or too large.</summary>
    internal static Ty CheckRuntime(Ty t)
    {
        if (t.Depth > MaxRuntimeDepth) throw new StarlarkException($"Type is nested too deeply: more than {MaxRuntimeDepth} levels");
        if (t.Size > MaxRuntimeSize) throw new StarlarkException($"Type is too large: more than {MaxRuntimeSize} parts");
        return t;
    }

    private static (int, int, int) Measure(Ty t)
    {
        int depth = 0;
        long size = 1;
        var h = new HashCode();
        h.Add(t.GetType());
        void Child(Ty? c)
        {
            if (c is null)
            {
                h.Add(0);
                return;
            }
            depth = Math.Max(depth, c.Depth + 1);
            size += c.Size;
            h.Add(c._hash);
        }
        switch (t)
        {
            case Name n: h.Add(n.TypeName); break;
            case ListTy l: Child(l.Item); break;
            case SetTy s: Child(s.Item); break;
            case DictTy d: Child(d.Key); Child(d.Value); break;
            case TupleTy tt: foreach (var x in tt.Items) Child(x); break;
            case TupleOfTy to: Child(to.Item); break;
            case UnionTy u: foreach (var x in u.Options) Child(x); break;
            case CallableTy c:
                h.Add(c.Params is null ? -1 : c.Params.Count);
                foreach (var x in c.Params ?? []) Child(x);
                Child(c.Result);
                break;
            case IterableTy i: Child(i.Item); break;
            case Custom c: h.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(c.Identity)); break;
            case StructTy st:
                h.Add(st.Fields is null ? -1 : st.Fields.Count);
                foreach (var f in st.Fields ?? [])
                {
                    h.Add(f.Key);
                    Child(f.Value);
                }
                break;
            case RecordTy r:
                // Identified by reference: the fields count towards the shape but not the hash.
                h = new HashCode();
                h.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(r.Identity));
                foreach (var f in r.Fields)
                {
                    depth = Math.Max(depth, f.Value.Depth + 1);
                    size += f.Value.Size;
                }
                break;
        }
        return (depth, (int)Math.Min(size, int.MaxValue), h.ToHashCode());
    }

    // Depth, Size and the cached hash describe the value; they are not part of it. Equal types have equal hashes, so a
    // hash mismatch settles most comparisons at once; the structural comparison the derived records add is guarded.
    public virtual bool Equals(Ty? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null || EqualityContract != other.EqualityContract || _hash != other._hash) return false;
        StackGuard.Check();
        return true;
    }

    public override int GetHashCode() => _hash;

    // ---------------------------------------------------------------- the lattice

    /// <summary><c>typing.Any</c>: matches every value.</summary>
    public sealed record AnyTy : Ty
    {
        public override int GetHashCode() => base.GetHashCode();
    }

    /// <summary><c>typing.Never</c>: no value matches (the type of <c>fail()</c>).</summary>
    public sealed record NeverTy : Ty
    {
        public override int GetHashCode() => base.GetHashCode();
    }

    /// <summary>A named built-in type: <c>int</c>, <c>str</c>, <c>bool</c>, <c>float</c>, <c>NoneType</c>, <c>bytes</c>, <c>range</c>, ...</summary>
    public sealed record Name(string TypeName) : Ty
    {
        public override int GetHashCode() => base.GetHashCode();
    }

    /// <summary><c>list[T]</c>.</summary>
    public sealed record ListTy(Ty Item) : Ty
    {
        public override int GetHashCode() => base.GetHashCode();
    }

    /// <summary><c>set[T]</c>.</summary>
    public sealed record SetTy(Ty Item) : Ty
    {
        public override int GetHashCode() => base.GetHashCode();
    }

    /// <summary><c>dict[K, V]</c>.</summary>
    public sealed record DictTy(Ty Key, Ty Value) : Ty
    {
        public override int GetHashCode() => base.GetHashCode();
    }

    /// <summary><c>tuple[A, B, C]</c>: a fixed arity.</summary>
    public sealed record TupleTy(IReadOnlyList<Ty> Items) : Ty
    {
        public bool Equals(TupleTy? o) => base.Equals(o) && Items.SequenceEqual(o!.Items);
        public override int GetHashCode() => base.GetHashCode();
    }

    /// <summary><c>tuple[T, ...]</c>: any arity.</summary>
    public sealed record TupleOfTy(Ty Item) : Ty
    {
        public override int GetHashCode() => base.GetHashCode();
    }

    /// <summary><c>A | B</c>.</summary>
    public sealed record UnionTy(IReadOnlyList<Ty> Options) : Ty
    {
        public bool Equals(UnionTy? o) => base.Equals(o) && Options.SequenceEqual(o!.Options);
        public override int GetHashCode() => base.GetHashCode();
    }

    /// <summary><c>typing.Callable</c>, optionally with a parameter and result description.</summary>
    public sealed record CallableTy(IReadOnlyList<Ty>? Params, Ty? Result) : Ty
    {
        public bool Equals(CallableTy? o) =>
            base.Equals(o) && Result == o!.Result && (Params is null ? o.Params is null : o.Params is not null && Params.SequenceEqual(o.Params));
        public override int GetHashCode() => base.GetHashCode();
    }

    /// <summary><c>typing.Iterable</c> / <c>typing.Iterable[T]</c>.</summary>
    public sealed record IterableTy(Ty Item) : Ty
    {
        public override int GetHashCode() => base.GetHashCode();
    }

    /// <summary>A user-defined type (<c>record</c>, <c>enum</c>, <c>struct</c>), identified by reference.</summary>
    public sealed record Custom(string TypeName, object Identity) : Ty
    {
        public bool Equals(Custom? o) => o is not null && ReferenceEquals(Identity, o.Identity);
        public override int GetHashCode() => base.GetHashCode();
    }

    /// <summary>
    /// What the static checker knows about a <c>struct(...)</c> value: its field names and their types.
    /// A struct with no fields known (<see cref="Fields"/> is null) is just "some struct".
    /// </summary>
    public sealed record StructTy(IReadOnlyList<KeyValuePair<string, Ty>>? Fields) : Ty
    {
        public bool Equals(StructTy? o) =>
            base.Equals(o) && (Fields is null ? o!.Fields is null : o!.Fields is not null && Fields.SequenceEqual(o.Fields));
        public override int GetHashCode() => base.GetHashCode();

        public bool TryGet(string name, out Ty ty)
        {
            foreach (var kv in Fields ?? [])
            {
                if (kv.Key == name)
                {
                    ty = kv.Value;
                    return true;
                }
            }
            ty = Any;
            return false;
        }
    }

    /// <summary>The type of a record's instances: its name, fields and their types. Identified by reference.</summary>
    public sealed record RecordTy(string TypeName, object Identity, IReadOnlyList<KeyValuePair<string, Ty>> Fields) : Ty
    {
        public bool Equals(RecordTy? o) => o is not null && ReferenceEquals(Identity, o.Identity);
        public override int GetHashCode() => base.GetHashCode();

        public bool TryGet(string name, out Ty ty)
        {
            foreach (var kv in Fields)
            {
                if (kv.Key == name)
                {
                    ty = kv.Value;
                    return true;
                }
            }
            ty = Any;
            return false;
        }
    }

    // ---------------------------------------------------------------- common instances

    public static readonly Ty Any = new AnyTy();
    public static readonly Ty Never = new NeverTy();
    public static readonly Ty None = new Name("NoneType");
    public static readonly Ty Bool = new Name("bool");
    public static readonly Ty Int = new Name("int");
    public static readonly Ty Float = new Name("float");
    public static readonly Ty Str = new Name("str");
    public static readonly Ty Utf16 = new Name("utf16");
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

    public sealed override string ToString() => Display(this);

    private static string Display(Ty t)
    {
        StackGuard.Check();
        return DisplayNode(t);
    }

    private static string DisplayNode(Ty t) => t switch
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
        StructTy { Fields: null } => "struct",
        StructTy s => "struct(" + string.Join(", ", s.Fields!.Select(f => f.Key + " = " + Display(f.Value))) + ")",
        RecordTy r => r.TypeName,
        _ => t.GetType().Name,
    };

    // ---------------------------------------------------------------- runtime matching

    /// <summary>Whether <paramref name="v"/> is a member of this type. Containers are checked element by element.</summary>
    /// <remarks>
    /// Every container visited counts as a step, so step limits, the deadline and cancellation stop a long check. Each
    /// (container, type) pair is checked once: a value that shares subvalues (<c>x = (x, x)</c>) is checked in time linear
    /// in its distinct parts, and a cyclic list matches as far as its finite structure goes. Pairs already under check are
    /// assumed to match, which is the coinductive reading of a cyclic value.
    /// </remarks>
    public bool Matches(object? v) => Matches(v, null);

    /// <summary>
    /// The results of (container, type) checks in one call of <see cref="Matches(object?)"/>. A pair still being checked
    /// has no result yet and is assumed to match. When a check fails, every result recorded while it ran is dropped, since
    /// those may rest on the assumption that it matched.
    /// </summary>
    private sealed class Memo
    {
        public readonly Dictionary<(object, Ty), bool?> Results = new(PairComparer.Instance);
        public readonly List<(object, Ty)> Order = [];
    }

    private bool Matches(object? v, Memo? seen)
    {
        switch (this)
        {
            case AnyTy: return true;
            case NeverTy: return false;
            case Name n: return MatchesName(n.TypeName, v);
            case CallableTy: return v is ICallable;
            case Custom c: return c.Identity is IUserType ut && ut.IsInstance(v);
            case RecordTy r: return v is StarRecord rec && ReferenceEquals(rec.Type, r.Identity);
        }
        StackGuard.Check();
        if (this is UnionTy u)
        {
            foreach (var o in u.Options) if (o.Matches(v, seen)) return true;
            return false;
        }
        if (v is not (StarList or StarSet or StarDict or StarTuple or StarStruct or StarRange or StarElems)) return false;
        ExecutionContext.Current?.Step();
        seen ??= new Memo();
        var key = ((object)v, this);
        if (seen.Results.TryGetValue(key, out var known)) return known ?? true;
        int mark = seen.Order.Count;
        seen.Results[key] = null;
        seen.Order.Add(key);
        bool ok = MatchesContainer(v, seen);
        if (ok) seen.Results[key] = true;
        else
        {
            // A recorded failure is a failure whatever was assumed, so only successes are dropped.
            int keep = mark + 1;
            for (int i = mark + 1; i < seen.Order.Count; i++)
            {
                var k = seen.Order[i];
                if (seen.Results[k] == false) seen.Order[keep++] = k;
                else seen.Results.Remove(k);
            }
            seen.Order.RemoveRange(keep, seen.Order.Count - keep);
            seen.Results[key] = false;
        }
        return ok;
    }

    private bool MatchesContainer(object v, Memo seen)
    {
        switch (this)
        {
            case ListTy l: return v is StarList list && (l.Item is AnyTy || All(list.Items, l.Item, seen));
            case SetTy s: return v is StarSet set && (s.Item is AnyTy || All(set.Items, s.Item, seen));
            case DictTy d:
                if (v is not StarDict dict) return false;
                if (d is { Key: AnyTy, Value: AnyTy }) return true;
                foreach (var kv in dict.Entries)
                {
                    if (!d.Key.Matches(kv.Key, seen) || !d.Value.Matches(kv.Value, seen)) return false;
                }
                return true;
            case TupleOfTy t: return v is StarTuple tup && (t.Item is AnyTy || All(tup.Items, t.Item, seen));
            case TupleTy tt:
                if (v is not StarTuple tup2 || tup2.Items.Length != tt.Items.Count) return false;
                for (int i = 0; i < tt.Items.Count; i++) if (!tt.Items[i].Matches(tup2.Items[i], seen)) return false;
                return true;
            case IterableTy it: return IsIterable(v) && (it.Item is AnyTy || All(SafeElements(v), it.Item, seen));
            case StructTy st:
                if (v is not StarStruct sv) return false;
                if (st.Fields is null) return true;
                foreach (var f in st.Fields) if (!sv.TryGet(f.Key, out var fv) || !f.Value.Matches(fv, seen)) return false;
                return true;
        }
        return false;
    }

    private static bool All(IEnumerable<object?> items, Ty item, Memo seen)
    {
        var ctx = ExecutionContext.Current;
        foreach (var x in items)
        {
            ctx?.Step();
            if (!item.Matches(x, seen)) return false;
        }
        return true;
    }

    /// <summary>Compares (value, type) pairs by reference on both sides.</summary>
    private sealed class PairComparer : IEqualityComparer<(object, Ty)>
    {
        public static readonly PairComparer Instance = new();
        public bool Equals((object, Ty) a, (object, Ty) b) => ReferenceEquals(a.Item1, b.Item1) && ReferenceEquals(a.Item2, b.Item2);
        public int GetHashCode((object, Ty) p) => HashCode.Combine(
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(p.Item1), System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(p.Item2));
    }

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
        "utf16" => v is StarUtf16,
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
