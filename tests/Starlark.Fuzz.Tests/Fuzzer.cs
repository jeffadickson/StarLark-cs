using System.Text;
using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Fuzz.Tests;

/// <summary>
/// Input generators for fuzzing. Everything is driven by a seeded <see cref="Random"/>, so a failing input
/// can be reproduced from its seed alone.
/// </summary>
public sealed class Fuzzer(int seed)
{
    public Random Rng { get; } = new(seed);

    // ---------------------------------------------------------------- vocabulary

    private static readonly string[] Keywords =
    [
        "and", "break", "continue", "def", "elif", "else", "for", "if", "in", "lambda", "load", "not", "or",
        "pass", "return", "None", "True", "False", "print", "len", "range", "str", "int", "list", "dict",
        "as", "import", "class", "while", "f", "r", "b", "rb", "fr",
    ];

    private static readonly string[] Symbols =
    [
        ",", ";", ":", "+=", "-=", "*=", "/=", "//=", "%=", "==", "!=", "<=", ">=", "**", "->", "=", "<", ">", "-", "+",
        "*", "%", "/", "//", ".", "&", "|", "^", "<<", ">>", "~", "&=", "|=", "^=", "<<=", ">>=", "...", "[", "]", "{",
        "}", "(", ")", "!",
    ];

    private static readonly string[] Atoms =
    [
        "0", "1", "42", "0x1F", "0b101", "0o17", "1.5", ".5", "1e10", "1e400", "123456789012345678901234567890",
        "\"s\"", "'t'", "\"\"\"tri\nple\"\"\"", "r'raw\\n'", "b'by'", "f\"{x}\"", "\"\\x41\\u00e9\\n\"", "x", "y", "_z",
        "\"\\", "'", "\"\"\"", "\\", "\t", "\r\n", "#c", "é", "\U0001F600", "\0", "$", "@", "`", "?",
    ];

    private string Pick(string[] xs) => xs[Rng.Next(xs.Length)];

    // ---------------------------------------------------------------- strategy 1: raw junk

    /// <summary>Arbitrary UTF-16 including lone surrogates and control characters.</summary>
    public string RandomChars(int maxLen)
    {
        int n = Rng.Next(maxLen + 1);
        var sb = new StringBuilder(n);
        for (int i = 0; i < n; i++)
        {
            sb.Append(Rng.Next(10) switch
            {
                < 6 => (char)Rng.Next(32, 127),
                6 => (char)Rng.Next(0, 32),
                7 => (char)Rng.Next(0x80, 0x800),
                8 => (char)Rng.Next(0xD800, 0xE000),   // surrogates, often unpaired
                _ => (char)Rng.Next(0x800, 0xFFFF),
            });
        }
        return sb.ToString();
    }

