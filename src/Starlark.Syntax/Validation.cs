namespace Starlark.Syntax;

/// <summary>Call-site checks: argument ordering and duplicate names.</summary>
internal static class CallValidation
{
    /// <summary>
    /// Check a function call is well-formed. Starlark invariants: no positional arguments after named ones,
    /// no repeated names, at most one <c>*args</c> (after all positional and named arguments) and at most one
    /// <c>**kwargs</c>. Violations are recorded as recoverable errors.
    /// </summary>
    public static CallExpr CheckCall(Expr function, List<Argument> args, ParserState state, Span span)
    {
        var call = new CallExpr(function, args, span);
        try
        {
            CallArgsUnpack.Unpack(args, state);
        }
        catch (StarlarkSyntaxException e)
        {
            state.Errors.Add(e);
        }
        return call;
    }
}

/// <summary>Validated call arguments.</summary>
public sealed class CallArgsUnpack
{
    public IReadOnlyList<Argument> Positional { get; }
    public IReadOnlyList<Argument> Named { get; }
    public Argument? Star { get; }
    public Argument? StarStar { get; }

    private CallArgsUnpack(IReadOnlyList<Argument> pos, IReadOnlyList<Argument> named, Argument? star, Argument? starStar)
    {
        Positional = pos;
        Named = named;
        Star = star;
        StarStar = starStar;
    }

    private enum Stage { Positional, Named, Args, Kwargs }

    internal static CallArgsUnpack Unpack(IReadOnlyList<Argument> args, ParserState state)
    {
        StarlarkSyntaxException Err(Span span, string msg) => state.MakeError(span, msg);

        var stage = Stage.Positional;
        var namedArgs = new HashSet<string>();
        int numPos = 0, numNamed = 0;
        Argument? star = null, starStar = null;

        foreach (var arg in args)
        {
            switch (arg)
            {
                case PositionalArgument:
                    if (stage != Stage.Positional)
                    {
                        throw Err(arg.Span, "positional argument after non positional");
                    }
                    numPos++;
                    break;
                case NamedArgument n:
                    if (stage > Stage.Named)
                    {
                        throw Err(arg.Span, "named argument after *args or **kwargs");
                    }
                    if (!namedArgs.Add(n.Name.Value))
                    {
                        throw Err(n.Name.Span, "repeated named argument");
                    }
                    stage = Stage.Named;
                    numNamed++;
                    break;
                case StarArgument:
                    if (stage > Stage.Named)
                    {
                        throw Err(arg.Span, "Args array after another args or kwargs");
                    }
                    stage = Stage.Args;
                    star = arg;
                    break;
                case StarStarArgument:
                    if (stage == Stage.Kwargs)
                    {
                        throw Err(arg.Span, "Multiple kwargs dictionary in arguments");
                    }
                    stage = Stage.Kwargs;
                    starStar = arg;
                    break;
            }
        }

        return new CallArgsUnpack(
            args.Take(numPos).ToList(),
            args.Skip(numPos).Take(numNamed).ToList(),
            star,
            starStar);
    }

    /// <summary>Validate and split the arguments of a call. Throws on an ill-formed call.</summary>
    public static CallArgsUnpack Unpack(CallExpr call, CodeMap codemap) =>
        Unpack(call.Args, new ParserState(Dialect.Standard, codemap, []));
}

public enum DefRegularParamMode
{
    PosOnly,
    PosOrName,
    NameOnly,
}

public enum DefParamKind
{
    Regular,
    Args,
    Kwargs,
}

/// <summary>One function parameter, with markers <c>/</c> and <c>*</c> folded away.</summary>
public sealed record DefParam(
    AssignIdent Ident,
    DefParamKind Kind,
    DefRegularParamMode Mode,
    Expr? Default,
    TypeExpr? Type,
    Span Span);

/// <summary>
/// Sizes and indices that split a flat parameter list (markers omitted) into its parts.
/// </summary>
public readonly record struct DefParamIndices(int NumPositional, int NumPositionalOnly, int? Args, int? Kwargs)
{
    public Range PosOnly => 0..NumPositionalOnly;
    public Range PosOrNamed => NumPositionalOnly..NumPositional;

    public Range NamedOnly(int paramCount) =>
        (Args is { } a ? a + 1 : NumPositional)..(Kwargs ?? paramCount);
}

