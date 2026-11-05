using Basic;

namespace Starlark.Apps.Tests;

/// <summary>
/// The expression compiler and evaluator of the Level II BASIC interpreter (<c>apps/Basic/scripts/expr.star</c>), tested one feature
/// at a time: every operator, every built-in function, the type rules, the error codes, arrays and DEF FN, and an exhaustive pass
/// over short strings that must never crash the machine.
/// </summary>
public class BasicExprTests
{
    private static Machine Fresh()
    {
        var m = new Machine();
        m.TakeOutput();
        return m;
    }

    /// <summary>What a direct-mode <c>PRINT expression</c> shows (without the echo, prompt or READY lines), after running <paramref name="setup"/> lines.</summary>
    private static string Print(string expression, params string[] setup)
    {
        var m = Fresh();
        foreach (var s in setup) m.Enter(s);
        m.TakeOutput();
        m.Enter("PRINT " + expression);
        return m.TakeOutput().Replace("\r", "").Split('\n')[1];
    }

    // ---------------------------------------------------------------- operators

    [Theory]
    [InlineData("7+8", " 15 ")]
    [InlineData("7-8", "-1 ")]
    [InlineData("7*8", " 56 ")]
    [InlineData("7/8", " .875 ")]
    [InlineData("2^10", " 1024 ")]
    [InlineData("2^-1", " .5 ")]
    [InlineData("--5", " 5 ")]
    [InlineData("- -5", " 5 ")]
    [InlineData("+5", " 5 ")]
    [InlineData("2*-3", "-6 ")]
    [InlineData("2+3*4-5", " 9 ")]
    [InlineData("(((1)))", " 1 ")]
    [InlineData("(1+2)*(3+4)", " 21 ")]
    [InlineData("8/2/2", " 2 ")]
    [InlineData("2^2*3", " 12 ")]
    public void ArithmeticOperators(string expression, string want) => Assert.Equal(want, Print(expression));

    [Fact]
    public void ModAndBackslashAreNotLevelIIOperators()
    {
        // MOD is an ordinary variable (value 0), and PRINT accepts items with no separator, so this prints three items.
        Assert.Equal(" 7  0  3 ", Print("7 MOD 3"));
        Assert.StartsWith("SN ERROR", Error("PRINT 7\\2"));
        // Two numbers side by side are two PRINT items.
        Assert.Equal(" 1  2 ", Print("1 2"));
    }

    [Theory]
    [InlineData("1=1", "-1 ")]
    [InlineData("1=2", " 0 ")]
    [InlineData("1<>2", "-1 ")]
    [InlineData("1<2", "-1 ")]
    [InlineData("2<1", " 0 ")]
    [InlineData("2>1", "-1 ")]
    [InlineData("2<=2", "-1 ")]
    [InlineData("3<=2", " 0 ")]
    [InlineData("2>=2", "-1 ")]
    [InlineData("1>=2", " 0 ")]
    [InlineData("1.5=1.5", "-1 ")]
    [InlineData("\"A\"=\"A\"", "-1 ")]
    [InlineData("\"A\"<>\"B\"", "-1 ")]
    [InlineData("\"A\"<\"B\"", "-1 ")]
    [InlineData("\"AB\">\"A\"", "-1 ")]
    [InlineData("\"\"<\"A\"", "-1 ")]
    [InlineData("\"a\">\"A\"", "-1 ")]
    public void ComparisonsGiveMinusOneOrZero(string expression, string want) => Assert.Equal(want, Print(expression));

    [Theory]
    [InlineData("5 AND 3", " 1 ")]
    [InlineData("5 OR 3", " 7 ")]
    [InlineData("NOT 0", "-1 ")]
    [InlineData("NOT -1", " 0 ")]
    [InlineData("NOT 5", "-6 ")]
    [InlineData("-1 AND 255", " 255 ")]
    [InlineData("1=1 AND 2=2", "-1 ")]
    [InlineData("1=1 AND 2=3", " 0 ")]
    [InlineData("1=2 OR 2=2", "-1 ")]
    [InlineData("1 OR 2 AND 4", " 1 ")]
    [InlineData("NOT 1=2", "-1 ")]
    public void LogicalOperatorsWorkOnSixteenBitIntegers(string expression, string want) => Assert.Equal(want, Print(expression));

