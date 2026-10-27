using Starlark.Eval;
using Starlark.Syntax;

namespace PolicyEngine;

/// <summary>A purchased line.</summary>
public sealed record Item(string Sku, double Price, int Qty);

/// <summary>The buyer.</summary>
public sealed record Customer(string Id, string Tier, string Region, bool Verified);

/// <summary>What the host passes to a tenant policy.</summary>
public sealed record Request(Customer Customer, List<Item> Items);

/// <summary>What a tenant policy must return.</summary>
public sealed record Decision(bool Allow, double Discount, string Reason);

/// <summary>A sandboxed rules engine: untrusted tenant scripts decide on requests.</summary>
public static class Program
{
    /// <summary>Entry point.</summary>
    public static int Main() { Run(Console.Out); return 0; }

    private static Interpreter Sandbox() => new()
    {
        Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableTypes = DialectTypes.Enable },
        MaxSteps = 50_000,
        Timeout = TimeSpan.FromSeconds(2),
        Print = _ => { },
    };

    /// <summary>Loads a tenant script and evaluates its decide() against a request.</summary>
    public static Decision Evaluate(string script, Request request, bool typecheck = false)
    {
        var interp = Sandbox();
        interp.TypecheckBeforeExecute = typecheck;
        var module = interp.Execute(script + ".star", Scripts.Read(script + ".star")!);
        return interp.Call<Decision>(module, "decide", request);
    }

    /// <summary>Runs the demo, writing to <paramref name="output"/>.</summary>
    public static void Run(TextWriter output)
    {
        var requests = new[]
        {
            new Request(new("c1", "Gold", "NO", true), [new("pen", 2.5, 100), new("ink", 40, 10)]),
            new Request(new("c2", "Silver", "XX", false), [new("desk", 6000, 1)]),
            new Request(new("c3", "Basic", "US", false), [new("mug", 12, 2), new("cap", 20, 3)]),
        };
        foreach (var tenant in new[] { "acme", "globex" })
        {
            output.WriteLine($"== tenant {tenant} ==");
            foreach (var r in requests)
            {
                var d = Evaluate(tenant, r);
                output.WriteLine($"{r.Customer.Id}: allow={d.Allow} discount={d.Discount:F2} reason={d.Reason}");
            }
        }

        output.WriteLine("== hostile tenant ==");
        try { Evaluate("hostile", requests[0]); }
        catch (ExecutionLimitException e) { output.WriteLine($"stopped ({e.Limit}): {e.Message}"); }

        output.WriteLine("== type-checked upload ==");
        try { Evaluate("badtypes", requests[0], typecheck: true); }
        catch (StarlarkException e) { output.WriteLine("rejected: " + e.Message.TrimEnd()); }
    }
}
