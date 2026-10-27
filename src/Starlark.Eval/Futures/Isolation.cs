using System.Runtime.CompilerServices;
using Starlark.Syntax;

namespace Starlark.Eval.Futures;

/// <summary>
/// Copies values so a task shares no mutable state with the thread that started it. Arguments are deep-copied (the
/// caller's own values are never frozen), aliasing inside one call is preserved, and every copy is frozen. Results are
/// frozen in place, because the task owns them.
/// </summary>
internal static class Isolation
{
    /// <summary>Deep-copies <paramref name="value"/>. <paramref name="seen"/> maps originals to copies so aliasing survives.</summary>
    public static object? Copy(object? value, Dictionary<object, object?> seen, int depth = 0, bool freeze = false)
    {
        if (depth > 200) throw new StarlarkException("Cannot pass a value this deeply nested to a task");
        // Each value copied costs one step and counts against the memory ceiling, so a huge argument cannot be copied for free.
        ExecutionContext.Current?.Charge(1);
        switch (value)
        {
            case null or bool or string or System.Numerics.BigInteger or double or StarBytes or StarUtf16 or StarRange:
                return value;
            case StarList l:
                if (l.Frozen) return value;
                return Remember(l, seen, () =>
                {
                    var copy = new StarList();
                    seen[l] = copy;
                    foreach (var x in l.Items) copy.Add(Copy(x, seen, depth + 1, freeze));
                    if (freeze) copy.Freeze();
                    return copy;
                });
            case StarDict d:
                if (d.Frozen) return value;
                return Remember(d, seen, () =>
                {
                    var copy = new StarDict();
                    seen[d] = copy;
                    foreach (var kv in d.Entries) copy.Set(Copy(kv.Key, seen, depth + 1, freeze), Copy(kv.Value, seen, depth + 1, freeze));
                    if (freeze) copy.Freeze();
                    return copy;
                });
            case StarSet s:
                if (s.Frozen) return value;
                return Remember(s, seen, () =>
                {
                    var copy = new StarSet();
                    seen[s] = copy;
                    foreach (var x in s.Items) copy.Add(Copy(x, seen, depth + 1, freeze));
                    if (freeze) copy.Freeze();
                    return copy;
                });
            case StarTuple t:
                return Remember(t, seen, () => new StarTuple(t.Items.Select(x => Copy(x, seen, depth + 1, freeze)).ToArray()));
            case StarStruct st:
                return Remember(st, seen, () =>
                    new StarStruct(st.Fields.Select(kv => new KeyValuePair<string, object?>(kv.Key, Copy(kv.Value, seen, depth + 1, freeze))).ToList()));
            case StarRecord rec:
                return Remember(rec, seen, () =>
                {
                    var values = rec.Values.Select(x => Copy(x, seen, depth + 1, freeze)).ToArray();
                    return new StarRecord(rec.Type, values);
                });
            case BoundMethod { Receiver: Mutable { Frozen: false } }:
                throw new StarlarkException("A method of a list, dict or set that can still change cannot be passed to a task: pass the values instead");
            case StarEnumValue or StarEnumType or StarRecordType or TypeValue or Builtin or BoundMethod:
                return value;   // immutable, or a reference to immutable machinery
            case StarFunction f:
                return Snapshot(f, seen);
            case StarFuture:
                throw new StarlarkException("A future cannot be passed to a task");
            case IHostValue h:
                return h is IThreadSafeHostValue
                    ? h
                    : throw new StarlarkException($"A host value of type '{h.TypeName}' cannot be passed to a task: it is not marked IThreadSafeHostValue");
            default:
                return value;
        }
    }

    private static object? Remember(object original, Dictionary<object, object?> seen, Func<object?> make)
    {
        if (seen.TryGetValue(original, out var existing)) return existing;
        var made = make();
        seen[original] = made;
        return made;
    }

    /// <summary>Freezes a value the task produced, so it can safely cross back to the thread that collects it.</summary>
    public static object? FreezeResult(object? value)
    {
        Freezer.Freeze(value);
        return value;
    }

