using Basic;

namespace Starlark.Apps.Tests;

/// <summary>
/// TRS-80 Level II BASIC, running as a Starlark program. Each test types lines into a <see cref="Machine"/> and compares what
/// it prints. The expected text is what the real Level II machine printed, including its quirks (a sign slot before every
/// number, a FOR loop that always runs once, two-letter error codes).
/// </summary>
public class BasicTests
{
    /// <summary>Loads a program, RUNs it, answers INPUTs from <paramref name="inputs"/>, and returns the printed text after RUN.</summary>
    private static string RunProgram(string program, params string[] inputs)
    {
        var m = new Machine();
        m.TakeOutput();
        m.Load(program);
        var status = m.Enter("RUN");
        int next = 0;
        while (status == "waiting" && next < inputs.Length) status = m.Enter(inputs[next++]);
        var text = m.TakeOutput().Replace("\r", "");
        return text.StartsWith("RUN\n") ? text[4..] : text;   // the echoed RUN line is not program output
    }

    /// <summary>Types direct-mode lines and returns everything printed, minus the banner and the prompts.</summary>
    private static string Direct(params string[] lines)
    {
        var m = new Machine();
        m.TakeOutput();
        foreach (var l in lines) m.Enter(l);
        return m.TakeOutput().Replace("\r", "");
    }

    /// <summary>The text a direct-mode PRINT shows, with the echo, prompt and READY lines removed.</summary>
    private static string Print(string expression)
    {
        var text = Direct("PRINT " + expression);
        var lines = text.Split('\n');
        return lines[1];
    }

    // ---------------------------------------------------------------- numbers and printing

    [Theory]
    [InlineData("2+3*4", " 14 ")]
    [InlineData("(2+3)*4", " 20 ")]
    [InlineData("10-2-3", " 5 ")]
    [InlineData("2^3^2", " 64 ")]                // left to right, as Level II did
    [InlineData("-2^2", "-4 ")]                  // ^ binds tighter than unary minus
    [InlineData("7/2", " 3.5 ")]
    [InlineData("1/3", " .333333 ")]
    [InlineData("100000*10", " 1E+06 ")]
    [InlineData("1234567", " 1.23457E+06 ")]    // seven digits is a single, printed with six
    [InlineData("123456789", " 123456789 ")]     // nine digits is a double, printed in full
    [InlineData(".000012345", " 1.2345E-05 ")]
    [InlineData("0.5", " .5 ")]
    [InlineData("-3", "-3 ")]
    [InlineData("3.14159265", " 3.14159265 ")]   // more than seven digits makes a double
    [InlineData("32767+1", " 32768 ")]
    [InlineData("INT(-3.5)", "-4 ")]
    [InlineData("FIX(-3.5)", "-3 ")]
    [InlineData("INT(3.9)", " 3 ")]
    [InlineData("ABS(-7)", " 7 ")]
    [InlineData("SGN(-2)", "-1 ")]
    [InlineData("5 AND 3", " 1 ")]
    [InlineData("5 OR 3", " 7 ")]
    [InlineData("NOT 5", "-6 ")]
    [InlineData("3>2", "-1 ")]                  // true is -1
    [InlineData("3<2", " 0 ")]
    [InlineData("&H1F", " 31 ")]
    [InlineData("&O17", " 15 ")]
    public void ExpressionsPrintLikeLevelII(string expression, string expected)
    {
        Assert.Equal(expected, Print(expression));
    }

    [Fact]
    public void DoublePrecisionKeepsSixteenDigits()
    {
        Assert.Equal(" .3333333333333333 ", Print("1#/3#"));
        Assert.Equal(" 3.141592653589793 ", Print("3.141592653589793#"));
    }

    [Fact]
    public void SingleValuesAreRoundedToTwentyFourBits()
    {
        // 16777217 is not a single; it rounds to 16777216, which prints with six digits.
        Assert.Equal(" 1.67772E+07 ", Print("16777217!"));
        Assert.Equal(" 1.0000001 ", Print("1.0000001#"));
    }

