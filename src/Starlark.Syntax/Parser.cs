namespace Starlark.Syntax;

/// <summary>
/// Recursive descent + Pratt expression parser for Starlark.
/// <para>
/// The grammar is the official Starlark language specification
/// (https://github.com/bazelbuild/starlark/blob/master/spec.md#grammar-reference). Production names here
/// (<c>ParseDefStmt</c>, <c>ParseSimpleStmt</c>, <c>ParseTest</c>, <c>ParseOrTest</c>, <c>ParseAtom</c>,
/// <c>ParseForClause</c>, ...) mirror the non-terminals in that grammar. Operator precedence uses the
/// Pratt algorithm from matklad's "Simple but Powerful Pratt Parsing".
/// </para>
/// </summary>
internal sealed class Parser
{
    private readonly IEnumerator<Lexeme> _tokens;
    private readonly ParserState _state;

    /// <summary>Current token with its span; null at EOF (or after a deferred lexer error).</summary>
    private Lexeme? _current;

    /// <summary>Deferred lexer error encountered while advancing the token stream.</summary>
    private StarlarkSyntaxException? _pendingError;

    /// <summary>End offset of the last consumed token; used for EOF spans.</summary>
    private int _lastEnd;

    public Parser(IEnumerator<Lexeme> tokens, ParserState state)
    {
        _tokens = tokens;
        _state = state;
        _current = NextFrom();
    }

    private Lexeme? NextFrom()
    {
        if (!_tokens.MoveNext())
        {
            return null;
        }
        var l = _tokens.Current;
        if (l.IsError)
        {
            _pendingError = l.Error;
            return null;
        }
        return l;
    }

    private void Advance()
    {
        if (_current is { } c)
        {
            _lastEnd = c.End;
        }
        // A deferred error stays the first one.
        _current = _pendingError is null ? NextFrom() : null;
    }

    /// <summary>Consume and return the current token.</summary>
    private Lexeme Take()
    {
        var old = _current!.Value;
        Advance();
        return old;
    }

    private TokenKind? Peek => _current?.Token!.Kind;

    private bool PeekIs(TokenKind k) => _current is { } c && c.Token!.Kind == k;

    private int Pos => _current?.Start ?? _lastEnd;

    private Span SpanOf(int l, int r) => new(l, r);

    private void Expect(TokenKind k)
    {
        if (!PeekIs(k))
        {
            throw ErrorExpected(Token.Of(k).ToString());
        }
        Advance();
    }

    private bool Eat(TokenKind k)
    {
        if (PeekIs(k))
        {
            Advance();
            return true;
        }
        return false;
    }

    private void SkipNewlines()
    {
        while (Eat(TokenKind.Newline))
        {
        }
    }

    private StarlarkSyntaxException ErrorExpected(string what)
    {
        if (_pendingError is { } pending)
        {
            _pendingError = null;
            return pending;
        }
        if (_current is { } c)
        {
            return _state.MakeError(SpanOf(c.Start, c.End), $"Parse error: unexpected {c.Token}, expected {what}");
        }
        int pos = Pos;
        return _state.MakeError(SpanOf(pos, pos), $"Parse error: unexpected end of file, expected {what}");
    }

    private StarlarkSyntaxException ErrorAt(int start, int end, string message) =>
        _state.MakeError(SpanOf(start, end), message);

    // ==================== Top-level ====================

    public Stmt ParseModule()
    {
        SkipNewlines();
        int l = Pos;
        var stmts = new List<Stmt>();
        while (_current is not null)
        {
            stmts.Add(ParseStmt());
            SkipNewlines();
        }
        int r = _lastEnd;
        if (_pendingError is { } err)
        {
            throw err;
        }
        return GrammarUtil.Statements(stmts, l, r);
    }

    // ==================== Statements ====================

    private Stmt ParseStmt() => Peek switch
    {
        TokenKind.Def => ParseDefStmt(),
        TokenKind.If => ParseIfStmt(),
        TokenKind.For => ParseForStmt(),
        _ => ParseSimpleStmt(),
    };

    private Stmt ParseDefStmt()
    {
        int l = Pos;
        Advance(); // def
        var name = ParseAssignIdent();
        Expect(TokenKind.OpeningRound);
        var parameters = ParseCommaSeparatedDefParams();
        Expect(TokenKind.ClosingRound);
        TypeExpr? returnType = Eat(TokenKind.MinusGreater) ? ParseTypeExpr() : null;
        Expect(TokenKind.Colon);
        var body = ParseSuite();
        return new DefStmt(name, parameters, returnType, body, SpanOf(l, _lastEnd));
    }

