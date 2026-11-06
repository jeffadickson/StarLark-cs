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

/// <summary>
/// The memory budget of the run in progress on this thread. Operations that can create a large object in one step ask
/// <see cref="Check"/> first, so a script cannot jump past <see cref="Interpreter.MaxAllocatedBytes"/> between polls.
/// </summary>
internal static class MemoryBudget
{
    [ThreadStatic] private static long _maxBytes;

    /// <summary>Sets the per-object ceiling for the current thread and returns the previous value.</summary>
    public static long Set(long maxBytes)
    {
        long old = _maxBytes;
        _maxBytes = maxBytes;
        return old;
    }

    /// <summary>Throws <see cref="ExecutionLimitException"/> if one object of about <paramref name="bytes"/> would exceed the budget.</summary>
    public static void Check(long bytes)
    {
        if (_maxBytes > 0 && bytes > _maxBytes)
        {
            throw new ExecutionLimitException("memory", $"Script tried to build an object of about {bytes} bytes, over the limit of {_maxBytes}");
        }
    }

    /// <summary>The same check for a count of elements of <paramref name="elementBytes"/> each.</summary>
    public static void CheckElements(long count, int elementBytes) => Check(checked(count * elementBytes));

    /// <summary>The longest string any one operation may build, in chars, even with no memory limit set (the repetition ceiling).</summary>
    public const long MaxStringChars = int.MaxValue / 2;

    /// <summary>
    /// For a string being built piece by piece (<c>replace</c>, <c>str</c> of a container): throws if it would grow to
    /// <paramref name="chars"/> chars, past the memory budget or past <see cref="MaxStringChars"/>.
    /// </summary>
    public static void CheckString(long chars)
    {
        if (chars > MaxStringChars) throw new StarlarkException("String result is too large");
        Check(chars * 2);
    }
}
