using System.Numerics;
using Starlark.Eval;
using Starlark.Syntax;

namespace Basic;

/// <summary>
/// A TRS-80 Level II BASIC machine whose interpreter is a Starlark program (<c>scripts/basic.star</c>). This class is the
/// whole host: it loads the script, hands it typed lines and a statement budget, and reads back what was printed. The
/// script cannot touch the console, the disk or the clock, so a BASIC program prints the same thing every time.
/// </summary>
public sealed class Machine
{
    private readonly Interpreter _interpreter;
    private readonly Module _module;
    private readonly object? _state;

    /// <summary>Statements executed per call to the script before control returns to the host.</summary>
    public const int Slice = 20_000;

    /// <summary>Creates a machine showing the "READY" prompt. <paramref name="maxSteps"/> caps the Starlark steps of each call.</summary>
    public Machine(long maxSteps = 400_000_000)
    {
        _interpreter = new Interpreter
        {
            Dialect = Dialect.Standard with { EnableTopLevelStmt = true },
            MaxSteps = maxSteps,
            Timeout = TimeSpan.FromMinutes(2),
            MaxAllocatedBytes = 512L * 1024 * 1024,
            SourceLoader = id => Scripts.Read(id),
        };
        _module = _interpreter.Execute("basic.star", Scripts.Read("basic.star") ?? throw new InvalidOperationException("basic.star is missing"));
        _state = _interpreter.CallValue(_module.Globals["new_state"], []);
    }

    private object? Call(string name, params object?[] args) => _interpreter.CallValue(_module.Globals[name], args);

    /// <summary>Types a line (a program line, a command, or the answer to an INPUT). Returns "done", "waiting" or "budget".</summary>
    public string Type(string line) => (string)Call("submit", _state, line, new BigInteger(Slice))!;

    /// <summary>Keeps running a program that used up its slice. Returns "done", "waiting" or "budget".</summary>
    public string Continue() => (string)Call("run", _state, new BigInteger(Slice))!;

    /// <summary>Runs until the machine is back at READY or waiting for an INPUT, or until <paramref name="maxSlices"/> slices have run.</summary>
    public string RunToIdle(string status, int maxSlices = 100_000)
    {
        for (int i = 0; i < maxSlices && status == "budget"; i++) status = Continue();
        return status;
    }

    /// <summary>Types a line and runs until the machine is idle.</summary>
    public string Enter(string line, int maxSlices = 100_000) => RunToIdle(Type(line), maxSlices);

    /// <summary>Everything printed since the last call.</summary>
    public string TakeOutput() => (string)Call("take_output", _state)!;

    /// <summary>The 16 lines of the 64-column screen, with trailing spaces removed.</summary>
    public IReadOnlyList<string> Screen() => ((StarList)Call("get_screen", _state)!).Items.Select(x => (string)x!).ToList();

    /// <summary>Queues keys for INKEY$.</summary>
    public void PressKeys(string keys) => Call("press_keys", _state, keys);

    /// <summary>Stops a running program the way the BREAK key does.</summary>
    public void Break() => Call("break_program", _state);

    /// <summary>Loads a whole program (lines separated by newlines) as if typed in.</summary>
    public void Load(string program)
    {
        foreach (var line in program.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Trim().Length > 0) Enter(line);
        }
        TakeOutput();
    }
}

/// <summary>Command-line front end: an interactive BASIC prompt, or a program file run to the end.</summary>
public static class Program
{
    /// <summary>
    /// <c>Basic</c> starts the interactive prompt. <c>Basic file.bas</c> loads and RUNs a file. <c>Basic --demo name</c> runs a
    /// bundled program, and <c>Basic --list</c> names them.
    /// </summary>
    public static int Main(string[] args)
    {
        if (args.Contains("--list"))
        {
            foreach (var name in Scripts.ProgramNames()) Console.WriteLine(name);
            return 0;
        }
        string? source = null;
        int demo = Array.IndexOf(args, "--demo");
        if (demo >= 0 && demo + 1 < args.Length)
        {
            source = Scripts.ReadProgram(args[demo + 1]);
            if (source is null)
            {
                Console.Error.WriteLine($"no bundled program '{args[demo + 1]}'; try --list");
                return 2;
            }
        }
        else if (args.Length > 0 && File.Exists(args[0]))
        {
            source = File.ReadAllText(args[0]);
        }
        return Run(Console.Out, Console.In, source);
    }

    /// <summary>Runs a machine against <paramref name="input"/>; if <paramref name="source"/> is given it is loaded and RUN first.</summary>
    public static int Run(TextWriter output, TextReader input, string? source)
    {
        var machine = new Machine();
        output.Write(machine.TakeOutput());
        string status = "done";
        if (source is not null)
        {
            machine.Load(source);
            status = machine.Enter("RUN");
            output.Write(machine.TakeOutput());
        }
        for (;;)
        {
            if (status == "waiting" || status == "done")
            {
                var line = input.ReadLine();
                if (line is null) break;
                status = machine.Enter(line);
                output.Write(machine.TakeOutput());
            }
        }
        return 0;
    }
}
