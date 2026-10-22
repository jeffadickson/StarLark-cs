using Starlark.Syntax;
using static Starlark.Syntax.Tests.TestSupport;

namespace Starlark.Syntax.Tests;

public class DefAndCorpusTests
{
    private static void Fails(string name, string program, Dialect? dialect = null)
    {
        var e = Assert.Throws<StarlarkSyntaxException>(() => AstModule.Parse("test.star", program, dialect ?? All));
        AssertGolden("def", name, $"Program:\n{program}\n\nError: {e.Message}\n");
    }

    private static void Passes(string program) => AstModule.Parse("test.star", program, All);

    [Fact]
    public void ParamsUnpack()
    {
        Fails("dup_name", "def test(x, y, x): pass");
        Fails("pos_after_default", "def test(x=1, y): pass");
        Fails("default_after_kwargs", "def test(**kwargs, y=1): pass");
        Fails("args_args", "def test(*x, *y): pass");
        Fails("kwargs_args", "def test(**x, *y): pass");
        Fails("kwargs_kwargs", "def test(**x, **y): pass");
        Passes("def test(x, y, z=1, *args, **kwargs): pass");
    }

    [Fact]
    public void ParamsNoArgs()
    {
        Fails("star_star", "def test(*, *): pass");
        Fails("normal_after_default", "def test(x, y=1, z): pass");
        Passes("def test(*args, x): pass");
        Passes("def test(*args, x=1): pass");
        Passes("def test(*args, x, y=1): pass");
        Passes("def test(x=1, *args, y): pass");
        Passes("def test(*args, x, y=1, z): pass");
        Passes("def test(*, x, y=1, z): pass");
    }

    [Fact]
    public void StarPlacement()
    {
        Fails("star_cannot_be_last", "def test(x, *): pass");
        Fails("star_then_args", "def test(x, *, *args): pass");
        Fails("star_then_kwargs", "def test(x, *, **kwargs): pass");
    }

    [Fact]
    public void PositionalOnly()
    {
        Passes("def test(x, /): pass");
        Fails("positional_only_cannot_be_first", "def test(/, x): pass");
        Fails("slash_slash", "def test(x, /, y, /): pass");
        Fails("slash_after_star", "def test(x, *, y, /): pass");
        Fails("slash_after_args", "def test(x, *args, /): pass");
        Fails("slash_after_kwargs", "def test(x, **kwargs, /): pass");
    }

    [Fact]
    public void DialectGatedParameters()
    {
        Fails("named_only_in_standard_dialect_def", "def test(*, x): pass", Dialect.Standard);
        Fails("named_only_in_standard_dialect_lambda", "lambda *, x: 17", Dialect.Standard);
        Fails("positional_only_in_standard_dialect_def", "def test(/, x): pass", Dialect.Standard);
        Fails("positional_only_in_standard_dialect_lambda", "lambda /, x: 17", Dialect.Standard);
    }

    [Fact]
    public void DefParamsIndices()
    {
        var def = Assert.IsType<DefStmt>(ParseAst("def f(a, b, /, c, d=1, *args, e, f=2, **kw): pass").Statement);
        var p = DefParams.Unpack(def.Parameters, new CodeMap("t", ""));
        Assert.Equal(new DefParamIndices(NumPositional: 4, NumPositionalOnly: 2, Args: 4, Kwargs: 7), p.Indices);
        Assert.Equal(8, p.Params.Count);
        Assert.Equal(DefRegularParamMode.PosOnly, p.Params[0].Mode);
        Assert.Equal(DefRegularParamMode.PosOrName, p.Params[2].Mode);
        Assert.Equal(DefRegularParamMode.NameOnly, p.Params[5].Mode);
        Assert.Equal(5..7, p.Indices.NamedOnly(p.Params.Count));
    }

    // ---------------------------------------------------------------- real-world corpus

    public static IEnumerable<object[]> CorpusFiles() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Testcases"), "*.star")
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => new object[] { Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void CorpusFileParses(string file)
    {
        string content = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Testcases", file));
        var ast = ParseAst(content);

        // The printed AST must itself parse, to the same printed AST.
        string printed = ast.Statement.ToString();
        Assert.Equal(printed, Parse(printed));

        // Lexing must also satisfy token position invariants and CRLF-independence.
        LexTokens(content);
    }
}