    [Theory]
    [InlineData("\"AB\"+\"CD\"", "ABCD")]
    [InlineData("\"\"+\"X\"", "X")]
    [InlineData("\"A\"+\"B\"+\"C\"", "ABC")]
    [InlineData("\"A\"+CHR$(66)", "AB")]
    public void StringsConcatenate(string expression, string want) => Assert.Equal(want, Print(expression));

    // ---------------------------------------------------------------- number types and printing

    [Theory]
    [InlineData("1/3", " .333333 ")]
    [InlineData("1/3#", " .3333333333333333 ")]
    [InlineData("1.5#+1.5#", " 3 ")]
    [InlineData("CINT(2.5)", " 2 ")]
    [InlineData("CINT(-2.5)", "-3 ")]
    [InlineData("CINT(2.4)", " 2 ")]
    [InlineData("CSNG(1/3#)", " .333333 ")]
    [InlineData("CDBL(.5)", " .5 ")]
    [InlineData("CDBL(1)/3", " .3333333333333333 ")]
    [InlineData("32767", " 32767 ")]
    [InlineData("-32768", "-32768 ")]
    [InlineData("32767+1", " 32768 ")]
    [InlineData("1E10", " 1E+10 ")]
    [InlineData("1E-10", " 1E-10 ")]
    [InlineData("123456", " 123456 ")]
    [InlineData("9999999", " 1E+07 ")]
    [InlineData("99999999", " 99999999 ")]
    [InlineData("0.1+0.2", " .3 ")]
    [InlineData("&H1F", " 31 ")]
    [InlineData("&O17", " 15 ")]
    [InlineData("&HFFFF", "-1 ")]
    public void NumberTypesAndFormatting(string expression, string want) => Assert.Equal(want, Print(expression));

    [Theory]
    [InlineData("INT(2.9)", " 2 ")]
    [InlineData("INT(-2.1)", "-3 ")]
    [InlineData("INT(5)", " 5 ")]
    [InlineData("FIX(2.9)", " 2 ")]
    [InlineData("FIX(-2.9)", "-2 ")]
    [InlineData("ABS(-5)", " 5 ")]
    [InlineData("ABS(5.5)", " 5.5 ")]
    [InlineData("ABS(0)", " 0 ")]
    [InlineData("SGN(-9)", "-1 ")]
    [InlineData("SGN(0)", " 0 ")]
    [InlineData("SGN(.1)", " 1 ")]
    [InlineData("SQR(16)", " 4 ")]
    [InlineData("SQR(2)", " 1.41421 ")]
    [InlineData("SQR(0)", " 0 ")]
    [InlineData("EXP(0)", " 1 ")]
    [InlineData("EXP(1)", " 2.71828 ")]
    [InlineData("LOG(1)", " 0 ")]
    [InlineData("LOG(10)", " 2.30259 ")]
    [InlineData("SIN(0)", " 0 ")]
    [InlineData("COS(0)", " 1 ")]
    [InlineData("TAN(0)", " 0 ")]
    [InlineData("ATN(0)", " 0 ")]
    [InlineData("TAN(1)", " 1.55741 ")]
    [InlineData("ATN(1)*4", " 3.14159 ")]
    public void NumericFunctions(string expression, string want) => Assert.Equal(want, Print(expression));

    [Fact]
    public void RndGivesValuesBelowOneAndTheIntegerFormGivesOneToN()
    {
        const string program =
            "10 FOR I=1 TO 300:X=RND(0):IF X<0 OR X>=1 THEN B=B+1\n" +
            "20 NEXT\n" +
            "30 FOR I=1 TO 300:X=RND(6):IF X<1 OR X>6 OR X<>INT(X) THEN C=C+1\n" +
            "40 NEXT\n" +
            "50 PRINT B;C\n";
        Assert.Equal(" 0  0 ", Run(program).TrimEnd() + " ");
    }

    // ---------------------------------------------------------------- string functions

