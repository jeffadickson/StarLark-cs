using System.Text;
using System.Text.RegularExpressions;
using Starlark.Syntax;

namespace Starlark.Syntax.Tests;

/// <summary>
/// The nesting limit adopted from Starlark-Go (QA-ROB-001): deeply nested source text is a syntax error containing
/// "excessive nesting", never a stack overflow, which would end the process. Every construct that nests is tried just
/// under, at and just over <see cref="SyntaxLimits.MaxNesting"/>, and far over it. Each parse runs on a thread with an
/// ordinary 1 MB stack, the size a host's thread usually has, so a parse that needs more stack than that fails the test
/// rather than passing only because the test runner's thread happened to be bigger. A Debug build of the library is
/// unoptimized and its frames are about twice as large, so there the thread gets 4 MB (run the tests in Release to check
/// the 1 MB claim).
/// </summary>
public class NestingLimitTests
{
    private const int Limit = SyntaxLimits.MaxNesting;

    /// <summary>The stack an ordinary host thread has: 1 MB, or 4 MB for an unoptimized (Debug) build of the library.</summary>
    public static int OrdinaryStack { get; } =
        typeof(AstModule).Assembly.GetCustomAttributes(typeof(System.Diagnostics.DebuggableAttribute), false)
            .Cast<System.Diagnostics.DebuggableAttribute>().Any(d => d.IsJITOptimizerDisabled) ? 4 << 20 : 1 << 20;

