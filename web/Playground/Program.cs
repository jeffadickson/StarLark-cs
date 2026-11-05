using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using Starlark.Eval;
using Starlark.Eval.Futures;
using Starlark.Syntax;

// Nothing runs at start-up; the page calls the exported functions of Playground.
return 0;

/// <summary>
/// The functions the browser page can call. They take and return plain strings (results as small JSON objects), so the
/// JavaScript side stays trivial. Every Starlark run builds a fresh interpreter with a step, time and memory budget, so a
/// runaway script in the page ends with a clean error message instead of freezing the tab.
/// </summary>
public static partial class Playground
{
    /// <summary>
    /// Runs a Starlark program. Returns JSON <c>{"output": text, "error": text or null, "ms": number, "steps": number}</c>.
    /// </summary>
    /// <param name="source">The program.</param>
    /// <param name="options">
    /// Comma-separated features: <c>types</c> (annotations, records, enums), <c>fstrings</c>, <c>futures</c> (parallel_map,
    /// spawn, all_of), <c>recursion</c>, <c>utf16</c> (UTF-16 string elements instead of UTF-8), <c>check</c> (run the static
    /// type checker first and do not run the program if it finds errors).
    /// </param>
    [JSExport]
    public static string Run(string source, string options)
    {
        var opts = options.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        var output = new StringBuilder();
        var it = new Interpreter
        {
            Dialect = Dialect.Standard with
            {
                EnableTopLevelStmt = true,
                EnableTypes = opts.Contains("types") || opts.Contains("check") ? DialectTypes.Enable : DialectTypes.Disable,
                EnableFStrings = opts.Contains("fstrings"),
                EnableFutures = opts.Contains("futures"),
                EnableRecursion = opts.Contains("recursion"),
                EnableKeywordOnlyArguments = true,
                EnablePositionalOnlyArguments = true,
            },
            // Output is capped so a print loop cannot fill the page's memory.
            Print = line => { if (output.Length < 200_000) output.Append(line).Append('\n'); },
            MaxSteps = 20_000_000,
            Timeout = TimeSpan.FromSeconds(10),
            MaxAllocatedBytes = 256L * 1024 * 1024,
            StringElements = opts.Contains("utf16") ? StringElements.Utf16 : StringElements.Utf8,
            // The browser has no threads, so tasks run inline (this is also the automatic choice there).
            FutureLimits = new FutureLimits { RunInline = true },
        };
        string? error = null;
        var clock = Stopwatch.StartNew();
        try
        {
            if (opts.Contains("check"))
            {
                var result = it.Typecheck("playground.star", source);
                foreach (var e in result.Errors) output.Append("type error: ").Append(e.Message).Append('\n');
                if (result.Errors.Count > 0) error = $"{result.Errors.Count} type error(s); the program was not run";
            }
            if (error is null) it.Execute("playground.star", source);
        }
        catch (StarlarkSyntaxException e)
        {
            error = e.Message;
        }
        catch (ExecutionLimitException e)
        {
            error = $"limit reached ({e.Limit}): {e.Message}";
        }
        catch (StarlarkException e)
        {
            error = e.Message;
        }
        catch (Exception e)
        {
            error = $"internal error: {e.GetType().Name}: {e.Message}";
        }
        long steps = it.LastRunStats?.Steps ?? 0;
        return "{\"output\":" + Json(output.ToString()) + ",\"error\":" + (error is null ? "null" : Json(error)) +
               ",\"ms\":" + clock.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture) + ",\"steps\":" + steps + "}";
    }

    /// <summary>The library's version, for the page footer.</summary>
    [JSExport]
    public static string Version() => typeof(Interpreter).Assembly.GetName().Version?.ToString() ?? "?";

    // ---------------------------------------------------------------- the BASIC machine

    private static Basic.Machine? _basic;

    /// <summary>Starts (or restarts) the BASIC machine and returns its banner.</summary>
    [JSExport]
    public static string BasicReset()
    {
        _basic = new Basic.Machine();
        return _basic.TakeOutput();
    }

    /// <summary>Types one line into the BASIC machine. Returns JSON <c>{"output": text, "status": "done"|"waiting"|"budget"}</c>.</summary>
    [JSExport]
    public static string BasicType(string line) => Step(() => (_basic ??= new Basic.Machine()).Type(line));

    /// <summary>Keeps a running BASIC program going for a little longer. The page calls this from a timer while the status is "budget".</summary>
    [JSExport]
    public static string BasicContinue() => _basic is null ? "{\"output\":\"\",\"status\":\"done\"}" : Step(() => _basic.Continue());

    /// <summary>Stops a running BASIC program, as the BREAK key does.</summary>
    [JSExport]
    public static void BasicBreak() => _basic?.Break();

    /// <summary>Loads a bundled BASIC program by name into a fresh machine and returns its listing.</summary>
    [JSExport]
    public static string BasicLoad(string name)
    {
        _basic = new Basic.Machine();
        _basic.TakeOutput();
        var text = Basic.Scripts.ReadProgram(name) ?? "";
        _basic.Load(text);
        return text;
    }

    /// <summary>The names of the bundled BASIC programs, one per line.</summary>
    [JSExport]
    public static string BasicPrograms() => string.Join("\n", Basic.Scripts.ProgramNames());

    /// <summary>
    /// Runs <paramref name="start"/>, then more slices until about 40 ms have passed, so the page stays responsive; a long
    /// program comes back with status "budget" and the page calls <see cref="BasicContinue"/>. A fault in the interpreter
    /// script itself is reported and the machine is put back at READY.
    /// </summary>
    private static string Step(Func<string> start)
    {
        string status;
        try
        {
            var clock = Stopwatch.StartNew();
            status = start();
            while (status == "budget" && clock.ElapsedMilliseconds < 40) status = _basic!.Continue();
        }
        catch (Exception e)
        {
            _basic!.Recover();
            return "{\"output\":" + Json("?INTERNAL ERROR: " + e.Message + "\n" + _basic.TakeOutput()) + ",\"status\":\"done\"}";
        }
        return "{\"output\":" + Json(_basic!.TakeOutput()) + ",\"status\":\"" + status + "\"}";
    }

    /// <summary>
    /// <paramref name="s"/> as a JSON string literal. Written out by hand because the trimmed WebAssembly build has
    /// reflection-based serialization switched off, and a string is all that ever needs encoding here.
    /// </summary>
    private static string Json(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
