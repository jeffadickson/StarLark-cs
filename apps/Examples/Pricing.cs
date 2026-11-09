using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Starlark.Eval;
using Starlark.Syntax;

namespace Examples;

/// <summary>A line on an order, as the service models it.</summary>
/// <param name="Sku">Product code.</param>
/// <param name="Quantity">How many.</param>
/// <param name="UnitPrice">List price of one, in the order's currency.</param>
public sealed record OrderLine(string Sku, int Quantity, decimal UnitPrice);

/// <summary>What a tenant's pricing script decided for one order.</summary>
/// <param name="Total">The price to charge.</param>
/// <param name="Explanation">One line per adjustment, for the invoice.</param>
public sealed record Quote(decimal Total, IReadOnlyList<string> Explanation);

/// <summary>Why a tenant's script was not accepted or did not produce a quote. The service shows this to the tenant.</summary>
/// <param name="Stage">"check" (the script was rejected before it ran) or "run" (it failed while pricing an order).</param>
/// <param name="Message">What went wrong, with the script location.</param>
public sealed record PricingError(string Stage, string Message);

/// <summary>
/// Example 1: tenant-specific pricing rules in a multi-tenant service.
///
/// Each tenant uploads a Starlark script that defines <c>price(order, customer)</c>. The service never trusts the script: it is
/// type checked against the host's own signatures when uploaded, so a typo is reported to the tenant at upload time, not on a
/// customer's checkout; it runs with a step budget, a time budget and a memory cap, so a bad loop cannot slow other tenants; and
/// it can only call the functions the service gives it (here, an exchange-rate lookup). A compiled script is cached by the hash
/// of its text, and because a loaded module is frozen it is safe to call from many request threads at once.
///
/// Errors follow one track: every failure becomes a <see cref="PricingError"/> value, never an exception that escapes to the
/// request pipeline.
/// </summary>
public sealed class PricingService
{
    private readonly ConcurrentDictionary<string, (Interpreter It, Module Module)> _compiled = new();
    private readonly Func<string, decimal> _rate;

    /// <summary>Creates the service with an exchange-rate source (currency code to rate against USD).</summary>
    public PricingService(Func<string, decimal> rate) => _rate = rate;

    /// <summary>The interpreter every tenant script runs in: the same dialect, budgets and host functions for all.</summary>
    private Interpreter NewInterpreter()
    {
        var it = new Interpreter
        {
            Dialect = Dialect.Standard with { EnableTypes = DialectTypes.Enable, EnableFStrings = true },
            MaxSteps = 200_000,
            Timeout = TimeSpan.FromMilliseconds(250),
            MaxAllocatedBytes = 4L * 1024 * 1024,
            TypecheckBeforeExecute = true,
            Print = _ => { },   // tenants' print() goes nowhere in production
        };
        // The only way out of the sandbox: a typed host function. The checker knows its signature (str -> float).
        it.Register("usd_rate", (string currency) => (double)_rate(currency));
        return it;
    }

    /// <summary>
    /// Accepts a tenant's script: checks it and loads it once. Returns null on success, or the reason it was rejected.
    /// </summary>
    public PricingError? Upload(string tenant, string script)
    {
        var it = NewInterpreter();
        try
        {
            // Typecheck parses first, so a script that does not parse raises StarlarkSyntaxException here: it is caught below with
            // the other script errors and returned as a value like them.
            var check = it.Typecheck(tenant + ".star", script);
            if (check.Errors.Count > 0)
                return new PricingError("check", string.Join("\n", check.Errors.Select(e => e.Message)));
            var module = it.Execute(tenant + ".star", script);
            if (!module.TryGet("price", out var fn) || fn is not ICallable)
                return new PricingError("check", "the script must define a function price(order, customer)");
            _compiled[Key(tenant, script)] = (it, module);
            _current[tenant] = Key(tenant, script);
            return null;
        }
        catch (Exception e) when (e is StarlarkException or StarlarkSyntaxException or ExecutionLimitException)
        {
            return new PricingError("check", e.Message);
        }
    }

    private readonly ConcurrentDictionary<string, string> _current = new();

    /// <summary>
    /// Prices an order with the tenant's current script. Exactly one of the two results is non-null.
    /// </summary>
    public (Quote? Quote, PricingError? Error) Price(string tenant, IReadOnlyList<OrderLine> lines, string customerTier, string currency)
    {
        if (!_current.TryGetValue(tenant, out var key) || !_compiled.TryGetValue(key, out var entry))
            return (null, new PricingError("run", $"tenant {tenant} has no pricing script"));

        // The order goes in as plain Starlark data: a list of dicts and a dict. Nothing the script receives can call back into the host.
        var order = lines.Select(l => new Dictionary<string, object?>
        {
            ["sku"] = l.Sku, ["qty"] = l.Quantity, ["unit"] = (double)l.UnitPrice,
        }).ToList();
        var customer = new Dictionary<string, object?> { ["tier"] = customerTier, ["currency"] = currency };
        try
        {
            var result = entry.It.CallValue(entry.Module.Globals["price"], [Interop.ToStar(order), Interop.ToStar(customer)]);
            // The script returns (total, [reasons]). Anything else is the tenant's mistake, reported as such.
            if (result is not StarTuple { Items: [var total, StarList reasons] } || total is not (double or System.Numerics.BigInteger))
                return (null, new PricingError("run", "price() must return (total, [explanations])"));
            decimal amount = total is double d ? (decimal)d : (decimal)(System.Numerics.BigInteger)total;
            return (new Quote(Math.Round(amount, 2), reasons.Items.Select(r => r?.ToString() ?? "").ToList()), null);
        }
        catch (Exception e) when (e is StarlarkException or ExecutionLimitException or OverflowException)
        {
            return (null, new PricingError("run", e.Message));
        }
    }

    private static string Key(string tenant, string script) =>
        tenant + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script)));
}

/// <summary>Runs example 1 and writes what happens.</summary>
public static class PricingDemo
{
    /// <summary>Uploads three tenant scripts (one good, one with a type error, one that runs forever) and prices orders.</summary>
    public static void Run(TextWriter output)
    {
        var rates = new Dictionary<string, decimal> { ["USD"] = 1m, ["EUR"] = 0.92m, ["GBP"] = 0.79m };
        var service = new PricingService(code => rates.TryGetValue(code, out var r) ? r : 1m);
        var lines = new List<OrderLine> { new("TEA-01", 3, 4.50m), new("CUP-07", 12, 2.25m) };

        output.WriteLine("Example 1: tenant pricing rules");
        foreach (var tenant in new[] { "acme", "typo-co", "loopy" })
        {
            var error = service.Upload(tenant, Scripts.Read($"pricing/{tenant}.star"));
            output.WriteLine(error is null ? $"  upload {tenant}: accepted" : $"  upload {tenant}: rejected at {error.Stage}: {FirstLine(error.Message)}");
        }
        foreach (var (tier, currency) in new[] { ("gold", "EUR"), ("standard", "USD") })
        {
            var (quote, error) = service.Price("acme", lines, tier, currency);
            if (quote is not null)
                output.WriteLine($"  acme {tier}/{currency}: {quote.Total} ({string.Join("; ", quote.Explanation)})");
            else
                output.WriteLine($"  acme {tier}/{currency}: error: {FirstLine(error!.Message)}");
        }
        var (_, loopError) = service.Price("loopy", lines, "gold", "USD");
        output.WriteLine($"  loopy: {FirstLine(loopError!.Message)}");
    }

    private static string FirstLine(string s) => s.Split('\n')[0];
}
