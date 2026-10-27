using System.Numerics;
using Starlark.Syntax;

namespace Starlark.Eval.TypeCheck;

/// <summary>One static type error, with the source it came from.</summary>
public sealed record TypeDiagnostic(string Message, Span Span, CodeMap CodeMap)
{
    /// <summary>The error rendered with a source snippet, like parse errors are.</summary>
    public string Render() => SnippetRenderer.Render(Message, CodeMap, Span);

    public override string ToString() => Render();
}

/// <summary>What the checker found out about a program.</summary>
public sealed class TypeCheckResult
{
    public IReadOnlyList<TypeDiagnostic> Errors { get; init; } = [];

    /// <summary>Places where the checker gave up and treated something as <c>typing.Any</c>.</summary>
    public IReadOnlyList<string> Approximations { get; init; } = [];

    /// <summary>The inferred type of each function parameter and local, by name (a union if assigned several types).</summary>
    public IReadOnlyDictionary<string, Ty> Types { get; init; } = new Dictionary<string, Ty>();

    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// A static type checker, run before execution. It is deliberately lenient: it reports an error only when a value
/// <em>cannot</em> have the required type (see <see cref="TypeRelations.Intersects"/>), and treats anything it cannot
/// work out as <c>typing.Any</c>. Variables have one type per scope: the union of everything assigned to them.
/// </summary>
public sealed class TypeChecker
{
    private readonly CodeMap _codemap;
    private readonly Func<string, bool> _isPredeclared;
    private readonly IReadOnlyDictionary<string, Ty> _predeclaredTypes;
    private readonly List<TypeDiagnostic> _errors = [];
    private readonly List<string> _approximations = [];
    private readonly Dictionary<string, Ty> _recorded = [];

    private sealed class Scope(Scope? parent)
    {
        public Scope? Parent { get; } = parent;
        public Dictionary<string, Ty> Vars { get; } = [];
        public Ty? ReturnType { get; set; }
        public List<Ty> Returned { get; } = [];
        public bool InFunction { get; set; }

        public bool TryGet(string name, out Ty ty)
        {
            for (var s = this; s is not null; s = s.Parent)
            {
                if (s.Vars.TryGetValue(name, out ty!)) return true;
            }
            ty = Ty.Any;
            return false;
        }
    }

    /// <summary>Functions defined in the program, so calls can be checked against their signatures.</summary>
    private readonly Dictionary<string, Signature> _functions = [];

    private TypeChecker(CodeMap codemap, Func<string, bool> isPredeclared, IReadOnlyDictionary<string, Ty> predeclaredTypes)
    {
        _codemap = codemap;
        _isPredeclared = isPredeclared;
        _predeclaredTypes = predeclaredTypes;
    }

    /// <summary>
    /// Check a parsed module. <paramref name="isPredeclared"/> says which names the host defines;
    /// <paramref name="predeclaredTypes"/> gives their types where known (others are <c>typing.Any</c>).
    /// </summary>
    public static TypeCheckResult Check(AstModule ast, Func<string, bool>? isPredeclared = null,
        IReadOnlyDictionary<string, Ty>? predeclaredTypes = null)
    {
        var c = new TypeChecker(ast.CodeMap, isPredeclared ?? (_ => false), predeclaredTypes ?? new Dictionary<string, Ty>());
        var module = new Scope(null);
        // Types first (a function may annotate with a record defined further down), then signatures.
        c.CollectDefinitions(ast.Statement, module);
        c.CollectSignatures(ast.Statement, module);
        c.Stmt(ast.Statement, module);
        return new TypeCheckResult
        {
            Errors = c._errors.OrderBy(e => e.Span.Begin).ToList(),
            Approximations = c._approximations,
            Types = c._recorded,
        };
    }

    // ---------------------------------------------------------------- reporting

    private void Error(Span span, string message) => _errors.Add(new TypeDiagnostic(message, span, _codemap));

    private Ty Approximate(string what, Span span)
    {
        _approximations.Add($"{what} at {_codemap.ResolveSpan(span).Begin}");
        return Ty.Any;
    }

    /// <summary>Check that <paramref name="got"/> may be a <paramref name="required"/>; report it otherwise.</summary>
    private void Expect(Ty got, Ty required, Span span)
    {
        if (!TypeRelations.Intersects(got, required))
        {
            Error(span, $"Expected type `{required}` but got `{got}`");
        }
    }

    // ---------------------------------------------------------------- type annotations

    /// <summary>
    /// Turn an annotation expression into a type, statically. Only forms that can be understood without running
    /// code are handled; anything else (a call, an imported alias) is an approximation to <c>typing.Any</c>.
    /// </summary>
    private Ty Annotation(Expr e, Scope scope)
    {
        switch (e)
        {
            case LiteralExpr { } when e is EllipsisLiteral:
                return Ty.Any;
            case IdentExpr id:
                return NamedType(id.Name, id.Span, scope);
            case DotExpr { Target: IdentExpr { Name: "typing" } } dot:
                return dot.Field.Value switch
                {
                    "Any" => Ty.Any,
                    "Never" => Ty.Never,
                    "Callable" => Ty.Callable,
                    "Iterable" => Ty.Iterable,
                    _ => Approximate("unknown typing member", e.Span),
                };
            case BinaryExpr { Op: BinOp.BitOr } b:
                return Ty.Union([Annotation(b.Left, scope), Annotation(b.Right, scope)]);
            case TupleExpr t:
                return new Ty.TupleTy(t.Items.Select(i => Annotation(i, scope)).ToList());
            case IndexExpr ix:
                return Parametrized(ix.Target, [ix.Index], scope, e.Span);
            case Index2Expr ix2:
                return Parametrized(ix2.Target, [ix2.First, ix2.Second], scope, e.Span);
            case ListExpr l when l.Items.Count > 1:
                return Ty.Union(l.Items.Select(i => Annotation(i, scope)));
            default:
                return Approximate("unsupported type expression", e.Span);
        }
    }