    private Stmt ParseIfStmt()
    {
        int l = Pos;
        Advance(); // if
        return ParseIfBody(l);
    }

    private Stmt ParseIfBody(int l)
    {
        var cond = ParseTest();
        Expect(TokenKind.Colon);
        var suite = ParseSuite();
        int r = _lastEnd;
        switch (Peek)
        {
            case TokenKind.Elif:
            {
                int elL = Pos;
                Advance();
                var elseClause = ParseIfBody(elL);
                return new IfStmt(cond, suite, elseClause, SpanOf(l, elseClause.Span.End));
            }
            case TokenKind.Else:
            {
                Advance();
                Expect(TokenKind.Colon);
                var elseSuite = ParseSuite();
                return new IfStmt(cond, suite, elseSuite, SpanOf(l, _lastEnd));
            }
            default:
                return new IfStmt(cond, suite, null, SpanOf(l, r));
        }
    }

    private Stmt ParseForStmt()
    {
        int l = Pos;
        Advance(); // for
        var varExpr = ParseExprList();
        Expect(TokenKind.In);
        var over = ParseTest();
        Expect(TokenKind.Colon);
        var body = ParseSuite();
        int r = _lastEnd;
        var variable = GrammarUtil.CheckAssign(_state, varExpr);
        return new ForStmt(variable, over, body, SpanOf(l, r));
    }

    private Stmt ParseSuite()
    {
        if (!PeekIs(TokenKind.Newline))
        {
            // Simple suite on the same line.
            return ParseSimpleStmt();
        }
        SkipNewlines();
        Expect(TokenKind.Indent);
        SkipNewlines();
        int l = Pos;
        var stmts = new List<Stmt>();
        while (true)
        {
            stmts.Add(ParseStmt());
            SkipNewlines();
            if (PeekIs(TokenKind.Dedent) || _current is null)
            {
                break;
            }
        }
        int r = _lastEnd;
        Eat(TokenKind.Dedent);
        return GrammarUtil.Statements(stmts, l, r);
    }

    private Stmt ParseSimpleStmt()
    {
        int l = Pos;
        var stmts = new List<Stmt> { ParseSmallStmt() };
        while (Eat(TokenKind.Semicolon))
        {
            if (PeekIs(TokenKind.Newline) || _current is null)
            {
                break;
            }
            stmts.Add(ParseSmallStmt());
        }
        Expect(TokenKind.Newline);
        int r = _lastEnd;
        return stmts.Count == 1 ? stmts[0] : new StatementsStmt(stmts, SpanOf(l, r));
    }

    private Stmt ParseSmallStmt()
    {
        int l = Pos;
        switch (Peek)
        {
            case TokenKind.Return:
            {
                Advance();
                Expr? expr = Peek is TokenKind.Newline or TokenKind.Semicolon or null ? null : ParseTestList(false);
                return new ReturnStmt(expr, SpanOf(l, _lastEnd));
            }
            case TokenKind.Break:
                Advance();
                return new BreakStmt(SpanOf(l, _lastEnd));
            case TokenKind.Continue:
                Advance();
                return new ContinueStmt(SpanOf(l, _lastEnd));
            case TokenKind.Pass:
                Advance();
                return new PassStmt(SpanOf(l, _lastEnd));
            case TokenKind.Load:
                return ParseLoadStmt();
            default:
                return ParseAssignOrExprStmt();
        }
    }

    private Stmt ParseAssignOrExprStmt()
    {
        int l = Pos;
        var first = ParseTest();
        bool lhsIsExprList = PeekIs(TokenKind.Comma);
        var lhs = lhsIsExprList ? ParseTestListTail(l, first, false) : first;

        // Type annotation.
        TypeExpr? ty = Eat(TokenKind.Colon) ? ParseTypeExpr() : null;

        if (TryParseAssignOp(out var op))
        {
            var rhs = ParseTestList(false);
            int r = _lastEnd;
            return GrammarUtil.CheckAssignment(_state, lhs, ty, op, rhs, SpanOf(l, r));
        }
        if (lhsIsExprList || ty is not null)
        {
            throw ErrorExpected("assignment operator");
        }
        return new ExpressionStmt(lhs, SpanOf(l, _lastEnd));
    }