/// <summary>Post-processed, validated function parameters.</summary>
public sealed class DefParams
{
    public IReadOnlyList<DefParam> Params { get; }
    public DefParamIndices Indices { get; }

    private DefParams(IReadOnlyList<DefParam> p, DefParamIndices i)
    {
        Params = p;
        Indices = i;
    }

    public static DefParams Unpack(IReadOnlyList<Parameter> astParams, CodeMap codemap) =>
        Unpack(astParams, new ParserState(Dialect.Standard, codemap, []));

    private enum State { Normal, SeenSlash, SeenStar, SeenStarStar }

    internal static DefParams Unpack(IReadOnlyList<Parameter> astParams, ParserState st)
    {
        StarlarkSyntaxException Err(string msg, Span span) => st.MakeError(span, msg);

        var argset = new HashSet<string>();
        bool seenOptional = false;
        var result = new List<DefParam>(astParams.Count);
        int numPositional = 0;
        int? args = null, kwargs = null;
        int? indexOfStar = null;

        int slash = -1;
        for (int i = 0; i < astParams.Count; i++)
        {
            if (astParams[i].Kind == ParameterKind.Slash)
            {
                slash = i;
                break;
            }
        }
        if (slash == 0)
        {
            throw Err("`/` cannot be first parameter", astParams[0].Span);
        }
        int numPositionalOnly = Math.Max(slash, 0);

        var state = numPositionalOnly == 0 ? State.SeenSlash : State.Normal;

        for (int i = 0; i < astParams.Count; i++)
        {
            var param = astParams[i];
            var span = param.Span;

            if (param.Name is { } name && !argset.Add(name.Name))
            {
                throw Err("duplicated parameter name", span);
            }

            switch (param.Kind)
            {
                case ParameterKind.Normal:
                {
                    if (state >= State.SeenStarStar)
                    {
                        throw Err("Parameter after kwargs", span);
                    }
                    if (param.Default is null)
                    {
                        if (seenOptional && state < State.SeenStar)
                        {
                            throw Err("positional parameter after non positional", span);
                        }
                    }
                    else
                    {
                        seenOptional = true;
                    }
                    if (state < State.SeenStar)
                    {
                        numPositional++;
                    }
                    var mode = state < State.SeenSlash ? DefRegularParamMode.PosOnly
                        : state < State.SeenStar ? DefRegularParamMode.PosOrName
                        : DefRegularParamMode.NameOnly;
                    result.Add(new DefParam(param.Name!, DefParamKind.Regular, mode, param.Default, param.Type, span));
                    break;
                }
                case ParameterKind.NoArgs:
                    if (state >= State.SeenStar)
                    {
                        throw Err("Args parameter after another args or kwargs parameter", span);
                    }
                    state = State.SeenStar;
                    indexOfStar = i;
                    break;
                case ParameterKind.Slash:
                    if (state == State.SeenSlash)
                    {
                        throw Err("Multiple `/` in parameters", span);
                    }
                    if (state > State.SeenSlash)
                    {
                        throw Err("`/` must come before `*`, `*args` and `**kwargs`", span);
                    }
                    state = State.SeenSlash;
                    break;
                case ParameterKind.Args:
                    if (state >= State.SeenStar)
                    {
                        throw Err("Args parameter after another args or kwargs parameter", span);
                    }
                    state = State.SeenStar;
                    args = result.Count;
                    result.Add(new DefParam(param.Name!, DefParamKind.Args, DefRegularParamMode.NameOnly, null, param.Type, span));
                    break;
                case ParameterKind.KwArgs:
                    if (state >= State.SeenStarStar)
                    {
                        throw Err("Multiple kwargs dictionary in parameters", span);
                    }
                    kwargs = result.Count;
                    state = State.SeenStarStar;
                    result.Add(new DefParam(param.Name!, DefParamKind.Kwargs, DefRegularParamMode.NameOnly, null, param.Type, span));
                    break;
            }
        }

        if (indexOfStar is { } star)
        {
            if (star + 1 >= astParams.Count)
            {
                throw Err("`*` parameter must not be last", astParams[star].Span);
            }
            var next = astParams[star + 1];
            if (next.Kind != ParameterKind.Normal)
            {
                // Only `**kwargs` gets here; the rest are handled above.
                throw Err("`*` must be followed by named parameter", next.Span);
            }
        }

        return new DefParams(result, new DefParamIndices(numPositional, numPositionalOnly, args, kwargs));
    }
}