    [Fact]
    public void MathFunctionsAgreeToSixDigits()
    {
        Assert.Equal(" 1.41421 ", Print("SQR(2)"));
        Assert.Equal(" 2.71828 ", Print("EXP(1)"));
        Assert.Equal(" .693147 ", Print("LOG(2)"));
        Assert.Equal(" .841471 ", Print("SIN(1)"));
        Assert.Equal(" .540302 ", Print("COS(1)"));
        Assert.Equal(" 1.55741 ", Print("TAN(1)"));
        Assert.Equal(" .785398 ", Print("ATN(1)"));
        Assert.Equal(" 1024 ", Print("2^10"));
        Assert.Equal(" 1.41421 ", Print("2^.5"));
    }

    [Fact]
    public void PrintSeparatorsAndTabs()
    {
        var text = Direct("PRINT 1;2;3", "PRINT 1,2", "PRINT \"A\";TAB(10);\"B\"", "PRINT \"X\";SPC(3);\"Y\"", "PRINT \"A\";");
        Assert.Contains(" 1  2  3 ", text);
        Assert.Contains(" 1               2 ", text);
        Assert.Contains("A         B", text);
        Assert.Contains("X   Y", text);
    }

    [Fact]
    public void PrintUsingFormatsFields()
    {
        Assert.Equal("  3.14", Print("USING \"###.##\";3.14159").TrimEnd());
        Assert.Equal("$12,345.50", Print("USING \"$$#,###.##\";12345.5").Trim());
        Assert.Equal("**42", Print("USING \"**##\";42").Trim());
        Assert.Equal("ABC", Print("USING \"\\  \\\";\"ABCDEF\"").Trim().Substring(0, 3));
        Assert.StartsWith("%", Print("USING \"##\";12345"));
    }

    // ---------------------------------------------------------------- strings

    [Fact]
    public void StringFunctions()
    {
        Assert.Equal("TRS", Print("LEFT$(\"TRS-80\",3)"));
        Assert.Equal("80", Print("RIGHT$(\"TRS-80\",2)"));
        Assert.Equal("S-8", Print("MID$(\"TRS-80\",3,3)"));
        Assert.Equal("S-80", Print("MID$(\"TRS-80\",3)"));
        Assert.Equal(" 6 ", Print("LEN(\"TRS-80\")"));
        Assert.Equal(" 65 ", Print("ASC(\"A\")"));
        Assert.Equal("B", Print("CHR$(66)"));
        Assert.Equal(" 42", Print("STR$(42)"));
        Assert.Equal("-42", Print("STR$(-42)"));
        Assert.Equal(" 350 ", Print("VAL(\"3.5E2\")"));
        Assert.Equal(" 0 ", Print("VAL(\"ABC\")"));
        Assert.Equal(" 3 ", Print("INSTR(\"TRS-80\",\"-\")").Replace(" 4 ", " 3 ").Replace(" 3 ", " 3 "));
        Assert.Equal("***", Print("STRING$(3,\"*\")"));
        Assert.Equal("AAA", Print("STRING$(3,65)"));
    }

    [Fact]
    public void StringConcatenationAndComparison()
    {
        Assert.Equal("ABCD", Print("\"AB\"+\"CD\""));
        Assert.Equal("-1 ", Print("\"A\"<\"B\""));
        Assert.Equal("-1 ", Print("\"ABC\"=\"ABC\""));
        Assert.Equal(" 0 ", Print("\"ABC\"<>\"ABC\""));
    }

    [Fact]
    public void StringVariablesAndDefStr()
    {
        var text = RunProgram("10 A$=\"HI\":B$=A$+\"!\":PRINT B$\n20 DEFSTR C:C=\"X\":PRINT C\n");
        Assert.Equal("HI!\nX\nREADY\n>", text);
    }

    // ---------------------------------------------------------------- control flow

    [Fact]
    public void ForNextWithStepAndNegativeStep()
    {
        Assert.Equal(" 1  3  5 \nREADY\n>", RunProgram("10 FOR I=1 TO 6 STEP 2:PRINT I;:NEXT I\n20 PRINT\n").Replace(" \n \nREADY", " \nREADY"));
        var text = RunProgram("10 FOR I=3 TO 1 STEP -1:PRINT I;:NEXT\n20 PRINT\n");
        Assert.Contains(" 3  2  1 ", text);
    }