    private Ty NamedType(string name, Span span, Scope scope)
    {
        switch (name)
        {
            case "int": return Ty.Int;
            case "float": return Ty.Float;
            case "bool": return Ty.Bool;
            case "str": return Ty.Str;
            case "utf16": return Ty.Utf16;
            case "bytes": return Ty.Bytes;
            case "list": return Ty.List;
            case "dict": return Ty.Dict;
            case "set": return Ty.Set;
            case "tuple": return Ty.Tuple;
            case "range": return Ty.Range;
            case "None": return Ty.None;
        }
        // A user-defined alias such as `MyList = list[int]`.
        if (_aliases.TryGetValue(name, out var alias)) return alias;
        return Approximate($"unknown type `{name}`", span);
    }

    private readonly Dictionary<string, Ty> _aliases = [];

    /// <summary>
    /// What a name statically denotes as a <em>definition</em> rather than a value: a record type, an enum type.
    /// These are what <c>R(...)</c> and <c>E(...)</c> construct from, and what annotations like <c>x: R</c> name.
    /// </summary>
    private abstract record Definition;
    private sealed record RecordDef(Ty.RecordTy Type, IReadOnlyList<(string Name, Ty Type, bool HasDefault)> Fields) : Definition;
    private sealed record EnumDef(Ty.Custom Type, IReadOnlyList<Ty> ValueTypes) : Definition;

    private readonly Dictionary<string, Definition> _definitions = [];

    /// <summary>Recognise <c>X = record(...)</c> / <c>X = enum(...)</c> and remember what <c>X</c> denotes.</summary>
    private void CollectDefinition(string name, Expr value, Scope scope)
    {
        if (value is not CallExpr { Function: IdentExpr { Name: var fn } } call) return;

        if (fn == "record")
        {
            var fields = new List<(string, Ty, bool)>();
            foreach (var arg in call.Args)
            {
                if (arg is not NamedArgument n) return;   // record() takes named arguments only; the runtime reports it
                // `field(T, default)` wraps a type with a default; a bare type has none.
                if (n.Value is CallExpr { Function: IdentExpr { Name: "field" }, Args: var fargs } && fargs.Count >= 1)
                {
                    fields.Add((n.Name.Value, Annotation(fargs[0].Value, scope), fargs.Count >= 2));
                }
                else
                {
                    fields.Add((n.Name.Value, Annotation(n.Value, scope), false));
                }
            }
            var identity = new object();
            var recordTy = new Ty.RecordTy(name, identity, fields.Select(f => new KeyValuePair<string, Ty>(f.Item1, f.Item2)).ToList());
            _definitions[name] = new RecordDef(recordTy, fields);
            _aliases[name] = recordTy;
        }
        else if (fn == "enum")
        {
            var valueTys = call.Args.Select(a => Infer(a.Value, scope)).ToList();
            var enumTy = new Ty.Custom(name, new object());
            _definitions[name] = new EnumDef(enumTy, valueTys);
            _aliases[name] = enumTy;
        }
    }

    private Ty Parametrized(Expr target, IReadOnlyList<Expr> args, Scope scope, Span span)
    {
        var baseTy = Annotation(target, scope);
        switch (baseTy)
        {
            case Ty.ListTy { Item: Ty.AnyTy } when args.Count == 1:
                return new Ty.ListTy(Annotation(args[0], scope));
            case Ty.SetTy { Item: Ty.AnyTy } when args.Count == 1:
                return new Ty.SetTy(Annotation(args[0], scope));
            case Ty.DictTy { Key: Ty.AnyTy, Value: Ty.AnyTy } when args.Count == 2:
                return new Ty.DictTy(Annotation(args[0], scope), Annotation(args[1], scope));
            case Ty.TupleOfTy { Item: Ty.AnyTy }:
                if (args.Count == 2 && args[1] is EllipsisLiteral)
                {
                    return new Ty.TupleOfTy(Annotation(args[0], scope));
                }
                return new Ty.TupleTy(args.Select(a => Annotation(a, scope)).ToList());
            case Ty.IterableTy { Item: Ty.AnyTy } when args.Count == 1:
                return new Ty.IterableTy(Annotation(args[0], scope));
            case Ty.CallableTy { Params: null, Result: null } when args.Count == 2:
            {
                var ps = args[0] is ListExpr l ? l.Items.Select(i => Annotation(i, scope)).ToList() : null;
                return new Ty.CallableTy(ps, Annotation(args[1], scope));
            }
        }
        if (baseTy is Ty.AnyTy) return Ty.Any;
        Error(span, args.Count > 1 && baseTy is Ty.ListTy or Ty.SetTy
            ? "[,] can only be applied to dict or tuple functions in type expression"
            : $"Type `{baseTy}` does not support type parameters: `[...]` is not supported");
        return Ty.Any;
    }

    // ---------------------------------------------------------------- signatures of user functions

    /// <summary>First pass: `R = record(...)` and `E = enum(...)` define types that annotations and constructors refer to.</summary>
    private void CollectDefinitions(Stmt stmt, Scope scope)
    {
        if (stmt is AssignStmt { Target: IdentTarget defId } defAssign)
        {
            CollectDefinition(defId.Ident.Name, defAssign.Value, scope);
        }
        // Function bodies hold locals, not module-level types.
        if (stmt is not DefStmt) stmt.VisitStmt(s => CollectDefinitions(s, scope));
    }

