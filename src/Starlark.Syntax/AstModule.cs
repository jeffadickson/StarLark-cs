namespace Starlark.Syntax;

/// <summary>A <c>load</c> statement: the module and the symbols loaded from it (local name → source name).</summary>
public sealed record AstLoad(FileSpan Span, string ModuleId, IReadOnlyList<(string Local, string Their)> Symbols);

/// <summary>
/// A parsed Starlark module. Create with <see cref="Parse(string, string, Dialect)"/>.
/// </summary>
public sealed class AstModule
{
    private readonly LintSuppressions _lintSuppressions;
    private readonly List<Span> _commentSpans;

    public CodeMap CodeMap { get; }

    /// <summary>The module's statements (a <see cref="StatementsStmt"/> unless there is exactly one).</summary>
    public Stmt Statement { get; }

    public Dialect Dialect { get; }

    /// <summary>Opt-in typecheck, requested with <c>@starlark-rust: typecheck</c>.</summary>
    public bool Typecheck { get; }

    private AstModule(CodeMap codemap, Stmt statement, Dialect dialect, bool typecheck, LintSuppressions lint, List<Span> comments)
    {
        CodeMap = codemap;
        Statement = statement;
        Dialect = dialect;
        Typecheck = typecheck;
        _lintSuppressions = lint;
        _commentSpans = comments;
    }

    /// <summary>Parse a file on disk.</summary>
    public static AstModule ParseFile(string path, Dialect dialect) =>
        Parse(path, File.ReadAllText(path), dialect);

    /// <summary>
    /// Parse a Starlark module. <paramref name="filename"/> is for error messages only.
    /// Throws <see cref="StarlarkSyntaxException"/> on the first syntax error.
    /// </summary>
    public static AstModule Parse(string filename, string content, Dialect dialect)
    {
        bool typecheck = content.Contains("@starlark-rust: typecheck", StringComparison.Ordinal);
        var codemap = new CodeMap(filename, content);
        var lint = new LintSuppressions.Builder();
        var comments = new List<Span>();
        var errors = new List<StarlarkSyntaxException>();
        var state = new ParserState(dialect, codemap, errors);

        // Comments never reach the parser; they feed lint suppressions and the comment list.
        IEnumerable<Lexeme> Filtered()
        {
            var lexer = new Lexer(codemap);
            bool inCommentBlock = false;
            while (lexer.Next() is { } lexeme)
            {
                if (lexeme is { Token.Kind: TokenKind.Comment })
                {
                    lint.ParseComment(codemap, lexeme.Token.Text!, lexeme.Start, lexeme.End);
                    comments.Add(new Span(lexeme.Start, lexeme.End));
                    inCommentBlock = true;
                    continue;
                }
                if (inCommentBlock)
                {
                    lint.EndOfCommentBlock(codemap);
                    inCommentBlock = false;
                }
                yield return lexeme;
            }
            if (inCommentBlock)
            {
                lint.EndOfCommentBlock(codemap);
            }
        }

        using var tokens = Filtered().GetEnumerator();
        var statement = new Parser(tokens, state).ParseModule();
        if (errors.Count > 0)
        {
            throw errors[0];
        }

        // Validate where statements and features may occur.
        var validationErrors = new List<StarlarkSyntaxException>();
        ModuleValidator.Validate(statement, new ParserState(dialect, codemap, validationErrors));
        if (validationErrors.Count > 0)
        {
            throw validationErrors[0];
        }

        return new AstModule(codemap, statement, dialect, typecheck, lint.Build(), comments);
    }

    /// <summary>
    /// The <c>load</c> statements of the module. Empty if the dialect disables <c>load</c>.
    /// Only the top level is searched, since loads must be there.
    /// </summary>
    public IReadOnlyList<AstLoad> Loads()
    {
        var loads = new List<AstLoad>();
        void Visit(Stmt s)
        {
            switch (s)
            {
                case LoadStmt load:
                    loads.Add(new AstLoad(
                        new FileSpan(CodeMap, load.Module.Span),
                        load.Module.Value,
                        load.Args.Select(a => (a.Local.Name, a.Their.Value)).ToList()));
                    break;
                case StatementsStmt stmts:
                    foreach (var x in stmts.Statements)
                    {
                        Visit(x);
                    }
                    break;
            }
        }
        Visit(Statement);
        return loads;
    }

    public FileSpan FileSpan(Span span) => CodeMap.FileSpan(span);

    /// <summary>Locations where statements occur.</summary>
    public IReadOnlyList<FileSpan> StmtLocations()
    {
        var res = new List<FileSpan>();
        void Go(Stmt x)
        {
            if (x is not StatementsStmt)
            {
                res.Add(CodeMap.FileSpan(x.Span));
            }
            x.VisitStmt(Go);
        }
        Go(Statement);
        return res;
    }

    /// <summary>The top-level statements, flattening nested <see cref="StatementsStmt"/>.</summary>
    public IReadOnlyList<Stmt> TopLevelStmts()
    {
        var res = new List<Stmt>();
        void F(Stmt s)
        {
            if (s is StatementsStmt xs)
            {
                foreach (var x in xs.Statements)
                {
                    F(x);
                }
            }
            else
            {
                res.Add(s);
            }
        }
        F(Statement);
        return res;
    }

    /// <summary>Is the lint <paramref name="issueShortName"/> suppressed for <paramref name="issueSpan"/>?</summary>
    public bool IsSuppressed(string issueShortName, Span issueSpan) =>
        _lintSuppressions.IsSuppressed(issueShortName, issueSpan);

    /// <summary>Spans of all comments, in source order; each includes the leading <c>#</c>.</summary>
    public IReadOnlyList<Span> Comments => _commentSpans;
}
