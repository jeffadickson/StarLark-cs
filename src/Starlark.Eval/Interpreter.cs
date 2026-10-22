using System.Numerics;
using Starlark.Syntax;

namespace Starlark.Eval;

/// <summary>A lexical scope. A module frame has <c>Locals == null</c>; its variables are the globals.</summary>
internal sealed class Frame(Frame? parent, HashSet<string>? locals)
{
    public Frame? Parent { get; } = parent;
    public HashSet<string>? Locals { get; } = locals;
    public Dictionary<string, object?> Vars { get; } = [];
    public object? ReturnValue { get; set; }
}

/// <summary>An executed module: its (frozen) global variables.</summary>
public sealed class Module(Dictionary<string, object?> globals)
{
    public IReadOnlyDictionary<string, object?> Globals => globals;
    public bool TryGet(string name, out object? value) => globals.TryGetValue(name, out value);
}

/// <summary>A function defined with <c>def</c> or <c>lambda</c>.</summary>
public sealed class StarFunction : ICallable
{
    internal StarFunction(string name, DefParams parms, Stmt? body, Expr? lambdaBody, object?[] defaults,
        Frame closure, HashSet<string> locals, Interpreter owner, CodeMap codemap)
    {
        Name = name;
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
    internal DefParams Params { get; }
    internal Stmt? Body { get; }
    internal Expr? LambdaBody { get; }
    internal object?[] Defaults { get; }
    internal Frame Closure { get; }
    internal HashSet<string> Locals { get; }
    internal Interpreter Owner { get; }
    internal CodeMap CodeMap { get; }

    public object? Call(Interpreter interp, IReadOnlyList<object?> args, IReadOnlyList<KeyValuePair<string, object?>> kwargs) =>
        Owner.CallFunction(this, args, kwargs);
}

/// <summary>
/// A tree-walking Starlark interpreter. Each <see cref="Interpreter"/> holds the universe of predeclared
/// names (builtins) and the module loader for <c>load()</c>.
/// </summary>
public sealed class Interpreter
{
    private enum Flow { Normal, Break, Continue, Return }

    private const int MaxCallDepth = 200;

    private readonly Dictionary<string, object?> _universe = [];
    private readonly Dictionary<string, Module> _moduleCache = [];
    private int _depth;
    private Frame _globals = null!;

    /// <summary>Loads a module by the identifier given to <c>load()</c>: returns its source, or null if unknown.</summary>
    public Func<string, string?>? SourceLoader { get; set; }

    /// <summary>Text written by <c>print()</c>.</summary>
    public Action<string> Print { get; set; } = Console.WriteLine;

    public Dialect Dialect { get; set; } = Dialect.Standard;

    public Interpreter()
    {
        Builtins.Install(_universe);
    }

    /// <summary>Predeclare a global visible in every module.</summary>
    public void Define(string name, object? value) => _universe[name] = value;

    public void Define(string name, Func<Interpreter, IReadOnlyList<object?>, IReadOnlyList<KeyValuePair<string, object?>>, object?> fn) =>
        _universe[name] = new Builtin(name, fn);

    // ------------------------------------------------------------------ entry points

    /// <summary>Parse and run a module, then freeze its globals.</summary>
    public Module Execute(string filename, string source)
    {
        var ast = AstModule.Parse(filename, source, Dialect);
        return Execute(ast);
    }

    public Module Execute(AstModule ast)
    {
        var saved = _globals;
        var frame = new Frame(null, null);
        _globals = frame;
        try
        {
            ExecStmt(ast.Statement, frame, ast.CodeMap);
        }
        finally
        {
            _globals = saved;
        }
        foreach (var v in frame.Vars.Values) Freezer.Freeze(v);
        return new Module(frame.Vars);
    }