    private void CollectSignatures(Stmt stmt, Scope scope)
    {
        switch (stmt)
        {
            case AssignStmt { Target: IdentTarget id } a when a.Value is IndexExpr or Index2Expr or BinaryExpr { Op: BinOp.BitOr }
                                                             || a.Value is IdentExpr { Name: "int" or "str" or "bool" or "float" or "list" or "dict" }:
                // Possibly a type alias (`MyList = list[int]`). Record the type it denotes if it is one.
                if (LooksLikeType(a.Value))
                {
                    _aliases[id.Ident.Name] = Annotation(a.Value, scope);
                }
                break;
            case DefStmt d:
                _functions[d.Name.Name] = SignatureOf(d, scope);
                return;
        }
        stmt.VisitStmt(s => CollectSignatures(s, scope));
    }

    private static bool LooksLikeType(Expr e) => e switch
    {
        IdentExpr { Name: "int" or "str" or "bool" or "float" or "list" or "dict" or "set" or "tuple" or "bytes" } => true,
        IndexExpr { Target: IdentExpr { Name: "list" or "dict" or "set" or "tuple" } } => true,
        Index2Expr { Target: IdentExpr { Name: "list" or "dict" or "set" or "tuple" } } => true,
        BinaryExpr { Op: BinOp.BitOr } b => LooksLikeType(b.Left) && LooksLikeType(b.Right),
        _ => false,
    };

    private Signature SignatureOf(DefStmt d, Scope scope)
    {
        var parms = new List<SigParam>();
        bool seenStar = false, seenSlash = false;
        int slashAt = d.Parameters.ToList().FindIndex(p => p.Kind == ParameterKind.Slash);
        for (int i = 0; i < d.Parameters.Count; i++)
        {
            var p = d.Parameters[i];
            Ty ty = p.Type is { } annotation ? Annotation(annotation.Expr, scope) : Ty.Any;
            switch (p.Kind)
            {
                case ParameterKind.Slash: seenSlash = true; break;
                case ParameterKind.NoArgs: seenStar = true; break;
                case ParameterKind.Normal:
                {
                    var mode = seenStar ? ParamMode.NameOnly : (slashAt >= 0 && !seenSlash) ? ParamMode.PosOnly : ParamMode.PosOrName;
                    parms.Add(new SigParam(p.Name!.Name, mode, ty, p.Default is null));
                    break;
                }
                case ParameterKind.Args:
                    seenStar = true;
                    parms.Add(new SigParam(p.Name!.Name, ParamMode.Args, ty, false));
                    break;
                case ParameterKind.KwArgs:
                    parms.Add(new SigParam(p.Name!.Name, ParamMode.Kwargs, ty, false));
                    break;
            }
        }
        Ty result = d.ReturnType is { } rt ? Annotation(rt.Expr, scope) : Ty.Any;
        return new Signature(parms, result, FormatSignature(d, parms, result));
    }

    private static string FormatSignature(DefStmt d, List<SigParam> parms, Ty result)
    {
        var parts = new List<string>();
        foreach (var p in d.Parameters)
        {
            parts.Add(p.Kind switch
            {
                ParameterKind.Slash => "/",
                ParameterKind.NoArgs => "*",
                ParameterKind.Args => "*" + p.Name!.Name + (p.Type is null ? "" : ": " + parms.First(x => x.Name == p.Name.Name).Type),
                ParameterKind.KwArgs => "**" + p.Name!.Name + (p.Type is null ? "" : ": " + parms.First(x => x.Name == p.Name.Name).Type),
                _ => p.Name!.Name + (p.Type is null ? "" : ": " + parms.First(x => x.Name == p.Name.Name).Type),
            });
        }
        return $"def({string.Join(", ", parts)}) -> {result}";
    }

    // ---------------------------------------------------------------- statements

    private void Bind(Scope scope, string name, Ty ty)
    {
        scope.Vars[name] = scope.Vars.TryGetValue(name, out var old) ? Ty.Union([old, ty]) : ty;
        _recorded[name] = scope.Vars[name];
    }

    private void Stmt(Stmt stmt, Scope scope)
    {
        switch (stmt)
        {
            case StatementsStmt s:
                foreach (var x in s.Statements) Stmt(x, scope);
                break;

            case ExpressionStmt e:
                Infer(e.Expr, scope);
                break;

            case AssignStmt a:
            {
                var valueTy = Infer(a.Value, scope);
                if (a.Type is { } annotation)
                {
                    var declared = Annotation(annotation.Expr, scope);
                    Expect(valueTy, declared, a.Value.Span);
                    valueTy = declared;
                }
                AssignTo(a.Target, valueTy, scope);
                break;
            }

            case AssignModifyStmt m:
            {
                var current = m.Target is IdentTarget it && scope.TryGet(it.Ident.Name, out var t) ? t : Ty.Any;
                var rhs = Infer(m.Value, scope);
                var result = BinaryResult(ToBinOp(m.Op), current, rhs, m.Span);
                if (m.Target is IdentTarget id) Bind(scope, id.Ident.Name, result);
                else m.Target.VisitExpr(x => Infer(x, scope));
                break;
            }

            case IfStmt i:
                Infer(i.Condition, scope);
                Stmt(i.Then, scope);
                if (i.Else is not null) Stmt(i.Else, scope);
                break;

            case ForStmt f:
            {
                var over = Infer(f.Over, scope);
                var item = TypeRelations.IterableItem(over);
                if (item is null)
                {
                    Error(f.Over.Span, $"Type `{over}` is not iterable");
                    item = Ty.Any;
                }
                AssignTo(f.Variable, item, scope);
                Stmt(f.Body, scope);
                break;
            }

            case DefStmt d:
                CheckFunction(d, scope);
                Bind(scope, d.Name.Name, Ty.Any);
                break;

            case ReturnStmt r:
            {
                var ty = r.Value is null ? Ty.None : Infer(r.Value, scope);
                scope.Returned.Add(ty);
                if (scope.ReturnType is { } expected)
                {
                    Expect(ty, expected, r.Value?.Span ?? r.Span);
                }
                break;
            }

            case LoadStmt l:
                foreach (var arg in l.Args) Bind(scope, arg.Local.Name, Ty.Any);
                break;
        }
    }

