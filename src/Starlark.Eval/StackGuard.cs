using System.Runtime.CompilerServices;

namespace Starlark.Eval;

/// <summary>
/// Protects the recursive walkers over script values (printing, equality, ordering, hashing, freezing) from deep nesting.
/// A script can build a list nested hundreds of thousands deep in a short loop, and a stack overflow cannot be caught: it
/// ends the whole process. Each walker calls <see cref="Check"/> on entry, which turns "about to run out of stack" into an
/// ordinary Starlark error.
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

    private static void Fail() => throw new StarlarkException("Value is nested too deeply");
}