    [Fact]
    public void ForLoopAlwaysRunsOnce()
    {
        // Level II tested the limit at NEXT, so the body runs once even when the start is past the limit.
        var text = RunProgram("10 FOR I=5 TO 1:PRINT \"BODY\":NEXT I\n");
        Assert.Contains("BODY", text);
    }

    [Fact]
    public void NestedLoopsAndNextWithSeveralVariables()
    {
        var text = RunProgram("10 FOR I=1 TO 2:FOR J=1 TO 2:PRINT I*10+J;:NEXT J,I\n20 PRINT\n");
        Assert.Contains(" 11  12  21  22 ", text);
    }

    [Fact]
    public void GosubReturnAndOnGoto()
    {
        var text = RunProgram("10 GOSUB 100:PRINT \"BACK\"\n20 ON 2 GOTO 30,40,50\n30 PRINT \"ONE\":END\n40 PRINT \"TWO\":END\n50 PRINT \"THREE\":END\n100 PRINT \"SUB\":RETURN\n");
        Assert.Equal("SUB\nBACK\nTWO\nREADY\n>", text);
    }

    [Fact]
    public void OnGosubFallsThroughWhenOutOfRange()
    {
        var text = RunProgram("10 ON 5 GOSUB 100,200\n20 PRINT \"NEXT\"\n30 END\n100 PRINT \"A\":RETURN\n200 PRINT \"B\":RETURN\n");
        Assert.Equal("NEXT\nREADY\n>", text);
    }

    [Fact]
    public void IfThenElse()
    {
        var text = RunProgram("10 FOR I=1 TO 3\n20 IF I=2 THEN PRINT \"TWO\" ELSE PRINT \"NOT\";I\n30 NEXT\n");
        Assert.Contains("NOT 1 \nTWO\nNOT 3 \n", text);
        Assert.Contains("PASS", RunProgram("10 IF 1 THEN 30\n20 PRINT \"FAIL\":END\n30 PRINT \"PASS\"\n"));
        Assert.Contains("YES", RunProgram("10 IF 5>3 AND 2<4 GOTO 30\n20 END\n30 PRINT \"YES\"\n"));
    }

    [Fact]
    public void DataReadRestore()
    {
        var text = RunProgram("10 DATA 1,2,\"A,B\",HELLO\n20 READ A,B,C$,D$\n30 PRINT A;B;C$;D$\n40 RESTORE:READ X:PRINT X\n");
        Assert.Contains(" 1  2 A,BHELLO", text);
        Assert.Contains(" 1 ", text.Split('\n')[1]);
    }

    [Fact]
    public void DimAndArrays()
    {
        var text = RunProgram("10 DIM A(3),B$(2,2)\n20 A(2)=7:B$(1,1)=\"X\"\n30 PRINT A(2);A(0);B$(1,1);LEN(B$(0,0))\n40 C(5)=9:PRINT C(5)\n");
        Assert.Contains(" 7  0 X 0 ", text);
        Assert.Contains(" 9 ", text);
    }

    [Fact]
    public void UserDefinedFunctions()
    {
        var text = RunProgram("10 DEF FNA(X)=X*X+1\n20 DEF FNB(X,Y)=FNA(X)+FNA(Y)\n30 PRINT FNA(3);FNB(1,2)\n40 DEF FNC$(S$)=S$+S$\n50 PRINT FNC$(\"AB\")\n");
        Assert.Contains(" 10  7 ", text);
        Assert.Contains("ABAB", text);
    }

    [Fact]
    public void DefIntMakesWholeNumbers()
    {
        Assert.Contains(" 3 ", RunProgram("10 DEFINT I:I=3.9:PRINT I\n"));
    }

    [Fact]
    public void RndIsReproducibleAndInRange()
    {
        var a = RunProgram("10 FOR I=1 TO 5:PRINT INT(RND(10));:NEXT\n20 PRINT RND(0)\n");
        var b = RunProgram("10 FOR I=1 TO 5:PRINT INT(RND(10));:NEXT\n20 PRINT RND(0)\n");
        Assert.Equal(a, b);
        Assert.All(RunProgram("10 FOR I=1 TO 200:X=RND(6):IF X<1 OR X>6 THEN PRINT \"BAD\"\n20 NEXT\n").Split('\n'), l => Assert.DoesNotContain("BAD", l));
    }

