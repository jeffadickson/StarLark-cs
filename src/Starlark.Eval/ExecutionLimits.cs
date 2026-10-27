namespace Starlark.Eval;

/// <summary>
/// Thrown when a script exceeds the step or time budget set on <see cref="Interpreter"/>. It is deliberately not a
/// <see cref="StarlarkException"/>: scripts cannot observe it, and hosts can tell "the script is wrong" from
/// "the script ran too long".
/// </summary>
public sealed class ExecutionLimitException : Exception
{
    /// <summary>Which budget ran out.</summary>
    public string Limit { get; }

    /// <summary>Creates the exception for the named budget.</summary>
    public ExecutionLimitException(string limit, string message) : base(message) => Limit = limit;
}