    /// <summary>Consumes an assignment operator if present. <paramref name="op"/> is null for plain <c>=</c>.</summary>
    private bool TryParseAssignOp(out AssignOp? op)
    {
        op = Peek switch
        {
            TokenKind.PlusEqual => AssignOp.Add,
            TokenKind.MinusEqual => AssignOp.Subtract,
            TokenKind.StarEqual => AssignOp.Multiply,
            TokenKind.SlashEqual => AssignOp.Divide,
            TokenKind.SlashSlashEqual => AssignOp.FloorDivide,
            TokenKind.PercentEqual => AssignOp.Percent,
            TokenKind.AmpersandEqual => AssignOp.BitAnd,
            TokenKind.PipeEqual => AssignOp.BitOr,
            TokenKind.CaretEqual => AssignOp.BitXor,
            TokenKind.LessLessEqual => AssignOp.LeftShift,
            TokenKind.GreaterGreaterEqual => AssignOp.RightShift,
            _ => null,
        };
        if (op is null && Peek != TokenKind.Equal)
        {
            return false;
        }
        Advance();
        return true;
    }

    private Stmt ParseLoadStmt()
    {
        int l = Pos;
        Advance(); // load
        Expect(TokenKind.OpeningRound);
        var module = ParseStringLiteral();

        // Empty load: load("module")
        if (Eat(TokenKind.ClosingRound))
        {
            return GrammarUtil.CheckLoad0(module, _state, l, _lastEnd);
        }

        Expect(TokenKind.Comma);

        var args = new List<LoadArg>();
        while (!PeekIs(TokenKind.ClosingRound))
        {
            var (local, their) = ParseLoadSymbol();
            if (PeekIs(TokenKind.Comma))
            {
                int cl = Pos;
                Advance();
                args.Add(new LoadArg(local, their, SpanOf(cl, _lastEnd)));
            }
            else
            {
                // Last symbol without a trailing comma.
                Expect(TokenKind.ClosingRound);
                args.Add(new LoadArg(local, their, null));
                return new LoadStmt(module, args, SpanOf(l, _lastEnd));
            }
        }

        Expect(TokenKind.ClosingRound);
        int r = _lastEnd;
        return args.Count == 0
            ? GrammarUtil.CheckLoad0(module, _state, l, r)
            : new LoadStmt(module, args, SpanOf(l, r));
    }

    private (AssignIdent, AstString) ParseLoadSymbol()
    {
        switch (Peek)
        {
            // Either `ident = "name"` ...
            case TokenKind.Identifier:
            {
                var id = Take();
                if (Eat(TokenKind.Equal))
                {
                    var their = ParseStringLiteral();
                    return (new AssignIdent(id.Token!.Text!, SpanOf(id.Start, id.End)), their);
                }
                throw ErrorAt(id.Start, id.End, "Parse error: expected string literal in load statement");
            }
            // ... or just `"name"`.
            case TokenKind.String:
            {
                var s = ParseStringLiteral();
                return (new AssignIdent(s.Value, s.Span), s);
            }
            default:
                throw ErrorExpected("string literal or identifier");
        }
    }

    // ==================== Expressions ====================

    /// <summary>
    /// A comma-separated list of <c>Test</c>s, producing a tuple if there is more than one or a trailing comma.
    /// With <paramref name="allowTrailingComma"/> false, an unparenthesised tuple ending in a comma is rejected.
    /// </summary>
    private Expr ParseTestList(bool allowTrailingComma)
    {
        int l = Pos;
        var first = ParseTest();
        return PeekIs(TokenKind.Comma) ? ParseTestListTail(l, first, allowTrailingComma) : first;
    }

    private Expr ParseTestListTail(int l, Expr first, bool allowTrailingComma)
    {
        var items = new List<Expr> { first };
        bool trailingComma = false;
        while (Eat(TokenKind.Comma))
        {
            trailingComma = true;
            if (IsTestStart())
            {
                items.Add(ParseTest());
                trailingComma = false;
            }
            else
            {
                break;
            }
        }
        int r = _lastEnd;
        if (items.Count == 1 && !trailingComma)
        {
            return items[0];
        }
        if (trailingComma && !allowTrailingComma)
        {
            return GrammarUtil.RejectUnparenthesizedTupleTrailingComma(_state, l, r);
        }
        return new TupleExpr(items, SpanOf(l, r));
    }

