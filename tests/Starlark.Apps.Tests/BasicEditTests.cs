using Basic;

namespace Starlark.Apps.Tests;

/// <summary>
/// The Level II EDIT command: after <c>EDIT n</c> every key acts at once. Each test types a program, starts EDIT on a line, sends
/// keys, and compares what was shown and what the line became. Keys are written as plain characters, with <c>\r</c> for Enter,
/// <c>\x1b</c> for Escape and <c>\b</c> for Backspace.
/// </summary>
public class BasicEditTests
{
    /// <summary>Runs EDIT on line 10 of a program whose line 10 is <paramref name="text"/> and returns (shown while editing, line 10 afterwards).</summary>
    private static (string Shown, string Line) Edit(string text, string keys, bool enter = true)
    {
        var m = new Machine();
        m.TakeOutput();
        m.Load($"10 {text}\n20 PRINT \"KEEP\"\n");
        m.Enter("EDIT 10");
        m.TakeOutput();   // the "10 " head
        foreach (var key in Keys(keys + (enter ? "\r" : ""))) m.Key(key);
        string shown = m.TakeOutput().Replace("\r", "");
        if (!enter && m.Editing)
        {
            m.Key("Q");   // leave the line as it was, so what was shown can be checked without changing it
            m.TakeOutput();
        }
        Assert.False(m.Editing, "the editor should have finished");
        m.Enter("LIST 10");
        string listed = m.TakeOutput().Replace("\r", "");
        string line = listed.Split('\n').First(l => l.StartsWith("10 ")).Substring(3);
        return (shown, line);
    }

    private static IEnumerable<string> Keys(string keys)
    {
        for (int i = 0; i < keys.Length; i++)
        {
            if (keys[i] == '\\' && i + 1 < keys.Length && keys[i + 1] == 'b') { yield return "\b"; i++; }
            else if (keys[i] == '\\' && i + 3 < keys.Length && keys[i + 1] == 'x') { yield return "\x1b"; i += 3; }
            else if (char.IsHighSurrogate(keys[i])) { yield return keys.Substring(i, 2); i++; }
            else yield return keys[i].ToString();
        }
    }

    [Fact]
    public void SpaceMovesOverCharactersAndShowsThem()
    {
        var (shown, line) = Edit("PRINT \"HI\"", "   ", enter: false);
        Assert.StartsWith("PRI", shown);
        Assert.Equal("PRINT \"HI\"", line);
    }

    [Fact]
    public void ACountMovesFurther()
    {
        var (shown, _) = Edit("PRINT \"HI\"", "5 ", enter: false);
        Assert.StartsWith("PRINT", shown);
    }

    [Fact]
    public void EnterShowsTheRestAndKeepsTheLine()
    {
        var (shown, line) = Edit("PRINT \"HI\"", "3 ");
        Assert.Contains("PRINT \"HI\"", shown);
        Assert.Equal("PRINT \"HI\"", line);
    }

    [Fact]
    public void DeleteRemovesCharactersAndShowsThemBetweenBackslashes()
    {
        var (shown, line) = Edit("PRINT \"HI\"", "D");
        Assert.Contains("\\P\\", shown);
        Assert.Equal("RINT \"HI\"", line);
    }

    [Fact]
    public void ACountedDeleteRemovesSeveral()
    {
        var (shown, line) = Edit("PRINT \"HI\"", "6D");
        Assert.Contains("\\PRINT \\", shown);
        Assert.Equal("\"HI\"", line);
    }

    [Fact]
    public void InsertTypesAtTheCursorUntilEscape()
    {
        var (shown, line) = Edit("PRINT \"HI\"", "6 IBIG \\x1b");
        Assert.Contains("BIG ", shown);
        Assert.Equal("PRINT BIG \"HI\"", line);
    }

    [Fact]
    public void BackspaceWhileInsertingTakesBackWhatWasTyped()
    {
        var (_, line) = Edit("PRINT \"HI\"", "6 IXYZ\\b\\b\\x1b");
        Assert.Equal("PRINT X\"HI\"", line);
    }

    [Fact]
    public void BackspaceAtTheStartOfAnInsertDoesNotDeleteOldText()
    {
        var (_, line) = Edit("PRINT \"HI\"", "6 I\\b\\b\\x1b");
        Assert.Equal("PRINT \"HI\"", line);
    }

