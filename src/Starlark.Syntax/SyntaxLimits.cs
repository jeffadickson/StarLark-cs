using System.Runtime.CompilerServices;

namespace Starlark.Syntax;

/// <summary>
/// Limits on the shape of source text, adopted from Starlark-Go so that no input can overflow the stack while it is parsed,
/// checked or run.
/// </summary>
public static class SyntaxLimits
{
    /// <summary>
    /// The deepest nesting the parser accepts: brackets, parentheses and braces (also counted by the lexer, as in Go's
    /// scanner), blocks, lambdas, <c>elif</c>s, unary operators and <c>not</c>, right operands of binary operators,
    /// conditional expressions, f-strings and <c>.name</c>/<c>[index]</c>/<c>(call)</c> suffixes. Past it the parse fails
    /// with a syntax error containing "excessive nesting". A chain of left-associative operators (<c>1 + 1 + ... + 1</c>) is
    /// not nesting and has no limit: every walker over the tree visits it with a loop (see <see cref="BinaryExpr.LeftChain"/>).
    /// </summary>
    public const int MaxNesting = 1000;

    /// <summary>
    /// For the recursive walkers over syntax trees in this library: throws <see cref="InsufficientExecutionStackException"/>
    /// (which can be caught, unlike a stack overflow) if the thread is close to running out of stack. Trees from the parser are
    /// at most <see cref="MaxNesting"/> deep; this protects trees built by hand and threads with very small stacks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EnsureStack() => RuntimeHelpers.EnsureSufficientExecutionStack();

    /// <summary>The same check during parsing and validation, where running short of stack is reported as a syntax error.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EnsureStack(ParserState st, Span span)
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw st.MakeError(span, "Parse error: excessive nesting: the thread ran out of stack");
        }
    }
}
