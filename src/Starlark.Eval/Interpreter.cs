using System.Numerics;
using Starlark.Syntax;

namespace Starlark.Eval;

/// <summary>A lexical scope: a module, a function call, or a comprehension.</summary>
internal sealed class Frame(Frame? parent, HashSet<string> locals, bool isModule)
{
    public Frame? Parent { get; } = parent;

    /// <summary>Every name bound in this block (known statically). A name in here but not in <see cref="Vars"/> is unassigned.</summary>
    public HashSet<string> Locals { get; } = locals;

    public bool IsModule { get; } = isModule;
    public Dictionary<string, object?> Vars { get; } = [];
    public object? ReturnValue { get; set; }
}

/// <summary>An executed module: its global variables, frozen.</summary>
public sealed class Module(Dictionary<string, object?> globals)
{
    public IReadOnlyDictionary<string, object?> Globals => globals;
    public bool TryGet(string name, out object? value) => globals.TryGetValue(name, out value);
}

/// <summary>A function defined with <c>def</c> or <c>lambda</c>.</summary>
public sealed class StarFunction : ICallable
{
    private bool _frozen;

    internal StarFunction(string name, object decl, DefParams parms, Stmt? body, Expr? lambdaBody, object?[] defaults,
        Frame closure, HashSet<string> locals, Interpreter owner, CodeMap codemap)
    {
        Name = name;
        Decl = decl;
        Params = parms;
        Body = body;
        LambdaBody = lambdaBody;
        Defaults = defaults;
        Closure = closure;
        Locals = locals;
        Owner = owner;
        CodeMap = codemap;
    }

    public string Name { get; }
    public string TypeName => "function";

    /// <summary>The syntax node this function was created from; used to detect recursion.</summary>
    internal object Decl { get; }

    internal DefParams Params { get; }

    /// <summary>Annotation per parameter (null where unannotated); null if the function has no annotations at all.</summary>
    internal Ty?[]? ParamTypes { get; init; }

    internal Ty? ReturnType { get; init; }

    /// <summary>The signature as <c>def(x: int, y) -&gt; str</c>, for diagnostics.</summary>
    public string Signature =>
        "def(" + string.Join(", ", Params.Params.Select((p, i) =>
            p.Ident.Name + (ParamTypes?[i] is { } t ? ": " + t : ""))) + ")" + (ReturnType is { } r ? " -> " + r : "");

    internal Stmt? Body { get; }
    internal Expr? LambdaBody { get; }
    internal object?[] Defaults { get; }
    internal Frame Closure { get; }
    internal HashSet<string> Locals { get; }
    internal Interpreter Owner { get; }
    internal CodeMap CodeMap { get; }

    public object? Call(Interpreter interp, IReadOnlyList<object?> args, IReadOnlyList<KeyValuePair<string, object?>> kwargs) =>
        Owner.CallFunction(this, args, kwargs);

    /// <summary>Freezing a function freezes its parameter defaults and the variables it captured.</summary>
    internal void FreezeContents()
    {
        if (_frozen) return;
        _frozen = true;
        foreach (var d in Defaults) Freezer.Freeze(d);
        for (var f = Closure; f is not null; f = f.Parent)
        {
            foreach (var v in f.Vars.Values) Freezer.Freeze(v);
        }
    }
}

/// <summary>
/// A tree-walking Starlark interpreter. Each <see cref="Interpreter"/> holds the universe of predeclared
/// names (builtins and host-defined values) and the module loader for <c>load()</c>.
/// </summary>
/// <remarks>
/// <para>
/// An instance is not thread-safe: use one per thread, or serialize calls. <see cref="Execute(AstModule)"/> runs
/// synchronously on the calling thread and records <see cref="StringElements"/> in a thread-static for the duration
/// of the call, so a host function must not hand control to another thread that evaluates Starlark values.
/// Frozen values are immutable and may be shared between interpreters.
/// </para>
/// <para>
/// For untrusted scripts set <see cref="MaxSteps"/>, <see cref="Timeout"/> and/or <see cref="Cancellation"/>.
/// Call depth is already bounded.
/// </para>
/// </remarks>
public sealed class Interpreter
{
    private enum Flow { Normal, Break, Continue, Return }

    private const int MaxCallDepth = 150;

    private readonly Dictionary<string, object?> _universe = [];
    private readonly Dictionary<string, Module?> _moduleCache = [];
    private readonly HashSet<object> _active = [];
    private int _depth;