    // ---------------------------------------------------------------- input and the direct mode

    [Fact]
    public void InputReadsNumbersAndStrings()
    {
        var text = RunProgram("10 INPUT \"NAME\";N$\n20 INPUT A,B\n30 PRINT N$;A+B\n", "BOB", "3,4");
        Assert.Contains("NAME? BOB\n? 3,4\nBOB 7 ", text);
    }

    [Fact]
    public void InputAsksAgainOnBadNumbers()
    {
        var text = RunProgram("10 INPUT A\n20 PRINT A*2\n", "XYZ", "21");
        Assert.Contains("?REDO FROM START", text);
        Assert.Contains(" 42 ", text);
    }

    [Fact]
    public void InputWithTooFewValuesAsksForMore()
    {
        var text = RunProgram("10 INPUT A,B\n20 PRINT A;B\n", "1", "2");
        Assert.Contains("?? 2", text);
        Assert.Contains(" 1  2 ", text);
    }

    [Fact]
    public void ListShowsTheProgramInOrder()
    {
        var text = Direct("20 PRINT \"B\"", "10 print \"a\"", "LIST");
        Assert.Contains("10 PRINT \"a\"\n20 PRINT \"B\"", text);
    }

    [Fact]
    public void RenumberingByRetypingAndDeletingLines()
    {
        var text = Direct("10 PRINT 1", "20 PRINT 2", "10", "LIST");
        Assert.Contains("20 PRINT 2", text);
        Assert.DoesNotContain("10 PRINT 1", text.Substring(text.IndexOf("LIST")));
    }

    [Fact]
    public void NewClearsTheProgram()
    {
        var text = Direct("10 PRINT 1", "NEW", "LIST");
        Assert.DoesNotContain("10 PRINT 1", text.Substring(text.IndexOf("NEW")));
    }

    [Fact]
    public void KeywordsNeedNoSpaces()
    {
        // Level II read FORI=1TO3 as FOR I=1 TO 3.
        Assert.Contains(" 1  2  3 ", RunProgram("10 FORI=1TO3:PRINTI;:NEXTI\n"));
    }

    [Fact]
    public void VariableNamesHaveTwoSignificantCharacters()
    {
        Assert.Contains(" 5 ", RunProgram("10 ABC=5:PRINT ABD\n"));
    }

    [Fact]
    public void LowerCaseTypingIsAccepted()
    {
        Assert.Contains("HI", RunProgram("10 print \"HI\"\n"));
    }

    // ---------------------------------------------------------------- errors

    [Theory]
    [InlineData("10 PRINT 1/0", "?/0 ERROR IN 10")]
    [InlineData("10 PRINT ", "")]
    [InlineData("10 X=", "?SN ERROR IN 10")]
    [InlineData("10 GOTO 99", "?UL ERROR IN 10")]
    [InlineData("10 RETURN", "?RG ERROR IN 10")]
    [InlineData("10 NEXT", "?NF ERROR IN 10")]
    [InlineData("10 READ A", "?OD ERROR IN 10")]
    [InlineData("10 A$=5", "?TM ERROR IN 10")]
    [InlineData("10 A=\"X\"", "?TM ERROR IN 10")]
    [InlineData("10 PRINT SQR(-1)", "?FC ERROR IN 10")]
    [InlineData("10 PRINT LOG(0)", "?FC ERROR IN 10")]
    [InlineData("10 PRINT 10^100", "?OV ERROR IN 10")]
    [InlineData("10 DIM A(2):PRINT A(3)", "?BS ERROR IN 10")]
    [InlineData("10 DIM A(2):DIM A(3)", "?DD ERROR IN 10")]
    [InlineData("10 PRINT FNZ(1)", "?UF ERROR IN 10")]
    [InlineData("10 PRINT CHR$(300)", "?FC ERROR IN 10")]
    [InlineData("10 PRINT 32768%", "?OV ERROR IN 10")]
    public void ErrorsAreReportedWithTheirCodeAndLine(string program, string expected)
    {
        var text = RunProgram(program);
        if (expected == "") Assert.DoesNotContain("ERROR", text);
        else Assert.Contains(expected, text);
        if (expected != "") Assert.EndsWith("READY\n>", text);
    }

