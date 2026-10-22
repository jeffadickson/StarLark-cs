using Starlark.Syntax;

namespace Starlark.Eval;

/// <summary>
/// Static name resolution, as described in the spec's "Name binding and variables" section. Runs before
/// execution and reports: references to names with no binding at all, rebinding of a global, a load
/// that binds an existing global, and augmented assignment to a global.
/// </summary>
internal sealed class Resolver
{
    private sealed class Scope(Scope? parent, bool isModule)
    {
        public Scope? Parent { get; } = parent;
        public bool IsModule { get; } = isModule;
        public HashSet<string> Names { get; } = [];
    }

    private readonly CodeMap _codemap;
    private readonly Func<string, bool> _isPredeclared;
    private readonly bool _allowGlobalReassign;
    private readonly Dictionary<string, int> _globalDeclaredLine = [];
    private readonly HashSet<string> _loaded = [];

    /// <summary>Names bound at module level.</summary>
    public HashSet<string> ModuleNames { get; } = [];

    private Resolver(CodeMap codemap, Func<string, bool> isPredeclared, bool allowGlobalReassign)
    {
        _codemap = codemap;
        _isPredeclared = isPredeclared;
        _allowGlobalReassign = allowGlobalReassign;
    }

    public static HashSet<string> Resolve(AstModule ast, Func<string, bool> isPredeclared, bool allowGlobalReassign)
    {
        var r = new Resolver(ast.CodeMap, isPredeclared, allowGlobalReassign);
        r.Run(ast.Statement);
        return r.ModuleNames;
    }

    private StarlarkException Error(Span span, string message)
    {
        var e = new StarlarkException(message);
        e.AttachLocation(span, _codemap);
        return e;
    }

    private void Run(Stmt root)
    {
        var module = new Scope(null, isModule: true);
        // Pass 1: every binding anywhere in the module block (not inside functions) belongs to the module.
        BindModule(root, module);
        foreach (var n in module.Names) ModuleNames.Add(n);
        // Pass 2: check every reference.
        CheckStmt(root, module);
    }

    // ---------------------------------------------------------------- binding collection

    private void DeclareGlobal(string name, Span span, bool isLoad)
    {
        if (isLoad)
        {
            if (!_loaded.Add(name) || _globalDeclaredLine.ContainsKey(name))
            {
                throw Error(span, $"cannot reassign global `{name}` bound by a load statement");
            }
            _globalDeclaredLine[name] = _codemap.FindLine(span.Begin) + 1;
            return;
        }
        if (_loaded.Contains(name))
        {
            throw Error(span, $"cannot reassign load-bound name `{name}`");
        }
        if (_globalDeclaredLine.TryGetValue(name, out int line) && !_allowGlobalReassign)
        {
            throw Error(span, $"cannot reassign global `{name}` declared on line {line}");
        }
        _globalDeclaredLine[name] = _codemap.FindLine(span.Begin) + 1;
    }

    private void BindModule(Stmt stmt, Scope module)
    {
        switch (stmt)
        {
            case AssignStmt a:
                a.Target.VisitLvalue(id => { DeclareGlobal(id.Name, id.Span, false); module.Names.Add(id.Name); });
                break;
            case AssignModifyStmt m:
                if (m.Target is IdentTarget it)
                {
                    throw Error(it.Span, $"cannot perform augmented assignment on global `{it.Ident.Name}`");
                }
                break;
            case ForStmt f:
                f.Variable.VisitLvalue(id => { DeclareGlobal(id.Name, id.Span, false); module.Names.Add(id.Name); });
                break;
            case DefStmt d:
                DeclareGlobal(d.Name.Name, d.Name.Span, false);
                module.Names.Add(d.Name.Name);
                return; // the body belongs to the function
            case LoadStmt l:
                foreach (var arg in l.Args)
                {
                    DeclareGlobal(arg.Local.Name, arg.Local.Span, true);
                    module.Names.Add(arg.Local.Name);
                }
                break;
        }
        stmt.VisitStmt(s => BindModule(s, module));
    }