    /// <summary>Evaluate a single expression in a fresh scope, e.g. <c>Eval("1 + 2")</c>.</summary>
    public object? Eval(string expression)
    {
        var ast = AstModule.Parse("<eval>", "_ = (" + expression + "\n)", Dialect);
        var assign = (AssignStmt)ast.Statement;
        var frame = new Frame(null, null);
        var saved = _globals;
        _globals = frame;
        try
        {
            return EvalExpr(assign.Value, frame, ast.CodeMap);
        }
        finally
        {
            _globals = saved;
        }
    }

    // ------------------------------------------------------------------ statements

    private Flow ExecStmt(Stmt stmt, Frame f, CodeMap cm)
    {
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
                Assign(a.Target, EvalExpr(a.Value, f, cm), f, cm);
                return Flow.Normal;

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
                f.Vars[d.Name.Name] = MakeFunction(d.Name.Name, d.Parameters, d.Body, null, f, cm);
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
            if (!module.TryGet(arg.Their.Value, out var v))
            {
                throw new StarlarkException($"Module `{l.Module.Value}` does not export `{arg.Their.Value}`");
            }
            f.Vars[arg.Local.Name] = v;
        }
    }

    private Module LoadModule(string id)
    {
        if (_moduleCache.TryGetValue(id, out var m)) return m;
        string? src = SourceLoader?.Invoke(id) ?? throw new StarlarkException($"Cannot load module `{id}`");
        _moduleCache[id] = null!; // detect cycles
        try
        {
            m = Execute(id, src);
        }
        catch
        {
            _moduleCache.Remove(id);
            throw;
        }
        return _moduleCache[id] = m;
    }

    // ------------------------------------------------------------------ assignment

    private void Assign(AssignTarget target, object? value, Frame f, CodeMap cm)
    {
        switch (target)
        {
            case IdentTarget id:
                Store(id.Ident.Name, value, f);
                break;
            case TupleTarget t:
            {
                var items = Ops.Iterate(value).ToList();
                if (items.Count != t.Items.Count)
                {
                    throw new StarlarkException(items.Count < t.Items.Count
                        ? $"Not enough values to unpack (got {items.Count}, expected {t.Items.Count})"
                        : $"Too many values to unpack (got {items.Count}, expected {t.Items.Count})");
                }
                for (int i = 0; i < items.Count; i++) Assign(t.Items[i], items[i], f, cm);
                break;
            }
            case IndexTarget ix:
                Ops.SetIndex(EvalExpr(ix.Target, f, cm), EvalExpr(ix.Index, f, cm), value);
                break;
            case DotTarget d:
                throw new StarlarkException($"Cannot assign to field `{d.Field.Value}` of type '{Types.Name(EvalExpr(d.Target, f, cm))}'");
        }
    }

    private static void Store(string name, object? value, Frame f)
    {
        f.Vars[name] = value;
    }

    private void AssignModify(AssignModifyStmt m, Frame f, CodeMap cm)
    {
        object? Combine(object? old, object? rhs)
        {
            // `list += iterable` mutates in place.
            if (m.Op == Syntax.AssignOp.Add && old is StarList list)
            {
                if (rhs is not StarList and not StarTuple)
                {
                    throw new StarlarkException($"Operation `+=` not supported on types 'list' and '{Types.Name(rhs)}'");
                }
                list.AddRange(Ops.Iterate(rhs).ToList());
                return list;
            }
            return Ops.Binary(ToBinOp(m.Op), old, rhs);
        }

        switch (m.Target)
        {
            case IdentTarget id:
            {
                var old = Lookup(id.Ident.Name, f);
                Store(id.Ident.Name, Combine(old, EvalExpr(m.Value, f, cm)), f);
                break;
            }
            case IndexTarget ix:
            {
                var obj = EvalExpr(ix.Target, f, cm);
                var idx = EvalExpr(ix.Index, f, cm);
                var old = Ops.Index(obj, idx);
                Ops.SetIndex(obj, idx, Combine(old, EvalExpr(m.Value, f, cm)));
                break;
            }
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
            if (fr.Locals is null)
            {
                // Module frame: globals, then the universe.
                if (fr.Vars.TryGetValue(name, out var g)) return g;
                break;
            }
            if (fr.Locals.Contains(name))
            {
                if (fr.Vars.TryGetValue(name, out var v)) return v;
                throw new StarlarkException($"Local variable `{name}` referenced before assignment");
            }
        }
        if (_universe.TryGetValue(name, out var u)) return u;
        throw new StarlarkException($"Variable `{name}` not found");
    }