    [Theory]
    [InlineData("LEFT$(\"HELLO\",2)", "HE")]
    [InlineData("LEFT$(\"HELLO\",0)", "")]
    [InlineData("LEFT$(\"HELLO\",9)", "HELLO")]
    [InlineData("RIGHT$(\"HELLO\",2)", "LO")]
    [InlineData("RIGHT$(\"HELLO\",9)", "HELLO")]
    [InlineData("MID$(\"HELLO\",2,3)", "ELL")]
    [InlineData("MID$(\"HELLO\",2)", "ELLO")]
    [InlineData("MID$(\"HELLO\",9)", "")]
    [InlineData("MID$(\"HELLO\",1,0)", "")]
    [InlineData("STR$(5)", " 5")]
    [InlineData("STR$(-5)", "-5")]
    [InlineData("STR$(1.5)", " 1.5")]
    [InlineData("CHR$(65)", "A")]
    [InlineData("STRING$(3,\"X\")", "XXX")]
    [InlineData("STRING$(3,65)", "AAA")]
    [InlineData("STRING$(0,\"X\")", "")]
    [InlineData("LEFT$(\"\",1)", "")]
    public void StringFunctionsReturnStrings(string expression, string want) => Assert.Equal(want, Print(expression));

    [Theory]
    [InlineData("LEN(\"HELLO\")", " 5 ")]
    [InlineData("LEN(\"\")", " 0 ")]
    [InlineData("ASC(\"A\")", " 65 ")]
    [InlineData("ASC(\"ABC\")", " 65 ")]
    [InlineData("VAL(\"12\")", " 12 ")]
    [InlineData("VAL(\"1.5\")", " 1.5 ")]
    [InlineData("VAL(\" 7\")", " 7 ")]
    [InlineData("VAL(\"ABC\")", " 0 ")]
    [InlineData("VAL(\"\")", " 0 ")]
    [InlineData("INSTR(\"HELLO\",\"LL\")", " 3 ")]
    [InlineData("INSTR(\"HELLO\",\"Z\")", " 0 ")]
    [InlineData("INSTR(\"HELLO\",\"\")", " 1 ")]
    [InlineData("INSTR(3,\"HELLOHELLO\",\"LL\")", " 3 ")]
    [InlineData("INSTR(4,\"HELLOHELLO\",\"LL\")", " 8 ")]
    public void StringFunctionsReturningNumbers(string expression, string want) => Assert.Equal(want, Print(expression));

    // ---------------------------------------------------------------- variables, arrays and DEF FN

    [Theory]
    [InlineData("A", " 0 ")]
    [InlineData("A$", "")]
    [InlineData("A%", " 0 ")]
    [InlineData("A#", " 0 ")]
    [InlineData("A(3)", " 0 ")]
    [InlineData("A$(3)", "")]
    public void UnsetVariablesAreZeroOrEmpty(string expression, string want) => Assert.Equal(want, Print(expression));

    [Fact]
    public void VariableNamesUseTwoSignificantCharacters()
    {
        Assert.Equal(" 5 ", Print("ALPHA", "ALPHABET=5"));
        // Only the first two characters count, so AB1 and AB2 are the same variable, AB.
        Assert.Equal(" 4 ", Print("AB1+AB2", "AB1=1", "AB2=2"));
    }

    [Fact]
    public void TypeSuffixesAreSeparateVariables() =>
        Assert.Equal(" 1  2 ", Print("A+0;A%+1", "A=1", "A%=1").Replace("  ", "  "));

    [Fact]
    public void ArraysDefaultToElevenElementsAndCanBeDimensioned()
    {
        Assert.Equal(" 7 ", Print("A(10)", "A(10)=7"));
        Assert.Equal(" 9 ", Print("B(100)", "DIM B(100)", "B(100)=9"));
        Assert.Equal(" 4 ", Print("C(2,3)", "DIM C(5,5)", "C(2,3)=4"));
        Assert.Equal(" 8 ", Print("D(1,2,3)", "D(1,2,3)=8"));
    }

    [Fact]
    public void ArrayElementsAreIndependentAndCanBeUsedAsSubscripts() =>
        Assert.Equal(" 30 ", Print("A(A(1))", "A(1)=2", "A(2)=30"));

    /// <summary>Loads and runs a program; returns its printed lines (after the echoed RUN, before READY) joined with "|".</summary>
    private static string Run(string program)
    {
        var m = Fresh();
        m.Load(program);
        m.Enter("RUN");
        var lines = m.TakeOutput().Replace("\r", "").Split('\n');
        return string.Join("|", lines.Skip(1).TakeWhile(l => l != "READY"));
    }

