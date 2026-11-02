using Starlark.Eval;
using Starlark.Syntax;

namespace Adventure;

/// <summary>
/// A text adventure whose world and rules are Starlark. This host does three things: it loads <c>game.star</c>, hands
/// each typed line to the script's <c>step</c> function, and prints the reply. The script cannot touch the console,
/// the disk or the clock, so the same game runs identically in a terminal and in a test.
/// </summary>
public static class Program
{
    /// <summary>Runs the game. With no arguments it reads from standard input; <c>--demo</c> plays a fixed walkthrough.</summary>
    public static int Main(string[] args) => Run(Console.Out, args.Contains("--demo") ? Walkthrough : null, Console.In);

    /// <summary>A complete winning game, used by <c>--demo</c> and by the tests.</summary>
    public static readonly string[] Walkthrough =
    [
        "look", "examine sign", "take lamp", "east", "take key", "west", "north", "in", "light lamp",
        "down", "east", "unlock door", "east", "examine chest", "take gem",
    ];

    /// <summary>Plays one game, reading commands from <paramref name="script"/> if given, else from <paramref name="input"/>.</summary>
    /// <returns>0 if the player won, 1 otherwise.</returns>
    public static int Run(TextWriter output, IReadOnlyList<string>? script = null, TextReader? input = null)
    {
        var it = new Interpreter
        {
            Dialect = Dialect.Standard with { EnableTopLevelStmt = true },
            MaxSteps = 5_000_000,
            Timeout = TimeSpan.FromSeconds(10),
            MaxAllocatedBytes = 32 * 1024 * 1024,
        };
        it.SourceLoader = id => Scripts.Read(id);

        Module module;
        try
        {
            module = it.Execute("game.star", Scripts.Read("game.star") ?? throw new InvalidOperationException("game.star is missing"));
        }
        catch (StarlarkException e)
        {
            output.WriteLine("The game script has an error:");
            output.WriteLine(e.Message);
            return 2;
        }

        object? state = it.CallValue(module.Globals["new_game"], []);
        var step = module.Globals["step"];
        var describe = module.Globals["describe"];

        output.WriteLine("THE LAMPLIGHTER'S CAVE");
        output.WriteLine("A small adventure. Type HELP for hints, QUIT to leave.");
        output.WriteLine();
        output.WriteLine(Text(it.CallValue(describe, [state, true])));

        int next = 0;
        while (!IsDone(state))
        {
            string? line;
            if (script is not null)
            {
                if (next >= script.Count) break;
                line = script[next++];
                output.WriteLine();
                output.WriteLine("> " + line);
            }
            else
            {
                output.WriteLine();
                output.Write("> ");
                line = input?.ReadLine();
                if (line is null) break;
            }

            try
            {
                output.WriteLine(Text(it.CallValue(step, [state, line])));
            }
            catch (StarlarkException e)
            {
                // A bug in the game script is shown, with where it happened, and play continues.
                output.WriteLine("[the game script failed: " + e.RawMessage + "]");
                output.WriteLine(e.MessageWithStack());
            }
            catch (ExecutionLimitException e)
            {
                output.WriteLine("[the game used too much time or memory: " + e.Message + "]");
                return 2;
            }
        }

        output.WriteLine();
        output.WriteLine($"Score: {Score(state)} in {Turns(state)} turns.");
        return IsWon(state) ? 0 : 1;
    }

    private static string Text(object? value) => value as string ?? "";

    private static object? Field(object? state, string key) => state is StarDict d && d.TryGet(key, out var v) ? v : null;

    private static bool IsDone(object? state) => Field(state, "done") is true;

    private static bool IsWon(object? state) => Field(state, "won") is true;

    private static object Score(object? state) => Field(state, "score") ?? 0;

    private static object Turns(object? state) => Field(state, "turns") ?? 0;
}