    /// <summary>Loads a module by the identifier given to <c>load()</c>: returns its source, or null if unknown.</summary>
    public Func<string, string?>? SourceLoader { get; set; }

    /// <summary>Text written by <c>print()</c>.</summary>
    public Action<string> Print { get; set; } = Console.WriteLine;

    public Dialect Dialect { get; set; } = Dialect.Standard;

    /// <summary>
    /// The unit of string length, indexing and slicing. <see cref="StringElements.Utf16"/> (default) matches .NET
    /// strings; <see cref="StringElements.Utf8"/> matches the Go and Rust implementations, so offsets agree with them.
    /// </summary>
    public StringElements StringElements { get; set; } = StringElements.Utf16;

    /// <summary>
    /// The spec makes rebinding a global a static error. Set this to allow it, as some implementations
    /// (and tests that reuse names) do.
    /// </summary>
    public bool AllowGlobalReassign { get; set; }

    /// <summary>
    /// The most statements and expressions one <see cref="Execute(AstModule)"/> call may evaluate, or 0 for no limit.
    /// Exceeding it throws <see cref="ExecutionLimitException"/>. This bounds runaway loops in untrusted scripts.
    /// </summary>
    public long MaxSteps { get; set; }

    /// <summary>The longest one <see cref="Execute(AstModule)"/> call may run, or null for no limit.</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>Cancels a running script cooperatively; checked on the same schedule as <see cref="MaxSteps"/>.</summary>
    public CancellationToken Cancellation { get; set; }

    private long _steps;
    private long _deadlineTicks;
    private int _executeNesting;

    /// <summary>Counts one unit of work and, every 1024 units, checks the time and cancellation budgets.</summary>
    private void Step()
    {
        if (MaxSteps > 0 && ++_steps > MaxSteps)
        {
            throw new ExecutionLimitException("steps", $"Script exceeded the limit of {MaxSteps} steps");
        }
        if ((_steps & 1023) == 0 || MaxSteps == 0)
        {
            PollBudgets();
        }
    }

    private int _poll;

    private void PollBudgets()
    {
        if (MaxSteps == 0 && (++_poll & 1023) != 0) return;
        Cancellation.ThrowIfCancellationRequested();
        if (_deadlineTicks != 0 && System.Diagnostics.Stopwatch.GetTimestamp() > _deadlineTicks)
        {
            throw new ExecutionLimitException("time", $"Script exceeded the time limit of {Timeout}");
        }
    }

    public Interpreter()
    {
        Builtins.Install(_universe);
    }

    /// <summary>Predeclare a global visible in every module.</summary>
    public void Define(string name, object? value) => _universe[name] = value;

    public void Define(string name, Func<Interpreter, IReadOnlyList<object?>, IReadOnlyList<KeyValuePair<string, object?>>, object?> fn) =>
        _universe[name] = new Builtin(name, fn);

    // ------------------------------------------------------------------ entry points

    /// <summary>Parse, resolve and run a module, then freeze its globals.</summary>
    public Module Execute(string filename, string source)
    {
        var ast = AstModule.Parse(filename, source, Dialect);
        return Execute(ast);
    }

    /// <summary>
    /// Statically check a module's types without running it. Reports calls with wrong argument types, operators on
    /// types that do not support them, mismatched returns, and so on. Never throws for a type error.
    /// </summary>
    public TypeCheck.TypeCheckResult Typecheck(string filename, string source)
    {
        var ast = AstModule.Parse(filename, source, Dialect);
        return Typecheck(ast);
    }

    public TypeCheck.TypeCheckResult Typecheck(AstModule ast) =>
        TypeCheck.TypeChecker.Check(ast, _universe.ContainsKey);

    /// <summary>
    /// When true, <see cref="Execute(AstModule)"/> type checks the module first and refuses to run it if the checker
    /// reports errors. Off by default, as in the Rust crate (checking is opt-in).
    /// </summary>
    public bool TypecheckBeforeExecute { get; set; }