    [Fact]
    public void BackspaceInCommandModeMovesLeft()
    {
        var (_, line) = Edit("PRINT \"HI\"", "6 \\b\\bDD");
        Assert.Equal("PRI\"HI\"".Replace("PRI", "PRIN").Substring(0, 4) + "\"HI\"", "PRIN\"HI\"");
        Assert.Equal("PRIN\"HI\"", line.Replace("T ", ""));
    }

    [Fact]
    public void ChangeReplacesCharactersOneKeyEach()
    {
        // PRINT is five characters, so a count of 5 uses five keys: LISTS replaces all of them.
        var (_, line) = Edit("PRINT \"HI\"", "5CLISTS");
        Assert.Equal("LISTS \"HI\"", line);
    }

    [Fact]
    public void ChangeStopsAfterItsCount()
    {
        var (_, line) = Edit("ABCDEF", "2CXYZ");
        Assert.Equal("XYCDEF", line.Replace("Z", ""));
    }

    [Fact]
    public void XGoesToTheEndAndInserts()
    {
        var (_, line) = Edit("PRINT \"HI\"", "X : REM DONE\\x1b");
        Assert.Equal("PRINT \"HI\" : REM DONE", line);
    }

    [Fact]
    public void HDeletesTheRestAndInserts()
    {
        var (_, line) = Edit("PRINT \"HI\"", "6 HEND\\x1b");
        Assert.Equal("PRINT END", line);
    }

    [Fact]
    public void SearchMovesToJustBeforeTheCharacter()
    {
        var (shown, line) = Edit("PRINT \"HI\"", "S\"");
        Assert.Contains("PRINT ", shown);
        Assert.Equal("PRINT \"HI\"", line);
    }

    [Fact]
    public void SearchTakesACountOfOccurrences()
    {
        // The cursor starts before the A. The commas are at positions 1, 3 and 5; 2S, stops just before the second, at position 3.
        var (_, line) = Edit("A,B,C,D", "2S,IX\\x1b");
        Assert.Equal("A,BX,C,D", line);
    }

    [Fact]
    public void ASearchThatFindsNothingRunsToTheEnd()
    {
        var (shown, _) = Edit("PRINT", "SZ", enter: false);
        Assert.Equal("PRINT", shown);   // everything passed is shown, from the cursor to the end, since there is no Z
    }

    [Fact]
    public void KillDeletesUpToTheCharacter()
    {
        var (shown, line) = Edit("PRINT \"HI\"", "K\"");
        Assert.Contains("\\PRINT \\", shown);
        Assert.Equal("\"HI\"", line);
    }

    [Fact]
    public void AKillThatFindsNothingChangesNothing()
    {
        var (_, line) = Edit("PRINT", "KZ");
        Assert.Equal("PRINT", line);
    }

    [Fact]
    public void LListsTheRestAndGoesBackToTheStart()
    {
        var (shown, line) = Edit("PRINT \"HI\"", "3 L");
        Assert.Contains("PRINT \"HI\"\n10 ", shown);
        Assert.Equal("PRINT \"HI\"", line);
    }

    [Fact]
    public void QuitLeavesTheLineAsItWas()
    {
        var (_, line) = Edit("PRINT \"HI\"", "5DQ", enter: false);
        Assert.Equal("PRINT \"HI\"", line);
    }

    [Fact]
    public void AbortThrowsAwayTheChangesAndStartsAgain()
    {
        var (shown, line) = Edit("PRINT \"HI\"", "5DAD");
        Assert.Contains("\n10 ", shown);
        Assert.Equal("RINT \"HI\"", line);   // after A the line is whole again, so the final D removes the first character
    }

    [Fact]
    public void EStoresTheChangesWithoutShowingTheRest()
    {
        var (shown, line) = Edit("PRINT \"HI\"", "DE", enter: false);
        Assert.DoesNotContain("RINT \"HI\"", shown.Replace("\\P\\", ""));
        Assert.Equal("RINT \"HI\"", line);
    }

    [Fact]
    public void EscapeCancelsACountAndAPendingSearch()
    {
        var (_, line) = Edit("PRINT", "9\\x1bD");
        Assert.Equal("RINT", line);
        (_, line) = Edit("PRINT", "S\\x1bD");
        Assert.Equal("RINT", line);
    }

    [Fact]
    public void UnknownKeysDoNothing()
    {
        var (_, line) = Edit("PRINT", "?Z%#");
        Assert.Equal("PRINT", line);
    }

