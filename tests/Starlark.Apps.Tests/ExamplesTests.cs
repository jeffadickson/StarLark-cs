using Examples;

namespace Starlark.Apps.Tests;

/// <summary>The three embedding examples in apps/Examples: each does what its documentation says, and every failure stays on its track.</summary>
public class ExamplesTests
{
    private static readonly Dictionary<string, decimal> Rates = new() { ["USD"] = 1m, ["EUR"] = 0.92m };
    private static readonly List<OrderLine> Lines = [new("TEA-01", 3, 4.50m), new("CUP-07", 12, 2.25m)];

    private static PricingService Pricing() => new(code => Rates.TryGetValue(code, out var r) ? r : 1m);

    [Fact]
    public void AGoodPricingScriptIsAcceptedAndPrices()
    {
        var service = Pricing();
        Assert.Null(service.Upload("acme", Scripts.Read("pricing/acme.star")));
        var (quote, error) = service.Price("acme", Lines, "gold", "EUR");
        Assert.Null(error);
        // 3 * 4.50 = 13.50, plus 12 * 2.25 = 27 at the 15% bulk rate = 22.95: 36.45, then 10% off = 32.805, at 0.92 = 30.18.
        Assert.Equal(30.18m, quote!.Total);
        Assert.Equal(["bulk rate on CUP-07", "gold discount 10%", "converted to EUR at 0.92"], quote.Explanation);
        Assert.Equal(36.45m, service.Price("acme", Lines, "standard", "USD").Quote!.Total);
    }

    [Fact]
    public void ATypeErrorIsRejectedAtUploadNotAtCheckout()
    {
        var error = Pricing().Upload("typo-co", Scripts.Read("pricing/typo-co.star"));
        Assert.NotNull(error);
        Assert.Equal("check", error.Stage);
        Assert.Contains("dict", error.Message);
    }

    [Fact]
    public void ARunawayScriptIsStoppedByItsBudgetAndReportedAsAValue()
    {
        var service = Pricing();
        Assert.Null(service.Upload("loopy", Scripts.Read("pricing/loopy.star")));
        var (quote, error) = service.Price("loopy", Lines, "gold", "USD");
        Assert.Null(quote);
        Assert.Equal("run", error!.Stage);
        Assert.Contains("steps", error.Message);
    }

    [Fact]
    public void PricingNeverThrowsForBadScriptsOrUnknownTenants()
    {
        var service = Pricing();
        Assert.NotNull(service.Upload("t1", "def price(:\n"));                       // does not parse
        Assert.NotNull(service.Upload("t2", "x = 1\n"));                              // no price()
        Assert.NotNull(service.Upload("t3", "def price(o, c):\n    return 1 // 0\n" + "x = price([], {})\n"));
        Assert.Null(service.Upload("t4", "def price(order: list, customer: dict) -> tuple:\n    return ('a lot', [])\n"));
        Assert.Equal("run", service.Price("t4", Lines, "gold", "USD").Error!.Stage);   // wrong result shape
        Assert.NotNull(service.Price("nobody", Lines, "gold", "USD").Error);
    }

    [Fact]
    public void AnUploadedScriptCanBeCalledFromManyThreadsAtOnce()
    {
        var service = Pricing();
        Assert.Null(service.Upload("acme", Scripts.Read("pricing/acme.star")));
        var totals = new decimal[64];
        Parallel.For(0, totals.Length, i => totals[i] = service.Price("acme", Lines, "gold", "EUR").Quote!.Total);
        Assert.All(totals, t => Assert.Equal(30.18m, t));
    }

    [Fact]
    public void ConfigurationsLoadIntoTypedRecords()
    {
        var staging = ConfigLoader.Load("staging", n => Scripts.TryRead("config/" + n));
        Assert.True(staging.Ok);
        Assert.Equal(["api", "worker", "web"], staging.Services.Select(s => s.Name).ToArray());
        Assert.Equal("registry.example.com/api:2.4.0-rc1", staging.Services[0].Image);
        Assert.Equal("debug", staging.Services[0].Env["LOG_LEVEL"]);

        var production = ConfigLoader.Load("production", n => Scripts.TryRead("config/" + n));
        Assert.True(production.Ok);
        // Replicas are computed from the expected peak load: ceil(1800 / 250) = 8, ceil(400 / 250) = 2, ceil(2600 / 250) = 11.
        Assert.Equal([8, 2, 11], production.Services.Select(s => s.Replicas).ToArray());
    }

    [Fact]
    public void EveryConfigurationProblemIsReportedInOnePass()
    {
        var broken = ConfigLoader.Load("broken", n => Scripts.TryRead("config/" + n));
        Assert.False(broken.Ok);
        Assert.Empty(broken.Services);
        Assert.Equal(3, broken.Errors.Count);
        Assert.Contains(broken.Errors, e => e.Contains("must include a tag"));
        Assert.Contains(broken.Errors, e => e.Contains("replicas must be"));
        Assert.Contains(broken.Errors, e => e.Contains("port 8080 is used by api and web"));
    }

