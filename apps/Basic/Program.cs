using System.Numerics;
using Starlark.Eval;
using Starlark.Syntax;

namespace Basic;

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
        // Both directions: without InputEncoding, Windows decodes what is typed in the console's OEM code page and "é" arrives as "├⌐".
        Console.InputEncoding = System.Text.Encoding.UTF8;
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        // On a terminal the user sees what they type, so the machine must not echo it again; Ctrl+C is BREAK.
        return Run(Console.Out, Console.In, source, interactive: !Console.IsInputRedirected);
    }

    /// <summary>The name the editor uses for a key: Enter is "\r", Escape "\x1b", Backspace "\b", anything else is the character itself.</summary>
    private static string KeyName(int c) => c switch
    {
        '\n' or '\r' => "\r",
        27 => "\x1b",
        8 or 127 => "\b",
        // Both readers return UTF-16 units, so a character outside the BMP arrives as two keys, one per surrogate.
        _ => ((char)c).ToString(),
    };

    /// <summary>One key from a terminal, without echo and without waiting for Enter. Falls back to the text reader if the console cannot.</summary>
    private static int ReadKey(TextReader fallback)
    {
        try
        {
            var k = Console.ReadKey(intercept: true);
            return k.Key switch
            {
                ConsoleKey.Enter => '\r',
                ConsoleKey.Escape => 27,
                ConsoleKey.Backspace => 8,
                _ => k.KeyChar == '\0' ? ' ' : k.KeyChar,
            };
        }
        catch (InvalidOperationException)
        {
            return fallback.Read();
        }
    }

    /// <summary>Runs a machine against <paramref name="input"/>; if <paramref name="source"/> is given it is loaded and RUN first.</summary>
    public static int Run(TextWriter output, TextReader input, string? source, bool interactive = false, long maxSteps = 400_000_000)
    {
        var machine = new Machine(maxSteps) { Echo = !interactive };
        output.Write(machine.TakeOutput());
        output.Flush();

        // Ctrl+C is the BREAK key. It arrives on another thread, and the machine is not thread-safe, so it only sets a flag that
        // the loop below checks between slices of the program.
        int breakRequested = 0;
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            Interlocked.Exchange(ref breakRequested, 1);
        };
        if (interactive) Console.CancelKeyPress += onCancel;
        try
        {
            string status = "done";
            string Drive(string status)
            {
                while (status == "budget")
                {
                    if (Interlocked.Exchange(ref breakRequested, 0) == 1) machine.Break();
                    status = machine.Continue();
                    output.Write(machine.TakeOutput());
                    output.Flush();
                }
                return status;
            }

            // Every call into the machine goes through Safe: loading and RUNning a file, a typed line, and a key in the line editor.
            string Safe(Func<string> step)
            {
                try
                {
                    return Drive(step());
                }
                catch (StarlarkException e)
                {
                    // A fault in the interpreter script must not end the session, whatever was typed: say so and return to READY.
                    output.Write(machine.TakeOutput());
                    output.WriteLine($"?INTERNAL ERROR: {e.RawMessage}");
                    return machine.Recover();
                }
                catch (ExecutionLimitException e)
                {
                    // A program that burns through the step, time or memory budget in one slice is stopped like BREAK, not allowed to end the session.
                    output.Write(machine.TakeOutput());
                    output.WriteLine($"BREAK ({e.Limit} limit)");
                    return machine.Recover();
                }
            }

            if (source is not null)
            {
                status = Safe(() => { machine.Load(source); return machine.Type("RUN"); });
                output.Write(machine.TakeOutput());
                output.Flush();
            }
            for (;;)
            {
                if (machine.Editing)
                {
                    // The line editor reacts to each key as it is pressed, so on a terminal read single keys without waiting for Enter.
                    // Piped input has no keystrokes: each character is a key, and the end of the line is Enter.
                    int c = interactive ? ReadKey(input) : input.Read();
                    if (c < 0) break;
                    status = Safe(() => { machine.Key(KeyName(c)); return "done"; });
                    output.Write(machine.TakeOutput());
                    output.Flush();
                    continue;
                }
                var line = input.ReadLine();
                if (line is null) break;
                Interlocked.Exchange(ref breakRequested, 0);
                status = Safe(() => machine.Type(line));
                output.Write(machine.TakeOutput());
                output.Flush();
            }
            return 0;
        }
        finally
        {
            if (interactive) Console.CancelKeyPress -= onCancel;
        }
    }
}
