using System.Numerics;

namespace Starlark.Eval;

/// <summary>
/// A named, read-only collection of members: the <c>module</c> type of Go's <c>starlarkstruct.Module</c>. <c>json</c> and <c>typing</c>
/// are modules, and a host can build its own with <c>new StarModule("name", members)</c> and hand it to
/// <see cref="Interpreter.DefineValue"/>. A script sees <c>type(m) == "module"</c>, <c>str(m) == '&lt;module "name"&gt;'</c>, and
/// <c>m.member</c>; it cannot assign to a member, add one, or use the module as a dictionary key.
/// </summary>
/// <remarks>Immutable once built, so it is safe to share between tasks.</remarks>
public sealed class StarModule : IThreadSafeHostValue
{
    private readonly Dictionary<string, object?> _members;

    /// <summary>Creates a module. The members are copied.</summary>
    public StarModule(string name, IEnumerable<KeyValuePair<string, object?>> members)
    {
        Name = name;
        _members = new Dictionary<string, object?>(members);
    }

    /// <summary>The module's name, as <c>str()</c> shows it.</summary>
    public string Name { get; }

    /// <summary>The members, by name.</summary>
    public IReadOnlyDictionary<string, object?> Members => _members;

    /// <inheritdoc />
    public string TypeName => "module";

    /// <inheritdoc />
    public string Repr() => $"<module \"{Name}\">";

    /// <inheritdoc />
    public bool TryGetAttribute(string name, out object? value) => _members.TryGetValue(name, out value);

    /// <inheritdoc />
    public IEnumerable<string> Attributes() => _members.Keys.OrderBy(k => k, StringComparer.Ordinal);

    /// <inheritdoc />
    public int HashValue() => throw new StarlarkException("unhashable: module");

    /// <summary>Go's wording for a member that does not exist, with a suggestion when one is close.</summary>
    internal string MissingMember(string name)
    {
        string message = $"module has no .{name} field or method";
        var near = _members.Keys.Select(k => (Key: k, Distance: EditDistance(name, k))).OrderBy(p => p.Distance).FirstOrDefault();
        if (near.Key is not null && near.Distance <= Math.Max(1, name.Length / 2) && near.Distance < name.Length)
        {
            message += $"; did you mean .{near.Key}?";
        }
        return message;
    }

    /// <summary>Go's wording for assigning to a member.</summary>
    internal static string CannotAssign(string field) => $"can't assign to .{field} field of module";

    private static int EditDistance(string a, string b)
    {
        var row = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) row[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            int diagonal = row[0];
            row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return row[b.Length];
    }
}
