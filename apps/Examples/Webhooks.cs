using Starlark.Eval;
using Starlark.Eval.Futures;
using Starlark.Syntax;

namespace Examples;

/// <summary>What a user's automation decided to do with one incoming event.</summary>
/// <param name="Rule">The rule file that ran.</param>
/// <param name="Actions">The actions it asked for, in order (for example "label bug" or "notify #oncall: ...").</param>
/// <param name="Error">Why the rule failed, or null. A failing rule never stops the others.</param>
/// <param name="Steps">How much work the rule did, for billing and for spotting slow rules.</param>
public sealed record RuleOutcome(string Rule, IReadOnlyList<string> Actions, string? Error, long Steps);

/// <summary>
/// Example 3: user-written automation rules that react to webhook events, with slow lookups done in parallel.
///
/// Users write small rules ("when an issue mentions crash, label it and page the on-call engineer if the reporter is a
/// paying customer"). The host passes each event in as plain data and offers a few host functions. Two of them,
/// <c>lookup_customer</c> and <c>http_get</c>, are slow network calls, so they are registered <c>asFuture</c>: a rule can start
/// several and wait for them together with <c>all_of</c>, and the host caps how many are in flight.
///
/// Rules never perform side effects themselves. They call <c>act(...)</c>, which only records an intention; the host applies the
/// actions after the rule finished successfully. A rule that fails or runs out of budget therefore changes nothing: the error is
/// reported for that rule alone, on the failure track, and the other rules still run.
/// </summary>
public sealed class RuleEngine
{
    private readonly Func<string, Task<string>> _customerTier;
    private readonly Func<string, Task<string>> _httpGet;

    /// <summary>Creates the engine with the two slow lookups (in production, a CRM call and an HTTP client).</summary>
    public RuleEngine(Func<string, Task<string>> customerTier, Func<string, Task<string>> httpGet)
    {
        _customerTier = customerTier;
        _httpGet = httpGet;
    }

    /// <summary>Runs every rule against one event. Each rule gets a fresh interpreter, so rules cannot see each other.</summary>
    public IReadOnlyList<RuleOutcome> Handle(string eventJson, IReadOnlyDictionary<string, string> rules)
    {
        var outcomes = new List<RuleOutcome>();
        foreach (var (name, source) in rules.OrderBy(r => r.Key, StringComparer.Ordinal))
            outcomes.Add(RunRule(name, source, eventJson));
        return outcomes;
    }

    private RuleOutcome RunRule(string name, string source, string eventJson)
    {
        var actions = new List<string>();
        var it = new Interpreter
        {
            Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableFutures = true, EnableFStrings = true },
            MaxSteps = 500_000,
            Timeout = TimeSpan.FromSeconds(3),
            MaxAllocatedBytes = 8L * 1024 * 1024,
            FutureLimits = new FutureLimits { MaxHostOperations = 4 },
            Print = _ => { },
        };
        // Slow host operations become futures: the script starts them, keeps going, and waits with all_of() or .get().
        it.Register("lookup_customer", (string email) => _customerTier(email), threadSafe: true, asFuture: true);
        it.Register("http_get", (string url) => _httpGet(url), threadSafe: true, asFuture: true);
        // act() records what the rule wants; nothing happens until the rule has finished without an error.
        it.Register("act", (string kind, string detail) => { actions.Add($"{kind} {detail}"); return true; });
        try
        {
            // The event arrives as ordinary Starlark data: dicts, lists, strings and numbers. The raw JSON text is handed over as a
            // value (never pasted into source code) and decoded by Starlark's own json module, so malformed JSON is a Starlark error
            // for this rule and not an exception in the host.
            it.DefineValue("_event_json", eventJson);
            it.DefineValue("event", it.Eval("json.decode(_event_json)"));
            var module = it.Execute(name, source);
            if (module.TryGet("on_event", out var fn) && fn is ICallable) it.CallValue(fn, []);
            return new RuleOutcome(name, actions, null, it.LastRunSteps);
        }
        catch (Exception e) when (e is StarlarkException or StarlarkSyntaxException or ExecutionLimitException or OperationCanceledException)
        {
            // The rule failed: its recorded actions are discarded, and only this rule's outcome carries the error.
            return new RuleOutcome(name, [], e.Message.Split('\n')[0], it.LastRunSteps);
        }
    }
}

/// <summary>Runs example 3 and writes what happens.</summary>
public static class WebhookDemo
{
    /// <summary>Delivers one "issue opened" event to three rules: a triage rule, an enrichment rule, and a broken one.</summary>
    public static void Run(TextWriter output)
    {
        var engine = new RuleEngine(
            customerTier: async email => { await Task.Delay(40); return email.EndsWith("@bigcorp.com", StringComparison.Ordinal) ? "enterprise" : "free"; },
            httpGet: async url => { await Task.Delay(40); return url.Contains("status", StringComparison.Ordinal) ? "{\"incident\": true}" : "{}"; });
        var rules = new[] { "triage.star", "enrich.star", "broken.star" }
            .ToDictionary(n => n, n => Scripts.Read("webhooks/" + n));
        const string issue = """
            {"kind": "issue_opened", "number": 4711, "title": "App crash on login",
             "body": "It crashes every time since 2.3.1", "reporter": "dana@bigcorp.com", "labels": []}
            """;
        output.WriteLine("Example 3: webhook automation rules");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (var o in engine.Handle(issue, rules))
        {
            if (o.Error is null)
                output.WriteLine($"  {o.Rule}: {(o.Actions.Count == 0 ? "no action" : string.Join("; ", o.Actions))}");
            else
                output.WriteLine($"  {o.Rule}: FAILED, nothing applied: {o.Error}");
        }
        output.WriteLine($"  (all rules took {clock.ElapsedMilliseconds} ms; enrich.star made two 40 ms lookups side by side)");
    }
}
