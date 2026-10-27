using System.Numerics;
using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>
/// Tests written from the survivors of a Stryker.NET run over <c>Methods.cs</c>. Most pin down behaviour the earlier
/// tests only touched: methods must reject extra arguments, error messages must name the parameter, and the split,
/// replace and index code must be right at its boundaries.
/// </summary>
public class MethodsMutationTests
{
    private static Interpreter New() => new()
    {
        Dialect = Dialect.Standard with { EnableTopLevelStmt = true },
    };

    private static object? Val(string expression) => New().Execute("t", "r = " + expression + "\n").Globals["r"];

    private static string Fails(string statements) =>
        Assert.Throws<StarlarkException>(() => New().Execute("t", statements)).RawMessage;

    // ---------------------------------------------------------------- stray arguments are rejected

    /// <summary>Every method that takes no arguments rejects a positional one, naming the method.</summary>
    [Theory]
    [InlineData("'a'.upper(1)", "upper")]
    [InlineData("'a'.lower(1)", "lower")]
    [InlineData("'a'.capitalize(1)", "capitalize")]
    [InlineData("'a'.title(1)", "title")]
    [InlineData("'a'.isalpha(1)", "isalpha")]
    [InlineData("'a'.isdigit(1)", "isdigit")]
    [InlineData("'a'.isalnum(1)", "isalnum")]
    [InlineData("'a'.isspace(1)", "isspace")]
    [InlineData("'a'.islower(1)", "islower")]
    [InlineData("'a'.isupper(1)", "isupper")]
    [InlineData("'a'.istitle(1)", "istitle")]
    [InlineData("'a'.elems(1)", "elems")]
    [InlineData("b'a'.elems(1)", "elems")]
    [InlineData("[1].clear(1)", "clear")]
    [InlineData("{}.clear(1)", "clear")]
    [InlineData("{}.keys(1)", "keys")]
    [InlineData("{}.values(1)", "values")]
    [InlineData("{}.items(1)", "items")]
    [InlineData("{}.popitem(1)", "popitem")]
    [InlineData("set().clear(1)", "clear")]
    [InlineData("set([1]).pop(1)", "pop")]
    public void MethodsRejectStrayPositionalArguments(string expression, string method)
    {
        string message = Fails($"x = {expression}\n");
        Assert.Contains($"Too many positional arguments to `{method}`", message);
    }

    /// <summary>The methods that take a required argument also reject a second one.</summary>
    [Theory]
    [InlineData("[1].append(1, 2)", "append")]
    [InlineData("[1].extend([1], 2)", "extend")]
    [InlineData("[1].remove(1, 2)", "remove")]
    [InlineData("[1].index(1, 0, 1, 5)", "index")]
    [InlineData("[1].insert(0, 1, 2)", "insert")]
    [InlineData("{}.get(1, 2, 3)", "get")]
    [InlineData("{}.setdefault(1, 2, 3)", "setdefault")]
    [InlineData("set().add(1, 2)", "add")]
    [InlineData("set().discard(1, 2)", "discard")]
    [InlineData("set().remove(1, 2)", "remove")]
    [InlineData("'a'.join(['b'], 2)", "join")]
    [InlineData("'a'.partition('a', 2)", "partition")]
    [InlineData("'a'.rpartition('a', 2)", "rpartition")]
    [InlineData("'a'.removeprefix('a', 2)", "removeprefix")]
    [InlineData("'a'.removesuffix('a', 2)", "removesuffix")]
    public void MethodsWithArgumentsRejectExtraOnes(string expression, string method) =>
        Assert.Contains($"`{method}`", Fails($"x = {expression}\n"));

    /// <summary>Unknown keyword arguments are rejected too.</summary>
    [Fact]
    public void UnknownKeywordsAreRejected()
    {
        Assert.Contains("Unexpected parameter", Fails("x = 'a'.upper(z = 1)\n"));
        Assert.Contains("Unexpected parameter", Fails("x = [1].clear(z = 1)\n"));
        Assert.Contains("Unexpected parameter", Fails("x = {}.keys(z = 1)\n"));
    }

    // ---------------------------------------------------------------- title

    /// <summary>The first letter of each run of letters is upper case and the rest lower case, whatever came before.</summary>
    [Theory]
    [InlineData("hELLO wORLD", "Hello World")]
    [InlineData("abc", "Abc")]
    [InlineData("ABC", "Abc")]
    [InlineData("a1b", "A1B")]
    [InlineData("it's", "It'S")]
    [InlineData("", "")]
    [InlineData("  x  y ", "  X  Y ")]
    [InlineData("o'neil-smith", "O'Neil-Smith")]
    public void Title(string input, string expected) => Assert.Equal(expected, Val($"'{input.Replace("'", "\\'")}'.title()"));

    // ---------------------------------------------------------------- replace

