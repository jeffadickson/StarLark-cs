using System.Globalization;
using Starlark.Eval;
using Starlark.Syntax;

namespace ReportPipeline;

/// <summary>One CSV record.</summary>
public sealed record Sale(string Id, string Country, string Product, double Amount, string Status);

/// <summary>One aggregated report row.</summary>
public sealed record SummaryRow(string Region, double Net, double Share);

/// <summary>Host service exposed to the script; its public methods become script-callable.</summary>
public sealed class Logger
{
    /// <summary>Captured log lines.</summary>
    public List<string> Lines { get; } = [];

    /// <summary>Records an informational message.</summary>
    public void Info(string message) => Lines.Add("info: " + message);
}

/// <summary>Maps countries to sales regions.</summary>
public sealed class RegionService
{
    private static readonly Dictionary<string, string> Map = new() { ["NO"] = "EMEA", ["DE"] = "EMEA", ["US"] = "AMER", ["BR"] = "AMER", ["JP"] = "APAC" };

    /// <summary>Region for a country code, or "OTHER".</summary>
    public string Lookup(string country) => Map.GetValueOrDefault(country, "OTHER");
}

/// <summary>ETL demo: CSV rows in, Starlark transforms, JSON and a table out.</summary>
public static class Program
{
    private const string Csv = """
        id,country,product,amount,status
        1,NO,widget,120.50,paid
        2,DE,gadget,80.00,paid
        3,US,widget,200.00,paid
        4,US,gizmo,45.25,cancelled
        5,BR,gadget,310.75,paid
        6,JP,gizmo,150.00,paid
        7,JP,widget,0,paid
        8,FR,gadget,99.99,paid
        9,US,gizmo,60.00,paid
        """;

    /// <summary>Entry point.</summary>
    public static int Main() { Run(Console.Out); return 0; }

    /// <summary>Parses the embedded CSV sample.</summary>
    public static List<Sale> ParseCsv(string csv) =>
        csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => l.Trim().Split(','))
           .Select(c => new Sale(c[0], c[1], c[2], double.Parse(c[3], CultureInfo.InvariantCulture), c[4])).ToList();

    /// <summary>Runs the pipeline and prints the report.</summary>
    public static void Run(TextWriter output)
    {
        var log = new Logger();
        var regions = new RegionService();
        var interp = new Interpreter
        {
            Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableTypes = DialectTypes.Enable },
            MaxSteps = 1_000_000,
            Print = _ => { },
        };
        interp.Expose("log", log);
        interp.Register("lookup_region", (string country) => regions.Lookup(country));
        interp.Register("net_of", (double amount, string country) => Math.Round(amount * (country is "NO" or "DE" ? 0.8 : 1.0), 2));
        interp.Register("round_to", (double x, int digits) => Math.Round(x, digits));
        interp.Register("sum_values", (List<double> xs) => xs.Sum());
        interp.DefineValue("rows", ParseCsv(Csv));

        var module = interp.Execute("report.star", Scripts.Read("report.star")!);

        var summary = (List<SummaryRow>)Interop.FromStar(module.Globals["summary"], typeof(List<SummaryRow>))!;
        output.WriteLine($"{"Region",-8}{"Net",10}{"Share",8}");
        output.WriteLine(new string('-', 26));
        foreach (var r in summary)
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{r.Region,-8}{r.Net,10:F2}{r.Share,7:F1}%"));

        output.WriteLine();
        output.WriteLine("JSON: " + (string)module.Globals["payload"]!);
        output.WriteLine();
        foreach (var line in log.Lines) output.WriteLine(line);
    }
}