    /// <summary>
    /// A comma-separated list of <c>Expr</c> (BitOr level), for assignment targets and <c>for</c> variables.
    /// Excludes comparisons, <c>and</c>, <c>or</c>, <c>not</c>, <c>in</c>. A trailing comma is always rejected.
    /// </summary>
    private Expr ParseExprList()
    {
        int l = Pos;
        var first = ParseBitOrExpr();
        if (!PeekIs(TokenKind.Comma))
        {
            return first;
        }
        var items = new List<Expr> { first };
        bool trailingComma = false;
        while (Eat(TokenKind.Comma))
        {
            trailingComma = true;
            if (IsExprStart())
            {
                items.Add(ParseBitOrExpr());
                trailingComma = false;
            }
            else
            {
                break;
            }
        }
        int r = _lastEnd;
        if (trailingComma)
        {
            return GrammarUtil.RejectUnparenthesizedTupleTrailingComma(_state, l, r);
        }
        return new TupleExpr(items, SpanOf(l, r));
    }

    private Expr ParseBitOrExpr()
    {
        var lhs = ParseUnary();
        // BitOr has left binding power 7, which excludes comparisons (5) and and/or (1-4).
        return ContinueInfix(lhs, 7);
    }

    private bool IsTestStart() => IsExprStart() || PeekIs(TokenKind.Lambda);

    private bool IsExprStart() => Peek is
        TokenKind.Identifier or TokenKind.Int or TokenKind.Float or TokenKind.String or TokenKind.Bytes
        or TokenKind.FStringStart or TokenKind.OpeningRound or TokenKind.OpeningSquare or TokenKind.OpeningCurly
        or TokenKind.Plus or TokenKind.Minus or TokenKind.Tilde or TokenKind.Not or TokenKind.Ellipsis;

    /// <summary>A <c>Test</c>: includes the ternary and lambda.</summary>
    private Expr ParseTest()
    {
        if (PeekIs(TokenKind.Lambda))
        {
            return ParseLambda();
        }
        return ContinueTernary(ParseExpr(0));
    }

    /// <summary>An <c>OrTest</c>, used in a ternary condition and comprehension <c>if</c> clauses.</summary>
    private Expr ParseOrTest() => ParseExpr(0);

    private Expr ParseLambda()
    {
        int l = Pos;
        Advance(); // lambda
        var parameters = ParseCommaSeparatedLambdaParams();
        Expect(TokenKind.Colon);
        var body = ParseTest();
        return new LambdaExpr(parameters, body, SpanOf(l, _lastEnd));
    }

    // ==================== Pratt Expression Parser ====================

    private static bool IsComparison(BinOp op) => op is
        BinOp.Equal or BinOp.NotEqual or BinOp.Less or BinOp.Greater or BinOp.LessOrEqual
        or BinOp.GreaterOrEqual or BinOp.In or BinOp.NotIn;

    /// <summary>
    /// Starlark forbids chained comparisons such as <c>a &lt; b &lt; c</c>, because comparisons are not associative.
    /// </summary>
    private void RejectChainedComparison()
    {
        if (Peek is { } k && InfixBindingPower(k) is { } p && IsComparison(p.Op))
        {
            throw ErrorExpected("end of expression (comparisons are not associative in Starlark)");
        }
    }