    /// <summary>
    /// The names a function body and its parameter defaults can read that are not its own parameters or locals: the
    /// free variables. Used to decide whether the function captures any mutable local state of a live frame.
    /// </summary>
    public static HashSet<string> FreeNames(StarFunction fn)
    {
        var free = new HashSet<string>();

        void VisitExpr(Expr e, HashSet<string> bound)
        {
            switch (e)
            {
                case IdentExpr id:
                    if (!fn.Locals.Contains(id.Name) && !bound.Contains(id.Name)) free.Add(id.Name);
                    return;
                case ListComprehension lc:
                {
                    var inner = new HashSet<string>(bound);
                    Interpreter.CollectTargetNames(lc.First.Variable, inner);
                    foreach (var c in lc.Rest)
                    {
                        if (c is ForClauseClause fc) Interpreter.CollectTargetNames(fc.For.Variable, inner);
                    }
                    VisitExpr(lc.First.Over, bound);   // the first iterable is evaluated outside the comprehension's own names
                    lc.First.Variable.VisitExpr(x => VisitExpr(x, inner));
                    foreach (var c in lc.Rest) c.VisitExpr(x => VisitExpr(x, inner));
                    VisitExpr(lc.Item, inner);
                    return;
                }
                case DictComprehension dc:
                {
                    var inner = new HashSet<string>(bound);
                    Interpreter.CollectTargetNames(dc.First.Variable, inner);
                    foreach (var c in dc.Rest)
                    {
                        if (c is ForClauseClause fc) Interpreter.CollectTargetNames(fc.For.Variable, inner);
                    }
                    VisitExpr(dc.First.Over, bound);
                    dc.First.Variable.VisitExpr(x => VisitExpr(x, inner));
                    foreach (var c in dc.Rest) c.VisitExpr(x => VisitExpr(x, inner));
                    VisitExpr(dc.Key, inner);
                    VisitExpr(dc.Value, inner);
                    return;
                }
                case LambdaExpr lam:
                {
                    var inner = new HashSet<string>(bound);
                    foreach (var p in lam.Parameters)
                    {
                        if (p.Name is not null) inner.Add(p.Name.Name);
                    }
                    foreach (var p in lam.Parameters) p.VisitExpr(x => VisitExpr(x, bound));   // defaults belong to the outer scope
                    VisitExpr(lam.Body, inner);
                    return;
                }
                default:
                    e.VisitExpr(x => VisitExpr(x, bound));
                    return;
            }
        }

        void VisitStmt(Stmt s, HashSet<string> bound)
        {
            if (s is DefStmt d)
            {
                s.VisitOwnExprs(x => VisitExpr(x, bound));   // parameter defaults and annotations
                var inner = new HashSet<string>(bound);
                foreach (var p in d.Parameters)
                {
                    if (p.Name is not null) inner.Add(p.Name.Name);
                }
                LocalCollector.Collect(d.Body, inner);
                VisitStmt(d.Body, inner);
                return;
            }
            s.VisitOwnExprs(x => VisitExpr(x, bound));
            s.VisitStmt(c => VisitStmt(c, bound));
        }

        var none = new HashSet<string>();
        if (fn.LambdaBody is not null) VisitExpr(fn.LambdaBody, none);
        if (fn.Body is not null) VisitStmt(fn.Body, none);
        return free;
    }

    /// <summary>
    /// Whether a function may be run in a task: it must not read variables of a frame that is still executing. A
    /// top-level function of a loaded (frozen) module reads only the module frame, whose globals are frozen.
    /// </summary>
    public static bool CapturesLiveFrame(StarFunction fn)
    {
        var free = FreeNames(fn);
        // Only frames below the module can be live: a module frame is handled by Snapshot (and is stable once sealed).
        for (var frame = fn.Closure; frame is not null && !frame.IsModule; frame = frame.Parent)
        {
            if (free.Any(frame.Locals.Contains)) return true;
        }
        return false;
    }

    /// <summary>
    /// A private copy of the module-level state a top-level function reads: exactly its free variables, each frozen. A
    /// function of a module that is still executing therefore sees the globals as they are now, and nothing the module
    /// binds later. The copy lives in a new, sealed module frame that no other thread writes.
    /// </summary>
    public static StarFunction Snapshot(StarFunction fn, Dictionary<object, object?> seen)
    {
        if (seen.TryGetValue(fn, out var done)) return (StarFunction)done!;
        if (CapturesLiveFrame(fn))
        {
            throw new StarlarkException($"A closure over variables of a function that is still running (`{fn.Name}`) cannot cross to a task: define it at module level or pass the values as arguments");
        }
        var module = fn.ModuleFrame;
        var free = FreeNames(fn);
        var frame = new Frame(null, module.Locals, isModule: true);
        var copy = fn.WithClosure(frame, d => Copy(d, seen, 0, freeze: true));
        seen[fn] = copy;   // before following references, so functions that call each other terminate
        foreach (var name in free)
        {
            if (module.Locals.Contains(name) && module.Vars.TryGetValue(name, out var value))
            {
                frame.Vars[name] = value is StarFunction other ? Snapshot(other, seen) : Copy(value, seen, 0, freeze: true);
            }
        }
        frame.Sealed = true;
        return copy;
    }
}
