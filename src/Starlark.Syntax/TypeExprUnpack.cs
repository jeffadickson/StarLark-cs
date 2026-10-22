namespace Starlark.Syntax;

/// <summary>Path component of a type, e.g. <c>a.b.c</c>.</summary>
public sealed record TypePath(IdentExpr First, IReadOnlyList<Spanned<string>> Rest);

/// <summary>A validated type expression.</summary>
public abstract record TypeNode(Span Span)
{
    public sealed record Ellipsis(Span S) : TypeNode(S);
    public sealed record Path(Span S, TypePath Value) : TypeNode(S);

    /// <summary><c>list[str]</c> or <c>ns.Generic[str]</c>.</summary>
    public sealed record Index(Span S, TypePath Base, TypeNode Arg) : TypeNode(S);

    /// <summary><c>dict[str, int]</c> or <c>typing.Callable[[int], str]</c>.</summary>
    public sealed record Index2(Span S, TypePath Base, TypeNode Arg0, TypeNode Arg1) : TypeNode(S);

    /// <summary>List argument in <c>typing.Callable[[int], str]</c>.</summary>
    public sealed record List(Span S, IReadOnlyList<TypeNode> Items) : TypeNode(S);

    public sealed record Union(Span S, IReadOnlyList<TypeNode> Items) : TypeNode(S);
    public sealed record Tuple(Span S, IReadOnlyList<TypeNode> Items) : TypeNode(S);
}

/// <summary>Checks that an expression in type position is a legal type, and decodes it.</summary>
internal static class TypeExprUnpack
{
    private static StarlarkSyntaxException Err(ParserState st, Span span, string message) => st.MakeError(span, message);

    private static StarlarkSyntaxException Invalid(ParserState st, Expr e, string what) =>
        Err(st, e.Span, $"{what} expression is not allowed in type expression");

    private static TypePath UnpackPath(Expr expr, ParserState st)
    {
        switch (expr)
        {
            case IdentExpr id:
                return new TypePath(id, []);
            case DotExpr dot:
            {
                Expr current = dot.Target;
                var rem = new List<Spanned<string>> { new(dot.Field.Value, dot.Field.Span) };
                while (true)
                {
                    switch (current)
                    {
                        case DotExpr o:
                            current = o.Target;
                            rem.Add(new Spanned<string>(o.Field.Value, o.Field.Span));
                            break;
                        case IdentExpr i:
                        {
                            rem.Reverse();
                            if (rem[^1].Node == "type")
                            {
                                string full = i.Name;
                                for (int k = 0; k < rem.Count - 1; k++)
                                {
                                    full += "." + rem[k].Node;
                                }
                                throw Err(st, current.Span,
                                    $"`{full}.type` is not allowed in type expression, use `{full}` instead");
                            }
                            return new TypePath(i, rem);
                        }
                        default:
                            throw Err(st, current.Span, "Only dot expression of form `ident.ident` is allowed in type expression");
                    }
                }
            }
            default:
                throw Err(st, expr.Span, "Expecting path like `a.b.c`");
        }
    }

    private static TypeNode UnpackArgument(Expr expr, ParserState st) =>
        expr is ListExpr l
            ? new TypeNode.List(expr.Span, l.Items.Select(x => UnpackArgument(x, st)).ToList())
            : Unpack(expr, st);

    public static TypeNode Unpack(Expr expr, ParserState st)
    {
        var span = expr.Span;
        switch (expr)
        {
            case TupleExpr t:
                return new TypeNode.Tuple(span, t.Items.Select(x => Unpack(x, st)).ToList());
            case DotExpr:
                return new TypeNode.Path(span, UnpackPath(expr, st));
            case CallExpr:
                throw Invalid(st, expr, "call");
            case IndexExpr i:
            {
                var path = UnpackPath(i.Target, st);
                return new TypeNode.Index(span, path, Unpack(i.Index, st));
            }
            case Index2Expr i2:
            {
                var path = UnpackPath(i2.Target, st);
                var a = UnpackArgument(i2.First, st);
                var b = UnpackArgument(i2.Second, st);
                return new TypeNode.Index2(span, path, a, b);
            }
            case SliceExpr:
                throw Invalid(st, expr, "slice");
            case IdentExpr:
                return new TypeNode.Path(span, UnpackPath(expr, st));
            case LambdaExpr:
                throw Invalid(st, expr, "lambda");
            case StringLiteral:
                throw Invalid(st, expr, "string literal");
            case IntLiteral:
                throw Invalid(st, expr, "int");
            case FloatLiteral:
                throw Invalid(st, expr, "float");
            case EllipsisLiteral:
                return new TypeNode.Ellipsis(span);
            case NotExpr:
                throw Invalid(st, expr, "not");
            case MinusExpr:
                throw Invalid(st, expr, "minus");
            case PlusExpr:
                throw Invalid(st, expr, "plus");
            case BitNotExpr:
                throw Invalid(st, expr, "bit not");
            case BinaryExpr { Op: BinOp.BitOr } b:
                return new TypeNode.Union(span, [Unpack(b.Left, st), Unpack(b.Right, st)]);
            case BinaryExpr:
                throw Invalid(st, expr, "bin op except `|`");
            case IfExpr:
                throw Invalid(st, expr, "if");
            case ListExpr l:
                if (l.Items.Count == 0)
                {
                    throw Err(st, span, "Empty list is not allowed in type expression");
                }
                if (l.Items.Count == 1)
                {
                    throw Invalid(st, expr, "list of 1 element");
                }
                return new TypeNode.Union(span, l.Items.Select(x => Unpack(x, st)).ToList());
            case DictExpr:
                throw Invalid(st, expr, "dict");
            case ListComprehension:
                throw Invalid(st, expr, "list comprehension");
            case DictComprehension:
                throw Invalid(st, expr, "dict comprehension");
            case FStringExpr:
                throw Invalid(st, expr, "f-string");
            case BytesLiteral:
                throw Invalid(st, expr, "bytes literal");
            default:
                throw Invalid(st, expr, expr.GetType().Name);
        }
    }
}
