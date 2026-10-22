using System.Numerics;

namespace Starlark.Eval;

/// <summary>
/// Binds the arguments of a native function. Parameters are taken by position first, then by keyword name.
/// Call <see cref="Done"/> last to reject surplus arguments.
/// </summary>
public sealed class ArgReader
{
    private readonly string _fn;
    private readonly IReadOnlyList<object?> _args;
    private readonly IReadOnlyList<KeyValuePair<string, object?>> _kwargs;
    private readonly HashSet<string> _usedKw = [];
    private int _pos;

    public ArgReader(string fn, IReadOnlyList<object?> args, IReadOnlyList<KeyValuePair<string, object?>> kwargs)
    {
        _fn = fn;
        _args = args;
        _kwargs = kwargs;
    }

    public int RemainingPositional => _args.Count - _pos;

    /// <summary>Whether the next parameter was supplied (even if its value is None).</summary>
    public bool Has(string name) =>
        _pos < _args.Count || _kwargs.Any(k => k.Key == name);

    private bool TryTake(string name, out object? value)
    {
        if (_pos < _args.Count)
        {
            value = _args[_pos++];
            return true;
        }
        foreach (var kv in _kwargs)
        {
            if (kv.Key == name)
            {
                _usedKw.Add(name);
                value = kv.Value;
                return true;
            }
        }
        value = null;
        return false;
    }

    public object? Required(string name) =>
        TryTake(name, out var v) ? v : throw new StarlarkException($"Missing parameter `{name}` for call to `{_fn}`");

    public object? Optional(string name, object? dflt = null) => TryTake(name, out var v) ? v : dflt;

    public string Str(string name)
    {
        var v = Required(name);
        return v is string s ? s : throw Err.Param(name, "string", v);
    }

    public string? OptStr(string name)
    {
        if (!TryTake(name, out var v) || v is null) return null;
        return v is string s ? s : throw Err.Param(name, "string", v);
    }

    public BigInteger Int(string name)
    {
        var v = Required(name);
        return v is BigInteger i ? i : throw Err.Param(name, "int", v);
    }

    /// <summary>An optional int that may also be None; returns null when absent or None.</summary>
    public BigInteger? OptInt(string name)
    {
        if (!TryTake(name, out var v) || v is null) return null;
        return v is BigInteger i ? i : throw Err.Param(name, "int", v);
    }

    public bool Bool(string name, bool dflt)
    {
        if (!TryTake(name, out var v)) return dflt;
        return v is bool b ? b : throw Err.Param(name, "bool", v);
    }

    /// <summary>Everything left over after the named parameters, as *args.</summary>
    public List<object?> Rest()
    {
        var rest = _args.Skip(_pos).ToList();
        _pos = _args.Count;
        return rest;
    }

    /// <summary>Keyword arguments not consumed by named parameters, as **kwargs.</summary>
    public List<KeyValuePair<string, object?>> RestKw()
    {
        _restKwTaken = true;
        return _kwargs.Where(k => !_usedKw.Contains(k.Key)).ToList();
    }

    private bool _restKwTaken;

    public void Done(bool allowKwargs = false)
    {
        if (_pos < _args.Count)
        {
            throw new StarlarkException(
                $"Too many positional arguments to `{_fn}`: expected {_pos}, got {_args.Count}");
        }
        if (!allowKwargs && !_restKwTaken)
        {
            foreach (var kv in _kwargs)
            {
                if (!_usedKw.Contains(kv.Key))
                {
                    throw new StarlarkException($"Unexpected parameter named `{kv.Key}` for call to `{_fn}`");
                }
            }
        }
    }
}
