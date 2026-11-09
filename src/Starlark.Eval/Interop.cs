using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Text;

namespace Starlark.Eval;

/// <summary>
/// Automatic conversion between CLR objects and Starlark values, so a host does not have to build
/// <see cref="StarList"/>, <see cref="StarDict"/> and <see cref="StarStruct"/> by hand.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ToStar"/> maps numbers, strings, enums (to their names), byte arrays, dictionaries, sequences and
/// delegates. Any other object becomes a <see cref="StarStruct"/> of its public readable properties, converted
/// recursively. The result is a snapshot: later changes to the object are not seen by the script.
/// </para>
/// <para>
/// <see cref="FromStar"/> goes the other way, converting a Starlark value to a requested CLR type. Structs, records and
/// dicts bind to a class or <c>record</c> by property name (matching either the exact name or its snake_case form).
/// </para>
/// </remarks>
public static class Interop
{
    private const int MaxDepth = 32;

    /// <summary>Converts a PascalCase or camelCase identifier to snake_case: <c>MaxRetryCount</c> becomes <c>max_retry_count</c>.</summary>
    public static string SnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c))
            {
                bool startsWord = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])
                    || (char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1])));
                if (startsWord) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>Converts a CLR value to a Starlark value.</summary>
    /// <param name="value">The value to convert.</param>
    /// <param name="snakeCase">Whether property names of converted objects become snake_case (the default).</param>
    /// <remarks>
    /// Called while a script runs (from a host function), each element of a host sequence or dictionary costs one step,
    /// so an endless or huge <see cref="IEnumerable"/> is stopped by <see cref="Interpreter.MaxSteps"/>,
    /// <see cref="Interpreter.Timeout"/>, <see cref="Interpreter.Cancellation"/> and
    /// <see cref="Interpreter.MaxAllocatedBytes"/> rather than hanging the script thread.
    /// </remarks>
    public static object? ToStar(object? value, bool snakeCase = true) =>
        ToStar(value, snakeCase, 0, ExecutionContext.Current is { } ctx ? Meter.For(ctx) : null);

    /// <summary>
    /// Converts a host task's result on the thread that completed it, where no script context is current: the
    /// conversion is bounded by the run's token (cancelled when the run ends), its deadline, the steps the owner had
    /// left and the memory ceiling.
    /// </summary>
    internal static object? ToStarDetached(object? value, bool snakeCase, ExecutionContext owner, CancellationToken token) =>
        ToStar(value, snakeCase, 0, Meter.Detached(owner, token));

    /// <summary>
    /// Bounds the conversion of host sequences: one step per element, checked against the run's limits. Inside a run
    /// it uses the run's own context; detached (a host future) it keeps its own count against what the owner had left.
    /// </summary>
    private sealed class Meter
    {
        private ExecutionContext? _ctx;
        private CancellationToken _token;
        private long _deadline, _stepsLeft, _maxBytes, _reportedSteps, _count;
        private TimeSpan? _timeout;

        public static Meter For(ExecutionContext ctx) => new() { _ctx = ctx, _maxBytes = ctx.MaxBytes };

        public static Meter Detached(ExecutionContext owner, CancellationToken token) => new()
        {
            _token = token,
            _deadline = owner.DeadlineTicks,
            _timeout = owner.Interpreter.Timeout,
            _stepsLeft = owner.MaxSteps > 0 ? Math.Max(1, owner.MaxSteps - Volatile.Read(ref owner.Steps)) : 0,
            _reportedSteps = owner.ReportedMaxSteps,
            _maxBytes = owner.MaxBytes,
        };

        /// <summary>Accounts for one more element of a collection that now holds <paramref name="size"/> items.</summary>
        public void Item(long size)
        {
            ++_count;
            // About 16 bytes per slot (a reference plus the converted value's box): an estimate, like the other checks.
            if (_maxBytes > 0 && size * 16 > _maxBytes)
            {
                throw new ExecutionLimitException("memory", $"Host value of {size} items is over the memory limit of {_maxBytes} bytes");
            }
            if (_ctx is not null)
            {
                _ctx.Step();
                return;
            }
            if (_stepsLeft > 0 && _count > _stepsLeft)
            {
                throw new ExecutionLimitException("steps", $"Script exceeded the limit of {_reportedSteps} steps");
            }
            if ((_count & 1023) == 0)
            {
                _token.ThrowIfCancellationRequested();
                if (_deadline != 0 && System.Diagnostics.Stopwatch.GetTimestamp() > _deadline)
                {
                    throw new ExecutionLimitException("time", $"Script exceeded the time limit of {_timeout}");
                }
            }
        }
    }

    private static object? ToStar(object? value, bool snake, int depth, Meter? meter)
    {
        if (depth > MaxDepth) throw new StarlarkException("Cannot convert host object: nesting is too deep (cyclic?)");
        switch (value)
        {
            case null: return null;
            case bool or string or double or BigInteger: return value;
            case IHostValue:
                return value;   // a host-defined Starlark value
            case StarUtf16 or StarBytes or StarList or StarTuple or StarDict or StarSet or StarStruct or StarRecord
                or StarEnumValue or StarRange or ICallable:
                return value;   // already a Starlark value
            case sbyte or byte or short or ushort or int or uint or long or ulong: return new BigInteger(Convert.ToDecimal(value));
            case Int128 i128: return (BigInteger)i128;
            case UInt128 u128: return (BigInteger)u128;
            case float f: return (double)f;
            case decimal m: return (double)m;
            case char c: return c.ToString();
            case Enum e: return e.ToString();
            case DateTime dt: return dt.ToString("o");
            case DateTimeOffset dto: return dto.ToString("o");
            case TimeSpan ts: return ts.TotalSeconds;
            case Guid g: return g.ToString();
            case byte[] bytes: return new StarBytes(bytes);
            case Delegate d: return FromDelegate(d, snake);
            case IDictionary dict:
            {
                var result = new StarDict();
                long n = 0;
                foreach (DictionaryEntry kv in dict)
                {
                    meter?.Item(++n);
                    result.Set(ToStar(kv.Key, snake, depth + 1, meter), ToStar(kv.Value, snake, depth + 1, meter));
                }
                return result;
            }
            case IEnumerable seq:
            {
                var items = new List<object?>();
                foreach (var x in seq)
                {
                    meter?.Item(items.Count + 1);
                    items.Add(ToStar(x, snake, depth + 1, meter));
                }
                return new StarList(items);
            }
        }
        var type = value.GetType();
        if (type.IsGenericType && type.FullName!.StartsWith("System.ValueTuple", StringComparison.Ordinal))
        {
            return new StarTuple(type.GetFields().Select(f => ToStar(f.GetValue(value), snake, depth + 1, meter)).ToArray());
        }
        return new StarStruct(ExposedMembers.Properties(type, snake).Select(p =>
            new KeyValuePair<string, object?>(p.Name, ToStar(p.Property.GetValue(value), snake, depth + 1, meter))));
    }

    private static IEnumerable<PropertyInfo> Properties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0);

    /// <summary>Wraps a delegate as a named callable Starlark function, converting arguments and result.</summary>
    public static Builtin Wrap(string name, Delegate d, bool threadSafe = false, bool asFuture = false) =>
        new(name, (interp, args, kwargs) => Invoke(interp, d.Target, d.Method, args, kwargs, true, serialize: !threadSafe, asFuture: asFuture, displayName: name));

    /// <summary>Wraps a delegate as a callable Starlark function, converting arguments and result.</summary>
    private static Builtin FromDelegate(Delegate d, bool snake) =>
        new(d.Method.Name, (interp, args, kwargs) => Invoke(interp, d.Target, d.Method, args, kwargs, snake));

    private static object? Invoke(Interpreter interp, object? target, MethodBase method, IReadOnlyList<object?> args,
        IReadOnlyList<KeyValuePair<string, object?>> kwargs, bool snake, bool serialize = true, bool asFuture = false, string? displayName = null)
    {
        var parameters = method.GetParameters();
        var bound = new object?[parameters.Length];
        var set = new bool[parameters.Length];
        // Errors name the function the script called, not the compiler's name for a lambda.
        string shownName = displayName ?? method.Name;

        // A host operation started from the script's own thread may return a future; it then gets the run's token, so
        // ending the run cancels it. Inside a task the call simply waits.
        var running = ExecutionContext.Current;
        var hostRun = asFuture && interp.Dialect.EnableFutures && running is { IsTask: false }
            ? Futures.FutureBuiltins.RunFor(running)
            : null;

        // An Interpreter or CancellationToken parameter is supplied by the host, not by the script.
        var visible = new List<int>();
        for (int i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].ParameterType == typeof(Interpreter))
            {
                bound[i] = interp;
                set[i] = true;
            }
            else if (parameters[i].ParameterType == typeof(CancellationToken))
            {
                bound[i] = hostRun?.Token ?? running?.Token ?? interp.Cancellation;
                set[i] = true;
            }
            else if (parameters[i].ParameterType == typeof(ScriptThread))
            {
                // Frame 0 is the host function itself, as Go counts the built-in that is running.
                bound[i] = running is null ? null : new ScriptThread(running, shownName);
                set[i] = true;
            }
            else
            {
                visible.Add(i);
            }
        }
        if (args.Count > visible.Count)
        {
            throw new StarlarkException($"Too many positional arguments to `{shownName}`: got {args.Count}, want at most {visible.Count}");
        }
        for (int j = 0; j < args.Count; j++)
        {
            int i = visible[j];
            bound[i] = FromStar(args[j], parameters[i].ParameterType);
            set[i] = true;
        }
        foreach (var (name, value) in kwargs)
        {
            int index = visible.FirstOrDefault(i => parameters[i].Name == name || (parameters[i].Name is not null && SnakeCase(parameters[i].Name!) == name), -1);
            if (index < 0) throw new StarlarkException($"Unexpected parameter named `{name}` for call to `{shownName}`");
            if (set[index]) throw new StarlarkException($"Parameter `{name}` was given twice in call to `{shownName}`");
            bound[index] = FromStar(value, parameters[index].ParameterType);
            set[index] = true;
        }
        for (int i = 0; i < parameters.Length; i++)
        {
            if (set[i]) continue;
            if (!parameters[i].HasDefaultValue)
            {
                throw new StarlarkException($"Missing parameter `{parameters[i].Name}` for call to `{shownName}`");
            }
            bound[i] = parameters[i].DefaultValue is DBNull ? null : parameters[i].DefaultValue;
        }
        bool reserved = false, handedOff = false;
        try
        {
            object? result;
            if (hostRun is not null)
            {
                hostRun.ReserveHostOperation();
                reserved = true;
            }
            if (serialize && running is { IsTask: true })
            {
                // Host code is not assumed to be thread-safe: calls from tasks take turns, and the wait still honours the deadline.
                while (!Monitor.TryEnter(interp.HostCallLock, 25)) running.PollNow();
                try
                {
                    result = method.Invoke(target, bound);
                }
                finally
                {
                    Monitor.Exit(interp.HostCallLock);
                }
            }
            else
            {
                result = method.Invoke(target, bound);
            }
            if (result is ValueTask vt) result = vt.AsTask();
            else if (result is not null && result.GetType() is { IsGenericType: true } rt
                     && rt.GetGenericTypeDefinition() == typeof(ValueTask<>))
            {
                result = rt.GetMethod("AsTask")!.Invoke(result, null);
            }
            if (result is Task task)
            {
                if (interp.Dialect.EnableFutures)
                {
                    running?.Charge(Math.Max(0, interp.FutureLimits.HostCallStepCost));
                    running?.Run?.NoteHostCall();
                }
                if (hostRun is not null)
                {
                    handedOff = true;   // the future now owns the reserved slot
                    var owner = running!;
                    return hostRun.StartHostFuture(task, token => ToStarDetached(TaskResult(task), snake, owner, token), owner, shownName);
                }
                interp.AwaitHostTask(task, shownName);
                result = TaskResult(task);
            }
            return ToStar(result, snake);
        }
        catch (Exception e) when (e is not (StarlarkException or ExecutionLimitException or OperationCanceledException))
        {
            // A host function that throws (wrapped by reflection or not), a lazy result that fails while it is
            // converted, or a method reflection cannot call: all reach the host as the same StarlarkException.
            var failure = HostFailure(e, shownName);
            if (failure == e.InnerException) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            throw failure;
        }
        finally
        {
            if (reserved && !handedOff) hostRun!.ReleaseHostOperation();
        }
    }

    /// <summary>
    /// The error a host sees for a CLR exception raised by host function <paramref name="function"/>: a Starlark,
    /// limit or cancellation exception inside a reflection wrapper is passed on unchanged, anything else becomes
    /// <c>Host function `name` failed: message</c>. Shared by direct calls and host futures so both report the same.
    /// </summary>
    internal static Exception HostFailure(Exception e, string function)
    {
        var inner = e is TargetInvocationException { InnerException: { } x } ? x : e;
        if (inner is StarlarkException or ExecutionLimitException or OperationCanceledException) return inner;
        return new StarlarkException($"Host function `{function}` failed: {inner.Message}");
    }

    /// <summary>The value a completed task produced, or null for a task without a result.</summary>
    private static object? TaskResult(Task task) =>
        task.GetType().IsGenericType && task.GetType().GetProperty("Result") is { } p && p.PropertyType.Name != "VoidTaskResult"
            ? p.GetValue(task)
            : null;

    /// <summary>
    /// Exposes a host object to scripts as a struct: public properties become fields (a snapshot taken now) and public
    /// methods become live calls on the object. Names are snake_case unless <paramref name="snakeCase"/> is false.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A visible method is generic or uses a pointer, by-ref or ref struct type, or two visible members (overloads,
    /// or a method and a property) share a script name: a host programming error, reported before any script runs.
    /// Rename the member with <see cref="ExposeAttribute"/> or mark it <see cref="HideAttribute"/>.
    /// </exception>
    public static StarStruct Expose(object target, bool snakeCase = true)
    {
        var fields = new List<KeyValuePair<string, object?>>();
        var type = target.GetType();
        foreach (var (property, name) in ExposedMembers.Properties(type, snakeCase))
        {
            fields.Add(new(name, ToStar(property.GetValue(target), snakeCase)));
        }
        foreach (var (m, name) in ExposedMembers.Methods(type, snakeCase))
        {
            var method = m;
            fields.Add(new(name, new Builtin(name, (interp, args, kwargs) => Invoke(interp, target, method, args, kwargs, snakeCase, displayName: name))));
        }
        return new StarStruct(fields);
    }

    /// <summary>The deepest Starlark value <see cref="FromStar"/> and <see cref="FromStarNatural"/> convert; deeper (or cyclic) values are an error.</summary>
    private const int MaxFromStarDepth = 200;

    /// <summary>Converts a Starlark value to a CLR value of the given type.</summary>
    /// <exception cref="StarlarkException">
    /// The value cannot be represented as <paramref name="type"/>, is nested too deeply (or is cyclic), or building the CLR
    /// value failed. No other exception type escapes, whatever the script passed.
    /// </exception>
    public static object? FromStar(object? value, Type type)
    {
        try
        {
            return FromStar(value, type, 0);
        }
        catch (Exception e) when (e is not (StarlarkException or ExecutionLimitException or OperationCanceledException))
        {
            throw ConversionFailed(e, type.Name);
        }
    }

    /// <summary>
    /// The error for a CLR exception raised while building a host value (a constructor or setter that throws, say), so
    /// that nothing but a Starlark, limit or cancellation exception reaches the host.
    /// </summary>
    private static Exception ConversionFailed(Exception e, string target)
    {
        var inner = e is TargetInvocationException { InnerException: { } x } ? x : e;
        if (inner is StarlarkException or ExecutionLimitException or OperationCanceledException)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw();
        }
        return new StarlarkException($"Cannot convert to the host type {target}: {inner.Message}");
    }

    private static void CheckDepth(int depth)
    {
        if (depth > MaxFromStarDepth) throw new StarlarkException("Cannot convert to a host value: nesting is too deep (cyclic?)");
        StackGuard.Check();
    }

    private static object? FromStar(object? value, Type type, int depth)
    {
        CheckDepth(depth);
        if (type == typeof(object)) return FromStarNatural(value, depth);
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            return value is null ? null : FromStar(value, underlying, depth);
        }
        if (value is null)
        {
            if (type.IsValueType) throw Mismatch(value, type);
            return null;
        }
        if (type.IsInstanceOfType(value) && !type.IsPrimitive) return value;

        if (type == typeof(bool)) return value is bool b ? b : throw Mismatch(value, type);
        if (type == typeof(string)) return value as string ?? (value is StarUtf16 u ? u.Value : value is StarEnumValue { Value: string ev } ? ev : throw Mismatch(value, type));
        if (type == typeof(char)) return value is string { Length: 1 } s ? s[0] : throw Mismatch(value, type);
        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
        {
            double d = value switch { double x => x, BigInteger i => Ops.BigToDouble(i), _ => throw Mismatch(value, type) };
            if (type == typeof(decimal))
            {
                // A decimal has no NaN or infinity and a much smaller range than a double.
                if (double.IsNaN(d) || Math.Abs(d) >= 7.9e28) throw DoesNotFit(d, type);
                return (decimal)d;
            }
            return type == typeof(double) ? d : (float)d;
        }
        if (type.IsEnum)
        {
            if (value is string name && Enum.TryParse(type, name, ignoreCase: false, out var e)) return e;
            if (value is StarEnumValue ev && Enum.TryParse(type, ev.Value?.ToString(), out var e2)) return e2;
            throw Mismatch(value, type);
        }
        if (type == typeof(BigInteger)) return value is BigInteger bi ? bi : throw Mismatch(value, type);
        if (IsInteger(type))
        {
            if (value is not BigInteger n) throw Mismatch(value, type);
            var (min, max) = IntegerRange(type);
            if (n < min || n > max) throw new StarlarkException($"Value {n} does not fit in {type.Name}");
            return type == typeof(int) ? (int)n : type == typeof(long) ? (long)n : type == typeof(short) ? (short)n
                : type == typeof(byte) ? (byte)n : type == typeof(sbyte) ? (sbyte)n : type == typeof(uint) ? (uint)n
                : type == typeof(ulong) ? (ulong)n : (ushort)n;
        }
        if (type == typeof(byte[])) return value is StarBytes sb ? sb.Value : throw Mismatch(value, type);
        if (type == typeof(TimeSpan))
        {
            if (value is not (double or BigInteger)) throw Mismatch(value, type);
            double secs = (double)FromStar(value, typeof(double), depth)!;
            // Inside a margin of TimeSpan's range, where FromSeconds cannot overflow.
            if (double.IsNaN(secs) || Math.Abs(secs) >= TimeSpan.MaxValue.TotalSeconds - 1) throw DoesNotFit(secs, type);
            return TimeSpan.FromSeconds(secs);
        }
        if (type == typeof(DateTime))
        {
            if (value is not string dts) throw Mismatch(value, type);
            return DateTime.TryParse(dts, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                ? dt
                : throw new StarlarkException($"Cannot convert `{dts}` to the host type DateTime: not a date and time");
        }
        if (type == typeof(Guid)) return value is string gs && Guid.TryParse(gs, out var g) ? g : throw Mismatch(value, type);

        if (type.IsArray)
        {
            var elem = type.GetElementType()!;
            var items = SequenceOf(value, type);
            var arr = Array.CreateInstance(elem, items.Count);
            for (int i = 0; i < items.Count; i++) arr.SetValue(FromStar(items[i], elem, depth + 1), i);
            return arr;
        }
        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments();
            if (def == typeof(Dictionary<,>) || def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>))
            {
                if (value is not StarDict sd) throw Mismatch(value, type);
                var dict = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(args))!;
                foreach (var (k, v) in sd.Entries)
                {
                    var key = FromStar(k, args[0], depth + 1) ?? throw new StarlarkException($"Cannot convert a dict with a None key to the host type {type.Name}");
                    dict.Add(key, FromStar(v, args[1], depth + 1));
                }
                return dict;
            }
            if (def == typeof(List<>) || def == typeof(IList<>) || def == typeof(IEnumerable<>) || def == typeof(IReadOnlyList<>)
                || def == typeof(ICollection<>) || def == typeof(IReadOnlyCollection<>))
            {
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(args))!;
                foreach (var x in SequenceOf(value, type)) list.Add(FromStar(x, args[0], depth + 1));
                return list;
            }
            if (def == typeof(HashSet<>) || def == typeof(ISet<>))
            {
                var set = Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(args))!;
                var add = set.GetType().GetMethod("Add")!;
                foreach (var x in SequenceOf(value, type)) add.Invoke(set, [FromStar(x, args[0], depth + 1)]);
                return set;
            }
        }
        return Bind(value, type, depth);
    }

    /// <summary>
    /// Converts a Starlark value to the natural CLR type: long or BigInteger, double, string, List, Dictionary. A dict with a
    /// None key, or a value nested too deeply (or cyclic), is a <see cref="StarlarkException"/>.
    /// </summary>
    public static object? FromStarNatural(object? value)
    {
        try
        {
            return FromStarNatural(value, 0);
        }
        catch (Exception e) when (e is not (StarlarkException or ExecutionLimitException or OperationCanceledException))
        {
            throw ConversionFailed(e, "object");
        }
    }

    private static object? FromStarNatural(object? value, int depth)
    {
        CheckDepth(depth);
        return value switch
        {
            null or bool or string or double => value,
            BigInteger i => i >= long.MinValue && i <= long.MaxValue ? (long)i : i,
            StarUtf16 u => u.Value,
            StarBytes b => b.Value,
            StarList l => l.Items.Select(x => FromStarNatural(x, depth + 1)).ToList(),
            StarTuple t => t.Items.Select(x => FromStarNatural(x, depth + 1)).ToList(),
            StarSet s => s.Items.Select(x => FromStarNatural(x, depth + 1)).ToList(),
            StarDict d => d.Entries.ToDictionary(
                kv => FromStarNatural(kv.Key, depth + 1) ?? throw new StarlarkException("Cannot convert a dict with a None key to a host dictionary"),
                kv => FromStarNatural(kv.Value, depth + 1)),
            StarStruct st => st.Fields.ToDictionary(kv => kv.Key, kv => FromStarNatural(kv.Value, depth + 1)),
            StarRecord r => r.Type.Fields.Select((fl, i) => (fl.Name, Value: r.Values[i])).ToDictionary(x => x.Name, x => FromStarNatural(x.Value, depth + 1)),
            StarEnumValue e => e.Value,
            _ => value,
        };
    }

    private static (BigInteger Min, BigInteger Max) IntegerRange(Type t) =>
        t == typeof(int) ? (int.MinValue, int.MaxValue) : t == typeof(long) ? (long.MinValue, long.MaxValue)
        : t == typeof(short) ? (short.MinValue, short.MaxValue) : t == typeof(byte) ? (byte.MinValue, byte.MaxValue)
        : t == typeof(sbyte) ? (sbyte.MinValue, sbyte.MaxValue) : t == typeof(uint) ? (uint.MinValue, uint.MaxValue)
        : t == typeof(ulong) ? (ulong.MinValue, ulong.MaxValue) : (ushort.MinValue, ushort.MaxValue);

    private static bool IsInteger(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) || t == typeof(sbyte)
        || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort);

    private static List<object?> SequenceOf(object? value, Type wanted) => value switch
    {
        StarList l => [.. l.Items],
        StarTuple t => [.. t.Items],
        StarSet s => [.. s.Items],
        _ => throw Mismatch(value, wanted),
    };

    /// <summary>Builds an instance of <paramref name="type"/> from a struct, record or string-keyed dict.</summary>
    private static object? Bind(object? value, Type type, int depth)
    {
        var source = new Dictionary<string, object?>();
        switch (value)
        {
            case StarStruct st: foreach (var kv in st.Fields) source[kv.Key] = kv.Value; break;
            case StarRecord r: for (int i = 0; i < r.Type.Fields.Count; i++) source[r.Type.Fields[i].Name] = r.Values[i]; break;
            case StarDict d:
                foreach (var (k, v) in d.Entries)
                {
                    if (k is not string key) throw Mismatch(value, type);
                    source[key] = v;
                }
                break;
            default: throw Mismatch(value, type);
        }

        bool Lookup(string name, out object? found) => source.TryGetValue(name, out found) || source.TryGetValue(SnakeCase(name), out found);

        // Prefer the constructor with the most parameters (a record's primary constructor).
        var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault()
                   ?? throw new StarlarkException($"Cannot convert to {type.Name}: it has no public constructor");
        var ps = ctor.GetParameters();
        var args = new object?[ps.Length];
        var used = new HashSet<string>();
        for (int i = 0; i < ps.Length; i++)
        {
            if (Lookup(ps[i].Name!, out var found))
            {
                args[i] = FromStar(found, ps[i].ParameterType, depth + 1);
                used.Add(ps[i].Name!);
            }
            else if (ps[i].HasDefaultValue)
            {
                args[i] = ps[i].DefaultValue is DBNull ? null : ps[i].DefaultValue;
            }
            else if (!ps[i].ParameterType.IsValueType || Nullable.GetUnderlyingType(ps[i].ParameterType) is not null)
            {
                args[i] = null;
            }
            else
            {
                throw new StarlarkException($"Cannot convert to {type.Name}: missing field `{SnakeCase(ps[i].Name!)}`");
            }
        }
        // A constructor or setter that throws is reported as a conversion error (Guarded unwraps TargetInvocationException).
        var instance = ctor.Invoke(args);
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite && p.SetMethod!.IsPublic))
        {
            if (used.Contains(p.Name) || ps.Any(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase))) continue;
            if (Lookup(p.Name, out var found)) p.SetValue(instance, FromStar(found, p.PropertyType, depth + 1));
        }
        return instance;
    }

    private static StarlarkException Mismatch(object? value, Type type) =>
        new($"Cannot convert a value of type '{Types.Name(value)}' to the host type {type.Name}");

    private static StarlarkException DoesNotFit(double d, Type type) =>
        new($"Value {Repr.FloatToString(d)} does not fit in {type.Name}");
}