    /// <summary>
    /// Binding powers encoding the Starlark operator precedence table (lowest precedence = lowest power).
    /// For left-associative operators <c>Left &lt; Right</c>; for the non-associative comparisons they are
    /// equal in spirit, with chains rejected by <see cref="RejectChainedComparison"/>.
    /// </summary>
    private static (BinOp Op, int Left, int Right)? InfixBindingPower(TokenKind k) => k switch
    {
        TokenKind.Or => (BinOp.Or, 1, 2),
        TokenKind.And => (BinOp.And, 3, 4),
        TokenKind.EqualEqual => (BinOp.Equal, 5, 6),
        TokenKind.BangEqual => (BinOp.NotEqual, 5, 6),
        TokenKind.LessThan => (BinOp.Less, 5, 6),
        TokenKind.GreaterThan => (BinOp.Greater, 5, 6),
        TokenKind.LessEqual => (BinOp.LessOrEqual, 5, 6),
        TokenKind.GreaterEqual => (BinOp.GreaterOrEqual, 5, 6),
        TokenKind.In => (BinOp.In, 5, 6),
        TokenKind.Not => (BinOp.NotIn, 5, 6), // `not in`, handled specially
        TokenKind.Pipe => (BinOp.BitOr, 7, 8),
        TokenKind.Caret => (BinOp.BitXor, 9, 10),
        TokenKind.Ampersand => (BinOp.BitAnd, 11, 12),
        TokenKind.LessLess => (BinOp.LeftShift, 13, 14),
        TokenKind.GreaterGreater => (BinOp.RightShift, 13, 14),
        TokenKind.Plus => (BinOp.Add, 15, 16),
        TokenKind.Minus => (BinOp.Subtract, 15, 16),
        TokenKind.Star => (BinOp.Multiply, 17, 18),
        TokenKind.Percent => (BinOp.Percent, 17, 18),
        TokenKind.Slash => (BinOp.Divide, 17, 18),
        TokenKind.SlashSlash => (BinOp.FloorDivide, 17, 18),
        _ => null,
    };

    private Expr ParseExpr(int minBp)
    {
        // `not` is a prefix operator at the level of comparisons: NotTest = "not" NotTest | CompTest,
        // so `not x in y` parses as `not (x in y)`.
        Expr lhs;
        if (PeekIs(TokenKind.Not) && minBp <= 5)
        {
            int l = Pos;
            Advance();
            var e = ParseExpr(5);
            lhs = new NotExpr(e, SpanOf(l, _lastEnd));
        }
        else
        {
            lhs = ParseUnary();
        }
        return ContinueInfix(lhs, minBp);
    }

    /// <summary>Continue parsing infix operators on an already-parsed left-hand side.</summary>
    private Expr ContinueInfix(Expr lhs, int minBp)
    {
        while (Peek is { } kind)
        {
            if (kind == TokenKind.Not)
            {
                // `not in` is a two-token operator.
                const int leftBp = 5;
                const int rightBp = 6;
                if (leftBp < minBp)
                {
                    break;
                }
                Advance();
                if (!PeekIs(TokenKind.In))
                {
                    throw ErrorExpected("'in' after 'not'");
                }
                Advance();
                var rhs = ParseExpr(rightBp);
                lhs = new BinaryExpr(lhs, BinOp.NotIn, rhs, SpanOf(lhs.Span.Begin, rhs.Span.End));
                RejectChainedComparison();
                continue;
            }

            if (InfixBindingPower(kind) is not { } p)
            {
                break;
            }
            if (p.Left < minBp)
            {
                break;
            }
            Advance(); // operator
            var right = ParseExpr(p.Right);
            lhs = new BinaryExpr(lhs, p.Op, right, SpanOf(lhs.Span.Begin, right.Span.End));
            if (IsComparison(p.Op))
            {
                RejectChainedComparison();
            }
        }
        return lhs;
    }

    private Expr ParseUnary()
    {
        int l = Pos;
        switch (Peek)
        {
            case TokenKind.Plus:
                Advance();
                return new PlusExpr(ParseUnary(), SpanOf(l, _lastEnd));
            case TokenKind.Minus:
                Advance();
                return new MinusExpr(ParseUnary(), SpanOf(l, _lastEnd));
            case TokenKind.Tilde:
                Advance();
                return new BitNotExpr(ParseUnary(), SpanOf(l, _lastEnd));
            default:
                return ParsePrimary();
        }
    }

    // ==================== Primary Expressions ====================

    private Expr ParsePrimary() => ContinuePrimary(ParseAtom());

    /// <summary>Parse the <c>.attr</c>, <c>[index]</c> and <c>(call)</c> suffixes on an already-parsed expression.</summary>
    private Expr ContinuePrimary(Expr lhs)
    {
        while (true)
        {
            int l = lhs.Span.Begin;
            switch (Peek)
            {
                case TokenKind.Dot:
                {
                    Advance();
                    var ident = ParseIdentifierString();
                    lhs = new DotExpr(lhs, ident, SpanOf(l, _lastEnd));
                    break;
                }
                case TokenKind.OpeningRound:
                {
                    Advance();
                    var args = ParseCommaSeparatedArgs();
                    Expect(TokenKind.ClosingRound);
                    int r = _lastEnd;
                    lhs = CallValidation.CheckCall(lhs, args, _state, SpanOf(l, r));
                    break;
                }
                case TokenKind.OpeningSquare:
                    Advance();
                    lhs = ParseIndexOrSlice(lhs, l);
                    break;
                default:
                    return lhs;
            }
        }
    }