    public Module Execute(AstModule ast)
    {
        if (TypecheckBeforeExecute)
        {
            var result = Typecheck(ast);
            if (result.Errors.Count > 0)
            {
                var first = result.Errors[0];
                var e = new StarlarkException(first.Message);
                e.AttachLocation(first.Span, first.CodeMap);
                throw e;
            }
        }
        var names = Resolver.Resolve(ast, _universe.ContainsKey, AllowGlobalReassign);
        var frame = new Frame(null, names, isModule: true);
        if (_executeNesting++ == 0)
        {
            // Budgets cover the outermost call, including every module it loads.
            _steps = 0;
            _deadlineTicks = Timeout is { } t
                ? System.Diagnostics.Stopwatch.GetTimestamp() + (long)(t.TotalSeconds * System.Diagnostics.Stopwatch.Frequency)
                : 0;
        }
        try
        {
            using (Strings.Enter(StringElements))
            {
                ExecStmt(ast.Statement, frame, ast.CodeMap);
            }
            foreach (var v in frame.Vars.Values) Freezer.Freeze(v);
            return new Module(frame.Vars);
        }
        finally
        {
            _executeNesting--;
        }
    }

    /// <summary>Evaluate a single expression, e.g. <c>Eval("1 + 2")</c>.</summary>
    public object? Eval(string expression)
    {
        var ast = AstModule.Parse("<eval>", "_ = (" + expression + "\n)", Dialect);
        var names = Resolver.Resolve(ast, _universe.ContainsKey, allowGlobalReassign: true);
        var frame = new Frame(null, names, isModule: true);
        var assign = (AssignStmt)ast.Statement;
        using var _ = Strings.Enter(StringElements);
        return EvalExpr(assign.Value, frame, ast.CodeMap);
    }

    // ------------------------------------------------------------------ statements

    private Flow ExecStmt(Stmt stmt, Frame f, CodeMap cm)
    {
        Step();
        try
        {
            return ExecStmtInner(stmt, f, cm);
        }
        catch (StarlarkException e) when (e.AttachLocation(stmt.Span, cm))
        {
            throw;
        }
    }

    private Flow ExecStmtInner(Stmt stmt, Frame f, CodeMap cm)
    {
        switch (stmt)
        {
            case StatementsStmt s:
                foreach (var x in s.Statements)
                {
                    var flow = ExecStmt(x, f, cm);
                    if (flow != Flow.Normal) return flow;
                }
                return Flow.Normal;

            case ExpressionStmt e:
                EvalExpr(e.Expr, f, cm);
                return Flow.Normal;

            case AssignStmt a:
            {
                var value = EvalExpr(a.Value, f, cm);
                if (a.Type is { } annotation)
                {
                    // `x: int = 1` checks the value against the annotation at run time.
                    var ty = TypeOf(annotation, f, cm);
                    if (!ty.Matches(value))
                    {
                        throw new StarlarkException(
                            $"Value `{Repr.Of(value)}` of type `{Types.Name(value)}` does not match the type annotation `{ty}` for assignment");
                    }
                }
                Assign(a.Target, value, f, cm);
                return Flow.Normal;
            }

            case AssignModifyStmt m:
                AssignModify(m, f, cm);
                return Flow.Normal;

            case IfStmt i:
                if (Types.Truthy(EvalExpr(i.Condition, f, cm)))
                {
                    return ExecStmt(i.Then, f, cm);
                }
                return i.Else is null ? Flow.Normal : ExecStmt(i.Else, f, cm);

            case ForStmt fo:
                return ExecFor(fo, f, cm);

            case DefStmt d:
                f.Vars[d.Name.Name] = MakeFunction(d.Name.Name, d, d.Parameters, d.Body, null, f, cm);
                return Flow.Normal;

            case ReturnStmt r:
                f.ReturnValue = r.Value is null ? null : EvalExpr(r.Value, f, cm);
                return Flow.Return;

            case BreakStmt:
                return Flow.Break;
            case ContinueStmt:
                return Flow.Continue;
            case PassStmt:
                return Flow.Normal;

            case LoadStmt l:
                ExecLoad(l, f);
                return Flow.Normal;
        }
        throw new InvalidOperationException(stmt.GetType().Name);
    }

    private Flow ExecFor(ForStmt fo, Frame f, CodeMap cm)
    {
        var iterable = EvalExpr(fo.Over, f, cm);
        foreach (var item in Ops.Iterate(iterable))
        {
            Assign(fo.Variable, item, f, cm);
            var flow = ExecStmt(fo.Body, f, cm);
            if (flow == Flow.Break) break;
            if (flow == Flow.Return) return flow;
        }
        return Flow.Normal;
    }