    private static void BindLocals(Stmt stmt, HashSet<string> names)
    {
        switch (stmt)
        {
            case AssignStmt a: a.Target.VisitLvalue(id => names.Add(id.Name)); break;
            case AssignModifyStmt m: m.Target.VisitLvalue(id => names.Add(id.Name)); break;
            case ForStmt f: f.Variable.VisitLvalue(id => names.Add(id.Name)); break;
            case DefStmt d: names.Add(d.Name.Name); return;
            case LoadStmt l: foreach (var arg in l.Args) names.Add(arg.Local.Name); break;
        }
        stmt.VisitStmt(s => BindLocals(s, names));
    }

    // ---------------------------------------------------------------- reference checking

    private bool IsBound(string name, Scope? scope)
    {
        for (var s = scope; s is not null; s = s.Parent)
        {
            if (s.Names.Contains(name)) return true;
        }
        return _isPredeclared(name);
    }

    private void CheckStmt(Stmt stmt, Scope scope)
    {
        switch (stmt)
        {
            case DefStmt d:
            {
                // Parameter defaults and annotations are evaluated in the enclosing scope.
                foreach (var p in d.Parameters) p.VisitExpr(e => CheckExpr(e, scope));
                if (d.ReturnType is not null) CheckExpr(d.ReturnType.Expr, scope);
                var fn = new Scope(scope, isModule: false);
                foreach (var p in d.Parameters.Where(p => p.Name is not null)) fn.Names.Add(p.Name!.Name);
                BindLocals(d.Body, fn.Names);
                CheckStmt(d.Body, fn);
                return;
            }
            default:
                stmt.VisitOwnExprs(e => CheckExpr(e, scope));
                if (stmt is AssignModifyStmt { Target: IdentTarget } or ForStmt)
                {
                    // Targets are bindings, not references; for augmented assignment the read is checked below.
                }
                stmt.VisitStmt(s => CheckStmt(s, scope));
                return;
        }
    }

    private void CheckExpr(Expr expr, Scope scope)
    {
        switch (expr)
        {
            case IdentExpr id:
                if (!IsBound(id.Name, scope))
                {
                    throw Error(id.Span, $"Variable `{id.Name}` not found");
                }
                return;

            case LambdaExpr lam:
            {
                foreach (var p in lam.Parameters) p.VisitExpr(e => CheckExpr(e, scope));
                var fn = new Scope(scope, isModule: false);
                foreach (var p in lam.Parameters.Where(p => p.Name is not null)) fn.Names.Add(p.Name!.Name);
                CheckExpr(lam.Body, fn);
                return;
            }

            case ListComprehension lc:
                CheckComprehension(lc.First, lc.Rest, scope, inner => CheckExpr(lc.Item, inner));
                return;

            case DictComprehension dc:
                CheckComprehension(dc.First, dc.Rest, scope, inner => { CheckExpr(dc.Key, inner); CheckExpr(dc.Value, inner); });
                return;

            default:
                expr.VisitExpr(e => CheckExpr(e, scope));
                return;
        }
    }

    private void CheckComprehension(ForClause first, IReadOnlyList<Clause> rest, Scope outer, Action<Scope> body)
    {
        var comp = new Scope(outer, isModule: false);
        first.Variable.VisitLvalue(id => comp.Names.Add(id.Name));
        foreach (var c in rest)
        {
            if (c is ForClauseClause fc) fc.For.Variable.VisitLvalue(id => comp.Names.Add(id.Name));
        }
        // The operand of the first loop is resolved in the enclosing scope.
        CheckExpr(first.Over, outer);
        first.Variable.VisitExpr(e => CheckExpr(e, comp));
        foreach (var c in rest)
        {
            switch (c)
            {
                case ForClauseClause fc:
                    CheckExpr(fc.For.Over, comp);
                    fc.For.Variable.VisitExpr(e => CheckExpr(e, comp));
                    break;
                case IfClause ic:
                    CheckExpr(ic.Condition, comp);
                    break;
            }
        }
        body(comp);
    }
}