    /// <summary>The count limits replacements exactly, with zero meaning none and negative meaning all.</summary>
    [Theory]
    [InlineData("'aaa'.replace('a', 'b', 0)", "aaa")]
    [InlineData("'aaa'.replace('a', 'b', 1)", "baa")]
    [InlineData("'aaa'.replace('a', 'b', 2)", "bba")]
    [InlineData("'aaa'.replace('a', 'b', 3)", "bbb")]
    [InlineData("'aaa'.replace('a', 'b', 99)", "bbb")]
    [InlineData("'aaa'.replace('a', 'b', -1)", "bbb")]
    [InlineData("'aaa'.replace('a', 'b')", "bbb")]
    [InlineData("'abc'.replace('', '-')", "-a-b-c-")]
    [InlineData("'abc'.replace('', '-', 2)", "-a-bc")]
    [InlineData("'abc'.replace('', '-', 0)", "abc")]
    [InlineData("''.replace('', 'x')", "x")]
    [InlineData("'abc'.replace('d', 'x')", "abc")]
    [InlineData("'abab'.replace('ab', '')", "")]
    [InlineData("'aXbXc'.replace('X', 'YY', 1)", "aYYbXc")]
    public void Replace(string expression, string expected) => Assert.Equal(expected, Val(expression));

    /// <summary>The tail after the last replacement is kept, and nothing is added when the match ends the string.</summary>
    [Fact]
    public void ReplaceKeepsTheTail()
    {
        Assert.Equal("xb", Val("'ab'.replace('a', 'x')"));
        Assert.Equal("bx", Val("'ba'.replace('a', 'x')"));
        Assert.Equal("x", Val("'a'.replace('a', 'x')"));
        Assert.Equal("-a-", Val("'a'.replace('', '-', 2)"));
    }

    // ---------------------------------------------------------------- split and rsplit

    /// <summary>The maximum number of splits is respected exactly, from either end, with and without a separator.</summary>
    [Theory]
    [InlineData("'a,b,c'.split(',', 0)", "[\"a,b,c\"]")]
    [InlineData("'a,b,c'.split(',', 1)", "[\"a\", \"b,c\"]")]
    [InlineData("'a,b,c'.split(',', 2)", "[\"a\", \"b\", \"c\"]")]
    [InlineData("'a,b,c'.split(',', 9)", "[\"a\", \"b\", \"c\"]")]
    [InlineData("'a,b,c'.rsplit(',', 0)", "[\"a,b,c\"]")]
    [InlineData("'a,b,c'.rsplit(',', 1)", "[\"a,b\", \"c\"]")]
    [InlineData("'a,b,c'.rsplit(',', 2)", "[\"a\", \"b\", \"c\"]")]
    [InlineData("' a b  c '.split(None, 0)", "[\"a b  c \"]")]
    [InlineData("' a b  c '.split(None, 1)", "[\"a\", \"b  c \"]")]
    [InlineData("' a b  c '.split(None, 2)", "[\"a\", \"b\", \"c \"]")]
    [InlineData("' a b  c '.rsplit(None, 0)", "[\" a b  c\"]")]
    [InlineData("' a b  c '.rsplit(None, 1)", "[\" a b\", \"c\"]")]
    [InlineData("' a b  c '.rsplit(None, 2)", "[\" a\", \"b\", \"c\"]")]
    [InlineData("' a b  c '.rsplit(None, 9)", "[\"a\", \"b\", \"c\"]")]
    [InlineData("''.rsplit()", "[]")]
    [InlineData("'   '.rsplit()", "[]")]
    [InlineData("'abc'.rsplit()", "[\"abc\"]")]
    [InlineData("' abc'.rsplit(None, 1)", "[\"abc\"]")]
    public void SplitMaximums(string expression, string expected) => Assert.Equal(expected, Repr.Of(Val(expression)));

    // ---------------------------------------------------------------- splitlines

    /// <summary>A carriage return followed by a line feed is one break; a lone carriage return is a break of its own.</summary>
    [Theory]
    [InlineData("'a\\r\\nb'.splitlines()", "[\"a\", \"b\"]")]
    [InlineData("'a\\rb'.splitlines()", "[\"a\", \"b\"]")]
    [InlineData("'a\\r'.splitlines()", "[\"a\"]")]
    [InlineData("'a\\r\\n'.splitlines()", "[\"a\"]")]
    [InlineData("'a\\r\\n'.splitlines(True)", "[\"a\\r\\n\"]")]
    [InlineData("'a\\n\\nb'.splitlines()", "[\"a\", \"\", \"b\"]")]
    [InlineData("'a\\r\\r\\nb'.splitlines(True)", "[\"a\\r\", \"\\r\\n\", \"b\"]")]
    [InlineData("''.splitlines()", "[]")]
    public void SplitLines(string expression, string expected) => Assert.Equal(expected, Repr.Of(Val(expression)));

    // ---------------------------------------------------------------- startswith and endswith messages