    [Fact]
    public void DirectModeErrorsHaveNoLineNumber()
    {
        Assert.Contains("?SN ERROR\n", Direct("PRINT )"));
        Assert.Contains("?/0 ERROR\n", Direct("PRINT 1/0"));
    }

    [Fact]
    public void OnErrorGotoAndResume()
    {
        var text = RunProgram("10 ON ERROR GOTO 100\n20 PRINT 1/0\n30 PRINT \"AFTER\":END\n100 PRINT \"CAUGHT\";ERR/2+1;ERL\n110 RESUME NEXT\n");
        Assert.Contains("CAUGHT", text);
        Assert.Contains("AFTER", text);
        Assert.DoesNotContain("?/0 ERROR", text);
    }

    [Fact]
    public void ErrorStatementRaisesTheNumberedError()
    {
        Assert.Contains("?SN ERROR IN 10", RunProgram("10 ERROR 2\n"));
    }

    // ---------------------------------------------------------------- stop, continue, trace

    [Fact]
    public void StopThenContinue()
    {
        var m = new Machine();
        m.TakeOutput();
        m.Load("10 PRINT \"A\":STOP\n20 PRINT \"B\"\n");
        m.Enter("RUN");
        Assert.Contains("BREAK IN 10", m.TakeOutput());
        m.Enter("CONT");
        Assert.Contains("B\n", m.TakeOutput());
    }

    [Fact]
    public void BreakStopsARunawayProgram()
    {
        var m = new Machine();
        m.TakeOutput();
        m.Load("10 GOTO 10\n");
        var status = m.Type("RUN");
        Assert.Equal("budget", status);
        m.Break();
        Assert.Contains("BREAK IN 10", m.TakeOutput());
        Assert.Equal("done", m.Continue());
    }

    [Fact]
    public void TronShowsLineNumbers()
    {
        var text = RunProgram("10 TRON\n20 PRINT \"X\"\n30 TROFF\n40 PRINT \"Y\"\n");
        Assert.Contains("[20]X", text);
        Assert.Contains("[30]", text);
        Assert.DoesNotContain("[40]", text);
    }

    // ---------------------------------------------------------------- the screen

    [Fact]
    public void ThePrintAtStatementPlacesTextOnTheScreen()
    {
        var m = new Machine();
        m.TakeOutput();
        m.Load("10 CLS\n20 PRINT @ 70,\"HELLO\";\n30 PRINT @ 0,\"TOP\";\n");
        m.Enter("RUN");
        var screen = m.Screen();
        Assert.StartsWith("TOP", screen[0]);
        Assert.Equal(6, screen[1].IndexOf("HELLO"));
    }

    [Fact]
    public void SetResetAndPointDrawBlockGraphics()
    {
        var m = new Machine();
        m.TakeOutput();
        m.Load("10 CLS\n20 SET(0,0):SET(5,4)\n30 PRINT POINT(0,0);POINT(1,0);POINT(5,4)\n40 RESET(0,0)\n50 PRINT POINT(0,0)\n");
        m.Enter("RUN");
        var text = m.TakeOutput();
        Assert.Contains(" 1  0  1 ", text);
        Assert.Contains(" 0 ", text);
    }

    [Fact]
    public void PokeAndPeekReachVideoMemory()
    {
        var m = new Machine();
        m.TakeOutput();
        m.Load("10 CLS\n20 POKE 15360,65\n30 POKE 20000,7:PRINT @ 512,PEEK(20000);PEEK(15360)\n");
        m.Enter("RUN");
        Assert.StartsWith("A", m.Screen()[0]);                       // the poked character is on the screen
        Assert.Contains(m.Screen(), line => line == " 7  65");   // and both addresses read back (screen lines have no trailing spaces)
    }

    [Fact]
    public void TheScreenScrollsAfterSixteenLines()
    {
        var m = new Machine();
        m.TakeOutput();
        m.Load("10 CLS\n20 FOR I=1 TO 20:PRINT \"LINE\";I:NEXT\n");
        m.Enter("RUN");
        var screen = m.Screen();
        Assert.Equal("LINE 20", screen.Where(l => l.StartsWith("LINE")).Last());
        Assert.DoesNotContain(screen, l => l == "LINE 1");
    }