    private Expr? ParseOptionalSliceBound(TokenKind terminator1, TokenKind? terminator2 = null)
    {
        if (PeekIs(terminator1) || (terminator2 is { } t2 && PeekIs(t2)))
        {
            return null;
        }
        return ParseTest();
    }

    /// <summary>After <c>:</c>: parses <c>[stop][:[step]]]</c> and the closing bracket.</summary>
    private Expr FinishSlice(Expr target, Expr? start, int l)
    {
        var stop = ParseOptionalSliceBound(TokenKind.ClosingSquare, TokenKind.Colon);
        Expr? step = null;
        if (Eat(TokenKind.Colon))
        {
            step = ParseOptionalSliceBound(TokenKind.ClosingSquare);
        }
        Expect(TokenKind.ClosingSquare);
        return new SliceExpr(target, start, stop, step, SpanOf(l, _lastEnd));
    }

    private Expr ParseIndexOrSlice(Expr expr, int l)
    {
        // [:...] — slice with no start.
        if (Eat(TokenKind.Colon))
        {
            return FinishSlice(expr, null, l);
        }

        var first = ParseTest();
        switch (Peek)
        {
            case TokenKind.Colon:
                Advance();
                return FinishSlice(expr, first, l);
            case TokenKind.Comma:
            {
                // [a, b]
                Advance();
                var second = ParseTest();
                Expect(TokenKind.ClosingSquare);
                return new Index2Expr(expr, first, second, SpanOf(l, _lastEnd));
            }
            default:
                Expect(TokenKind.ClosingSquare);
                return new IndexExpr(expr, first, SpanOf(l, _lastEnd));
        }
    }

    private Expr ParseAtom()
    {
        switch (Peek)
        {
            case TokenKind.Identifier:
            {
                var t = Take();
                return new IdentExpr(t.Token!.Text!, SpanOf(t.Start, t.End));
            }
            case TokenKind.Int:
            {
                var t = Take();
                return new IntLiteral(t.Token!.IntValue, SpanOf(t.Start, t.End));
            }
            case TokenKind.Float:
            {
                var t = Take();
                return new FloatLiteral(t.Token!.FloatValue, SpanOf(t.Start, t.End));
            }
            case TokenKind.String:
            {
                var s = ParseStringLiteral();
                return new StringLiteral(s.Value, s.Span);
            }
            case TokenKind.Bytes:
            {
                var t = Take();
                return new BytesLiteral(t.Token!.BytesValue!, SpanOf(t.Start, t.End));
            }
            case TokenKind.FStringStart:
                return ParseFString();
            case TokenKind.Ellipsis:
            {
                var t = Take();
                return new EllipsisLiteral(SpanOf(t.Start, t.End));
            }
            case TokenKind.OpeningRound:
            {
                int l = Pos;
                Advance();
                if (Eat(TokenKind.ClosingRound))
                {
                    return new TupleExpr([], SpanOf(l, _lastEnd));
                }
                var expr = ParseTestList(true);
                Expect(TokenKind.ClosingRound);
                return expr; // parenthesised expressions keep their inner span
            }
            case TokenKind.OpeningSquare:
                return ParseListOrComprehension();
            case TokenKind.OpeningCurly:
                return ParseDictOrComprehension();
            default:
                throw ErrorExpected("expression");
        }
    }

    private Expr ParseFString()
    {
        var start = Take();
        int l = start.Start;
        var parts = new List<FStringElement>();
        while (Peek is not (TokenKind.FStringEnd or null))
        {
            if (PeekIs(TokenKind.FStringText))
            {
                parts.Add(new FStringElement.Text(Take().Token!.Text!));
            }
            else if (PeekIs(TokenKind.FStringExprStart))
            {
                Advance();
                var expr = ParseTest();
                var conv = FStringConv.None;
                if (PeekIs(TokenKind.FStringBang))
                {
                    int bl = Pos;
                    Advance();
                    if (!PeekIs(TokenKind.Identifier))
                    {
                        throw GrammarUtil.FStringConvError(bl, Pos, _state);
                    }
                    var id = Take();
                    conv = id.Token!.Text switch
                    {
                        "s" => FStringConv.Str,
                        "r" => FStringConv.Repr,
                        _ => throw GrammarUtil.FStringConvError(bl, id.End, _state),
                    };
                }
                Expect(TokenKind.FStringExprEnd);
                parts.Add(new FStringElement.Expression(expr, conv));
            }
            else
            {
                break;
            }
        }
        Expect(TokenKind.FStringEnd);
        return GrammarUtil.FStringFromParts(parts, l, _lastEnd, _state);
    }