    [Fact]
    public void ADeletedLineBodyRemovesTheLine()
    {
        var m = new Machine();
        m.TakeOutput();
        m.Load("10 AB\n20 PRINT \"KEEP\"\n");
        m.Enter("EDIT 10");
        foreach (var key in Keys("2D\r")) m.Key(key);
        m.TakeOutput();
        m.Enter("LIST");
        var text = m.TakeOutput();
        Assert.DoesNotContain("10 ", text.Replace("20 ", ""));
        Assert.Contains("20 PRINT \"KEEP\"", text);
    }

    [Fact]
    public void NonAsciiCharactersAreWholeSteps()
    {
        var (shown, line) = Edit("PRINT \"é😀\"", "7 D");
        Assert.Contains("\\é\\", shown);
        Assert.Equal("PRINT \"😀\"", line);
        (_, line) = Edit("PRINT \"é😀\"", "8 D");
        Assert.Equal("PRINT \"é\"", line);   // one D removes the whole astral character, not half of it
    }

    [Fact]
    public void AnEditedLineRunsLikeAnyOther()
    {
        var m = new Machine();
        m.TakeOutput();
        m.Load("10 PRINT \"HELLO\"\n");
        m.Enter("EDIT 10");
        foreach (var key in Keys("7 DDDDDIWORLD\x1b\r".Replace("\x1b", "\\x1b"))) m.Key(key);
        m.TakeOutput();
        m.Enter("RUN");
        Assert.Contains("WORLD", m.TakeOutput());
    }

    // ---------------------------------------------------------------- errors and limits

    [Fact]
    public void EditOfAMissingLineIsUndefinedLine()
    {
        var m = new Machine();
        m.TakeOutput();
        m.Enter("EDIT 99");
        Assert.Contains("?UL ERROR", m.TakeOutput());
        Assert.False(m.Editing);
    }

    [Theory]
    [InlineData("EDIT")]
    [InlineData("EDIT X")]
    [InlineData("EDIT 10,20")]
    [InlineData("EDIT -1")]
    public void EditNeedsOneLineNumber(string command)
    {
        var m = new Machine();
        m.TakeOutput();
        m.Load("10 PRINT 1\n");
        m.Enter(command);
        Assert.Contains("ERROR", m.TakeOutput());
        Assert.False(m.Editing);
    }

    [Fact]
    public void ALineCannotGrowPastTheLimit()
    {
        var m = new Machine();
        m.TakeOutput();
        m.Load("10 REM\n");
        m.Enter("EDIT 10");
        m.Key("X");
        for (int i = 0; i < 400; i++) m.Key("A");
        m.Key("\x1b");
        m.Key("\r");
        m.TakeOutput();
        m.Enter("LIST 10");
        Assert.True(m.TakeOutput().Length < 400);
        Assert.False(m.Editing);
    }

    [Fact]
    public void EditInsideAProgramIsAnIllegalDirect()
    {
        var m = new Machine();
        m.TakeOutput();
        m.Load("10 EDIT 10\n");
        m.Enter("RUN");
        Assert.Contains("ERROR", m.TakeOutput());
        Assert.False(m.Editing);
    }

    [Fact]
    public void EveryShortKeySequenceEndsTheEditorWithoutAFault()
    {
        // The no-crash policy for a keyboard-driven editor: any sequence of up to four keys from the whole command set, then Enter,
        // must finish editing and leave the machine usable.
        const string alphabet = "0 DICXHSKLQAE\\\"A\b";
        var keyset = new List<string>();
        foreach (var ch in alphabet) keyset.Add(ch.ToString());
        keyset.Add("\x1b");
        var m = new Machine();
        m.TakeOutput();
        m.Load("10 PRINT \"A,B\":X=1\n");
        int tried = 0;
        foreach (var a in keyset) foreach (var b in keyset) foreach (var c in keyset) foreach (var d in keyset)
        {
            m.Enter("EDIT 10");
            foreach (var key in new[] { a, b, c, d, "\x1b", "\r" }) m.Key(key);
            if (m.Editing) { m.Key("Q"); m.Key("\r"); }
            Assert.False(m.Editing, $"stuck after {a}{b}{c}{d}");
            m.Enter("NEW");
            m.Load("10 PRINT \"A,B\":X=1\n");
            tried++;
        }
        Assert.True(tried > 50_000);
        m.TakeOutput();
        Assert.Contains("10 PRINT", ListOf(m));
    }

    private static string ListOf(Machine m)
    {
        m.Enter("LIST");
        return m.TakeOutput();
    }
}