    [Fact]
    public void ConfigurationCannotReachOutsideItsFolder()
    {
        var files = new Dictionary<string, string>
        {
            ["escape.star"] = "load('../secrets.star', 'password')\ndef services():\n    return []\n",
            ["../secrets.star"] = "password = 'hunter2'\n",
        };
        var result = ConfigLoader.Load("escape", n => files.GetValueOrDefault(n));
        Assert.False(result.Ok);
        Assert.DoesNotContain(result.Errors, e => e.Contains("hunter2"));
    }

    [Fact]
    public void ConfigurationFailuresAreValuesNotExceptions()
    {
        Assert.False(ConfigLoader.Load("missing", _ => null).Ok);
        Assert.False(ConfigLoader.Load("x", _ => "def services(:\n").Ok);
        Assert.False(ConfigLoader.Load("x", _ => "def services():\n    return 5\n").Ok);
        Assert.False(ConfigLoader.Load("x", _ => "def services():\n    return [1]\n").Ok);
        Assert.False(ConfigLoader.Load("x", _ => "def services():\n    for i in range(100000000):\n        pass\n    return []\n").Ok);
    }

    private static RuleEngine Engine(int delayMs = 40) => new(
        customerTier: async email => { await Task.Delay(delayMs); return email.EndsWith("@bigcorp.com", StringComparison.Ordinal) ? "enterprise" : "free"; },
        httpGet: async url => { await Task.Delay(delayMs); return url.Contains("status", StringComparison.Ordinal) ? "{\"incident\": true}" : "{}"; });

    private const string Issue = """
        {"kind": "issue_opened", "number": 4711, "title": "App crash on login",
         "body": "It crashes every time", "reporter": "dana@bigcorp.com", "labels": []}
        """;

    private static Dictionary<string, string> Rules(params string[] names) => names.ToDictionary(n => n, n => Scripts.Read("webhooks/" + n));

    [Fact]
    public void RulesReactToTheEvent()
    {
        var outcomes = Engine().Handle(Issue, Rules("triage.star", "enrich.star"));
        var triage = outcomes.Single(o => o.Rule == "triage.star");
        Assert.Null(triage.Error);
        Assert.Equal(["label crash", "notify #oncall: enterprise crash report #4711: App crash on login"], triage.Actions);
        var enrich = outcomes.Single(o => o.Rule == "enrich.star");
        Assert.Equal(["comment reporter tier: enterprise; an incident is open, link this issue to it"], enrich.Actions);
        Assert.True(enrich.Steps > 0);
    }

    [Fact]
    public void AFailingRuleAppliesNothingAndDoesNotStopTheOthers()
    {
        var outcomes = Engine().Handle(Issue, Rules("broken.star", "triage.star"));
        var broken = outcomes.Single(o => o.Rule == "broken.star");
        Assert.NotNull(broken.Error);
        Assert.Contains("team", broken.Error);
        Assert.Empty(broken.Actions);                         // its "label needs-team" was recorded, then discarded
        Assert.Null(outcomes.Single(o => o.Rule == "triage.star").Error);
    }

    [Fact]
    public void TheTwoLookupsInARuleOverlap()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var outcome = Engine(delayMs: 300).Handle(Issue, Rules("enrich.star")).Single();
        Assert.Null(outcome.Error);
        // One after the other they take 600 ms; side by side about 300. Leave room for a slow machine.
        Assert.True(clock.ElapsedMilliseconds < 550, $"took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void HostileEventsAndRulesStayOnTheFailureTrack()
    {
        var engine = Engine(delayMs: 1);
        Assert.NotNull(engine.Handle("not json at all", Rules("triage.star")).Single().Error);
        Assert.NotNull(engine.Handle("[1, 2, 3]", Rules("triage.star")).Single().Error);
        var hostile = new Dictionary<string, string>
        {
            ["spin.star"] = "def on_event():\n    for i in range(100000000):\n        pass\n",
            ["deep.star"] = "x = " + new string('[', 5000) + new string(']', 5000) + "\n",
            ["leak.star"] = "def on_event():\n    fs = [lookup_customer('a') for _ in range(10)]\n",
            ["noparse.star"] = "def on_event(:\n",
        };
        foreach (var o in engine.Handle(Issue, hostile))
        {
            Assert.NotNull(o.Error);
            Assert.Empty(o.Actions);
        }
    }

    [Fact]
    public void TheProgramRunsAllThreeExamples()
    {
        var sw = new StringWriter();
        PricingDemo.Run(sw);
        ConfigDemo.Run(sw);
        WebhookDemo.Run(sw);
        var o = sw.ToString();
        Assert.Contains("upload typo-co: rejected at check", o);
        Assert.Contains("production: 3 services", o);
        Assert.Contains("broken.star: FAILED, nothing applied", o);
    }
}
