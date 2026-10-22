using System.Text;

namespace Starlark.Syntax;

/// <summary>An element of an f-string during parsing: literal text, or an expression with its conversion.</summary>
internal abstract record FStringElement
{
    public sealed record Text(string Value) : FStringElement;
    public sealed record Expression(Expr Expr, FStringConv Conv) : FStringElement;
}

/// <summary>Code called by the parser to handle cases not expressible in the grammar itself.</summary>
internal static class GrammarUtil
{
    public static Stmt Statements(List<Stmt> xs, int begin, int end) =>
        xs.Count == 1 ? xs[0] : new StatementsStmt(xs, new Span(begin, end));

    public static AssignTarget CheckAssign(ParserState state, Expr x)
    {
        switch (x)
        {
            case TupleExpr t:
                return new TupleTarget(t.Items.Select(i => CheckAssign(state, i)).ToList(), x.Span);
            case ListExpr l:
                return new TupleTarget(l.Items.Select(i => CheckAssign(state, i)).ToList(), x.Span);
            case DotExpr d:
                return new DotTarget(d.Target, d.Field, x.Span);
            case IndexExpr i:
                return new IndexTarget(i.Target, i.Index, x.Span);
            case IdentExpr id:
                return new IdentTarget(new AssignIdent(id.Name, id.Span), x.Span);
            default:
                throw state.MakeError(x.Span, "left-hand-side of assignment must take the form `a`, `a.b` or `a[b]`");
        }
    }

    public static Stmt CheckAssignment(ParserState state, Expr lhs, TypeExpr? ty, AssignOp? op, Expr rhs, Span span)
    {
        if (op is not null && lhs is TupleExpr or ListExpr)
        {
            // For augmented assignment, Starlark doesn't allow tuple/list.
            throw state.MakeError(lhs.Span, "left-hand-side of modifying assignment cannot be a list or tuple");
        }
        var target = CheckAssign(state, lhs);
        if (ty is not null)
        {
            string? err = op is not null
                ? "type annotations not allowed on augmented assignments"
                : target is TupleTarget
                    ? "type annotations not allowed on multiple assignments"
                    : null;
            if (err is not null)
            {
                throw state.MakeError(ty.Span, err);
            }
        }
        return op is { } o
            ? new AssignModifyStmt(target, o, rhs, span)
            : new AssignStmt(target, ty, rhs, span);
    }

    public static Expr RejectUnparenthesizedTupleTrailingComma(ParserState state, int begin, int end) =>
        throw state.MakeError(new Span(begin, end), "unparenthesized tuple with trailing comma");

    /// <summary>A <c>load</c> with no symbols is an error, but we recover so later errors are still found.</summary>
    public static LoadStmt CheckLoad0(AstString module, ParserState state, int l, int r)
    {
        state.Error(module.Span, "`load` statement requires at least two arguments");
        return new LoadStmt(module, [], new Span(l, r));
    }

    public static FStringExpr FStringFromParts(List<FStringElement> parts, int begin, int end, ParserState state)
    {
        var span = new Span(begin, end);
        if (!state.Dialect.EnableFStrings)
        {
            state.Error(span, "Your Starlark dialect must enable f-strings to use them");
        }

        var format = new StringBuilder();
        var expressions = new List<Expr>();
        foreach (var part in parts)
        {
            switch (part)
            {
                case FStringElement.Text t:
                    // Escape literal braces for the format string.
                    foreach (char c in t.Value)
                    {
                        switch (c)
                        {
                            case '{': format.Append("{{"); break;
                            case '}': format.Append("}}"); break;
                            default: format.Append(c); break;
                        }
                    }
                    break;
                case FStringElement.Expression e:
                    expressions.Add(e.Expr);
                    format.Append(e.Conv == FStringConv.Repr ? "{!r}" : "{}");
                    break;
            }
        }
        return new FStringExpr(format.ToString(), expressions, span);
    }

    public static StarlarkSyntaxException FStringConvError(int l, int r, ParserState state) =>
        state.MakeError(new Span(l, r), "invalid f-string conversion specifier, expected 's' or 'r'");

    public static TypeExpr DialectCheckType(ParserState state, Expr x)
    {
        if (state.Dialect.EnableTypes == DialectTypes.Disable)
        {
            throw state.MakeError(x.Span, "type annotations are not allowed in this dialect");
        }
        TypeExprUnpack.Unpack(x, state);
        return new TypeExpr(x);
    }
}
