using System.Reflection;

namespace Starlark.Eval;

/// <summary>
/// The Starlark packages that ship inside the library, loaded as <c>load("std/approx_math.star", ...)</c>. A host's own
/// <see cref="Interpreter.SourceLoader"/> is asked first, so it can replace or hide any of them.
/// </summary>
public static class StandardLibrary
{
    /// <summary>The source of a bundled package, or null if there is none with that identifier.</summary>
    public static string? Read(string id)
    {
        if (!id.StartsWith("std/", StringComparison.Ordinal)) return null;
        var asm = typeof(StandardLibrary).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.Replace((char)92, '/') == id);
        if (name is null) return null;
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>The identifiers of the bundled packages.</summary>
    public static IEnumerable<string> Packages() =>
        typeof(StandardLibrary).Assembly.GetManifestResourceNames()
            .Select(n => n.Replace((char)92, '/'))
            .Where(n => n.StartsWith("std/", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal);
}