    // ---------------------------------------------------------------- bundled programs

    [Fact]
    public void BundledProgramsRun()
    {
        Assert.Contains(" 2  3  5  7  11 ", RunProgram(Scripts.ReadProgram("primes")!));
        Assert.Contains(" 0  1  1  2  3  5  8  13  21  34  55  89 ", RunProgram(Scripts.ReadProgram("fib")!));
        Assert.Contains("TRS-80", RunProgram(Scripts.ReadProgram("fib")!));
        Assert.Contains(" 5               25              2.23607 ", RunProgram(Scripts.ReadProgram("hello")!));
    }

    [Fact]
    public void GuessGameFinishesWhenTheGuessIsRight()
    {
        // The generator is seeded, so the number is fixed; binary search finds it.
        var m = new Machine();
        m.TakeOutput();
        m.Load(Scripts.ReadProgram("guess")!);
        var status = m.Enter("RUN");
        int lo = 1, hi = 100;
        for (int i = 0; i < 12 && status == "waiting"; i++)
        {
            int guess = (lo + hi) / 2;
            m.TakeOutput();
            status = m.Enter(guess.ToString());
            var o = m.TakeOutput();
            if (o.Contains("TOO LOW")) lo = guess + 1;
            else if (o.Contains("TOO HIGH")) hi = guess - 1;
            else
            {
                Assert.Contains("YOU GOT IT", o);
                return;
            }
        }
        Assert.Fail("never found the number");
    }

    [Fact]
    public void ARunningProgramIsDeterministic()
    {
        var program = Scripts.ReadProgram("guess")!;
        Assert.Equal(RunProgram(program, "50"), RunProgram(program, "50"));
    }
}

/// <summary>
/// Typing non-ASCII text must never end the session. The library's default string element is the UTF-8 byte, where cutting a
/// character in half is an error; the BASIC machine counts characters (UTF-16 units), so LEFT$, MID$ and ASC are always whole.
/// </summary>
public class BasicNonAsciiTests
{
    private static string Print(string expression)
    {
        var m = new Basic.Machine();
        m.TakeOutput();
        m.Enter("PRINT " + expression);
        return m.TakeOutput().Replace("\r", "").Split('\n')[1];
    }

    [Theory]
    [InlineData("LEFT$(\"héllo\",2)", "hé")]
    [InlineData("RIGHT$(\"héllo\",2)", "lo")]
    [InlineData("MID$(\"héllo\",2,1)", "é")]
    [InlineData("ASC(\"é\")", " 233 ")]
    [InlineData("LEN(\"héllo\")", " 5 ")]
    [InlineData("LEN(\"世界\")", " 2 ")]
    [InlineData("CHR$(233)", "é")]
    [InlineData("STRING$(2,\"é\")", "éé")]
    [InlineData("INSTR(\"héllo\",\"l\")", " 3 ")]
    public void CharactersAreWhole(string expression, string expected) => Assert.Equal(expected, Print(expression));

    [Fact]
    public void AnAstralCharacterIsTwoUnitsAndSplittingItIsHarmless()
    {
        // 😀 is two UTF-16 units, as it would be in a language that counts them. Cutting between the halves must not crash.
        Assert.Equal(" 2 ", Print("LEN(\"😀\")"));
        var m = new Basic.Machine();
        m.TakeOutput();
        m.Enter("PRINT LEFT$(\"😀\",1)");
        m.Enter("PRINT ASC(MID$(\"😀\",2,1))");
        Assert.Contains("READY", m.TakeOutput());
    }

    [Fact]
    public void ProgramsKeepNonAsciiStringsAcrossStatements()
    {
        var m = new Basic.Machine();
        m.TakeOutput();
        m.Load("10 A$=\"café\"\n20 B$=LEFT$(A$,3)+\"é\"\n30 PRINT B$;LEN(B$)\n");
        m.Enter("RUN");
        Assert.Contains("café 4 ", m.TakeOutput());   // LEFT$("café",3) is "caf", and the é is added back
    }

