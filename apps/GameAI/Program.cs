using Starlark.Eval;
using Starlark.Syntax;

namespace GameAI;

/// <summary>An agent's state as seen by scripts.</summary>
public sealed class Fighter
{
    /// <summary>Display name.</summary>
    public string Name { get; set; } = "";
    /// <summary>Position on the line.</summary>
    public int Pos { get; set; }
    /// <summary>Hit points.</summary>
    public int Hp { get; set; } = 30;
    /// <summary>Energy; attacks cost 2.</summary>
    public int Energy { get; set; } = 6;
    /// <summary>Script file currently driving this fighter.</summary>
    public string Script { get; set; } = "";
}

/// <summary>The action a script returns (converted from a Starlark record).</summary>
public sealed record GameAction(string Kind, int Dir);

/// <summary>Hosts per-agent Starlark behaviours with hot reload and step budgets.</summary>
public sealed class Brain(string name, int budget)
{
    private Interpreter? _interp;
    private Module? _module;
    private string _loadedSource = "";

    /// <summary>Per-turn step budget.</summary>
    public int Budget { get; } = budget;

    /// <summary>Name of the script currently loaded.</summary>
    public string Name { get; private set; } = name;

    /// <summary>Re-executes the script if its source differs from what is loaded; returns true if it reloaded.</summary>
    public bool Reload(string script)
    {
        var src = Scripts.Read(script)!;
        if (script == Name && src == _loadedSource && _module is not null) return false;
        _interp = new Interpreter
        {
            Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableTypes = DialectTypes.Enable },
            SourceLoader = Scripts.Read,
            MaxSteps = Budget,
            Print = _ => { },
        };
        _module = _interp.Execute(script, src);
        _loadedSource = src;
        Name = script;
        return true;
    }

    /// <summary>Asks the script for an action; a script that errors or overruns its budget just rests.</summary>
    public GameAction Act(object state, out string? fault)
    {
        fault = null;
        try { return _interp!.Call<GameAction>(_module!, "act", state); }
        catch (ExecutionLimitException e) { fault = e.Message; }
        catch (StarlarkException e) { fault = e.RawMessage; }
        return new GameAction("rest", 0);
    }
}

/// <summary>A deterministic arena match.</summary>
public static class Program
{
    /// <summary>Entry point.</summary>
    public static int Main() { Run(Console.Out); return 0; }

    /// <summary>Plays the match, writing the log to <paramref name="output"/>.</summary>
    public static void Run(TextWriter output)
    {
        var fighters = new[]
        {
            new Fighter { Name = "Ann", Pos = 0, Script = "rusher.star" },
            new Fighter { Name = "Bob", Pos = 9, Script = "timid.star" },
            new Fighter { Name = "Cy", Pos = 5, Script = "glutton.star" },
        };
        var brains = fighters.ToDictionary(f => f.Name, f => new Brain(f.Name, 2000));
        // Hot reload schedule: at turn 4 Bob's file "changes" on disk to the sniper script.
        var edits = new Dictionary<int, (string, string)> { [4] = ("Bob", "sniper.star") };

        for (int turn = 1; turn <= 12; turn++)
        {
            if (edits.TryGetValue(turn, out var edit)) fighters.First(f => f.Name == edit.Item1).Script = edit.Item2;
            foreach (var me in fighters.Where(f => f.Hp > 0))
            {
                if (brains[me.Name].Reload(me.Script)) output.WriteLine($"T{turn:00} {me.Name}: loaded {me.Script}");
                var foes = fighters.Where(f => f != me && f.Hp > 0).OrderBy(f => Math.Abs(f.Pos - me.Pos)).ThenBy(f => f.Name).ToList();
                if (foes.Count == 0) continue;
                var action = brains[me.Name].Act(new { me, foes }, out var fault);
                if (fault is not null) output.WriteLine($"T{turn:00} {me.Name}: FAULT {fault}");
                Apply(output, turn, me, foes, action);
            }
            foreach (var f in fighters) f.Energy = Math.Min(6, f.Energy + 1);
            if (fighters.Count(f => f.Hp > 0) <= 1) break;
        }
        var winner = fighters.Where(f => f.Hp > 0).OrderByDescending(f => f.Hp).First();
        output.WriteLine("Final: " + string.Join(", ", fighters.Select(f => $"{f.Name} hp={f.Hp}")));
        output.WriteLine($"Winner: {winner.Name}");
    }

    private static void Apply(TextWriter output, int turn, Fighter me, List<Fighter> foes, GameAction a)
    {
        switch (a.Kind)
        {
            case "move":
                me.Pos = Math.Clamp(me.Pos + a.Dir, 0, 12);
                output.WriteLine($"T{turn:00} {me.Name}: move to {me.Pos}");
                break;
            case "attack" when me.Energy >= 2:
                me.Energy -= 2;
                var target = foes.FirstOrDefault(f => Math.Abs(f.Pos - me.Pos) <= 2);
                if (target is null) { output.WriteLine($"T{turn:00} {me.Name}: attack misses"); break; }
                target.Hp = Math.Max(0, target.Hp - 10);
                output.WriteLine($"T{turn:00} {me.Name}: hits {target.Name} (hp {target.Hp})");
                break;
            default:
                me.Energy = Math.Min(6, me.Energy + 2);
                output.WriteLine($"T{turn:00} {me.Name}: rest (energy {me.Energy})");
                break;
        }
    }
}