    private void ExecLoad(LoadStmt l, Frame f)
    {
        var module = LoadModule(l.Module.Value);
        foreach (var arg in l.Args)
        {
            string their = arg.Their.Value;
            if (their.StartsWith('_') || !module.TryGet(their, out var v))
            {
                throw new StarlarkException($"Module `{l.Module.Value}` does not export `{their}`");
            }
            f.Vars[arg.Local.Name] = v;
        }
    }

    private Module LoadModule(string id)
    {
        if (_moduleCache.TryGetValue(id, out var cached))
        {
            return cached ?? throw new StarlarkException($"Cycle in load graph: module `{id}` is being loaded");
        }
        string src = SourceLoader?.Invoke(id) ?? throw new StarlarkException($"Cannot load module `{id}`");
        _moduleCache[id] = null; // mark as in progress, to detect cycles
        try
        {
            var saved = (_depth, _active.ToArray());
            _depth = 0;
            _active.Clear();
            try
            {
                var m = Execute(id, src);
                _moduleCache[id] = m;
                return m;
            }
            finally
            {
                _depth = saved._depth;
                _active.Clear();
                foreach (var x in saved.Item2) _active.Add(x);
            }
        }
        catch
        {
            _moduleCache.Remove(id);
            throw;
        }
    }

    // ------------------------------------------------------------------ assignment

    private void Assign(AssignTarget target, object? value, Frame f, CodeMap cm)
    {
        switch (target)
        {
            case IdentTarget id:
                f.Vars[id.Ident.Name] = value;
                break;
            case TupleTarget t:
            {
                var items = Ops.Iterate(value).ToList();
                if (items.Count != t.Items.Count)
                {
                    throw new StarlarkException(items.Count < t.Items.Count
                        ? $"Too few values to unpack (got {items.Count}, expected {t.Items.Count}): length mismatch"
                        : $"Too many values to unpack (got {items.Count}, expected {t.Items.Count}): length mismatch");
                }
                for (int i = 0; i < items.Count; i++) Assign(t.Items[i], items[i], f, cm);
                break;
            }
            case IndexTarget ix:
                Ops.SetIndex(EvalExpr(ix.Target, f, cm), EvalExpr(ix.Index, f, cm), value);
                break;
            case DotTarget d:
                throw new StarlarkException(
                    $"Cannot assign to field `{d.Field.Value}` of a value of type '{Types.Name(EvalExpr(d.Target, f, cm))}': not supported");
        }
    }

    private void AssignModify(AssignModifyStmt m, Frame f, CodeMap cm)
    {
        // `x += y` on a list extends it in place; `|=`, `&=`, `-=`, `^=` on dicts and sets update them in place.
        object? Combine(object? old, object? rhs)
        {
            switch (m.Op)
            {
                case Syntax.AssignOp.Add when old is StarList list:
                    list.AddRange(Ops.Iterate(rhs).ToList());
                    return list;
                case Syntax.AssignOp.BitOr when old is StarDict d && rhs is StarDict other:
                    foreach (var kv in other.Entries.ToList()) d.Set(kv.Key, kv.Value);
                    return d;
                case Syntax.AssignOp.BitOr when old is StarSet s && rhs is StarSet other:
                    foreach (var x in other.Items.ToList()) s.Add(x);
                    return s;
                case Syntax.AssignOp.BitAnd when old is StarSet s && rhs is StarSet other:
                    foreach (var x in s.Items.ToList()) if (!other.Contains(x)) s.Remove(x);
                    return s;
                case Syntax.AssignOp.Subtract when old is StarSet s && rhs is StarSet other:
                    foreach (var x in other.Items.ToList()) s.Remove(x);
                    return s;
                case Syntax.AssignOp.BitXor when old is StarSet s && rhs is StarSet other:
                    foreach (var x in other.Items.ToList()) { if (s.Contains(x)) s.Remove(x); else s.Add(x); }
                    return s;
            }
            return Ops.Binary(ToBinOp(m.Op), old, rhs);
        }

        switch (m.Target)
        {
            case IdentTarget id:
            {
                var old = Lookup(id.Ident.Name, f);
                f.Vars[id.Ident.Name] = Combine(old, EvalExpr(m.Value, f, cm));
                break;
            }
            case IndexTarget ix:
            {
                // The target's subexpressions are evaluated exactly once, before the right-hand side.
                var obj = EvalExpr(ix.Target, f, cm);
                var idx = EvalExpr(ix.Index, f, cm);
                var old = Ops.Index(obj, idx);
                Ops.SetIndex(obj, idx, Combine(old, EvalExpr(m.Value, f, cm)));
                break;
            }
            case DotTarget d:
                throw new StarlarkException(
                    $"Cannot assign to field `{d.Field.Value}` of a value of type '{Types.Name(EvalExpr(d.Target, f, cm))}': not supported");
            default:
                throw new StarlarkException("Invalid target of augmented assignment");
        }
    }

