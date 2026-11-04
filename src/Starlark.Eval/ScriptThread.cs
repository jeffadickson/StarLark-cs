namespace Starlark.Eval;

/// <summary>One frame of a script thread's call stack: the function that is running and where control is inside it.</summary>
/// <param name="Name">The function's name, or <c>&lt;module&gt;</c> for top-level code.</param>
/// <param name="Position">The place in the source, as <c>file:line:column</c>; <c>&lt;builtin&gt;</c> for a built-in function.</param>
/// <param name="Line">The 1-based line, or 0 for a built-in function.</param>
/// <param name="Column">The 1-based column, or 0 for a built-in function.</param>
public sealed record CallFrame(string Name, string Position, int Line, int Column)
{
    /// <summary>Go's rendering: <c>file:line:col: in name</c>.</summary>
    public override string ToString() => $"{Position}: in {Name}";
}

/// <summary>
/// The thread of execution that is running a script: the .NET counterpart of Go's <c>starlark.Thread</c>. It gives host code
/// (functions registered with <see cref="Interpreter.Register"/> or <see cref="Interpreter.Define(string, Func{Interpreter, IReadOnlyList{object?}, IReadOnlyList{KeyValuePair{string, object?}}, object?})"/>)
/// the name, step count, call stack and thread-local storage of the run that called them.
/// </summary>
/// <remarks>
/// <para>
/// Obtain one by declaring a <c>ScriptThread</c> parameter on a registered function (the script does not see it, as with
/// <see cref="Interpreter"/> and <see cref="CancellationToken"/>), or from <see cref="Interpreter.CurrentThread"/> inside
/// any host call. A thread is only meaningful while its run is in progress and only on the thread that is running the script;
/// reading it afterwards, or from another thread, gives an error.
/// </para>
/// <para>
/// The mapping to Go: <c>Name</c>, <c>Steps</c> (<c>ExecutionSteps</c>), <c>CallStack</c>, <c>CallFrame(depth)</c>,
/// <c>CallStackDepth</c>, <c>SetLocal</c> and <c>Local</c> have the same meaning. Go's <c>Print</c> and <c>Load</c> fields are
/// <see cref="Interpreter.Print"/> and <see cref="Interpreter.SourceLoader"/> here, reachable as <see cref="Print"/> and
/// <see cref="Load"/>. Step counting is always on, so <see cref="Steps"/> is useful even without a limit.
/// </para>
/// </remarks>
public sealed class ScriptThread
{
    private readonly ExecutionContext _context;
    private readonly int _builtinDepth;

    internal ScriptThread(ExecutionContext context, string? builtin)
    {
        _context = context;
        _builtinDepth = builtin is null ? 0 : 1;
        BuiltinName = builtin;
    }

    /// <summary>The name of the built-in that is being called, or null. Go counts it as frame 0 of the stack.</summary>
    internal string? BuiltinName { get; }

    private void Check()
    {
        if (!ReferenceEquals(ExecutionContext.Current, _context))
        {
            throw new InvalidOperationException("A ScriptThread can only be used by the thread that is running its script, while the run is in progress");
        }
    }

    /// <summary>The thread's name, for debugging. Set it with <see cref="Interpreter.ThreadName"/> before the run or here during it.</summary>
    public string Name
    {
        get => _context.Name;
        set => _context.Name = value;
    }

    /// <summary>The interpreter this thread belongs to.</summary>
    public Interpreter Interpreter => _context.Interpreter;

    /// <summary>
    /// Abstract computation steps taken so far by this thread (Go's <c>Steps</c> / <c>ExecutionSteps()</c>). Counted by the
    /// interpreter whether or not a limit is set; use the difference between two readings as a cost.
    /// </summary>
    public long Steps => _context.Steps;

    /// <summary>The step limit, or 0 for none (Go's <c>SetMaxExecutionSteps</c> lives on <see cref="Interpreter.MaxSteps"/>).</summary>
    public long MaxSteps => _context.MaxSteps;

    /// <summary>Text written by the script's <c>print</c> goes through this (Go's <c>Thread.Print</c>).</summary>
    public Action<string> Print => Interpreter.Print;

    /// <summary>The source of a module, as the host's loader gives it (Go's <c>Thread.Load</c>); null if there is none.</summary>
    public string? Load(string module) => Interpreter.SourceLoader?.Invoke(module) ?? StandardLibrary.Read(module);

    /// <summary>The number of frames on the call stack, counting the running built-in (Go's <c>CallStackDepth()</c>).</summary>
    public int CallStackDepth
    {
        get
        {
            Check();
            return _context.FrameCount + _builtinDepth;
        }
    }

    /// <summary>
    /// A copy of the frame <paramref name="depth"/> places below the top. As in Go, depth 0 is the running built-in itself
    /// when there is one (otherwise the innermost script function), 1 is its caller, and so on.
    /// </summary>
    public CallFrame CallFrame(int depth)
    {
        Check();
        if (depth < 0 || depth >= CallStackDepth) throw new ArgumentOutOfRangeException(nameof(depth), depth, $"the stack has {CallStackDepth} frames");
        if (_builtinDepth == 1)
        {
            if (depth == 0) return new CallFrame(BuiltinName!, "<builtin>", 0, 0);
            depth--;
        }
        return Describe(_context.FrameAt(depth));
    }

    /// <summary>The whole call stack, outermost frame first (Go's <c>CallStack()</c>).</summary>
    public IReadOnlyList<CallFrame> CallStack()
    {
        Check();
        var frames = new List<CallFrame>(CallStackDepth);
        for (int depth = CallStackDepth - 1; depth >= 0; depth--) frames.Add(CallFrame(depth));
        return frames;
    }

    /// <summary>Go's <c>CallStack.String()</c>: a traceback, outermost call first.</summary>
    public string Traceback()
    {
        var stack = CallStack();
        if (stack.Count == 0) return "";
        return "Traceback (most recent call last):\n" + string.Concat(stack.Select(f => $"  {f}\n"));
    }

    private static CallFrame Describe(ExecutionContext.LiveFrame frame)
    {
        if (frame.Span is { } span && frame.Map is { } map)
        {
            var begin = map.ResolveSpan(span).Begin;
            return new CallFrame(frame.Name, $"{map.FileName}:{begin}", begin.Line + 1, begin.Column + 1);
        }
        return new CallFrame(frame.Name, frame.Map is { } m ? $"{m.FileName}:1:1" : "<unknown>", frame.Map is null ? 0 : 1, frame.Map is null ? 0 : 1);
    }

    /// <summary>Stores host data on this thread, invisible to scripts (Go's <c>SetLocal</c>).</summary>
    public void SetLocal(string key, object? value)
    {
        Check();
        _context.Locals[key] = value;
    }

    /// <summary>The host data stored under <paramref name="key"/>, or null (Go's <c>Local</c>).</summary>
    public object? Local(string key)
    {
        Check();
        if (_context.Locals.TryGetValue(key, out var v)) return v;
        return Interpreter.ThreadLocals is { } seeded && seeded.TryGetValue(key, out var s) ? s : null;
    }
}
