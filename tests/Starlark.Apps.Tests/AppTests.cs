namespace Starlark.Apps.Tests;

/// <summary>Runs each sample application and asserts on its key output lines.</summary>
public class AppTests
{
    private static string Capture(Action<TextWriter> run)
    {
        var sw = new StringWriter();
        run(sw);
        return sw.ToString().Replace("\r\n", "\n");
    }

    [Fact]
    public void BuildRulesPlansInDependencyOrderAndReportsCycles()
    {
        var o = Capture(BuildRules.Program.Run);
        Assert.Contains("[1/5] cc_library base", o);
        Assert.Contains("[5/5] cc_binary  client", o);
        Assert.Contains("error: dependency cycle: a -> b -> c -> a", o);
        Assert.Contains("is not a valid element of this enumeration", o);
        Assert.Contains("//rules:cc.star:15", o);
    }

    [Fact]
    public void PolicyEngineDecidesLimitsAndRejects()
    {
        var o = Capture(PolicyEngine.Program.Run);
        Assert.Contains("c1: allow=True discount=97.50 reason=ok", o);
        Assert.Contains("c2: allow=False discount=0.00 reason=region blocked", o);
        Assert.Contains("stopped (steps)", o);
        Assert.Contains("rejected: error: Expected type `dict` but got `int`", o);
    }

    [Fact]
    public void GameAiPlaysDeterministicallyAndHotReloads()
    {
        var o = Capture(GameAI.Program.Run);
        Assert.Contains("T04 Bob: loaded sniper.star", o);
        Assert.Contains("Cy: FAULT Script exceeded the limit", o);
        Assert.Contains("Winner: Bob", o);
        Assert.Equal(o, Capture(GameAI.Program.Run));
    }

    [Fact]
    public void ReportPipelineAggregates()
    {
        var o = Capture(ReportPipeline.Program.Run);
        Assert.Contains("AMER        570.75   58.2%", o);
        Assert.Contains("\"top\":[{\"product\":\"gadget\"", o);
        Assert.Contains("info: kept 7, dropped 2", o);
    }

    [Fact]
    public void AdventureWalkthroughWinsTheGame()
    {
        var sw = new StringWriter();
        Assert.Equal(0, Adventure.Program.Run(sw, Adventure.Program.Walkthrough));
        var o = sw.ToString();
        Assert.Contains("Carved into the wood", o);
        Assert.Contains("The bronze key turns with a groan", o);
        Assert.Contains("*** YOU HAVE WON ***", o);
        Assert.Contains("Score: 105 in 15 turns.", o);
        Assert.DoesNotContain("the game script failed", o);
    }

    [Fact]
    public void AdventureRulesHold()
    {
        string Play(params string[] commands)
        {
            var sw = new StringWriter();
            Adventure.Program.Run(sw, commands);
            return sw.ToString();
        }

        // Walking into the dark without a lit lamp is fatal, and the game is then over.
        var dead = Play("north", "in", "down");
        Assert.Contains("YOU HAVE DIED", dead);

        // The locked door stays locked without the key.
        Assert.Contains("is locked", Play("take lamp", "north", "in", "light lamp", "down", "east", "east"));

        // The lamp can be taken, the sign cannot, and unknown words get a polite answer.
        var o = Play("take sign", "dance", "take lamp", "inventory");
        Assert.Contains("You can't take the sign.", o);
        Assert.Contains("I don't know how to \"dance\".", o);
        Assert.Contains("You are carrying: lamp.", o);

        // The same input always gives the same game.
        Assert.Equal(Play("look", "take lamp", "east"), Play("look", "take lamp", "east"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MandelbrotPictureDoesNotDependOnTheNumberOfWorkers(bool colour)
    {
        var sw = new StringWriter();
        var view = Mandelbrot.View.All[0];
        Assert.Equal(0, Mandelbrot.Program.Run(sw, (30, 10), view, colour, [1, 3, 8]));
        var o = sw.ToString();
        Assert.Contains("Every worker count produced exactly the same picture.", o);
        Assert.DoesNotContain("MISMATCH", o);
        // True colour output uses the 24-bit escape sequences and half-block characters; plain output uses neither.
        Assert.Equal(colour, o.Contains("\u001b[38;2;"));
        Assert.Equal(colour, o.Contains('▀'));
    }

    [Fact]
    public void MandelbrotShowsTheSetAndItsSurroundings()
    {
        var sw = new StringWriter();
        Mandelbrot.Program.Run(sw, (60, 22), Mandelbrot.View.All[0], false, [2]);
        var o = sw.ToString();
        Assert.Contains("@", o);   // inside the set
        Assert.Contains(",", o);   // far outside
        // The set is symmetric about the real axis, so the picture is too (a row and its mirror agree).
        var rows = o.Split('\n').Skip(2).Take(22).Select(r => r.TrimEnd('\r')).ToArray();
        Assert.Equal(rows[0], rows[^1]);
    }

    [Fact]
    public void RayTracerPictureDoesNotDependOnTheNumberOfWorkers()
    {
        var sw = new StringWriter();
        Assert.Equal(0, RayTracer.Program.Run(sw, (32, 14), [1, 3, 8]));
        var o = sw.ToString();
        Assert.Contains("Every worker count produced exactly the same picture.", o);
        Assert.DoesNotContain("MISMATCH", o);
        Assert.Equal(3, o.Split('\n').Count(l => l.Contains("worker(s)")));
    }

    [Fact]
    public void ConfigInspectorValidatesAndSetsExitCode()
    {
        var sw = new StringWriter();
        Assert.Equal(0, ConfigInspector.Program.Run(sw, []));
        var o = sw.ToString();
        Assert.Contains("name=app-prod replicas=6", o);
        Assert.Contains("Binary operator `+` is not available", o);
        Assert.Equal(0, ConfigInspector.Program.Run(new StringWriter(), ["--check"]));
        Assert.Equal(1, ConfigInspector.Program.Run(new StringWriter(), ["--check", "broken"]));
    }
}