    [Fact]
    public void DefFnTakesArgumentsAndSeesVariables()
    {
        Assert.Equal(" 25 ", Run("10 DEF FNA(X)=X*X\n20 PRINT FNA(5)\n"));
        Assert.Equal(" 7 ", Run("10 DEF FNB(X,Y)=X+Y\n20 PRINT FNB(3,4)\n"));
        Assert.Equal(" 15 ", Run("10 K=10\n20 DEF FNC(X)=X+K\n30 PRINT FNC(5)\n"));
        Assert.Equal("ab", Run("10 DEF FND$(S$)=S$+\"b\"\n20 PRINT FND$(\"a\")\n"));
        Assert.Equal(" 9 ", Run("10 DEF FNE(X)=FNF(X)+1\n20 DEF FNF(Y)=Y*2\n30 PRINT FNE(4)\n"));
    }

    [Fact]
    public void DefFnIsNotAllowedInDirectMode() => Assert.StartsWith("ID ERROR", Error("DEF FNA(X)=X"));

    [Fact]
    public void DefFnDoesNotChangeTheCallersVariable() =>
        Assert.Equal(" 3  6 ", Run("10 X=3\n20 DEF FNA(X)=X+1\n30 PRINT X;FNA(5)\n"));

    [Fact]
    public void CallingAFunctionThatWasNeverDefinedIsAnError() => Assert.StartsWith("UF ERROR", Error("PRINT FNZ(1)"));

    // ---------------------------------------------------------------- errors: each code, from direct mode

    private static string Error(string line, params string[] setup)
    {
        var m = Fresh();
        foreach (var s in setup) m.Enter(s);
        m.TakeOutput();
        m.Enter(line);
        var text = m.TakeOutput().Replace("\r", "");
        var at = text.IndexOf('?');
        return at < 0 ? "(no error) " + text : text[(at + 1)..].Split('\n')[0];
    }

    [Theory]
    [InlineData("PRINT 1/0", "/0 ERROR")]
    [InlineData("PRINT 1\\0", "SN ERROR")]
    [InlineData("PRINT 0^-1", "/0 ERROR")]
    [InlineData("PRINT 1E38*10", "OV ERROR")]
    [InlineData("PRINT 2^200", "OV ERROR")]
    [InlineData("PRINT EXP(100)", "OV ERROR")]
    [InlineData("PRINT 1+", "SN ERROR")]
    [InlineData("PRINT (1", "SN ERROR")]
    [InlineData("PRINT 1)", "SN ERROR")]
    [InlineData("PRINT *2", "SN ERROR")]
    [InlineData("PRINT \"A\"+1", "TM ERROR")]
    [InlineData("PRINT 1+\"A\"", "TM ERROR")]
    [InlineData("PRINT \"A\"<1", "TM ERROR")]
    [InlineData("PRINT -\"A\"", "TM ERROR")]
    [InlineData("PRINT NOT \"A\"", "TM ERROR")]
    [InlineData("PRINT LEN(5)", "TM ERROR")]
    [InlineData("PRINT SIN(\"A\")", "TM ERROR")]
    [InlineData("PRINT SQR(-1)", "FC ERROR")]
    [InlineData("PRINT LOG(0)", "FC ERROR")]
    [InlineData("PRINT LOG(-1)", "FC ERROR")]
    [InlineData("PRINT CHR$(256)", "FC ERROR")]
    [InlineData("PRINT CHR$(-1)", "FC ERROR")]
    [InlineData("PRINT LEFT$(\"A\",-1)", "FC ERROR")]
    [InlineData("PRINT MID$(\"A\",0)", "FC ERROR")]
    [InlineData("PRINT STRING$(-1,\"X\")", "FC ERROR")]
    [InlineData("PRINT ASC(\"\")", "FC ERROR")]
    [InlineData("PRINT 70000 AND 1", "OV ERROR")]
    [InlineData("PRINT CINT(40000)", "OV ERROR")]
    [InlineData("PRINT POINT(\"A\",1)", "TM ERROR")]
    [InlineData("PRINT POINT(1,\"A\")", "TM ERROR")]
    [InlineData("PRINT POINT(200,1)", "FC ERROR")]
    public void ErrorCodes(string line, string want) => Assert.Equal(want + " ", Error(line) + " ");