    private static BinOp ToBinOp(Syntax.AssignOp op) => op switch
    {
        Syntax.AssignOp.Add => BinOp.Add,
        Syntax.AssignOp.Subtract => BinOp.Subtract,
        Syntax.AssignOp.Multiply => BinOp.Multiply,
        Syntax.AssignOp.Divide => BinOp.Divide,
        Syntax.AssignOp.FloorDivide => BinOp.FloorDivide,
        Syntax.AssignOp.Percent => BinOp.Percent,
        Syntax.AssignOp.BitAnd => BinOp.BitAnd,
        Syntax.AssignOp.BitOr => BinOp.BitOr,
        Syntax.AssignOp.BitXor => BinOp.BitXor,
        Syntax.AssignOp.LeftShift => BinOp.LeftShift,
        Syntax.AssignOp.RightShift => BinOp.RightShift,
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    // ------------------------------------------------------------------ variables

    private object? Lookup(string name, Frame f)
    {
        for (var fr = f; fr is not null; fr = fr.Parent)
        {
            if (!fr.Locals.Contains(name)) continue;
            if (fr.Vars.TryGetValue(name, out var v)) return v;
            throw new StarlarkException(
                $"{(fr.IsModule ? "Global" : "Local")} variable `{name}` referenced before assignment");
        }
        if (_universe.TryGetValue(name, out var u)) return u;
        throw new StarlarkException($"Variable `{name}` not found");
    }

    // ------------------------------------------------------------------ functions

    private StarFunction MakeFunction(string name, object decl, IReadOnlyList<Syntax.Parameter> parameters, Stmt? body,
        Expr? lambdaBody, Frame f, CodeMap cm)
    {
        var defs = DefParams.Unpack(parameters, cm);
        var defaults = defs.Params.Select(p => p.Default is null ? Missing.Value : EvalExpr(p.Default, f, cm)).ToArray();
        var locals = new HashSet<string>(parameters.Where(p => p.Name is not null).Select(p => p.Name!.Name));
        if (body is not null) LocalCollector.Collect(body, locals);

        // Annotations are ordinary expressions evaluated when the `def` runs, then compiled to types.
        var paramTypes = defs.Params.Select(p => p.Type is null ? null : TypeOf(p.Type, f, cm)).ToArray();
        Ty? returnType = (decl as DefStmt)?.ReturnType is { } rt ? TypeOf(rt, f, cm) : null;
        // A default that violates its own annotation is an error at definition time, even if never used.
        for (int i = 0; i < paramTypes.Length; i++)
        {
            if (paramTypes[i] is { } pt && defaults[i] is not Missing && !pt.Matches(defaults[i]))
            {
                Annotations.Check(pt, defaults[i], "argument", defs.Params[i].Ident.Name);
            }
        }
        return new StarFunction(name, decl, defs, body, lambdaBody, defaults, f, locals, this, cm)
        {
            ParamTypes = paramTypes,
            ReturnType = returnType,
        };
    }

    /// <summary>Evaluate an annotation expression and turn the result into a type.</summary>
    private Ty TypeOf(TypeExpr annotation, Frame f, CodeMap cm)
    {
        try
        {
            return TypeEval.ToTy(EvalExpr(annotation.Expr, f, cm));
        }
        catch (StarlarkException e) when (e.AttachLocation(annotation.Span, cm))
        {
            throw;
        }
    }

    internal sealed class Missing
    {
        public static readonly Missing Value = new();
    }

    internal object? CallFunction(StarFunction fn, IReadOnlyList<object?> args, IReadOnlyList<KeyValuePair<string, object?>> kwargs)
    {
        if (_depth >= MaxCallDepth || !System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw new StarlarkException("Maximum call depth exceeded");
        }
        if (!_active.Add(fn.Decl))
        {
            throw new StarlarkException($"Function `{fn.Name}` was called recursively: recursion is not allowed in Starlark");
        }

        var frame = new Frame(fn.Closure, fn.Locals, isModule: false);
        _depth++;
        try
        {
            BindArguments(fn, args, kwargs, frame);
            CheckParameterTypes(fn, frame);
            object? result;
            if (fn.LambdaBody is not null)
            {
                result = EvalExpr(fn.LambdaBody, frame, fn.CodeMap);
            }
            else
            {
                var flow = ExecStmt(fn.Body!, frame, fn.CodeMap);
                result = flow == Flow.Return ? frame.ReturnValue : null;
            }
            if (fn.ReturnType is { } rt && !rt.Matches(result))
            {
                Annotations.Check(rt, result, "return type");
            }
            return result;
        }
        finally
        {
            _depth--;
            _active.Remove(fn.Decl);
        }
    }

    /// <summary>Check each bound argument against its annotation. <c>*args</c> and <c>**kwargs</c> check every element.</summary>
    private static void CheckParameterTypes(StarFunction fn, Frame frame)
    {
        var types = fn.ParamTypes;
        if (types is null) return;
        var ps = fn.Params.Params;
        for (int i = 0; i < ps.Count; i++)
        {
            if (types[i] is not { } ty) continue;
            var value = frame.Vars[ps[i].Ident.Name];
            string name = ps[i].Ident.Name;
            switch (ps[i].Kind)
            {
                case DefParamKind.Args:
                    foreach (var x in ((StarTuple)value!).Items) Annotations.Check(ty, x, "argument", name);
                    break;
                case DefParamKind.Kwargs:
                    foreach (var v in ((StarDict)value!).Values) Annotations.Check(ty, v, "argument", name);
                    break;
                default:
                    if (!ty.Matches(value)) Annotations.Check(ty, value, "argument", name);
                    break;
            }
        }
    }

    private static void BindArguments(StarFunction fn, IReadOnlyList<object?> args,
        IReadOnlyList<KeyValuePair<string, object?>> kwargs, Frame frame)
    {
        var ps = fn.Params.Params;
        var idx = fn.Params.Indices;
        var bound = new object?[ps.Count];
        var isSet = new bool[ps.Count];

        if (args.Count > idx.NumPositional && idx.Args is null)
        {
            throw new StarlarkException(
                $"Too many positional arguments to `{fn.Name}`: function accepts {idx.NumPositional} positional argument(s), {args.Count} given");
        }
        for (int i = 0; i < Math.Min(args.Count, idx.NumPositional); i++)
        {
            bound[i] = args[i];
            isSet[i] = true;
        }
        if (idx.Args is { } ai)
        {
            bound[ai] = new StarTuple(args.Skip(idx.NumPositional).ToArray());
            isSet[ai] = true;
        }

        StarDict? extra = idx.Kwargs is null ? null : new StarDict();
        var seen = new HashSet<string>();
        foreach (var (name, value) in kwargs)
        {
            if (!seen.Add(name))
            {
                throw new StarlarkException($"Argument `{name}` of `{fn.Name}` was supplied more than once (repeated named argument)");
            }
            int target = -1;
            for (int i = 0; i < ps.Count; i++)
            {
                if (ps[i].Kind == DefParamKind.Regular && ps[i].Ident.Name == name)
                {
                    target = i;
                    break;
                }
            }
            if (target >= 0 && ps[target].Mode != DefRegularParamMode.PosOnly)
            {
                if (isSet[target])
                {
                    throw new StarlarkException($"Argument `{name}` of `{fn.Name}` was supplied more than once");
                }
                bound[target] = value;
                isSet[target] = true;
            }
            else if (extra is not null)
            {
                extra.Set(name, value);
            }
            else
            {
                throw new StarlarkException($"Unexpected parameter named `{name}` for call to `{fn.Name}`");
            }
        }
        if (idx.Kwargs is { } ki)
        {
            bound[ki] = extra;
            isSet[ki] = true;
        }

        for (int i = 0; i < ps.Count; i++)
        {
            if (!isSet[i])
            {
                if (fn.Defaults[i] is Missing)
                {
                    throw new StarlarkException($"Missing parameter `{ps[i].Ident.Name}` for call to `{fn.Name}`");
                }
                bound[i] = fn.Defaults[i];
            }
            frame.Vars[ps[i].Ident.Name] = bound[i];
        }
    }

    /// <summary>Call any callable value from native code.</summary>
    public object? CallValue(object? callee, IReadOnlyList<object?> args, IReadOnlyList<KeyValuePair<string, object?>>? kwargs = null)
    {
        if (callee is ICallable c)
        {
            using var _ = Strings.Enter(StringElements);
            return c.Call(this, args, kwargs ?? []);
        }
        throw new StarlarkException($"Operation `call()` not supported on type '{Types.Name(callee)}': not callable");
    }

    // ------------------------------------------------------------------ expressions

    private object? EvalExpr(Expr expr, Frame f, CodeMap cm)
    {
        Step();
        try
        {
            return EvalInner(expr, f, cm);
        }
        catch (StarlarkException e) when (e.AttachLocation(expr.Span, cm))
        {
            throw;
        }
    }

    private object? EvalInner(Expr expr, Frame f, CodeMap cm)
    {
        switch (expr)
        {
            case IdentExpr id:
                return Lookup(id.Name, f);
            case IntLiteral i:
                return i.Value.Value;
            case FloatLiteral fl:
                return fl.Value;
            case StringLiteral s:
                return s.Value;
            case BytesLiteral b:
                return new StarBytes(b.Value);
            case EllipsisLiteral:
                // Only meaningful inside a type, as in `tuple[int, ...]`.
                return EllipsisMarker.Instance;

            case TupleExpr t:
                return new StarTuple(t.Items.Select(x => EvalExpr(x, f, cm)).ToArray());
            case ListExpr l:
                return new StarList(l.Items.Select(x => EvalExpr(x, f, cm)).ToList());
            case DictExpr d:
            {
                var dict = new StarDict();
                foreach (var (k, v) in d.Entries)
                {
                    var key = EvalExpr(k, f, cm);
                    if (dict.ContainsKey(key))
                    {
                        throw new StarlarkException($"Duplicate dictionary key `{Repr.Of(key)}`");
                    }
                    dict.Set(key, EvalExpr(v, f, cm));
                }
                return dict;
            }

            case NotExpr n:
                return !Types.Truthy(EvalExpr(n.Operand, f, cm));
            case MinusExpr m:
                return Ops.Negate(EvalExpr(m.Operand, f, cm));
            case PlusExpr p:
                return Ops.Plus(EvalExpr(p.Operand, f, cm));
            case BitNotExpr bn:
                return Ops.BitNot(EvalExpr(bn.Operand, f, cm));

            case BinaryExpr bin:
                return EvalBinary(bin, f, cm);

            case IfExpr ie:
                return Types.Truthy(EvalExpr(ie.Condition, f, cm))
                    ? EvalExpr(ie.IfTrue, f, cm)
                    : EvalExpr(ie.IfFalse, f, cm);

            case DotExpr dot:
                return Methods.GetAttr(EvalExpr(dot.Target, f, cm), dot.Field.Value);

            case IndexExpr ix:
            {
                var target = EvalExpr(ix.Target, f, cm);
                var index = EvalExpr(ix.Index, f, cm);
                return TypeEval.IsParametrizable(target) ? TypeEval.Parametrize(target, index) : Ops.Index(target, index);
            }
            case Index2Expr ix2:
            {
                var target = EvalExpr(ix2.Target, f, cm);
                var pair = new StarTuple([EvalExpr(ix2.First, f, cm), EvalExpr(ix2.Second, f, cm)]);
                return TypeEval.IsParametrizable(target) ? TypeEval.Parametrize(target, pair) : Ops.Index(target, pair);
            }

            case SliceExpr sl:
                return Ops.Slice(
                    EvalExpr(sl.Target, f, cm),
                    sl.Start is null ? null : EvalExpr(sl.Start, f, cm),
                    sl.Stop is null ? null : EvalExpr(sl.Stop, f, cm),
                    sl.Step is null ? null : EvalExpr(sl.Step, f, cm));

            case CallExpr call:
                return EvalCall(call, f, cm);

            case LambdaExpr lam:
                return MakeFunction("lambda", lam, lam.Parameters, null, lam.Body, f, cm);

            case ListComprehension lc:
            {
                var result = new List<object?>();
                RunComprehension(lc.First, lc.Rest, f, cm, inner => result.Add(EvalExpr(lc.Item, inner, cm)));
                return new StarList(result);
            }
            case DictComprehension dc:
            {
                var dict = new StarDict();
                RunComprehension(dc.First, dc.Rest, f, cm,
                    inner => dict.Set(EvalExpr(dc.Key, inner, cm), EvalExpr(dc.Value, inner, cm)));
                return dict;
            }

            case FStringExpr fs:
                return StringFormat.Format(fs.Format, fs.Expressions.Select(x => EvalExpr(x, f, cm)).ToList(), []);
        }
        throw new InvalidOperationException(expr.GetType().Name);
    }

    private object? EvalBinary(BinaryExpr bin, Frame f, CodeMap cm)
    {
        var left = EvalExpr(bin.Left, f, cm);
        switch (bin.Op)
        {
            // Short-circuit, returning the deciding operand rather than a bool.
            case BinOp.And:
                return Types.Truthy(left) ? EvalExpr(bin.Right, f, cm) : left;
            case BinOp.Or:
                return Types.Truthy(left) ? left : EvalExpr(bin.Right, f, cm);
        }
        return Ops.Binary(bin.Op, left, EvalExpr(bin.Right, f, cm));
    }

    private void RunComprehension(ForClause first, IReadOnlyList<Clause> rest, Frame outer, CodeMap cm, Action<Frame> body)
    {
        // The first iterable is evaluated in the enclosing scope; loop variables live in a new scope.
        var names = new HashSet<string>();
        CollectTargetNames(first.Variable, names);
        foreach (var c in rest)
        {
            if (c is ForClauseClause fc) CollectTargetNames(fc.For.Variable, names);
        }
        var scope = new Frame(outer, names, isModule: false);

        void Run(ForClause clause, int clauseIndex, bool isFirst)
        {
            var iterable = EvalExpr(clause.Over, isFirst ? outer : scope, cm);
            foreach (var item in Ops.Iterate(iterable))
            {
                Assign(clause.Variable, item, scope, cm);
                Step(clauseIndex);
            }
        }

        void Step(int i)
        {
            for (; i < rest.Count; i++)
            {
                switch (rest[i])
                {
                    case IfClause ic:
                        if (!Types.Truthy(EvalExpr(ic.Condition, scope, cm))) return;
                        break;
                    case ForClauseClause fc:
                        Run(fc.For, i + 1, isFirst: false);
                        return;
                }
            }
            body(scope);
        }

        Run(first, 0, isFirst: true);
    }

    internal static void CollectTargetNames(AssignTarget t, HashSet<string> names) =>
        t.VisitLvalue(id => names.Add(id.Name));

    // ------------------------------------------------------------------ calls

    private object? EvalCall(CallExpr call, Frame f, CodeMap cm)
    {
        var callee = EvalExpr(call.Function, f, cm);
        var args = new List<object?>();
        var kwargs = new List<KeyValuePair<string, object?>>();
        foreach (var a in call.Args)
        {
            switch (a)
            {
                case PositionalArgument p:
                    args.Add(EvalExpr(p.Value, f, cm));
                    break;
                case NamedArgument n:
                    kwargs.Add(new(n.Name.Value, EvalExpr(n.Value, f, cm)));
                    break;
                case StarArgument s:
                    args.AddRange(Ops.Iterate(EvalExpr(s.Value, f, cm)).ToList());
                    break;
                case StarStarArgument ss:
                {
                    if (EvalExpr(ss.Value, f, cm) is not StarDict d)
                    {
                        throw new StarlarkException("Argument after ** must be a dictionary");
                    }
                    foreach (var kv in d.Entries)
                    {
                        if (kv.Key is not string key)
                        {
                            throw new StarlarkException("Dictionary keys passed with ** must be strings");
                        }
                        kwargs.Add(new(key, kv.Value));
                    }
                    break;
                }
            }
        }
        if (callee is not ICallable c)
        {
            throw new StarlarkException($"Operation `call()` not supported on type '{Types.Name(callee)}': not callable");
        }
        return c.Call(this, args, kwargs);
    }
}

/// <summary>Finds the names a function body binds locally: assignment targets, loop variables, nested defs, loads.</summary>
internal static class LocalCollector
{
    public static void Collect(Stmt stmt, HashSet<string> locals)
    {
        switch (stmt)
        {
            case AssignStmt a:
                Interpreter.CollectTargetNames(a.Target, locals);
                break;
            case AssignModifyStmt m:
                Interpreter.CollectTargetNames(m.Target, locals);
                break;
            case ForStmt f:
                Interpreter.CollectTargetNames(f.Variable, locals);
                break;
            case DefStmt d:
                locals.Add(d.Name.Name);
                return; // don't look inside the nested function
            case LoadStmt l:
                foreach (var arg in l.Args) locals.Add(arg.Local.Name);
                break;
        }
        stmt.VisitStmt(s => Collect(s, locals));
    }
}