    private Expr ParseListOrComprehension()
    {
        int l = Pos;
        Advance(); // [
        if (Eat(TokenKind.ClosingSquare))
        {
            return new ListExpr([], SpanOf(l, _lastEnd));
        }

        var first = ParseTest();

        if (PeekIs(TokenKind.For))
        {
            var (forClause, clauses) = ParseCompClauses();
            Expect(TokenKind.ClosingSquare);
            return new ListComprehension(first, forClause, clauses, SpanOf(l, _lastEnd));
        }

        var items = new List<Expr> { first };
        while (Eat(TokenKind.Comma))
        {
            if (PeekIs(TokenKind.ClosingSquare))
            {
                break;
            }
            items.Add(ParseTest());
        }
        Expect(TokenKind.ClosingSquare);
        return new ListExpr(items, SpanOf(l, _lastEnd));
    }

    private Expr ParseDictOrComprehension()
    {
        int l = Pos;
        Advance(); // {
        if (Eat(TokenKind.ClosingCurly))
        {
            return new DictExpr([], SpanOf(l, _lastEnd));
        }

        var key = ParseTest();
        Expect(TokenKind.Colon);
        var value = ParseTest();

        if (PeekIs(TokenKind.For))
        {
            var (forClause, clauses) = ParseCompClauses();
            Expect(TokenKind.ClosingCurly);
            return new DictComprehension(key, value, forClause, clauses, SpanOf(l, _lastEnd));
        }

        var entries = new List<(Expr, Expr)> { (key, value) };
        while (Eat(TokenKind.Comma))
        {
            if (PeekIs(TokenKind.ClosingCurly))
            {
                break;
            }
            var k = ParseTest();
            Expect(TokenKind.Colon);
            var v = ParseTest();
            entries.Add((k, v));
        }
        Expect(TokenKind.ClosingCurly);
        return new DictExpr(entries, SpanOf(l, _lastEnd));
    }

    /// <summary>The <c>{CompClause}</c> tail of a comprehension: <c>for LoopVariables in Test | if Test</c>.</summary>
    private (ForClause, List<Clause>) ParseCompClauses()
    {
        var forClause = ParseForClause();
        var clauses = new List<Clause>();
        while (true)
        {
            if (PeekIs(TokenKind.For))
            {
                clauses.Add(new ForClauseClause(ParseForClause()));
            }
            else if (PeekIs(TokenKind.If))
            {
                Advance();
                clauses.Add(new IfClause(ParseOrTest()));
            }
            else
            {
                return (forClause, clauses);
            }
        }
    }

    private ForClause ParseForClause()
    {
        Expect(TokenKind.For);
        var varExpr = ParseExprList();
        Expect(TokenKind.In);
        var over = ParseOrTest();
        return new ForClause(GrammarUtil.CheckAssign(_state, varExpr), over);
    }

    // ==================== Helpers ====================

    private AstString ParseIdentifierString()
    {
        if (!PeekIs(TokenKind.Identifier))
        {
            throw ErrorExpected("identifier");
        }
        var t = Take();
        return new AstString(t.Token!.Text!, SpanOf(t.Start, t.End));
    }

    private AssignIdent ParseAssignIdent()
    {
        var id = ParseIdentifierString();
        return new AssignIdent(id.Value, id.Span);
    }

    private AstString ParseStringLiteral()
    {
        if (!PeekIs(TokenKind.String))
        {
            throw ErrorExpected("string literal");
        }
        var t = Take();
        return new AstString(t.Token!.Text!, SpanOf(t.Start, t.End));
    }

    private TypeExpr ParseTypeExpr() => GrammarUtil.DialectCheckType(_state, ParseTest());

