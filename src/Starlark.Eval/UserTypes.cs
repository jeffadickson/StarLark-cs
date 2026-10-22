using System.Numerics;
using System.Text;

namespace Starlark.Eval;

/// <summary>
/// The result of <c>struct(a=1, b=2)</c>: an immutable-ish bag of named fields with attribute access.
/// Fields keep their insertion order.
/// </summary>
public sealed class StarStruct
{
    private readonly List<KeyValuePair<string, object?>> _fields;

    public StarStruct(IEnumerable<KeyValuePair<string, object?>> fields) => _fields = [.. fields];

    public IReadOnlyList<KeyValuePair<string, object?>> Fields => _fields;

    public bool TryGet(string name, out object? value)
    {
        foreach (var kv in _fields)
        {
            if (kv.Key == name)
            {
                value = kv.Value;
                return true;
            }
        }
        value = null;
        return false;
    }

    public void Freeze()
    {
        foreach (var kv in _fields) Freezer.Freeze(kv.Value);
    }
}

/// <summary>A field of a <see cref="StarRecordType"/>: its type and optional default.</summary>
public sealed record RecordField(string Name, Ty Type, object? Default, bool HasDefault);

/// <summary>
/// A record type created by <c>record(host=str, port=field(int, 80))</c>. Calling it builds a
/// <see cref="StarRecord"/>, checking every field against its declared type.
/// </summary>
public sealed class StarRecordType : ICallable, IUserType
{
    public StarRecordType(IReadOnlyList<RecordField> fields, string? name = null)
    {
        Fields = fields;
        Name = name ?? "record";
        Ty = new Ty.Custom(Name, this);
    }

    public string Name { get; internal set; }
    public string TypeName => "record";
    public IReadOnlyList<RecordField> Fields { get; }
    public Ty Ty { get; }

    public bool IsInstance(object? v) => v is StarRecord r && ReferenceEquals(r.Type, this);

    public object? Call(Interpreter interp, IReadOnlyList<object?> args, IReadOnlyList<KeyValuePair<string, object?>> kwargs)
    {
        if (args.Count > 0)
        {
            throw new StarlarkException($"Too many positional arguments: records are constructed with named arguments only");
        }
        var values = new object?[Fields.Count];
        var set = new bool[Fields.Count];
        foreach (var (name, value) in kwargs)
        {
            int i = Fields.ToList().FindIndex(f => f.Name == name);
            if (i < 0) throw new StarlarkException($"Unexpected parameter named `{name}` for call to `{Name}`");
            if (set[i]) throw new StarlarkException($"Argument `{name}` was supplied more than once");
            Annotations.Check(Fields[i].Type, value, "argument", name);
            values[i] = value;
            set[i] = true;
        }
        for (int i = 0; i < Fields.Count; i++)
        {
            if (set[i]) continue;
            if (!Fields[i].HasDefault) throw new StarlarkException($"Missing parameter `{Fields[i].Name}` for call to `{Name}`");
            values[i] = Fields[i].Default;
        }
        return new StarRecord(this, values);
    }
}

public sealed class StarRecord(StarRecordType type, object?[] values)
{
    public StarRecordType Type { get; } = type;
    public object?[] Values { get; } = values;

    public bool TryGet(string name, out object? value)
    {
        for (int i = 0; i < Type.Fields.Count; i++)
        {
            if (Type.Fields[i].Name == name)
            {
                value = Values[i];
                return true;
            }
        }
        value = null;
        return false;
    }

    public void Freeze()
    {
        foreach (var v in Values) Freezer.Freeze(v);
    }
}

/// <summary>Returned by <c>field(type, default)</c>; only meaningful inside <c>record(...)</c>.</summary>
public sealed record FieldSpec(Ty Type, object? Default, bool HasDefault);

/// <summary>An enum type created by <c>enum("a", "b")</c>.</summary>
public sealed class StarEnumType : ICallable, IUserType
{
    public StarEnumType(IReadOnlyList<object?> values)
    {
        Values = values;
        Name = "enum";
        Ty = new Ty.Custom("enum", this);
        Instances = values.Select((v, i) => new StarEnumValue(this, v, i)).ToList();
    }

    public string Name { get; internal set; }
    public string TypeName => "enum";
    public IReadOnlyList<object?> Values { get; }
    public IReadOnlyList<StarEnumValue> Instances { get; }
    public Ty Ty { get; }

    public bool IsInstance(object? v) => v is StarEnumValue e && ReferenceEquals(e.Type, this);

    public object? Call(Interpreter interp, IReadOnlyList<object?> args, IReadOnlyList<KeyValuePair<string, object?>> kwargs)
    {
        var r = new ArgReader(Name, args, kwargs);
        var x = r.Required("value");
        r.Done();
        if (x is StarEnumValue ev && ReferenceEquals(ev.Type, this)) return ev;
        foreach (var inst in Instances)
        {
            if (Ops.Equal(inst.Value, x)) return inst;
        }
        throw new StarlarkException(
            $"Value {Repr.Of(x)} is not a valid element of this enumeration, expected one of: {string.Join(", ", Values.Select(Repr.Of))}");
    }
}

public sealed class StarEnumValue(StarEnumType type, object? value, int index)
{
    public StarEnumType Type { get; } = type;
    public object? Value { get; } = value;
    public int Index { get; } = index;
}

/// <summary>Type checking of values against annotations at runtime.</summary>
public static class Annotations
{
    /// <summary>Throws if <paramref name="v"/> does not match <paramref name="ty"/>.</summary>
    /// <param name="what"><c>argument</c> or <c>return type</c>.</param>
    public static void Check(Ty ty, object? v, string what, string? name = null)
    {
        if (ty.Matches(v)) return;
        string target = name is null ? what : $"{what} `{name}`";
        throw new StarlarkException(
            $"Value `{Repr.Of(v)}` of type `{Types.Name(v)}` does not match the type annotation `{ty}` for {target}");
    }
}