    /// <summary>The error names the right parameter for each method.</summary>
    [Fact]
    public void AffixErrorsNameTheirParameter()
    {
        Assert.Contains("`prefix`", Fails("x = 'a'.startswith()\n"));
        Assert.Contains("`suffix`", Fails("x = 'a'.endswith()\n"));
        Assert.Contains("`prefix`", Fails("x = 'a'.startswith(1)\n"));
        Assert.Contains("`suffix`", Fails("x = 'a'.endswith(1)\n"));
        Assert.Contains("`prefix`", Fails("x = 'a'.startswith((1,))\n"));
        Assert.Contains("`suffix`", Fails("x = 'a'.endswith((1,))\n"));
        Assert.Contains("string or tuple of strings", Fails("x = 'a'.startswith(1.5)\n"));
        Assert.Equal(true, Val("'abc'.startswith(('x', 'ab'))"));
        Assert.Equal(true, Val("'abc'.endswith(('x', 'bc'))"));
        Assert.Equal(false, Val("'abc'.startswith(('x', 'y'))"));
        Assert.Equal(false, Val("'abc'.endswith(('x', 'y'))"));
    }

    // ---------------------------------------------------------------- join and the memory budget

    /// <summary>The joined size counts both the parts and the separators against the memory budget.</summary>
    [Fact]
    public void JoinCountsSeparators()
    {
        // 3 parts of 100 chars plus 2 separators of 100 chars = 500 chars = 1000 bytes
        var it = New();
        it.MaxAllocatedBytes = 900;
        var e = Assert.Throws<ExecutionLimitException>(() =>
            it.Execute("t", "p = 'x' * 100\ns = 'y' * 100\nr = s.join([p, p, p])\n"));
        Assert.Equal("memory", e.Limit);
        var ok = New();
        ok.MaxAllocatedBytes = 1100;
        ok.Execute("u", "p = 'x' * 100\ns = 'y' * 100\nr = s.join([p, p, p])\n");
    }

    /// <summary>Joining nothing or one string needs no separators and cannot underflow the size.</summary>
    [Fact]
    public void JoinOfNothingAndOne()
    {
        var it = New();
        it.MaxAllocatedBytes = 10;
        it.Execute("t", "a = ''.join([])\nb = 'abcdefghij'.join([])\nc = 'x'.join(['q'])\n");
    }

    // ---------------------------------------------------------------- list indexing

    /// <summary>list.pop, insert and index report the exact bound that was violated.</summary>
    [Fact]
    public void IndexBounds()
    {
        Assert.Equal(new BigInteger(3), Val("[1, 2, 3].pop()"));
        Assert.Equal(new BigInteger(1), Val("[1, 2, 3].pop(0)"));
        Assert.Equal(new BigInteger(3), Val("[1, 2, 3].pop(-1)"));
        Assert.Contains("out of bound", Fails("x = [1, 2, 3].pop(3)\n"));
        Assert.Contains("out of bound", Fails("x = [1, 2, 3].pop(-4)\n"));
        Assert.Contains("Cannot pop from an empty list", Fails("x = [].pop()\n"));
        Assert.Equal("[1, 9, 2]", Repr.Of(New().Execute("t", "l = [1, 2]\nl.insert(1, 9)\n").Globals["l"]));
        Assert.Equal("[9, 1, 2]", Repr.Of(New().Execute("t", "l = [1, 2]\nl.insert(-5, 9)\n").Globals["l"]));
        Assert.Equal("[1, 2, 9]", Repr.Of(New().Execute("t", "l = [1, 2]\nl.insert(5, 9)\n").Globals["l"]));
    }

    // ---------------------------------------------------------------- sets

    /// <summary>issubset is true exactly when no element is outside the other set.</summary>
    [Theory]
    [InlineData("set([1]).issubset(set([1, 2]))", true)]
    [InlineData("set([1, 2]).issubset(set([1]))", false)]
    [InlineData("set([1, 3]).issubset(set([1, 2]))", false)]
    [InlineData("set().issubset(set([1]))", true)]
    [InlineData("set([1]).issubset(set())", false)]
    [InlineData("set([1, 2]).issubset(set([1, 2]))", true)]
    [InlineData("set([1, 2]).issuperset(set([1]))", true)]
    [InlineData("set([1]).issuperset(set([1, 2]))", false)]
    [InlineData("set([1, 2]).isdisjoint(set([3]))", true)]
    [InlineData("set([1, 2]).isdisjoint(set([2]))", false)]
    public void SetRelations(string expression, bool expected) => Assert.Equal(expected, Val(expression));

    /// <summary>Set.add rejects unhashable values before changing anything.</summary>
    [Fact]
    public void SetAddChecksHashability()
    {
        Assert.Contains("unhashable", Fails("s = set()\ns.add([1])\n"));
        Assert.Contains("unhashable", Fails("s = set()\ns.discard([1])\n"));
        Assert.Contains("unhashable", Fails("s = set()\ns.remove([1])\n"));
    }
}