    private TypeExpr? ParseOptionalType() => Eat(TokenKind.Colon) ? ParseTypeExpr() : null;

    // ==================== Parameters ====================

    private List<Parameter> ParseCommaSeparatedDefParams() => ParseParams(TokenKind.ClosingRound, allowTypes: true);

    private List<Parameter> ParseCommaSeparatedLambdaParams() => ParseParams(TokenKind.Colon, allowTypes: false);

    private List<Parameter> ParseParams(TokenKind terminator, bool allowTypes)
    {
        var parameters = new List<Parameter>();
        if (PeekIs(terminator))
        {
            return parameters;
        }
        while (true)
        {
            parameters.Add(ParseParam(allowTypes));
            if (!Eat(TokenKind.Comma) || PeekIs(terminator))
            {
                break;
            }
        }
        return parameters;
    }

    private Parameter ParseParam(bool allowTypes)
    {
        int l = Pos;
        switch (Peek)
        {
            case TokenKind.Slash:
                Advance();
                return new Parameter(ParameterKind.Slash, null, null, null, SpanOf(l, _lastEnd));
            case TokenKind.StarStar:
            {
                Advance();
                var name = ParseAssignIdent();
                var ty = allowTypes ? ParseOptionalType() : null;
                return new Parameter(ParameterKind.KwArgs, name, ty, null, SpanOf(l, _lastEnd));
            }
            case TokenKind.Star:
            {
                Advance();
                if (PeekIs(TokenKind.Identifier))
                {
                    var name = ParseAssignIdent();
                    var ty = allowTypes ? ParseOptionalType() : null;
                    return new Parameter(ParameterKind.Args, name, ty, null, SpanOf(l, _lastEnd));
                }
                return new Parameter(ParameterKind.NoArgs, null, null, null, SpanOf(l, _lastEnd));
            }
            default:
            {
                var name = ParseAssignIdent();
                var ty = allowTypes ? ParseOptionalType() : null;
                Expr? def = Eat(TokenKind.Equal) ? ParseTest() : null;
                return new Parameter(ParameterKind.Normal, name, ty, def, SpanOf(l, _lastEnd));
            }
        }
    }

    // ==================== Arguments ====================

    private List<Argument> ParseCommaSeparatedArgs()
    {
        var args = new List<Argument>();
        if (PeekIs(TokenKind.ClosingRound))
        {
            return args;
        }
        while (true)
        {
            args.Add(ParseArgument());
            if (!Eat(TokenKind.Comma) || PeekIs(TokenKind.ClosingRound))
            {
                break;
            }
        }
        return args;
    }

    private Argument ParseArgument()
    {
        int l = Pos;
        switch (Peek)
        {
            case TokenKind.StarStar:
            {
                Advance();
                var e = ParseTest();
                return new StarStarArgument(e, SpanOf(l, _lastEnd));
            }
            case TokenKind.Star:
            {
                Advance();
                var e = ParseTest();
                return new StarArgument(e, SpanOf(l, _lastEnd));
            }
            case TokenKind.Identifier:
            {
                // `ident = expr` (named) or a positional expression that starts with an identifier.
                var id = Take();
                var span = SpanOf(id.Start, id.End);
                string name = id.Token!.Text!;
                if (Eat(TokenKind.Equal))
                {
                    var value = ParseTest();
                    return new NamedArgument(new AstString(name, span), value, SpanOf(l, _lastEnd));
                }
                // We already consumed the identifier: rebuild it and continue as a primary + infix + ternary.
                Expr expr = new IdentExpr(name, span);
                expr = ContinuePrimary(expr);
                expr = ContinueInfix(expr, 0);
                expr = ContinueTernary(expr);
                return new PositionalArgument(expr, SpanOf(l, _lastEnd));
            }
            default:
            {
                var e = ParseTest();
                return new PositionalArgument(e, SpanOf(l, _lastEnd));
            }
        }
    }

    /// <summary>Handle <c>expr if cond else expr</c> after the first expression.</summary>
    private Expr ContinueTernary(Expr expr)
    {
        if (!PeekIs(TokenKind.If))
        {
            return expr;
        }
        int l = expr.Span.Begin;
        Advance();
        var cond = ParseOrTest();
        Expect(TokenKind.Else);
        var elseExpr = ParseTest();
        return new IfExpr(cond, expr, elseExpr, SpanOf(l, _lastEnd));
    }
}