    [Fact]
    public void ArrayErrors()
    {
        Assert.StartsWith("BS ERROR", Error("PRINT A(11)"));
        Assert.StartsWith("BS ERROR", Error("PRINT A(-1)"));
        Assert.StartsWith("BS ERROR", Error("PRINT A(1,1)", "DIM A(5)"));
        Assert.StartsWith("BS ERROR", Error("PRINT A(6)", "DIM A(5)"));
        Assert.StartsWith("DD ERROR", Error("DIM A(5)", "A(1)=1"));
        Assert.StartsWith("DD ERROR", Error("DIM A(5)", "DIM A(6)"));
    }

    [Fact]
    public void AStringTooLongIsAnError()
    {
        var m = Fresh();
        m.Enter("A$=STRING$(255,\"X\")");
        m.Enter("B$=A$+A$");
        Assert.Contains("LS ERROR", m.TakeOutput());
    }

    // ---------------------------------------------------------------- the same expressions inside a program

    [Fact]
    public void AnExpressionGivesTheSameAnswerEachTimeALineRuns()
    {
        // The compiled form of an expression is cached per line and position; running the line many times must not change it.
        var m = Fresh();
        m.Load("10 FOR I=1 TO 5:T=T+I*2:NEXT\n20 PRINT T\n");
        m.Enter("RUN");
        m.Enter("RUN");
        var text = m.TakeOutput().Replace("\r", "");
        Assert.Equal(2, text.Split('\n').Count(l => l.Trim() == "30"));
    }

    [Fact]
    public void EditingALineDropsItsCachedExpression()
    {
        var m = Fresh();
        m.Load("10 PRINT 1+1\n");
        m.Enter("RUN");
        m.Enter("10 PRINT 2+2");
        m.Enter("RUN");
        var text = m.TakeOutput().Replace("\r", "");
        Assert.Contains(" 2 ", text);
        Assert.Contains(" 4 ", text);
    }

    // ---------------------------------------------------------------- no crash on any short string (QA-ROB-001)

    /// <summary>Every string up to length 3 over a small alphabet of expression characters, as the operand of PRINT and as an assignment.</summary>
    [Fact]
    public void EveryShortExpressionIsHandled()
    {
        const string alphabet = "1A(+-*\"$,=<";
        var m = Fresh();
        var inputs = new List<string> { "" };
        var frontier = new List<string> { "" };
        for (int len = 1; len <= 3; len++)
        {
            var next = new List<string>();
            foreach (var p in frontier) foreach (var c in alphabet) next.Add(p + c);
            inputs.AddRange(next);
            frontier = next;
        }
        foreach (var s in inputs)
        {
            var status = m.Enter("PRINT " + s);
            Assert.NotNull(status);
            var text = m.TakeOutput();
            Assert.DoesNotContain("INTERNAL ERROR", text);
            m.Enter("X=" + s);
            Assert.DoesNotContain("INTERNAL ERROR", m.TakeOutput());
        }
        // The machine is still usable afterwards.
        m.Enter("PRINT 1+1");
        Assert.Contains(" 2 ", m.TakeOutput());
    }

    /// <summary>Every call of every built-in function with every combination of a few awkward arguments is an answer or a BASIC error.</summary>
    [Fact]
    public void EveryFunctionWithAwkwardArgumentsIsHandled()
    {
        string[] functions =
        [
            "ABS", "ASC", "ATN", "CDBL", "CHR$", "CINT", "COS", "CSNG", "EXP", "FIX", "INT", "LEN", "LOG", "PEEK", "RND", "SGN",
            "SIN", "SQR", "STR$", "TAN", "VAL", "LEFT$", "RIGHT$", "MID$", "INSTR", "STRING$", "POINT",
        ];
        string[] arguments = ["", "0", "-1", "1E38", "32767", "65535", "\"\"", "\"A\"", "1,1", "1,\"A\"", "\"A\",1", "1,2,3", "A", "A$"];
        var m = Fresh();
        foreach (var f in functions)
            foreach (var a in arguments)
            {
                m.Enter($"PRINT {f}({a})");
                Assert.DoesNotContain("INTERNAL ERROR", m.TakeOutput());
            }
        m.Enter("PRINT 2*3");
        Assert.Contains(" 6 ", m.TakeOutput());
    }
}
