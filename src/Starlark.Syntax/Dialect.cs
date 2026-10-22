namespace Starlark.Syntax;

/// <summary>How to handle type annotations.</summary>
public enum DialectTypes
{
    /// <summary>Prohibit types at parse time.</summary>
    Disable,

    /// <summary>Allow types at parse time, but ignore them at runtime.</summary>
    ParseOnly,

    /// <summary>Check types at runtime.</summary>
    Enable,
}

/// <summary>
/// Starlark language features to enable. Use <c>Dialect.Standard with { ... }</c> to customise.
/// </summary>
public sealed record Dialect
{
    /// <summary>Are <c>def</c> statements permitted.</summary>
    public bool EnableDef { get; init; } = true;

    /// <summary>Are <c>lambda</c> expressions permitted.</summary>
    public bool EnableLambda { get; init; } = true;

    /// <summary>Are <c>load</c> statements permitted.</summary>
    public bool EnableLoad { get; init; } = true;

    /// <summary>Are <c>*</c> keyword-only arguments allowed (PEP 3102).</summary>
    public bool EnableKeywordOnlyArguments { get; init; }

    /// <summary>Are <c>/</c> positional-only arguments allowed.</summary>
    public bool EnablePositionalOnlyArguments { get; init; }

    /// <summary>Are expressions allowed in type positions (PEP 484).</summary>
    public DialectTypes EnableTypes { get; init; } = DialectTypes.Disable;

    /// <summary>Do <c>load()</c> statements reexport their definition.</summary>
    public bool EnableLoadReexport { get; init; } = true;

    /// <summary>Are <c>for</c>, <c>if</c> and other statements allowed at the top level.</summary>
    public bool EnableTopLevelStmt { get; init; }

    /// <summary>Are <c>f"{expression}"</c> strings supported.</summary>
    public bool EnableFStrings { get; init; }

    /// <summary>Follow the Starlark language standard as much as possible.</summary>
    public static Dialect Standard => new();

    /// <summary>Deprecated; extend <see cref="Standard"/> instead.</summary>
    public static Dialect Extended => new()
    {
        EnableKeywordOnlyArguments = true,
        EnableTypes = DialectTypes.Enable,
        EnableTopLevelStmt = true,
    };

    /// <summary>Every option on. Used by the original's self tests, and ours.</summary>
    public static Dialect AllOptionsInternal => new()
    {
        EnableKeywordOnlyArguments = true,
        EnablePositionalOnlyArguments = true,
        EnableTypes = DialectTypes.Enable,
        EnableTopLevelStmt = true,
        EnableFStrings = true,
    };
}