/// <summary>Checks that statements and dialect features only occur where they are allowed.</summary>
internal static class ModuleValidator
{
    public static void Validate(Stmt stmt, ParserState st)
    {
        CheckStmt(stmt, st, topLevel: true, insideFor: false, insideDef: false);
        stmt.VisitExpr(x => CheckExpr(x, st));
    }

    private static void ValidateParams(IReadOnlyList<Parameter> parameters, ParserState st)
    {
        if (!st.Dialect.EnableKeywordOnlyArguments)
        {
            foreach (var p in parameters.Where(p => p.Kind == ParameterKind.NoArgs))
            {
                st.Error(p.Span, "* keyword-only-arguments is not allowed in this dialect");
            }
        }
        if (!st.Dialect.EnablePositionalOnlyArguments)
        {
            foreach (var p in parameters.Where(p => p.Kind == ParameterKind.Slash))
            {
                st.Error(p.Span, "/ positional-only-arguments is not allowed in this dialect");
            }
        }
        try
        {
            DefParams.Unpack(parameters, st);
        }
        catch (StarlarkSyntaxException e)
        {
            st.Errors.Add(e);
        }
    }

    // Inside a `for` we allow continue/break, unless we go beneath a `def`. Inside a `def` we allow return.
    // All `load`s must occur at the top level. At the top level we only allow for/if when the dialect permits.
    private static void CheckStmt(Stmt stmt, ParserState st, bool topLevel, bool insideFor, bool insideDef)
    {
        var span = stmt.Span;
        switch (stmt)
        {
            case DefStmt def:
                if (!st.Dialect.EnableDef)
                {
                    st.Error(span, "`def` is not allowed in this dialect");
                }
                ValidateParams(def.Parameters, st);
                CheckStmt(def.Body, st, false, false, true);
                break;

            case ForStmt f:
                if (topLevel && !st.Dialect.EnableTopLevelStmt)
                {
                    st.Error(span, "`for` cannot be used outside `def` in this dialect");
                }
                else
                {
                    CheckStmt(f.Body, st, false, true, insideDef);
                }
                break;

            case IfStmt:
                if (topLevel && !st.Dialect.EnableTopLevelStmt)
                {
                    st.Error(span, "`if` cannot be used outside `def` in this dialect");
                }
                else
                {
                    stmt.VisitStmt(x => CheckStmt(x, st, false, insideFor, insideDef));
                }
                break;

            case BreakStmt when !insideFor:
                st.Error(span, "`break` cannot be used outside of a `for` loop");
                break;

            case ContinueStmt when !insideFor:
                st.Error(span, "`continue` cannot be used outside of a `for` loop");
                break;

            case ReturnStmt when !insideDef:
                st.Error(span, "`return` cannot be used outside of a `def` function");
                break;

            case LoadStmt:
                if (!topLevel)
                {
                    st.Error(span, "`load` must only occur at the top of a module");
                }
                if (!st.Dialect.EnableLoad)
                {
                    st.Error(span, "`load` is not allowed in this dialect");
                }
                break;

            default:
                stmt.VisitStmt(x => CheckStmt(x, st, topLevel, insideFor, insideDef));
                break;
        }
    }

    private static void CheckExpr(Expr x, ParserState st)
    {
        switch (x)
        {
            case EllipsisLiteral when st.Dialect.EnableTypes == DialectTypes.Disable:
                st.Error(x.Span, "`...` is not allowed in this dialect");
                break;
            case LambdaExpr lambda:
                if (!st.Dialect.EnableLambda)
                {
                    st.Error(x.Span, "`lambda` is not allowed in this dialect");
                }
                ValidateParams(lambda.Parameters, st);
                break;
        }
        x.VisitExpr(c => CheckExpr(c, st));
    }
}
