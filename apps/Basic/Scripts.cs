namespace Basic;

/// <summary>Reads the embedded interpreter scripts and the bundled BASIC programs.</summary>
public static class Scripts
{
    private static string? ReadResource(string want)
    {
        var asm = typeof(Scripts).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.Replace((char)92, '/') == want);
        if (name is null) return null;
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>Returns the source of an embedded script such as <c>basic.star</c>, or null.</summary>
    public static string? Read(string path) => ReadResource("scripts/" + path.Replace((char)92, '/').TrimStart('/'));

    /// <summary>Returns a bundled BASIC program by name (without the .bas), or null.</summary>
    public static string? ReadProgram(string name) => ReadResource("programs/" + name + ".bas");

    /// <summary>The names of the bundled BASIC programs.</summary>
    public static IEnumerable<string> ProgramNames() =>
        typeof(Scripts).Assembly.GetManifestResourceNames()
            .Select(n => n.Replace((char)92, '/'))
            .Where(n => n.StartsWith("programs/") && n.EndsWith(".bas"))
            .Select(n => n["programs/".Length..^".bas".Length])
            .OrderBy(n => n);
}