    private void CheckFunction(DefStmt d, Scope outer)
    {
        var scope = new Scope(outer) { InFunction = true };
        var sig = _functions.TryGetValue(d.Name.Name, out var known) ? known : SignatureOf(d, outer);
        foreach (var p in sig.Params)
        {
            Ty ty = p.Mode switch
            {
                ParamMode.Args => new Ty.TupleOfTy(p.Type),
                ParamMode.Kwargs => new Ty.DictTy(Ty.Str, p.Type),
                _ => p.Type,
            };
            Bind(scope, p.Name, ty);
        }
        // A default must fit its own annotation.
        foreach (var p in d.Parameters.Where(p => p.Default is not null && p.Type is not null))
        {
            Expect(Infer(p.Default!, outer), Annotation(p.Type!.Expr, outer), p.Default!.Span);
        }
        scope.ReturnType = d.ReturnType is null ? null : sig.Result;
        Stmt(d.Body, scope);

        // Falling off the end returns None.
        if (scope.ReturnType is { } rt && !AlwaysReturns(d.Body))
        {
            Expect(Ty.None, rt, d.Body.Span.EndSpan);
        }
    }

    private static bool AlwaysReturns(Stmt s) => s switch
    {
        ReturnStmt => true,
        StatementsStmt { Statements: var xs } => xs.Any(AlwaysReturns),
        IfStmt { Else: { } e } i => AlwaysReturns(i.Then) && AlwaysReturns(e),
        _ => false,
    };

