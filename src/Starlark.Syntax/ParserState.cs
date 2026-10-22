namespace Starlark.Syntax;

/// <summary>Shared state while parsing: the dialect, the file, and recoverable errors collected so far.</summary>
internal sealed class ParserState(Dialect dialect, CodeMap codemap, List<StarlarkSyntaxException> errors)
{
    public Dialect Dialect { get; } = dialect;
    public CodeMap CodeMap { get; } = codemap;

    /// <summary>Recoverable errors. The first one is reported if parsing otherwise succeeds.</summary>
    public List<StarlarkSyntaxException> Errors { get; } = errors;

    public void Error(Span span, string message) => Errors.Add(new StarlarkSyntaxException(message, span, CodeMap));

    public StarlarkSyntaxException MakeError(Span span, string message) => new(message, span, CodeMap);
}
