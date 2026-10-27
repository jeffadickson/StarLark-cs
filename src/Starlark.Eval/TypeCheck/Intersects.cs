namespace Starlark.Eval.TypeCheck;

/// <summary>
/// The static checker's compatibility test. Like the Rust crate's oracle, it asks "could a value of type
/// <c>got</c> possibly satisfy <c>required</c>?" — two types <em>intersect</em> unless they are certainly disjoint.
/// That keeps the checker free of false positives: anything it cannot decide is allowed.
/// </summary>
public static class TypeRelations
{
    /// <summary>Whether some value could have both types.</summary>
    public static bool Intersects(Ty x, Ty y)
    {
        if (x is Ty.AnyTy or Ty.NeverTy || y is Ty.AnyTy or Ty.NeverTy) return true;
        foreach (var a in Options(x))
        {
            foreach (var b in Options(y))
            {
                if (IntersectsOne(a, b) || IntersectsOne(b, a)) return true;
            }
        }
        return false;
    }

    private static IEnumerable<Ty> Options(Ty t) => t is Ty.UnionTy u ? u.Options : [t];

    /// <summary>Directional check: can <paramref name="x"/> be recognised as overlapping <paramref name="y"/>?</summary>
    private static bool IntersectsOne(Ty x, Ty y)
    {
        if (x.Equals(y)) return true;
        switch (x, y)
        {
            case (Ty.AnyTy, _) or (_, Ty.AnyTy):
                return true;

            case (Ty.ListTy a, Ty.ListTy b): return Intersects(a.Item, b.Item);
            case (Ty.SetTy a, Ty.SetTy b): return Intersects(a.Item, b.Item);
            case (Ty.DictTy a, Ty.DictTy b): return Intersects(a.Key, b.Key) && Intersects(a.Value, b.Value);

            case (Ty.TupleOfTy a, Ty.TupleOfTy b): return Intersects(a.Item, b.Item);
            case (Ty.TupleTy a, Ty.TupleTy b):
                return a.Items.Count == b.Items.Count && a.Items.Zip(b.Items).All(p => Intersects(p.First, p.Second));
            case (Ty.TupleTy a, Ty.TupleOfTy b): return a.Items.All(i => Intersects(i, b.Item));

            case (Ty.IterableTy a, Ty.IterableTy b): return Intersects(a.Item, b.Item);
            case (Ty.IterableTy it, _): return IterableItem(y) is { } item && Intersects(it.Item, item);

            case (Ty.CallableTy, Ty.CallableTy): return true;
            case (Ty.CallableTy, Ty.Name n): return n.TypeName is "function" or "builtin_function_or_method";

            case (Ty.Name a, Ty.Name b): return a.TypeName == b.TypeName;
            case (Ty.Custom a, Ty.Custom b): return a.Equals(b);
            case (Ty.RecordTy a, Ty.RecordTy b): return a.Equals(b);
            case (Ty.StructTy a, Ty.StructTy b): return StructsIntersect(a, b);
        }
        return false;
    }

    /// <summary>
    /// Structs intersect when every field they have in common could hold the same value. A struct with unknown
    /// fields overlaps any struct.
    /// </summary>
    private static bool StructsIntersect(Ty.StructTy a, Ty.StructTy b)
    {
        if (a.Fields is null || b.Fields is null) return true;
        foreach (var f in a.Fields)
        {
            if (b.TryGet(f.Key, out var other) && !Intersects(f.Value, other)) return false;
        }
        return true;
    }

    /// <summary>The element type produced by iterating a value of this type, or null if it cannot be iterated.</summary>
    public static Ty? IterableItem(Ty t) => t switch
    {
        Ty.ListTy l => l.Item,
        Ty.SetTy s => s.Item,
        Ty.DictTy d => d.Key,
        Ty.TupleOfTy to => to.Item,
        Ty.TupleTy tt => Ty.Union(tt.Items),
        Ty.IterableTy it => it.Item,
        Ty.Name { TypeName: "range" } => Ty.Int,
        Ty.UnionTy u => IterUnion(u),
        Ty.AnyTy => Ty.Any,
        Ty.NeverTy => Ty.Never,
        Ty.Custom { Identity: StarEnumType } => t,
        Ty.Custom { TypeName: "enum" } => t,
        _ => null,
    };

    private static Ty? IterUnion(Ty.UnionTy u)
    {
        var parts = new List<Ty>();
        foreach (var o in u.Options)
        {
            var i = IterableItem(o);
            if (i is null) return null;
            parts.Add(i);
        }
        return Ty.Union(parts);
    }

    /// <summary>
    /// <c>int</c> and <c>float</c> compare equal numerically, and so do containers of them, so for <c>==</c> and
    /// <c>!=</c> each numeric type is widened to <c>int | float</c>, recursively. <c>bool</c> is deliberately not widened.
    /// </summary>
    public static Ty WidenNumeric(Ty t) => t switch
    {
        Ty.UnionTy u => Ty.Union(u.Options.Select(WidenNumeric)),
        Ty.Name { TypeName: "int" or "float" } => Ty.Union([Ty.Int, Ty.Float]),
        Ty.ListTy l => new Ty.ListTy(WidenNumeric(l.Item)),
        Ty.SetTy s => new Ty.SetTy(WidenNumeric(s.Item)),
        Ty.DictTy d => new Ty.DictTy(WidenNumeric(d.Key), WidenNumeric(d.Value)),
        Ty.TupleOfTy to => new Ty.TupleOfTy(WidenNumeric(to.Item)),
        Ty.TupleTy tt => new Ty.TupleTy(tt.Items.Select(WidenNumeric).ToList()),
        _ => t,
    };
}