    private void AssignTo(AssignTarget target, Ty value, Scope scope)
    {
        switch (target)
        {
            case IdentTarget id:
                Bind(scope, id.Ident.Name, value);
                break;
            case TupleTarget t:
            {
                bool mismatch = value is Ty.TupleTy fixedArity && fixedArity.Items.Count != t.Items.Count;
                if (mismatch)
                {
                    Error(t.Span, $"Unpacking {((Ty.TupleTy)value).Items.Count} values into {t.Items.Count} variables: length mismatch");
                }
                for (int i = 0; i < t.Items.Count; i++)
                {
                    Ty element = value switch
                    {
                        Ty.TupleTy tt when tt.Items.Count == t.Items.Count => tt.Items[i],
                        Ty.TupleTy => Ty.Any,
                        _ => TypeRelations.IterableItem(value) ?? Ty.Any,
                    };
                    AssignTo(t.Items[i], element, scope);
                }
                break;
            }
            case IndexTarget ix:
                Infer(ix.Target, scope);
                Infer(ix.Index, scope);
                break;
            case DotTarget d:
                Infer(d.Target, scope);
                break;
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
        _ => BinOp.RightShift,
    };

    // ---------------------------------------------------------------- expressions

    private Ty Infer(Expr e, Scope scope)
    {
        switch (e)
        {
            case IntLiteral: return Ty.Int;
            case FloatLiteral: return Ty.Float;
            case StringLiteral: return Ty.Str;
            case BytesLiteral: return Ty.Bytes;
            case EllipsisLiteral: return Ty.Any;
            case FStringExpr fs:
                foreach (var x in fs.Expressions) Infer(x, scope);
                return Ty.Str;

            case IdentExpr id:
                if (id.Name == "None") return Ty.None;
                if (id.Name is "True" or "False") return Ty.Bool;
                if (scope.TryGet(id.Name, out var ty)) return ty;
                if (_predeclaredTypes.TryGetValue(id.Name, out var pre)) return pre;
                return Ty.Any;

            case TupleExpr t:
                return new Ty.TupleTy(t.Items.Select(i => Infer(i, scope)).ToList());

            case ListExpr l:
                return new Ty.ListTy(Ty.Union(l.Items.Select(i => Infer(i, scope))));

            case DictExpr d:
            {
                var keys = new List<Ty>();
                var values = new List<Ty>();
                foreach (var (k, v) in d.Entries)
                {
                    keys.Add(Infer(k, scope));
                    values.Add(Infer(v, scope));
                }
                return new Ty.DictTy(Ty.Union(keys), Ty.Union(values));
            }

            case NotExpr n:
                Infer(n.Operand, scope);
                return Ty.Bool;

            case MinusExpr or PlusExpr:
                return Unary(e, ((dynamic)e).Operand, scope, e is MinusExpr ? "-" : "+", Ty.Union([Ty.Int, Ty.Float]));
            case BitNotExpr bn:
                return Unary(e, bn.Operand, scope, "~", Ty.Int);

            case BinaryExpr b:
                return BinaryResult(b.Op, Infer(b.Left, scope), Infer(b.Right, scope), b.Span);

            case IfExpr ie:
                Infer(ie.Condition, scope);
                return Ty.Union([Infer(ie.IfTrue, scope), Infer(ie.IfFalse, scope)]);

            case DotExpr dot:
                return Attribute(Infer(dot.Target, scope), dot.Field.Value, dot.Span);

            case IndexExpr ix:
                return IndexResult(Infer(ix.Target, scope), Infer(ix.Index, scope), ix.Span);
            case Index2Expr ix2:
                Infer(ix2.Target, scope);
                Infer(ix2.First, scope);
                Infer(ix2.Second, scope);
                return Ty.Any;

            case SliceExpr sl:
            {
                var target = Infer(sl.Target, scope);
                if (sl.Start is not null) Infer(sl.Start, scope);
                if (sl.Stop is not null) Infer(sl.Stop, scope);
                if (sl.Step is not null) Infer(sl.Step, scope);
                return target is Ty.Name { TypeName: "str" or "bytes" } or Ty.ListTy or Ty.TupleOfTy ? target
                    : target is Ty.TupleTy tt ? new Ty.TupleOfTy(Ty.Union(tt.Items)) : Ty.Any;
            }

            case CallExpr call:
                return Call(call, scope);

            case LambdaExpr lam:
            {
                var inner = new Scope(scope) { InFunction = true };
                foreach (var p in lam.Parameters.Where(p => p.Name is not null)) Bind(inner, p.Name!.Name, Ty.Any);
                Infer(lam.Body, inner);
                return Ty.Callable;
            }

            case ListComprehension lc:
            {
                var inner = ComprehensionScope(lc.First, lc.Rest, scope);
                return new Ty.ListTy(Infer(lc.Item, inner));
            }
            case DictComprehension dc:
            {
                var inner = ComprehensionScope(dc.First, dc.Rest, scope);
                return new Ty.DictTy(Infer(dc.Key, inner), Infer(dc.Value, inner));
            }
        }
        return Approximate("unhandled expression " + e.GetType().Name, e.Span);
    }

    /// <summary>
    /// The type of <c>receiver.name</c> when it is not being called. Methods of built-in types are functions, so they
    /// are <c>typing.Callable</c>; fields of structs, records and enums have the types we recorded for them.
    /// </summary>
    private Ty Attribute(Ty receiver, string name, Span span)
    {
        var results = new List<Ty>();
        foreach (var r in Opts(receiver))
        {
            switch (r)
            {
                case Ty.AnyTy or Ty.NeverTy:
                    results.Add(r);
                    break;
                case Ty.StructTy { Fields: null }:
                    results.Add(Ty.Any);
                    break;
                case Ty.StructTy s:
                    if (s.TryGet(name, out var sf)) results.Add(sf);
                    else Unavailable(r, name, span);
                    break;
                case Ty.RecordTy rec:
                    if (rec.TryGet(name, out var rf)) results.Add(rf);
                    else Unavailable(r, name, span);
                    break;
                case Ty.Custom c when _definitions.Values.OfType<EnumDef>().Any(d => d.Type.Equals(c)):
                    // A value of the enum has `.value` and `.index`.
                    if (name == "value") results.Add(Ty.Union(_definitions.Values.OfType<EnumDef>().First(d => d.Type.Equals(c)).ValueTypes));
                    else if (name == "index") results.Add(Ty.Int);
                    else Unavailable(r, name, span);
                    break;
                default:
                    // Methods and everything else we have no table for: the call site decides, and only calls check it.
                    results.Add(HasMethod(r, name) ? Ty.Callable : Ty.Any);
                    break;
            }
        }
        return results.Count == 0 ? Ty.Never : Ty.Union(results);
    }

    private void Unavailable(Ty receiver, string name, Span span) =>
        Error(span, $"The attribute `{name}` is not available on the type `{receiver}`");

    private static bool HasMethod(Ty t, string name) =>
        BuiltinSignatures.MethodResult(t, name, []) is not null;

    private Ty Unary(Expr whole, Expr operand, Scope scope, string op, Ty allowed)
    {
        var ty = Infer(operand, scope);
        if (ty is Ty.NeverTy) return Ty.Never;

        // Of a union, keep the options the operator is defined for; it is an error only if none of them are.
        var good = Opts(ty).Where(o => o is Ty.AnyTy || TypeRelations.Intersects(o, allowed)).ToList();
        if (good.Count == 0)
        {
            Error(whole.Span, $"Unary operator `{op}` is not available on the type `{ty}`");
            return Ty.Never;
        }
        return Ty.Union(good);
    }

    private Scope ComprehensionScope(ForClause first, IReadOnlyList<Clause> rest, Scope outer)
    {
        var inner = new Scope(outer) { InFunction = outer.InFunction };
        ForPart(first, outer, inner);
        foreach (var c in rest)
        {
            switch (c)
            {
                case ForClauseClause fc: ForPart(fc.For, inner, inner); break;
                case IfClause ic: Infer(ic.Condition, inner); break;
            }
        }
        return inner;
    }

    private void ForPart(ForClause clause, Scope evalIn, Scope bindIn)
    {
        var over = Infer(clause.Over, evalIn);
        var item = TypeRelations.IterableItem(over);
        if (item is null)
        {
            Error(clause.Over.Span, $"Type `{over}` is not iterable");
            item = Ty.Any;
        }
        AssignTo(clause.Variable, item, bindIn);
    }

    // ---------------------------------------------------------------- operators

    private Ty BinaryResult(BinOp op, Ty left, Ty right, Span span)
    {
        // Never or Any on either side: nothing can be said.
        if (left is Ty.NeverTy || right is Ty.NeverTy) return Ty.Never;
        if (left is Ty.AnyTy || right is Ty.AnyTy)
        {
            return op is BinOp.Equal or BinOp.NotEqual or BinOp.Less or BinOp.Greater or BinOp.LessOrEqual
                or BinOp.GreaterOrEqual or BinOp.In or BinOp.NotIn ? Ty.Bool : Ty.Any;
        }

        switch (op)
        {
            case BinOp.And or BinOp.Or:
                return Ty.Union([left, right]);

            case BinOp.Equal or BinOp.NotEqual:
                if (!TypeRelations.Intersects(TypeRelations.WidenNumeric(left), right) &&
                    !TypeRelations.Intersects(left, TypeRelations.WidenNumeric(right)))
                {
                    Error(span, $"Expected type `{left}` but got `{right}`");
                }
                return Ty.Bool;

            case BinOp.Less or BinOp.Greater or BinOp.LessOrEqual or BinOp.GreaterOrEqual:
                if (!Orderable(left, right)) return BinaryError(op, left, right, span);
                return Ty.Bool;

            case BinOp.In or BinOp.NotIn:
                if (!ContainsOk(right)) return BinaryError(op, left, right, span);
                return Ty.Bool;
        }

        var result = Arithmetic(op, left, right);
        return result ?? BinaryError(op, left, right, span);
    }

    private Ty BinaryError(BinOp op, Ty left, Ty right, Span span)
    {
        Error(span, $"Binary operator `{op.Symbol()}` is not available on the types `{left}` and `{right}`");
        return Ty.Never;
    }

    private static bool IsNumber(Ty t) => t is Ty.Name { TypeName: "int" or "float" };

    private static bool Orderable(Ty l, Ty r)
    {
        foreach (var a in Opts(l))
        {
            foreach (var b in Opts(r))
            {
                if (OrderableOne(a, b)) return true;
            }
        }
        return false;
    }

    private static bool OrderableOne(Ty a, Ty b) => (a, b) switch
    {
        (Ty.AnyTy, _) or (_, Ty.AnyTy) => true,
        _ when IsNumber(a) && IsNumber(b) => true,
        (Ty.Name x, Ty.Name y) => x.TypeName == y.TypeName && x.TypeName is "str" or "utf16" or "bool" or "bytes",
        (Ty.ListTy x, Ty.ListTy y) => TypeRelations.Intersects(x.Item, y.Item),
        (Ty.TupleOfTy or Ty.TupleTy, Ty.TupleOfTy or Ty.TupleTy) => true,
        _ => false,
    };

    private static bool ContainsOk(Ty container) => Opts(container).Any(c => c switch
    {
        Ty.AnyTy => true,
        Ty.ListTy or Ty.TupleOfTy or Ty.TupleTy or Ty.DictTy or Ty.SetTy or Ty.IterableTy => true,
        Ty.Name { TypeName: "str" or "utf16" or "bytes" or "range" } => true,
        _ => false,
    });

    private static IEnumerable<Ty> Opts(Ty t) => t is Ty.UnionTy u ? u.Options : [t];

    /// <summary>The result type of an arithmetic operator, or null if it is not defined for these operand types.</summary>
    private static Ty? Arithmetic(BinOp op, Ty left, Ty right)
    {
        var results = new List<Ty>();
        foreach (var a in Opts(left))
        {
            foreach (var b in Opts(right))
            {
                var r = ArithmeticOne(op, a, b);
                if (r is null) continue;
                results.Add(r);
            }
        }
        return results.Count == 0 ? null : Ty.Union(results);
    }

    private static Ty? ArithmeticOne(BinOp op, Ty a, Ty b)
    {
        if (a is Ty.AnyTy || b is Ty.AnyTy) return Ty.Any;

        // Numbers.
        if (IsNumber(a) && IsNumber(b))
        {
            bool bothInt = a is Ty.Name { TypeName: "int" } && b is Ty.Name { TypeName: "int" };
            return op switch
            {
                BinOp.Divide => Ty.Float,
                BinOp.Add or BinOp.Subtract or BinOp.Multiply or BinOp.FloorDivide or BinOp.Percent => bothInt ? Ty.Int : Ty.Float,
                BinOp.BitAnd or BinOp.BitOr or BinOp.BitXor or BinOp.LeftShift or BinOp.RightShift => bothInt ? Ty.Int : null,
                _ => null,
            };
        }

        // bool behaves as an unrelated type for arithmetic.
        switch (op)
        {
            case BinOp.Add:
                if (a is Ty.Name { TypeName: "str" } && b is Ty.Name { TypeName: "str" }) return Ty.Str;
                if (a is Ty.Name { TypeName: "utf16" } && b is Ty.Name { TypeName: "utf16" }) return Ty.Utf16;
                if (a is Ty.Name { TypeName: "bytes" } && b is Ty.Name { TypeName: "bytes" }) return Ty.Bytes;
                if (a is Ty.ListTy la && b is Ty.ListTy lb) return new Ty.ListTy(Ty.Union([la.Item, lb.Item]));
                if (a is Ty.TupleOfTy ta && b is Ty.TupleOfTy tb) return new Ty.TupleOfTy(Ty.Union([ta.Item, tb.Item]));
                if (a is Ty.TupleTy xa && b is Ty.TupleTy xb) return new Ty.TupleTy([.. xa.Items, .. xb.Items]);
                if (a is Ty.TupleTy or Ty.TupleOfTy && b is Ty.TupleTy or Ty.TupleOfTy)
                {
                    return new Ty.TupleOfTy(Ty.Union([ItemOf(a), ItemOf(b)]));
                }
                return null;
            case BinOp.Multiply:
                if (a is Ty.Name { TypeName: "int" } && IsSequence(b)) return b;
                if (IsSequence(a) && b is Ty.Name { TypeName: "int" }) return a;
                return null;
            case BinOp.Percent:
                return a is Ty.Name { TypeName: "str" } ? Ty.Str : a is Ty.Name { TypeName: "utf16" } ? Ty.Utf16 : null;
            case BinOp.BitOr:
                if (a is Ty.DictTy da && b is Ty.DictTy db) return new Ty.DictTy(Ty.Union([da.Key, db.Key]), Ty.Union([da.Value, db.Value]));
                if (a is Ty.SetTy sa && b is Ty.SetTy sb) return new Ty.SetTy(Ty.Union([sa.Item, sb.Item]));
                return null;
            case BinOp.BitAnd or BinOp.BitXor or BinOp.Subtract:
                if (a is Ty.SetTy s1 && b is Ty.SetTy s2) return new Ty.SetTy(Ty.Union([s1.Item, s2.Item]));
                return null;
        }
        return null;
    }

    private static Ty ItemOf(Ty t) => t switch
    {
        Ty.TupleOfTy to => to.Item,
        Ty.TupleTy tt => Ty.Union(tt.Items),
        _ => Ty.Any,
    };

    private static bool IsSequence(Ty t) =>
        t is Ty.ListTy or Ty.TupleOfTy or Ty.TupleTy or Ty.Name { TypeName: "str" or "utf16" or "bytes" };

    private Ty IndexResult(Ty target, Ty index, Span span)
    {
        var results = new List<Ty>();
        foreach (var t in Opts(target))
        {
            switch (t)
            {
                case Ty.AnyTy: results.Add(Ty.Any); break;
                // Never already means "an error was reported upstream": don't report it again.
                case Ty.NeverTy: results.Add(Ty.Never); break;
                case Ty.ListTy l:
                    if (!TypeRelations.Intersects(index, Ty.Int)) { Expected(index, Ty.Int, span); break; }
                    results.Add(l.Item);
                    break;
                case Ty.TupleOfTy to:
                    if (!TypeRelations.Intersects(index, Ty.Int)) { Expected(index, Ty.Int, span); break; }
                    results.Add(to.Item);
                    break;
                case Ty.TupleTy tt:
                    results.Add(Ty.Union(tt.Items));
                    break;
                case Ty.DictTy d:
                    // A lookup by Never or Any is not an error; a key of the wrong type is.
                    if (index is not Ty.NeverTy && !TypeRelations.Intersects(index, d.Key)) { Expected(index, d.Key, span); break; }
                    results.Add(d.Value);
                    break;
                case Ty.Name { TypeName: "str" }: results.Add(Ty.Str); break;
                case Ty.Name { TypeName: "utf16" }: results.Add(Ty.Utf16); break;
                case Ty.Name { TypeName: "bytes" }: results.Add(Ty.Int); break;
                case Ty.Name { TypeName: "range" }: results.Add(Ty.Int); break;
                case Ty.Custom { Identity: StarEnumType }: results.Add(t); break;
                default:
                    Error(span, $"Type `{t}` is not indexable");
                    break;
            }
        }
        return results.Count == 0 ? Ty.Never : Ty.Union(results);

        void Expected(Ty got, Ty required, Span s) => Error(s, $"Expected type `{required}` but got `{got}`");
    }

    // ---------------------------------------------------------------- calls

    private Ty Call(CallExpr call, Scope scope)
    {
        // Infer every argument first, so errors inside them are always reported.
        var pos = new List<(Ty Ty, Span Span)>();
        var named = new List<(string Name, Ty Ty, Span Span)>();
        bool hasStar = false;
        foreach (var arg in call.Args)
        {
            var ty = Infer(arg.Value, scope);
            switch (arg)
            {
                case PositionalArgument: pos.Add((ty, arg.Span)); break;
                case NamedArgument n: named.Add((n.Name.Value, ty, arg.Span)); break;
                case StarArgument or StarStarArgument: hasStar = true; break;
            }
        }

        // Method call: receiver.method(...)
        if (call.Function is DotExpr dot)
        {
            var recv = Infer(dot.Target, scope);
            var argTypes = pos.Select(p => p.Ty).ToList();

            // `json.encode(x)`, `json.decode(s)`, `json.indent(s)`.
            if (dot.Target is IdentExpr { Name: "json" } && !scope.TryGet("json", out _))
            {
                return JsonCall(dot.Field.Value, pos, call);
            }
            // `Color.values()` on an enum type.
            if (dot.Target is IdentExpr enumName && _definitions.TryGetValue(enumName.Name, out var d) && d is EnumDef ed
                && dot.Field.Value == "values")
            {
                return new Ty.ListTy(Ty.Union(ed.ValueTypes));
            }
            // A struct or record field holding a function: the attribute is checked, the result is unknown.
            if (Opts(recv).Any(o => o is Ty.StructTy or Ty.RecordTy))
            {
                var field = Attribute(recv, dot.Field.Value, dot.Span);
                return field is Ty.CallableTy { Result: { } fr } ? fr : Ty.Any;
            }
            return BuiltinSignatures.MethodResult(recv, dot.Field.Value, argTypes) ?? Ty.Any;
        }

        if (call.Function is not IdentExpr fn)
        {
            var callee = Infer(call.Function, scope);
            if (!Opts(callee).Any(c => c is Ty.AnyTy or Ty.CallableTy or Ty.Custom || c is Ty.Name { TypeName: "function" }))
            {
                Error(call.Function.Span, $"Call to a non-callable type `{callee}`");
            }
            return Ty.Any;
        }

        // Constructors: `R(host='h', port=1)` builds a record, `E('A')` an enum value.
        if (_definitions.TryGetValue(fn.Name, out var def) && !IsShadowedLocally(fn.Name, scope))
        {
            return Construct(def, pos, named, hasStar, call);
        }

        // `struct(a=1, b='x')` has the type `struct(a = int, b = str)`.
        if (fn.Name == "struct" && !scope.TryGet("struct", out _) && !_functions.ContainsKey("struct"))
        {
            if (pos.Count > 0) Error(pos[0].Span, "Too many positional arguments");
            return new Ty.StructTy(named.Select(n => new KeyValuePair<string, Ty>(n.Name, n.Ty)).ToList());
        }

        // A name bound locally to a non-callable value.
        if (scope.TryGet(fn.Name, out var bound) && !_functions.ContainsKey(fn.Name))
        {
            if (!Opts(bound).Any(c => c is Ty.AnyTy or Ty.CallableTy or Ty.Custom || c is Ty.Name { TypeName: "function" }))
            {
                Error(call.Function.Span, $"Call to a non-callable type `{bound}`");
            }
            return Ty.Any;
        }

        if (_functions.TryGetValue(fn.Name, out var sig))
        {
            CheckArguments(sig, pos, named, hasStar, call);
            return sig.Result;
        }

        if (!_isPredeclared(fn.Name) && !BuiltinSignatures.Functions.ContainsKey(fn.Name) && !IsBuiltinName(fn.Name))
        {
            return Ty.Any;
        }

        var argTys = pos.Select(p => p.Ty).ToList();
        if (BuiltinSignatures.Functions.TryGetValue(fn.Name, out var builtin))
        {
            CheckArguments(builtin, pos, named, hasStar, call);
            return BuiltinSignatures.Result(fn.Name, argTys) ?? builtin.Result;
        }
        return BuiltinSignatures.Result(fn.Name, argTys) ?? Ty.Any;
    }

    /// <summary>
    /// Whether <paramref name="name"/> is bound in a scope nearer than the module, such as a parameter or a local.
    /// The module scope has no parent, so only scopes that have one can shadow a module-level definition.
    /// </summary>
    private static bool IsShadowedLocally(string name, Scope scope)
    {
        for (var s = scope; s?.Parent is not null; s = s.Parent)
        {
            if (s.Vars.ContainsKey(name)) return true;
        }
        return false;
    }

    private Ty Construct(Definition def, List<(Ty Ty, Span Span)> pos, List<(string Name, Ty Ty, Span Span)> named,
        bool hasStar, CallExpr call)
    {
        switch (def)
        {
            case RecordDef rd:
            {
                if (pos.Count > 0)
                {
                    // Reported once, as the single mistake it is; don't also list every field as missing.
                    Error(pos[0].Span, "Too many positional arguments: records are constructed with named arguments only");
                    return rd.Type;
                }
                var supplied = new HashSet<string>();
                foreach (var (name, ty, span) in named)
                {
                    var f = rd.Fields.FirstOrDefault(x => x.Name == name);
                    if (f.Name is null)
                    {
                        Error(span, $"Unexpected parameter named `{name}`");
                        continue;
                    }
                    Expect(ty, f.Type, span);
                    supplied.Add(name);
                }
                if (!hasStar)
                {
                    foreach (var f in rd.Fields.Where(f => !f.HasDefault && !supplied.Contains(f.Name)))
                    {
                        Error(call.Function.Span, $"Missing required parameter `{f.Name}`");
                    }
                }
                return rd.Type;
            }
            case EnumDef ed:
            {
                if (pos.Count == 1)
                {
                    // The argument must be able to be one of the enum's values.
                    Expect(pos[0].Ty, Ty.Union(ed.ValueTypes), pos[0].Span);
                }
                else if (!hasStar)
                {
                    Error(call.Function.Span, pos.Count == 0 ? "Missing required parameter `value`" : "Too many positional arguments");
                }
                return ed.Type;
            }
        }
        return Ty.Any;
    }

    private Ty JsonCall(string name, List<(Ty Ty, Span Span)> pos, CallExpr call)
    {
        switch (name)
        {
            case "encode":
                if (pos.Count != 1) Error(call.Function.Span, pos.Count == 0 ? "Missing required parameter `x`" : "Too many positional arguments");
                return Ty.Str;
            case "decode":
                if (pos.Count < 1) Error(call.Function.Span, "Missing required parameter `x`");
                else Expect(pos[0].Ty, Ty.Str, pos[0].Span);
                // JSON can decode to any value.
                return Ty.Any;
            case "indent":
                if (pos.Count < 1) Error(call.Function.Span, "Missing required parameter `s`");
                else Expect(pos[0].Ty, Ty.Str, pos[0].Span);
                return Ty.Str;
            default:
                Error(call.Function.Span, $"The attribute `{name}` is not available on the type `struct(encode = typing.Callable, decode = typing.Callable, indent = typing.Callable)`");
                return Ty.Never;
        }
    }

    private static bool IsBuiltinName(string n) => n is "list" or "tuple" or "set" or "dict" or "sorted" or "reversed" or "range"
        or "enumerate" or "zip" or "min" or "max";

    private void CheckArguments(Signature sig, List<(Ty Ty, Span Span)> pos, List<(string Name, Ty Ty, Span Span)> named,
        bool hasStar, CallExpr call)
    {
        var ps = sig.Params;
        var supplied = new HashSet<string>();
        int next = 0;

        foreach (var (ty, span) in pos)
        {
            // Skip to the next parameter that accepts a positional argument.
            while (next < ps.Count && ps[next].Mode is ParamMode.NameOnly or ParamMode.Kwargs) next++;
            if (next >= ps.Count)
            {
                Error(span, "Too many positional arguments");
                return;
            }
            var p = ps[next];
            Expect(ty, p.Type, span);
            supplied.Add(p.Name);
            if (p.Mode != ParamMode.Args) next++;
        }

        foreach (var (name, ty, span) in named)
        {
            var p = ps.FirstOrDefault(x => x.Name == name && x.Mode is ParamMode.PosOrName or ParamMode.NameOnly);
            if (p is null)
            {
                var kw = ps.FirstOrDefault(x => x.Mode == ParamMode.Kwargs);
                if (kw is null)
                {
                    // Once a call is known to be wrong, don't pile "missing parameter" on top of the same mistake.
                    Error(span, $"Unexpected parameter named `{name}`");
                    return;
                }
                Expect(ty, kw.Type, span);
                continue;
            }
            Expect(ty, p.Type, span);
            supplied.Add(p.Name);
        }

        if (hasStar) return; // `*args` or `**kwargs` may supply anything
        foreach (var p in ps)
        {
            if (p.Required && p.Mode is not (ParamMode.Args or ParamMode.Kwargs) && !supplied.Contains(p.Name))
            {
                Error(call.Function.Span, $"Missing required parameter `{p.Name}`");
            }
        }
    }
}
