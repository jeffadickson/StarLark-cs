using System.Reflection;

namespace GameAI;

/// <summary>Reads the .star scripts embedded in this application's assembly.</summary>
public static class Scripts
{
    /// <summary>Returns the source of an embedded script such as <c>rules/cc.star</c>, or null if there is none.</summary>
    public static string? Read(string path)
    {
        var asm = typeof(Scripts).Assembly;
        var want = "scripts/" + path.Replace((char)92, '/').TrimStart('/');
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.Replace((char)92, '/') == want);
        if (name is null) return null;
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
