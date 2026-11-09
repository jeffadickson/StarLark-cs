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
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method, AllowMultiple = false)]
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
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method, AllowMultiple = false)]
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
    /// <exception cref="ArgumentException">
    /// A visible method cannot be called from a script (it is generic, or takes or returns a pointer, a by-ref or a
    /// ref struct such as <see cref="Span{T}"/>), or two visible members share a script name (overloads, or a method
    /// named like a property). Rename one with <see cref="ExposeAttribute.Name"/> or mark it <see cref="HideAttribute"/>.
    /// </exception>
    public static List<(System.Reflection.MethodInfo Method, string Name)> Methods(Type type, bool snakeCase)
    {
        bool optIn = UsesOptIn(type);
        var names = new HashSet<string>(Properties(type, snakeCase).Select(p => p.Name));
        var result = new List<(System.Reflection.MethodInfo, string)>();
        foreach (var m in type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
        {
            if (m.IsSpecialName) continue;
            if (Attribute.IsDefined(m, typeof(HideAttribute))) continue;
            var mark = (ExposeAttribute?)Attribute.GetCustomAttribute(m, typeof(ExposeAttribute));
            if (optIn && mark is null) continue;
            if (m.ContainsGenericParameters || Unsupported(m.ReturnType) || m.GetParameters().Any(p => Unsupported(p.ParameterType)))
            {
                throw new ArgumentException($"{type.Name}.{m.Name} cannot be exposed to scripts: generic methods and pointer, by-ref "
                    + "or ref struct parameters and results are not supported; mark it [Hide].");
            }
            string name = mark?.Name ?? Name(m.Name, snakeCase);
            if (!names.Add(name))
            {
                throw new ArgumentException($"{type.Name} exposes several members named '{name}' (overloads are not supported); "
                    + "rename one with [Expose(\"...\")] or mark it [Hide].");
            }
            result.Add((m, name));
        }
        return result;
    }

    private static bool Unsupported(Type t) => t.IsByRef || t.IsByRefLike || t.IsPointer || t.IsFunctionPointer;

    /// <summary>True if any public property or method of the type is marked <see cref="ExposeAttribute"/>.</summary>
    public static bool UsesOptIn(Type type) =>
        type.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(m => m.MemberType is System.Reflection.MemberTypes.Property or System.Reflection.MemberTypes.Method)
            .Any(m => Attribute.IsDefined(m, typeof(ExposeAttribute)));

    private static string Name(string name, bool snakeCase) => snakeCase ? Interop.SnakeCase(name) : name;
}
