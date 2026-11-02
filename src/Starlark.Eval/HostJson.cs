using System.Text;
using System.Text.Json;

namespace Starlark.Eval;

/// <summary>
/// One-call JSON conversion between .NET values and Starlark values, so a host can hand a script a document and read
/// one back without building <see cref="StarDict"/> and <see cref="StarList"/> by hand.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ToScript"/> parses JSON text into Starlark values (objects become dicts, arrays lists, integers
/// <c>int</c>, other numbers <c>float</c>). <see cref="FromScript{T}"/> serializes a Starlark value to JSON and reads it
/// into <typeparamref name="T"/> with System.Text.Json, so ordinary DTO classes and records work. Property names use
/// snake_case in scripts and camelCase-insensitive matching on the .NET side.
/// </para>
/// </remarks>
public static class HostJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    /// <summary>Parses JSON text into a Starlark value (the same result as the script-side <c>json.decode</c>).</summary>
    /// <exception cref="StarlarkException">The text is not valid JSON.</exception>
    public static object? ToScript(string json)
    {
        var decode = (Builtin)JsonModule.Create().Fields.First(f => f.Key == "decode").Value!;
        return decode.Call(null!, [json], []);
    }

    /// <summary>Converts any .NET value to a Starlark value by way of JSON, using snake_case property names.</summary>
    public static object? ToScript<T>(T value) => ToScript(JsonSerializer.Serialize(value, Options));

    /// <summary>Serializes a Starlark value to JSON text (the same result as the script-side <c>json.encode</c>).</summary>
    public static string ToJson(object? value)
    {
        var encode = (Builtin)JsonModule.Create().Fields.First(f => f.Key == "encode").Value!;
        return (string)encode.Call(null!, [value], [])!;
    }

    /// <summary>Reads a Starlark value into a .NET type by way of JSON.</summary>
    /// <exception cref="StarlarkException">The value cannot be serialized, or the JSON does not fit <typeparamref name="T"/>.</exception>
    public static T FromScript<T>(object? value)
    {
        string json = ToJson(value);
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options)
                ?? throw new StarlarkException($"The script value is null, which cannot become {typeof(T).Name}");
        }
        catch (JsonException e)
        {
            throw new StarlarkException($"The script value does not fit {typeof(T).Name}: {e.Message}");
        }
    }
}
