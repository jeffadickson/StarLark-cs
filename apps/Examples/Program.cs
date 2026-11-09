namespace Examples;

/// <summary>Reads the embedded example scripts.</summary>
public static class Scripts
{
    /// <summary>The source of <c>scripts/&lt;path&gt;</c>, or null if there is none.</summary>
    public static string? TryRead(string path)
    {
        var asm = typeof(Scripts).Assembly;
        var want = "scripts/" + path.Replace('\\', '/');
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.Replace('\\', '/') == want);
        if (name is null) return null;
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>The source of <c>scripts/&lt;path&gt;</c>; a missing file is a bug in the examples, so it throws.</summary>
    public static string Read(string path) => TryRead(path) ?? throw new FileNotFoundException("no embedded script " + path);
}

/// <summary>Three examples of embedding Starlark in a C# program.</summary>
public static class Program
{
    /// <summary><c>Examples pricing|config|webhooks</c> runs one example; no argument runs all three.</summary>
    public static int Main(string[] args)
    {
        var which = args.Length > 0 ? args[0] : "all";
        if (which is "pricing" or "all") PricingDemo.Run(Console.Out);
        if (which is "config" or "all") ConfigDemo.Run(Console.Out);
        if (which is "webhooks" or "all") WebhookDemo.Run(Console.Out);
        if (which is not ("pricing" or "config" or "webhooks" or "all"))
        {
            Console.Error.WriteLine("usage: Examples [pricing|config|webhooks]");
            return 2;
        }
        return 0;
    }
}
