namespace Starlark.Eval;

/// <summary>
/// Marks a property or method of a host class as visible to scripts when an instance is given to
/// <see cref="Interpreter.Expose"/>. If any member of a class carries this attribute, only the marked members are
/// visible and everything else stays hidden; a class with no marked members exposes all its public members, as before.
/// </summary>
/// <remarks>
/// The script-side name is the member's name in snake_case unless <see cref="Name"/> is set. Use
/// <see cref="HideAttribute"/> to keep a member out of an otherwise open class.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method | AttributeTargets.Field, AllowMultiple = false)]
public sealed class ExposeAttribute : Attribute
{
    /// <summary>Creates the attribute with the default (snake_case) script name.</summary>
    public ExposeAttribute()
    {
    }

    /// <summary>Creates the attribute with an explicit script name.</summary>
    public ExposeAttribute(string name) => Name = name;

    /// <summary>The name scripts use, or null for the member's name in snake_case.</summary>
    public string? Name { get; }
}

/// <summary>Keeps a public member of a host class out of the script-visible surface, whatever else is marked.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method | AttributeTargets.Field, AllowMultiple = false)]
public sealed class HideAttribute : Attribute
{
}

/// <summary>Which members of a host type scripts may see, and under what names.</summary>
internal static class ExposedMembers
{
    /// <summary>The public readable properties scripts may see, with their script names.</summary>
    public static IEnumerable<(System.Reflection.PropertyInfo Property, string Name)> Properties(Type type, bool snakeCase)
    {
        bool optIn = UsesOptIn(type);
        foreach (var p in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
            if (Attribute.IsDefined(p, typeof(HideAttribute))) continue;
            var mark = (ExposeAttribute?)Attribute.GetCustomAttribute(p, typeof(ExposeAttribute));
            if (optIn && mark is null) continue;
            yield return (p, mark?.Name ?? Name(p.Name, snakeCase));
        }
    }

    /// <summary>The public methods scripts may see, with their script names.</summary>
    public static IEnumerable<(System.Reflection.MethodInfo Method, string Name)> Methods(Type type, bool snakeCase)
    {
        bool optIn = UsesOptIn(type);
        foreach (var m in type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
        {
            if (m.IsSpecialName) continue;
            if (Attribute.IsDefined(m, typeof(HideAttribute))) continue;
            var mark = (ExposeAttribute?)Attribute.GetCustomAttribute(m, typeof(ExposeAttribute));
            if (optIn && mark is null) continue;
            yield return (m, mark?.Name ?? Name(m.Name, snakeCase));
        }
    }

    /// <summary>True if any public member of the type is marked <see cref="ExposeAttribute"/>.</summary>
    public static bool UsesOptIn(Type type) =>
        type.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Any(m => Attribute.IsDefined(m, typeof(ExposeAttribute)));

    private static string Name(string name, bool snakeCase) => snakeCase ? Interop.SnakeCase(name) : name;
}