    /// <summary>A soup of real tokens with random spacing and newlines: mostly lexes, rarely parses.</summary>
    public string TokenSoup(int maxTokens)
    {
        int n = Rng.Next(maxTokens + 1);
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            sb.Append(Rng.Next(3) switch
            {
                0 => Pick(Keywords),
                1 => Pick(Symbols),
                _ => Pick(Atoms),
            });
            sb.Append(Rng.Next(8) switch
            {
                0 => "\n",
                1 => "\n    ",
                2 => "\n  ",
                3 => "",
                _ => " ",
            });
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- strategy 2: mutate real programs

    public string Mutate(string source)
    {
        var sb = new StringBuilder(source);
        int edits = 1 + Rng.Next(4);
        for (int e = 0; e < edits && sb.Length > 0; e++)
        {
            int at = Rng.Next(sb.Length);
            switch (Rng.Next(7))
            {
                case 0: sb.Remove(at, Math.Min(sb.Length - at, 1 + Rng.Next(8))); break;
                case 1: sb.Insert(at, Pick(Symbols)); break;
                case 2: sb.Insert(at, Pick(Keywords) + " "); break;
                case 3: sb.Insert(at, Pick(Atoms)); break;
                case 4: sb[at] = (char)Rng.Next(32, 127); break;
                case 5:
                {
                    // Duplicate a chunk elsewhere.
                    int len = Math.Min(sb.Length - at, 1 + Rng.Next(40));
                    sb.Insert(Rng.Next(sb.Length), sb.ToString(at, len));
                    break;
                }
                default:
                    // Truncate: exercises every "unexpected end of file" path.
                    sb.Length = at;
                    break;
            }
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- strategy 3: valid programs

    private int _tmp;

    /// <summary>A syntactically valid program with a bounded depth. May still fail at run time, and should.</summary>
    public string Program()
    {
        _tmp = 0;
        var sb = new StringBuilder();
        // Define every variable the generator may read, so programs get past static name resolution and run.
        sb.Append("v0 = 1\nv1 = \"s\"\nv2 = [1, 2, 3]\nv3 = {\"a\": 1}\nv4 = (1, 2)\n");
        int n = 1 + Rng.Next(6);
        for (int i = 0; i < n; i++) Stmt(sb, 0, inDef: false, inLoop: false);
        return sb.ToString();
    }

    private static void Indent(StringBuilder sb, int d) => sb.Append(' ', d * 2);

    private string Name() => "v" + Rng.Next(5);

    private void Stmt(StringBuilder sb, int depth, bool inDef, bool inLoop)
    {
        Indent(sb, depth);
        int k = Rng.Next(depth >= 3 ? 5 : 11);
        switch (k)
        {
            case 0: sb.Append(Name()).Append(" = ").Append(Expr(3)).Append('\n'); break;
            case 1: sb.Append("print(").Append(Expr(3)).Append(")\n"); break;
            case 2:
                // `x += y` on a global is a static error, so use it only inside functions (where it makes x local).
                sb.Append(Name()).Append(inDef ? " += " : " = ").Append(Expr(2)).Append('\n');
                break;
            case 3: sb.Append(Expr(3)).Append('\n'); break;
            case 4: sb.Append(inDef ? "return " + Expr(2) : "pass").Append('\n'); break;
            case 5:
                sb.Append("if ").Append(Expr(2)).Append(":\n");
                Block(sb, depth + 1, inDef, inLoop);
                if (Rng.Next(2) == 0)
                {
                    Indent(sb, depth);
                    sb.Append("else:\n");
                    Block(sb, depth + 1, inDef, inLoop);
                }
                break;
            case 6:
                sb.Append("for ").Append(Name()).Append(" in ").Append(Expr(2)).Append(":\n");
                Block(sb, depth + 1, inDef, inLoop: true);
                break;
            case 7:
                sb.Append("def f").Append(_tmp++).Append('(').Append(Rng.Next(2) == 0 ? "a, b=1" : "*args, **kw").Append("):\n");
                Block(sb, depth + 1, inDef: true, inLoop: false);
                break;
            case 8: sb.Append(inLoop ? "break" : "pass").Append('\n'); break;
            case 9: sb.Append(Name()).Append('[').Append(Expr(1)).Append("] = ").Append(Expr(2)).Append('\n'); break;
            default: sb.Append(Name()).Append(", ").Append(Name()).Append(" = ").Append(Expr(2)).Append('\n'); break;
        }
    }

    private void Block(StringBuilder sb, int depth, bool inDef, bool inLoop)
    {
        int n = 1 + Rng.Next(3);
        for (int i = 0; i < n; i++) Stmt(sb, depth, inDef, inLoop);
    }

    private static readonly string[] BinOps =
        ["+", "-", "*", "/", "//", "%", "==", "!=", "<", ">", "<=", ">=", "in", "not in", "and", "or", "&", "|", "^", "<<", ">>"];

    private static readonly string[] Funcs =
        ["len", "str", "repr", "int", "float", "bool", "list", "dict", "tuple", "set", "sorted", "reversed", "range",
         "enumerate", "zip", "min", "max", "abs", "any", "all", "type", "hash", "bytes", "dir"];

    private static readonly string[] Methods =
        ["append", "extend", "pop", "keys", "values", "items", "get", "split", "join", "format", "upper", "strip",
         "find", "replace", "startswith", "count", "update", "setdefault", "remove", "insert", "index"];

    public string Expr(int depth)
    {
        if (depth <= 0) return Leaf();
        switch (Rng.Next(14))
        {
            case 0: return Leaf();
            case 1: return "(" + Expr(depth - 1) + " " + BinOps[Rng.Next(BinOps.Length)] + " " + Expr(depth - 1) + ")";
            case 2: return "-" + Expr(depth - 1);
            case 3: return "(not " + Expr(depth - 1) + ")";
            case 4: return "[" + string.Join(", ", Enumerable.Range(0, Rng.Next(4)).Select(_ => Expr(depth - 1))) + "]";
            case 5: return "(" + Expr(depth - 1) + ", " + Expr(depth - 1) + ")";
            case 6: return "{" + Expr(depth - 1) + ": " + Expr(depth - 1) + "}";
            case 7: return Funcs[Rng.Next(Funcs.Length)] + "(" + Expr(depth - 1) + (Rng.Next(3) == 0 ? ", " + Expr(depth - 1) : "") + ")";
            case 8: return "(" + Expr(depth - 1) + ")" + "." + Methods[Rng.Next(Methods.Length)] + "(" + Expr(depth - 1) + ")";
            case 9: return "(" + Expr(depth - 1) + ")" + "[" + Expr(depth - 1) + "]";
            case 10: return "(" + Expr(depth - 1) + ")" + "[" + OptExpr(depth - 1) + ":" + OptExpr(depth - 1) + (Rng.Next(2) == 0 ? ":" + OptExpr(depth - 1) : "") + "]";
            case 11: return "[" + Expr(depth - 1) + " for " + Name() + " in " + Expr(depth - 1) + (Rng.Next(2) == 0 ? " if " + Expr(depth - 1) : "") + "]";
            case 12: return "(" + Expr(depth - 1) + " if " + Expr(depth - 1) + " else " + Expr(depth - 1) + ")";
            default: return "(lambda x: " + Expr(depth - 1) + ")(" + Expr(depth - 1) + ")";
        }
    }

    private string OptExpr(int depth) => Rng.Next(3) == 0 ? "" : Expr(depth);

    private string Leaf() => Rng.Next(9) switch
    {
        0 => Rng.Next(-5, 100).ToString(),
        1 => "\"" + new string(Enumerable.Range(0, Rng.Next(6)).Select(_ => (char)Rng.Next('a', 'g')).ToArray()) + "\"",
        2 => "None",
        3 => Rng.Next(2) == 0 ? "True" : "False",
        4 => (Rng.NextDouble() * 100).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        5 => Name(),
        6 => "\"é世😀\"",
        7 => "[1, 2, 3]",
        _ => "0",
    };
}

/// <summary>Checks the invariants every input must satisfy, however malformed.</summary>
public static class Oracle
{
    /// <summary>Time allowed for a single input before it is reported as a hang.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    public static Dialect Dialect { get; } = Dialect.AllOptionsInternal;

    /// <summary>
    /// Lex and parse <paramref name="src"/>. Only <see cref="StarlarkSyntaxException"/> may escape the library;
    /// anything else is a bug. Returns the AST if it parsed.
    /// </summary>
    public static AstModule? Parse(string src)
    {
        try
        {
            return AstModule.Parse("fuzz.star", src, Dialect);
        }
        catch (StarlarkSyntaxException e)
        {
            // Rendering the diagnostic must never throw either (it indexes into the source by span).
            _ = e.Message;
            return null;
        }
    }

    /// <summary>Lex only: exercises the tokenizer on input the parser would reject early.</summary>
    public static void Lex(string src)
    {
        var lexemes = Lexer.LexAll(new CodeMap("fuzz.star", src));
        int pos = 0;
        foreach (var l in lexemes)
        {
            if (l.IsError)
            {
                _ = l.Error!.Message;
                continue;
            }
            if (l.Start < pos || l.End < l.Start || l.End > src.Length)
            {
                throw new InvalidOperationException($"bad span {l.Start}..{l.End} after {pos} for {l.Token}");
            }
            pos = l.End;
        }
    }

    /// <summary>A parsed program prints to text that parses again to the same text (a fixed point).</summary>
    public static void RoundTrip(AstModule ast)
    {
        string printed = ast.Statement.ToString();
        var again = Parse(printed);
        if (again is null)
        {
            throw new InvalidOperationException($"printed program no longer parses:\n{printed}");
        }
        string twice = again.Statement.ToString();
        if (printed != twice)
        {
            throw new InvalidOperationException($"print is not a fixed point:\n--- first ---\n{printed}\n--- second ---\n{twice}");
        }
    }

    /// <summary>
    /// Run a program. Only <see cref="StarlarkException"/> / <see cref="StarlarkSyntaxException"/> may escape;
    /// any other exception (null reference, bad cast, out of range, ...) is a bug in the interpreter.
    /// </summary>
    public static void Run(string src, StringElements elements = StringElements.Utf16)
    {
        var it = new Interpreter
        {
            Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableFStrings = true, EnableKeywordOnlyArguments = true },
            AllowGlobalReassign = true,
            Print = _ => { },
            StringElements = elements,
        };
        try
        {
            it.Execute("fuzz.star", src);
        }
        catch (StarlarkException e)
        {
            _ = e.Message;
        }
        catch (StarlarkSyntaxException e)
        {
            _ = e.Message;
        }
    }
}
