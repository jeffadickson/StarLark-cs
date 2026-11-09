using System.Runtime.CompilerServices;

namespace Starlark.Eval;

/// <summary>
/// Protects the recursive walkers over script values (printing, equality, ordering, hashing, freezing) and over syntax trees
/// (the resolver, the evaluator, the type checker) from deep nesting. A script can build a list nested hundreds of thousands
/// deep in a short loop, and a stack overflow cannot be caught: it ends the whole process. Each walker calls
/// <see cref="Check"/> (or <see cref="CheckSyntax"/>) on entry, which turns "about to run out of stack" into an ordinary error.
/// </summary>
internal static class StackGuard
{
    /// <summary>Throws a <see cref="StarlarkException"/> if the current thread is close to running out of stack.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Check()
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            Fail();
        }
    }

    /// <summary>
    /// <see cref="Check"/> for a walker over script values that visits one container: also counts a step, so a walk over a
    /// value that shares its parts (<c>t = (t, t)</c> sixty times is small, but has 2^60 paths) is stopped by the step
    /// limit, the deadline and cancellation instead of running inside one step forever.
    /// </summary>
    public static void CheckNode()
    {
        Check();
        ExecutionContext.Current?.Step();
    }

    /// <summary>The same check for walkers over a syntax tree, whose failure is about the program text rather than a value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CheckSyntax()
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            FailSyntax();
        }
    }

    /// <summary><see cref="CheckSyntax"/> for use inside an expression: always returns true.</summary>
    public static bool CheckedSyntax()
    {
        CheckSyntax();
        return true;
    }

    // "maximum recursion" is the phrase Go uses for comparing a value with itself through a cycle; "nested too deeply" says it for
    // the other cause (a legitimately deep value), so the message carries both.
    private static void Fail() => throw new StarlarkException("Value is nested too deeply: maximum recursion depth reached");

    private static void FailSyntax() =>
        throw new StarlarkException("Program is nested too deeply: excessive nesting for the remaining stack of this thread");
}