    // ------------------------------------------------------------------ functions

    private StarFunction MakeFunction(string name, IReadOnlyList<Syntax.Parameter> parameters, Stmt? body, Expr? lambdaBody,
        Frame f, CodeMap cm)
    {
        var defs = DefParams.Unpack(parameters, cm);
        var defaults = defs.Params.Select(p => p.Default is null ? Missing.Value : EvalExpr(p.Default, f, cm)).ToArray();
        var locals = new HashSet<string>(parameters.Where(p => p.Name is not null).Select(p => p.Name!.Name));
        if (body is not null) LocalCollector.Collect(body, locals);
        return new StarFunction(name, defs, body, lambdaBody, defaults, f, locals, this, cm);
    }

    internal sealed class Missing
    {
        public static readonly Missing Value = new();
    }

    internal object? CallFunction(StarFunction fn, IReadOnlyList<object?> args, IReadOnlyList<KeyValuePair<string, object?>> kwargs)
    {
        if (_depth >= MaxCallDepth || !System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw new StarlarkException("Maximum call depth exceeded (recursion is not allowed in Starlark)");
        }
        var frame = new Frame(fn.Closure, fn.Locals);
        BindArguments(fn, args, kwargs, frame);

        _depth++;
        try
        {
            if (fn.LambdaBody is not null)
            {
                return EvalExpr(fn.LambdaBody, frame, fn.CodeMap);
            }
            var flow = ExecStmt(fn.Body!, frame, fn.CodeMap);
            return flow == Flow.Return ? frame.ReturnValue : null;
        }
        finally
        {
            _depth--;
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
            throw new StarlarkException($"Too many positional arguments to `{fn.Name}`: expected {idx.NumPositional}, got {args.Count}");
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
        foreach (var (name, value) in kwargs)
        {
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
                throw new StarlarkException($"Unexpected parameter named `{name}` for `{fn.Name}`");
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
            return c.Call(this, args, kwargs ?? []);
        }
        throw new StarlarkException($"Operation `call()` not supported on type '{Types.Name(callee)}'");
    }

    // ------------------------------------------------------------------ expressions

    private object? EvalExpr(Expr expr, Frame f, CodeMap cm)
    {
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
                throw new StarlarkException("`...` cannot be evaluated");

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
                return Ops.Index(EvalExpr(ix.Target, f, cm), EvalExpr(ix.Index, f, cm));
            case Index2Expr ix2:
                return Ops.Index(EvalExpr(ix2.Target, f, cm), new StarTuple([EvalExpr(ix2.First, f, cm), EvalExpr(ix2.Second, f, cm)]));

            case SliceExpr sl:
                return Ops.Slice(
                    EvalExpr(sl.Target, f, cm),
                    sl.Start is null ? null : EvalExpr(sl.Start, f, cm),
                    sl.Stop is null ? null : EvalExpr(sl.Stop, f, cm),
                    sl.Step is null ? null : EvalExpr(sl.Step, f, cm));

            case CallExpr call:
                return EvalCall(call, f, cm);

            case LambdaExpr lam:
                return MakeFunction("lambda", lam.Parameters, null, lam.Body, f, cm);

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
        var scope = new Frame(outer, names);

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
            throw new StarlarkException($"Operation `call()` not supported on type '{Types.Name(callee)}'");
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
