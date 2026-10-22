using System.Globalization;
using System.Numerics;
using Hedgehog;
using Hedgehog.Linq;
using Starlark.Eval;
using Starlark.Syntax;
using Gen = Hedgehog.Linq.Gen;
using Range = Hedgehog.Linq.Range;

namespace Starlark.Fuzz.Tests;

/// <summary>
/// Property-based tests using Hedgehog. Unlike the seeded fuzzers, a failing property is shrunk to a minimal
/// counterexample. Each property states a law of the language and checks it against an independent model.
/// </summary>
public class PropertyTests
{
    private static int Tests => int.TryParse(Environment.GetEnvironmentVariable("STARLARK_PROPERTY_TESTS"), out var n) ? n : 300;

    private static Interpreter New()
    {
        var it = new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true } };
        it.Print = _ => { };
        return it;
    }

    private static void Hold(Property<bool> property) =>
        property.Check(PropertyConfig.Default.WithTests(Tests));

    private static object? Eval(string expr) => New().Eval(expr);

    // ---------------------------------------------------------------- generators

    private static Gen<int> SmallInt => Gen.Int32(Range.LinearInt32(-20, 20));
    private static Gen<List<int>> IntList => SmallInt.List(Range.LinearInt32(0, 12));

    private static string Lit(IEnumerable<int> xs) => "[" + string.Join(", ", xs) + "]";

    /// <summary>A slice bound: None, or an int that may fall well outside the sequence.</summary>
    private static Gen<int?> Bound => Gen.Frequency(
    [
        Tuple.Create(1, Gen.Constant<int?>(null)),
        Tuple.Create(4, SmallInt.Select(i => (int?)i)),
    ]);

    private static string B(int? b) => b is null ? "None" : b.Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The model: Python-style slicing computed independently of the interpreter.</summary>
    private static List<int> ModelSlice(List<int> xs, int? start, int? stop, int step)
    {
        int n = xs.Count;
        var res = new List<int>();
        if (step > 0)
        {
            int lo = Math.Clamp(start is null ? 0 : start.Value < 0 ? start.Value + n : start.Value, 0, n);
            int hi = Math.Clamp(stop is null ? n : stop.Value < 0 ? stop.Value + n : stop.Value, 0, n);
            for (int i = lo; i < hi; i += step) res.Add(xs[i]);
        }
        else
        {
            int lo = Math.Clamp(start is null ? n - 1 : start.Value < 0 ? start.Value + n : start.Value, -1, n - 1);
            int hi = Math.Clamp(stop is null ? -1 : stop.Value < 0 ? stop.Value + n : stop.Value, -1, n - 1);
            for (int i = lo; i > hi; i += step) res.Add(xs[i]);
        }
        return res;
    }

    // ---------------------------------------------------------------- sequences

    [Fact]
    public void SlicingMatchesTheModel() => Hold(
        from xs in IntList.ForAll()
        from start in Bound.ForAll()
        from stop in Bound.ForAll()
        from step in Gen.Int32(Range.LinearInt32(-4, 4)).Where(s => s != 0).ForAll()
        select ModelSlice(xs, start, stop, step).SequenceEqual(
            ((StarList)Eval($"{Lit(xs)}[{B(start)}:{B(stop)}:{step}]")!).Items.Select(x => (int)(BigInteger)x!)));

    [Fact]
    public void ReversingTwiceIsTheIdentity() => Hold(
        from xs in IntList.ForAll()
        select Ops.Equal(Eval($"reversed(reversed({Lit(xs)}))"), Eval(Lit(xs))));

    [Fact]
    public void SortedIsOrderedAndAPermutation() => Hold(
        from xs in IntList.ForAll()
        let sorted = ((StarList)Eval($"sorted({Lit(xs)})")!).Items.Select(x => (int)(BigInteger)x!).ToList()
        select sorted.SequenceEqual(xs.OrderBy(x => x)));

    [Fact]
    public void SortIsStable() => Hold(
        from xs in SmallInt.List(Range.LinearInt32(0, 10)).ForAll()
        let pairs = xs.Select((x, i) => (Key: Math.Abs(x) % 3, Index: i)).ToList()
        let literal = "[" + string.Join(", ", pairs.Select(p => $"({p.Key}, {p.Index})")) + "]"
        let got = ((StarList)Eval($"sorted({literal}, key=lambda p: p[0])")!).Items
            .Select(t => ((int)(BigInteger)((StarTuple)t!).Items[0]!, (int)(BigInteger)((StarTuple)t!).Items[1]!)).ToList()
        select got.SequenceEqual(pairs.OrderBy(p => p.Key).Select(p => (p.Key, p.Index))));

    [Fact]
    public void ConcatenationLengthAddsUp() => Hold(
        from a in IntList.ForAll()
        from b in IntList.ForAll()
        select (BigInteger)Eval($"len({Lit(a)} + {Lit(b)})")! == a.Count + b.Count);

    [Fact]
    public void RepetitionMultipliesLengthAndNegativeCountsAreEmpty() => Hold(
        from xs in IntList.ForAll()
        from n in Gen.Int32(Range.LinearInt32(-3, 6)).ForAll()
        select (BigInteger)Eval($"len({Lit(xs)} * {n})")! == xs.Count * Math.Max(n, 0));

    [Fact]
    public void IndexingNegativeIsFromTheEnd() => Hold(
        from xs in IntList.Where(l => l.Count > 0).ForAll()
        from i in Gen.Int32(Range.LinearInt32(0, 11)).Where(i => true).ForAll()
        let k = i % xs.Count
        select Ops.Equal(Eval($"{Lit(xs)}[{k - xs.Count}]"), Eval($"{Lit(xs)}[{k}]")));

    // ---------------------------------------------------------------- arithmetic

    [Fact]
    public void FloorDivisionAndModuloSatisfyTheirIdentity() => Hold(
        from x in Gen.Int64(Range.LinearInt64(-1_000_000, 1_000_000)).ForAll()
        from y in Gen.Int64(Range.LinearInt64(-1000, 1000)).Where(v => v != 0).ForAll()
        let q = (BigInteger)Eval($"({x}) // ({y})")!
        let r = (BigInteger)Eval($"({x}) % ({y})")!
        select q * y + r == x && (r.IsZero || (r.Sign < 0) == (y < 0)) && BigInteger.Abs(r) < BigInteger.Abs(y));

    [Fact]
    public void IntegerArithmeticMatchesBigInteger() => Hold(
        from a in Gen.Int64(Range.LinearInt64(long.MinValue / 2, long.MaxValue / 2)).ForAll()
        from b in Gen.Int64(Range.LinearInt64(long.MinValue / 2, long.MaxValue / 2)).ForAll()
        select (BigInteger)Eval($"({a}) + ({b})")! == (BigInteger)a + b
            && (BigInteger)Eval($"({a}) * ({b})")! == (BigInteger)a * b
            && (BigInteger)Eval($"({a}) - ({b})")! == (BigInteger)a - b);

    [Fact]
    public void BitwiseNotIsNegationMinusOne() => Hold(
        from x in Gen.Int64(Range.LinearInt64(-1_000_000, 1_000_000)).ForAll()
        select (BigInteger)Eval($"~({x})")! == -(BigInteger)x - 1);

    [Fact]
    public void LeftThenRightShiftRoundTrips() => Hold(
        from x in Gen.Int64(Range.LinearInt64(-1_000_000, 1_000_000)).ForAll()
        from s in Gen.Int32(Range.LinearInt32(0, 70)).ForAll()
        select (BigInteger)Eval($"(({x}) << {s}) >> {s}")! == x);

    [Fact]
    public void ComparisonsAreConsistentWithBigInteger() => Hold(
        from a in Gen.Int64(Range.LinearInt64(-1000, 1000)).ForAll()
        from b in Gen.Int64(Range.LinearInt64(-1000, 1000)).ForAll()
        select (bool)Eval($"({a}) < ({b})")! == a < b
            && (bool)Eval($"({a}) == ({b})")! == (a == b)
            && (bool)Eval($"({a}) >= ({b})")! == a >= b);

    // ---------------------------------------------------------------- strings

    private static Gen<string> Word => Gen.Char('a', 'f').String(Range.LinearInt32(0, 8));

    private static string Q(string s) => "\"" + s + "\"";

    [Fact]
    public void SplitThenJoinIsTheIdentity() => Hold(
        from s in Word.ForAll()
        from sep in Gen.Char('a', 'f').String(Range.Constant(1, 2)).ForAll()
        select Eval($"{Q(sep)}.join({Q(s)}.split({Q(sep)}))") is string joined && joined == s);

    [Fact]
    public void ReplaceMatchesDotNet() => Hold(
        from s in Word.ForAll()
        from old in Gen.Char('a', 'f').String(Range.Constant(1, 2)).ForAll()
        select Eval($"{Q(s)}.replace({Q(old)}, \"ZZ\")") is string r && r == s.Replace(old, "ZZ", StringComparison.Ordinal));

    [Fact]
    public void FindAgreesWithIndexOfAndCountWithNonOverlappingMatches() => Hold(
        from s in Word.ForAll()
        from sub in Gen.Char('a', 'f').String(Range.Constant(1, 2)).ForAll()
        let expectedCount = CountNonOverlapping(s, sub)
        select (BigInteger)Eval($"{Q(s)}.find({Q(sub)})")! == s.IndexOf(sub, StringComparison.Ordinal)
            && (BigInteger)Eval($"{Q(s)}.count({Q(sub)})")! == expectedCount);

    private static int CountNonOverlapping(string s, string sub)
    {
        int n = 0, p = 0;
        while ((p = s.IndexOf(sub, p, StringComparison.Ordinal)) >= 0) { n++; p += sub.Length; }
        return n;
    }

    [Fact]
    public void UpperThenLowerEqualsLower() => Hold(
        from s in Word.ForAll()
        select Eval($"{Q(s)}.upper().lower()") is string r && r == s.ToLowerInvariant());

    [Fact]
    public void StripRemovesExactlyTheEnds() => Hold(
        from core in Word.ForAll()
        from l in Gen.Int32(Range.LinearInt32(0, 3)).ForAll()
        from r in Gen.Int32(Range.LinearInt32(0, 3)).ForAll()
        let padded = new string(' ', l) + core + new string(' ', r)
        select Eval($"{Q(padded)}.strip()") is string s && s == padded.Trim(' '));

    [Fact]
    public void Utf8LengthEqualsByteCountAndUtf16LengthEqualsCodeUnits() => Hold(
        from s in Gen.Item(["a", "é", "世", "😀", "z"]).List(Range.LinearInt32(0, 8)).ForAll()
        let text = string.Concat(s)
        let u16 = new Interpreter { StringElements = StringElements.Utf16 }.Eval($"len({Q(text)})")
        let u8 = new Interpreter { StringElements = StringElements.Utf8 }.Eval($"len({Q(text)})")
        select (BigInteger)u16! == text.Length && (BigInteger)u8! == System.Text.Encoding.UTF8.GetByteCount(text));

    // ---------------------------------------------------------------- containers

    [Fact]
    public void DictPreservesInsertionOrderAndLastWriteWins() => Hold(
        from keys in Gen.Int32(Range.LinearInt32(0, 6)).List(Range.LinearInt32(0, 12)).ForAll()
        let assigns = string.Join("\n", keys.Select((k, i) => $"d[{k}] = {i}"))
        let program = "def f():\n  d = {}\n" + string.Join("", keys.Select((k, i) => $"  d[{k}] = {i}\n")) + "  return d\nresult = f()\n"
        let got = (StarDict)New().Execute("t", program).Globals["result"]!
        let expected = keys.Select((k, i) => (k, i)).GroupBy(p => p.k).Select(g => (g.Key, g.Last().i)).ToList()
        select got.Entries.Select(kv => ((int)(BigInteger)kv.Key!, (int)(BigInteger)kv.Value!)).SequenceEqual(expected));

    [Fact]
    public void SetsHaveNoDuplicatesAndKeepFirstSeenOrder() => Hold(
        from xs in IntList.ForAll()
        let got = ((StarList)Eval($"list(set({Lit(xs)}))")!).Items.Select(x => (int)(BigInteger)x!).ToList()
        select got.SequenceEqual(xs.Distinct()));

    [Fact]
    public void SetOperationsMatchLinq() => Hold(
        from a in IntList.ForAll()
        from b in IntList.ForAll()
        let sa = $"set({Lit(a)})"
        let sb = $"set({Lit(b)})"
        select List(Eval($"list({sa} | {sb})")).SequenceEqual(a.Union(b))
            && List(Eval($"list({sa} & {sb})")).SequenceEqual(a.Distinct().Where(b.Contains))
            && List(Eval($"list({sa} - {sb})")).SequenceEqual(a.Distinct().Where(x => !b.Contains(x))));

    private static List<int> List(object? v) => ((StarList)v!).Items.Select(x => (int)(BigInteger)x!).ToList();

    [Fact]
    public void ListMutationBecomesImpossibleOnceFrozen() => Hold(
        from xs in IntList.ForAll()
        let it = New()
        let m = it.Execute("t", $"xs = {Lit(xs)}\n")
        let frozen = (StarList)m.Globals["xs"]!
        select frozen.Frozen && Throws(() => frozen.Add(1)));

    private static bool Throws(Action a)
    {
        try { a(); return false; }
        catch (StarlarkException) { return true; }
    }

    // ---------------------------------------------------------------- JSON

    /// <summary>JSON-able Starlark source text, built recursively.</summary>
    private static Gen<string> JsonValue(int depth)
    {
        var scalar = Gen.Choice(new Gen<string>[]
        {
            Gen.Constant("None"), Gen.Constant("True"), Gen.Constant("False"),
            Gen.Int32(Range.LinearInt32(-1000, 1000)).Select(i => i.ToString(CultureInfo.InvariantCulture)),
            Gen.Constant("123456789012345678901234567890"),
            Gen.Double(Range.LinearDouble(-1000, 1000)).Select(d => d.ToString("R", CultureInfo.InvariantCulture)),
            Word.Select(Q),
            Gen.Constant("\"quote\\\" back\\\\slash\\n\\ttab\""),
        });
        if (depth <= 0) return scalar;
        var inner = JsonValue(depth - 1);
        return Gen.Frequency(
        [
            Tuple.Create(4, scalar),
            Tuple.Create(1, inner.List(Range.LinearInt32(0, 3)).Select(xs => "[" + string.Join(", ", xs) + "]")),
            Tuple.Create(1, inner.List(Range.LinearInt32(0, 3)).Select(xs =>
                "{" + string.Join(", ", xs.Select((x, i) => $"\"k{i}\": {x}")) + "}")),
        ]);
    }

    [Fact]
    public void JsonDecodeOfEncodeIsTheIdentity() => Hold(
        from v in JsonValue(3).ForAll()
        let r = (StarList)Eval($"(lambda v: [json.decode(json.encode(v)), v])({v})")!
        select Ops.Equal(r[0], r[1]));

    [Fact]
    public void JsonEncodeIsStableUnderReDecoding() => Hold(
        from v in JsonValue(3).ForAll()
        let once = (string)Eval($"json.encode({v})")!
        let twice = (string)Eval($"json.encode(json.decode({Q(once.Replace("\\", "\\\\").Replace("\"", "\\\""))}))")!
        select once == twice);

    // ---------------------------------------------------------------- front end

    // ---------------------------------------------------------------- static vs dynamic types

    /// <summary>Source for an expression of a known type, and that type's annotation.</summary>
    private static readonly (string Annotation, Gen<string> Value)[] Typed =
    [
        ("int", SmallInt.Select(i => i.ToString(CultureInfo.InvariantCulture))),
        ("str", Word.Select(Q)),
        ("bool", Gen.Item(["True", "False"])),
        ("float", Gen.Double(Range.LinearDouble(-9, 9)).Select(d => d.ToString("R", CultureInfo.InvariantCulture))),
        ("list[int]", IntList.Select(Lit)),
        ("list[str]", Word.List(Range.LinearInt32(0, 3)).Select(ws => "[" + string.Join(", ", ws.Select(Q)) + "]")),
        ("dict[str, int]", Gen.Constant("{'a': 1}")),
        ("tuple[int, str]", Gen.Constant("(1, 'a')")),
        ("None", Gen.Constant("None")),
    ];

    private static Gen<(string Annotation, string Value)> TypedValue =>
        Gen.Item(Typed).SelectMany(t => t.Value.Select(v => (t.Annotation, v)));

    [Fact]
    public void CheckerAndRuntimeAgreeOnWhetherACallFitsItsAnnotation() => Hold(
        from param in TypedValue.ForAll()
        from arg in TypedValue.ForAll()
        let program = $"def f(x: {param.Annotation}):\n    pass\n\ndef g():\n    f({arg.Value})\n\ng()\n"
        let it = new Interpreter { Dialect = Dialect.Standard with { EnableTypes = DialectTypes.Enable } }
        let staticOk = it.Typecheck("t", program).Errors.Count == 0
        let dynamicOk = RunsWithoutAnnotationError(it, program)
        // The checker is allowed to be lenient (accept something that fails at run time, e.g. list[int] vs list[str]
        // of an empty list), but it must never reject a program that runs fine.
        select !dynamicOk || staticOk);

    [Fact]
    public void WhatTheRuntimeRejectsForLiteralArgumentsTheCheckerRejectsToo() => Hold(
        from param in TypedValue.ForAll()
        from arg in TypedValue.ForAll()
        let program = $"def f(x: {param.Annotation}):\n    pass\n\ndef g():\n    f({arg.Value})\n\ng()\n"
        let it = new Interpreter { Dialect = Dialect.Standard with { EnableTypes = DialectTypes.Enable } }
        let staticOk = it.Typecheck("t", program).Errors.Count == 0
        let dynamicOk = RunsWithoutAnnotationError(it, program)
        // The converse of the property above. The arguments are concrete literals, so there is no excuse for
        // leniency: if the call fails at run time, the checker must have flagged it before the program ran.
        select !staticOk || dynamicOk);

    private static bool RunsWithoutAnnotationError(Interpreter it, string program)
    {
        try
        {
            it.Execute("t", program);
            return true;
        }
        catch (StarlarkException e) when (e.RawMessage.Contains("does not match the type annotation"))
        {
            return false;
        }
    }

    [Fact]
    public void InferredTypesAreSoundForLiterals() => Hold(
        from v in TypedValue.ForAll()
        let program = $"def f():\n    x = {v.Value}\n"
        let inferred = new Interpreter { Dialect = Dialect.Standard }.Typecheck("t", program).Types["x"]
        let value = Eval(v.Value)
        // A value must be a member of the type the checker infers for the expression that produced it.
        select inferred.Matches(value));

    [Fact]
    public void ParsedIntegerLiteralsHaveTheirValue() => Hold(
        from n in Gen.Int64(Range.LinearInt64(0, long.MaxValue)).ForAll()
        from radix in Gen.Item([10, 16, 8, 2]).ForAll()
        let text = radix switch
        {
            16 => "0x" + n.ToString("x"),
            8 => "0o" + Convert.ToString(n, 8),
            2 => "0b" + Convert.ToString(n, 2),
            _ => n.ToString(CultureInfo.InvariantCulture),
        }
        select (BigInteger)Eval(text)! == n);

    [Fact]
    public void LexerSpansTileTheInputInOrder() => Hold(
        from s in Gen.Char(' ', '~').String(Range.LinearInt32(0, 60)).ForAll()
        let lexemes = Lexer.LexAll(new CodeMap("t", s))
        select Ordered(lexemes, s.Length));

    private static bool Ordered(List<Lexeme> lexemes, int length)
    {
        int pos = 0;
        foreach (var l in lexemes)
        {
            if (l.IsError) continue;
            if (l.Start < pos || l.End < l.Start || l.End > length) return false;
            pos = l.End;
        }
        return true;
    }
}