    /// <summary>Runs <paramref name="body"/> on a new thread with <paramref name="stack"/> bytes of stack; returns what it threw, or null.</summary>
    public static Exception? OnThread(Action body, int stack)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            // Nothing may escape a thread: an unhandled exception there ends the test host.
            try { body(); }
            catch (Exception e) { failure = e; }
        }, stack);
        thread.Start();
        thread.Join();
        return failure;
    }

    /// <summary>Source that nests one construct <c>n</c> levels deep, by name (<c>1 + (</c> is two levels: an operand and a parenthesis).</summary>
    public static string Nested(string construct, int n) => construct switch
    {
        "parens" => "x = " + new string('(', n) + "1" + new string(')', n) + "\n",
        "lists" => "x = " + new string('[', n) + new string(']', n) + "\n",
        "dicts" => "x = " + Repeat("{1: ", n) + "1" + new string('}', n) + "\n",
        "tuples" => "x = " + Repeat("(1, ", n) + "1" + new string(')', n) + "\n",
        "minus" => "x = " + new string('-', n) + "1\n",
        "plus" => "x = " + new string('+', n) + "1\n",
        "tilde" => "x = " + new string('~', n) + "1\n",
        "not" => "x = " + Repeat("not ", n) + "True\n",
        "lambda" => "x = " + Repeat("lambda: ", n) + "1\n",
        "ternary" => "x = " + Repeat("0 if False else ", n) + "1\n",
        "right-operand" => "x = " + Repeat("1 + (", n / 2) + "1" + new string(')', n / 2) + "\n",
        "comparison" => "x = " + Repeat("1 < (", n / 2) + "1" + new string(')', n / 2) + "\n",
        "calls" => "x = " + Repeat("f(", n) + "1" + new string(')', n) + "\n",
        "suffixes" => "x = a" + Repeat(".b", n) + "\n",
        "indexes" => "x = a" + Repeat("[0]", n) + "\n",
        "comprehension" => "x = " + Repeat("[y for y in ", n) + "z" + new string(']', n) + "\n",
        "fstring" => "x = " + Repeat("f'{", n) + "1" + Repeat("}'", n) + "\n",
        "if" => NestedBlocks("if True:", n),
        "for" => NestedBlocks("for i in x:", n),
        "def" => NestedBlocks("def f():", n),
        "elif" => "if a:\n  pass\n" + Repeat("elif a:\n  pass\n", n),
        _ => throw new ArgumentException(construct),
    };

    public static readonly TheoryData<string> Constructs = new()
    {
        "parens", "lists", "dicts", "tuples", "minus", "plus", "tilde", "not", "lambda", "ternary", "right-operand", "comparison", "calls",
        "suffixes", "indexes", "comprehension", "fstring", "if", "for", "def", "elif",
    };

    private static string Repeat(string s, int n) => new StringBuilder(s.Length * n).Insert(0, s, n).ToString();

    /// <summary>
    /// Blocks nested <paramref name="n"/> deep, one more space of indentation each. Past 2000 levels the indentation stops
    /// growing (n levels at one more space each would be n²/2 characters: 1.25 GB for 50,000); the parser has given up at
    /// level 1001 long before, so the text is still nested past the limit where it matters.
    /// </summary>
    private static string NestedBlocks(string header, int n)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++) sb.Append(' ', Math.Min(i, 2000)).Append(header).Append('\n');
        return sb.Append(' ', Math.Min(n, 2001)).Append("pass\n").ToString();
    }

    /// <summary>Parses <paramref name="source"/> on an <see cref="OrdinaryStack"/>; returns null on success or the exception.</summary>
    private static Exception? ParseOnOrdinaryStack(string source) =>
        OnThread(() => AstModule.Parse("t.star", source, TestSupport.All), OrdinaryStack);

    /// <summary>998 levels (999 for a construct whose innermost body adds a level) parse, on an ordinary stack.</summary>
    [Theory]
    [MemberData(nameof(Constructs))]
    public void JustUnderTheLimitParses(string construct) =>
        Assert.Null(ParseOnOrdinaryStack(Nested(construct, Limit - 2)));

    [Theory]
    [MemberData(nameof(Constructs))]
    public void JustOverTheLimitIsExcessiveNesting(string construct)
    {
        var e = ParseOnOrdinaryStack(Nested(construct, Limit + 2));
        var syntax = Assert.IsType<StarlarkSyntaxException>(e);
        Assert.Contains("excessive nesting", syntax.RawMessage);
        _ = syntax.Message;   // rendering the snippet must work too
    }

    [Theory]
    [MemberData(nameof(Constructs))]
    public void FarOverTheLimitIsExcessiveNesting(string construct)
    {
        var e = ParseOnOrdinaryStack(Nested(construct, 50_000));
        Assert.Contains("excessive nesting", Assert.IsType<StarlarkSyntaxException>(e).RawMessage);
    }

    /// <summary>Exactly at the limit: 1000 parentheses parse, 1001 do not (the lexer and the parser agree on the count).</summary>
    [Fact]
    public void TheLimitIsExact()
    {
        Assert.Null(ParseOnOrdinaryStack(Nested("parens", Limit)));
        Assert.Null(ParseOnOrdinaryStack(Nested("minus", Limit)));
        Assert.Contains("excessive nesting", Assert.IsType<StarlarkSyntaxException>(ParseOnOrdinaryStack(Nested("parens", Limit + 1))).RawMessage);
        Assert.Contains("excessive nesting", Assert.IsType<StarlarkSyntaxException>(ParseOnOrdinaryStack(Nested("minus", Limit + 1))).RawMessage);
    }

    /// <summary>The inputs of Go's own TestDepthLimit (ten million deep), each rejected with "excessive nesting".</summary>
    [Theory]
    [InlineData("(", "")]
    [InlineData("not ", "0")]
    [InlineData("-", "0")]
    [InlineData("0 if a else ", "0")]
    [InlineData("lambda: ", "0")]
    public void GoDepthLimitInputs(string unit, string tail)
    {
        var e = ParseOnOrdinaryStack(Repeat(unit, 10_000_000) + tail);
        Assert.Contains("excessive nesting", Assert.IsType<StarlarkSyntaxException>(e).RawMessage);
    }

    /// <summary>
    /// The case reported as a crash: 200,000 nested <c>if</c> blocks. Nested 20,000 deep they overflowed the stack. (At
    /// 200,000 the reported <c>ArgumentOutOfRangeException</c> came from building the text, about 2*10^10 characters of
    /// indentation, which no string can hold, not from the interpreter.) Now the parse stops at level 1001.
    /// </summary>
    [Fact]
    public void TwoHundredThousandNestedIfsAreACleanError()
    {
        var e = ParseOnOrdinaryStack(NestedBlocks("if True:", 200_000));
        Assert.Contains("excessive nesting", Assert.IsType<StarlarkSyntaxException>(e).RawMessage);
        Assert.Contains("excessive nesting", Assert.IsType<StarlarkSyntaxException>(ParseOnOrdinaryStack(NestedBlocks("if True:", 20_000))).RawMessage);
    }

    /// <summary>The deepest allowed block nesting, as real indentation (one space more each line), parses.</summary>
    [Fact]
    public void ThousandNestedBlocksWithRealIndentationParse() =>
        Assert.Null(ParseOnOrdinaryStack(NestedBlocks("if True:", Limit - 1)));

    /// <summary>A long chain of one operator is not nesting: it is parsed by a loop, as in Go, at any length.</summary>
    [Theory]
    [InlineData(" + 1")]
    [InlineData(" * 1")]
    [InlineData(" or x")]
    [InlineData(" and x")]
    [InlineData(" | x")]
    [InlineData(" - 1 + 1")]
    public void ALongOperatorChainIsNotNesting(string unit)
    {
        Assert.Null(ParseOnOrdinaryStack("x = 1" + Repeat(unit, 100_000) + "\n"));
    }

    /// <summary>Printing a parsed tree (Stmt.ToString) of the deepest allowed nesting, and of a long chain, works.</summary>
    [Fact]
    public void DeepTreesPrint()
    {
        string printed = "";
        Assert.Null(OnThread(() =>
        {
            printed = AstModule.Parse("t.star", Nested("lists", Limit - 2), TestSupport.All).Statement.ToString()
                + AstModule.Parse("t.star", "x = 1" + Repeat(" + 1", 100_000) + "\n", TestSupport.All).Statement.ToString();
        }, OrdinaryStack));
        Assert.Contains(new string('[', Limit - 2), printed);
        Assert.Contains(new string('(', 100_000) + "1 + 1)", printed);
    }

    /// <summary>The lexer counts open brackets as Go's scanner does, with the same "excessive nesting" message.</summary>
    [Fact]
    public void TheLexerCountsBracketsToo()
    {
        var lexemes = Lexer.LexAll(new CodeMap("t.star", new string('(', Limit + 1)), stopAtError: true);
        Assert.Contains("excessive nesting", lexemes[^1].Error!.RawMessage);
        Assert.DoesNotContain(Lexer.LexAll(new CodeMap("t.star", new string('(', Limit)), stopAtError: true), l => l.IsError);
    }

    /// <summary>The limit is Go's, and the error text contains Go's phrase.</summary>
    [Fact]
    public void TheLimitIsGos() => Assert.Equal(1000, SyntaxLimits.MaxNesting);

    /// <summary>
    /// The backstop: on a thread with very little stack the parser runs out before reaching the limit, and reports that as
    /// excessive nesting too (it checks the remaining stack on every level) instead of overflowing.
    /// </summary>
    [Theory]
    [InlineData("lists")]
    [InlineData("calls")]
    [InlineData("if")]
    [InlineData("lambda")]
    public void ATinyStackEndsInTheSameError(string construct)
    {
        var failure = OnThread(() => AstModule.Parse("t.star", Nested(construct, Limit - 2), TestSupport.All), 192 * 1024);
        Assert.Contains("excessive nesting", Assert.IsType<StarlarkSyntaxException>(failure).RawMessage);
    }

    // ---------------------------------------------------------------- the call graph of the parser

    /// <summary>
    /// The equivalent of Go's TestParserCallGraphCycles: every cycle in the parser's call graph must pass through a method
    /// that counts a level of nesting (calls <c>Enter()</c>), or a deeply nested input could recurse without bound. The graph
    /// is read from the source of Parser.cs: one node per method, an edge for every call to another method of the class.
    /// A method that calls <c>Enter()</c> breaks the cycles through it. The test fails naming any cycle that does not.
    /// </summary>
    [Fact]
    public void EveryCycleInTheParserCallGraphCallsEnter()
    {
        var methods = ParserMethods();
        Assert.True(methods.Count > 40, $"found only {methods.Count} methods: the source scan is broken");
        var breaking = methods.Where(m => Regex.IsMatch(m.Value, @"\bEnter\(\)")).Select(m => m.Key).ToHashSet();
        Assert.Contains("ParseSuite", breaking);
        Assert.Contains("ParseNestedAtom", breaking);

        var edges = methods.ToDictionary(
            m => m.Key,
            m => methods.Keys.Where(callee => callee != "Enter" && Regex.IsMatch(m.Value, @"(?<![\w.])" + Regex.Escape(callee) + @"\(")).ToList());

        var cycles = new List<string>();
        var onPath = new List<string>();
        var done = new HashSet<string>();
        void Visit(string node)
        {
            int at = onPath.IndexOf(node);
            if (at >= 0)
            {
                cycles.Add(string.Join(" -> ", onPath.Skip(at).Append(node)));
                return;
            }
            if (breaking.Contains(node) || !done.Add(node)) return;
            onPath.Add(node);
            foreach (var callee in edges[node]) Visit(callee);
            onPath.RemoveAt(onPath.Count - 1);
        }
        foreach (var m in methods.Keys)
        {
            done.Clear();
            Visit(m);
        }
        Assert.True(cycles.Count == 0, "cycles without a nesting check:\n" + string.Join("\n", cycles.Distinct()));
    }

    /// <summary>
    /// The methods of src/Starlark.Syntax/Parser.cs (copied next to the tests by the project file), by name with overloads
    /// merged, with their bodies.
    /// </summary>
    private static Dictionary<string, string> ParserMethods()
    {
        string source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Source", "Parser.cs.txt"));
        // A method starts at a line indented four spaces that declares it; its body runs to the next such line.
        var header = new Regex(@"^    (?:(?:private|public|internal|static|readonly|override)\s+)+[\w<>\[\]?,() ]+?\s(\w+)\(", RegexOptions.Multiline);
        var matches = header.Matches(source);
        var methods = new Dictionary<string, string>();
        for (int i = 0; i < matches.Count; i++)
        {
            int start = matches[i].Index;
            int end = i + 1 < matches.Count ? matches[i + 1].Index : source.Length;
            string name = matches[i].Groups[1].Value;
            string body = source[(matches[i].Index + matches[i].Length)..end];
            body = Regex.Replace(body, @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);   // calls in comments do not count
            body = Regex.Replace(body, @"^\s*\[[^\n]*\]\s*$", "", RegexOptions.Multiline);   // nor attributes of the next method
            methods[name] = methods.TryGetValue(name, out var earlier) ? earlier + body : body;
        }
        return methods;
    }

    /// <summary>The scan finds the recursion it must: a method that calls itself with no check is reported as a cycle.</summary>
    [Fact]
    public void TheCallGraphScanFindsKnownEdges()
    {
        var methods = ParserMethods();
        Assert.Contains("ParseTest", methods.Keys);
        Assert.Contains("ParseUnaryOperator", methods.Keys);
        Assert.Matches(@"(?<![\w.])ParseUnary\(", methods["ParseUnaryOperator"]);   // -x recurses on the operand
        Assert.Matches(@"(?<![\w.])ParseExpr\(", methods["ParseTest"]);
        Assert.Matches(@"\bEnter\(\)", methods["ParseUnaryOperator"]);
    }
}