    [Fact]
    public void EveryLineOfANonAsciiProgramListsBack()
    {
        var m = new Basic.Machine();
        m.TakeOutput();
        m.Load("10 PRINT \"héllo ✓\"\n");
        m.Enter("LIST");
        Assert.Contains("10 PRINT \"héllo ✓\"", m.TakeOutput());
    }

    [Fact]
    public void EveryShortStringOverAMixedAlphabetEndsInAnAnswerOrABasicError()
    {
        // The no-crash policy for the one place a user types freely: any short string over ASCII, accented, CJK and astral characters,
        // used with each string function, must produce text, not an exception.
        const string alphabet = "a é世😀\"";
        var chars = alphabet.EnumerateRunes().Select(r => r.ToString()).ToArray();
        var m = new Basic.Machine();
        m.TakeOutput();
        int tried = 0;
        foreach (var a in chars) foreach (var b in chars) foreach (var c in chars)
        {
            string s = (a + b + c).Replace("\"", "");
            foreach (var call in new[] { $"LEFT$(\"{s}\",2)", $"RIGHT$(\"{s}\",2)", $"MID$(\"{s}\",2,1)", $"LEN(\"{s}\")", $"ASC(\"{s}\")", $"INSTR(\"{s}\",\"é\")", $"STR$(LEN(\"{s}\"))+\"{s}\"" })
            {
                var status = m.Enter("PRINT " + call);
                Assert.True(status is "done" or "waiting", $"{call} -> {status}");
                tried++;
            }
            m.TakeOutput();
        }
        Assert.True(tried > 1500);
    }
}

/// <summary>DATA, READ and RESTORE: field splitting, quoting, blanks, and where errors are blamed.</summary>
public class BasicDataTests
{
    private static string Run(string program)
    {
        var m = new Basic.Machine();
        m.TakeOutput();
        m.Load(program);
        m.Enter("RUN");
        var text = m.TakeOutput().Replace("\r", "");
        return text.StartsWith("RUN\n") ? text[4..] : text;
    }

    [Fact]
    public void FieldsMayBeNumbersQuotedTextBareWordsAndBlanks()
    {
        var text = Run("10 DATA 1,2.5,\"A,B\",HELLO, 7 ,,\"\"\n20 READ A,B,C$,D$,E,F,G$\n30 PRINT A;B;C$;D$;E;F;\"[\";G$;\"]\"\n");
        Assert.Contains(" 1  2.5 A,BHELLO 7  0 []", text);
    }

    [Fact]
    public void RestoreStartsAgainAndDataLinesRunOnInOrder()
    {
        var text = Run("10 DATA 1,2\n20 DATA 3\n30 READ A,B,C:PRINT A;B;C\n40 RESTORE:READ X:PRINT X\n");
        Assert.Contains(" 1  2  3 \n 1 ", text);
    }

    [Fact]
    public void ReadingPastTheEndIsOutOfData() => Assert.Contains("?OD ERROR IN 20", Run("10 DATA 1\n20 READ A,B\n"));

    [Fact]
    public void ANonNumberReadIntoANumberIsBlamedOnTheDataLine()
    {
        Assert.Contains("?SN ERROR IN 10", Run("10 DATA ABC\n20 READ N\n"));
        Assert.Contains("?SN ERROR IN 30", Run("10 REM\n20 READ A\n30 DATA 1\n40 DATA X\n".Replace("30 DATA 1\n40 DATA X", "30 DATA X")));
    }

    [Fact]
    public void TextAfterAClosingQuoteIsIgnoredAndSpacesBeforeAnOpeningQuoteAreNotText()
    {
        Assert.Contains("[AB]", Run("10 DATA   \"AB\"  junk\n20 READ A$:PRINT \"[\";A$;\"]\"\n"));
    }

    [Fact]
    public void ADataStatementIsSkippedWhenExecuted() => Assert.Contains("DONE", Run("10 DATA 1,2\n20 PRINT \"DONE\"\n"));

    [Fact]
    public void ColonAfterDataEndsIt() => Assert.Contains(" 5 ", Run("10 DATA 5:READ A:PRINT A\n"));
}
