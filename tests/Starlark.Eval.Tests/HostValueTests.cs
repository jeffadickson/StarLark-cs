using System.Numerics;
using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>Host-defined Starlark value types (<see cref="IHostValue"/>).</summary>
public class HostValueTests
{
    private static Interpreter New() => new() { Dialect = Dialect.Standard with { EnableTopLevelStmt = true } };

    /// <summary>An amount of money that supports arithmetic, ordering, hashing, length, indexing and iteration.</summary>
    public sealed class Money(long cents, string currencyCode = "EUR") : IHostValue
    {
        public long Cents { get; } = cents;
        private string currency { get; } = currencyCode;
        public string TypeName => "money";
        public string Repr() => $"money({Cents / 100}.{Cents % 100:D2} {currency})";
        public bool Truthy() => Cents != 0;
        public bool EqualsValue(object? o) => o is Money m && m.Cents == Cents && m.currency == currency;
        public int HashValue() => HashCode.Combine(Cents, currency);
        public int? CompareTo(object? o) => o is Money m ? Cents.CompareTo(m.Cents) : null;
        public BigInteger? Length() => Cents.ToString().Length;

        public bool TryGetAttribute(string name, out object? value)
        {
            value = name switch
            {
                "cents" => new BigInteger(Cents),
                "currency" => currency,
                "doubled" => Interop.Wrap("doubled", () => new Money(Cents * 2, currency)),
                _ => null,
            };
            return name is "cents" or "currency" or "doubled";
        }

        public IEnumerable<string> Attributes() => ["cents", "currency", "doubled"];

        public bool TryBinary(BinOp op, object? other, bool reflected, out object? result)
        {
            result = null;
            switch (op, other)
            {
                case (BinOp.Add, Money m): result = new Money(Cents + m.Cents, currency); return true;
                case (BinOp.Subtract, Money m) when !reflected: result = new Money(Cents - m.Cents, currency); return true;
                case (BinOp.Multiply, BigInteger n): result = new Money(Cents * (long)n, currency); return true;
            }
            return false;
        }

        public bool TryNegate(out object? result)
        {
            result = new Money(-Cents, currency);
            return true;
        }

        public bool TryIndex(object? index, out object? result)
        {
            result = index is BigInteger i ? new BigInteger(Cents.ToString()[(int)i] - '0') : null;
            return result is not null;
        }

        public IEnumerable<object?>? Iterate() => Cents.ToString().Select(c => (object?)new BigInteger(c - '0'));
    }

    private static Module Run(string code)
    {
        var it = New();
        it.DefineValue("price", new Money(1250));
        it.Define("money", (_, args, _) => new Money((long)(BigInteger)args[0]!));
        return it.Execute("t", code);
    }

    /// <summary>The type reports its name, repr and truthiness, and shows its attributes.</summary>
    [Fact]
    public void BehavesAsATypeOfItsOwn()
    {
        var m = Run("t = type(price)\nr = repr(price)\nb = bool(money(0))\ntruth = bool(price)\nd = dir(price)\n");
        Assert.Equal("money", m.Globals["t"]);
        Assert.Equal("money(12.50 EUR)", m.Globals["r"]);
        Assert.Equal(false, m.Globals["b"]);
        Assert.Equal(true, m.Globals["truth"]);
        Assert.Equal("[\"cents\", \"currency\", \"doubled\"]", Repr.Of(m.Globals["d"]));
    }

    /// <summary>Operators and unary minus dispatch to the host type, on either side.</summary>
    [Fact]
    public void OperatorsAreOverloaded()
    {
        var m = Run("a = (price + money(50)).cents\nb = (price - money(50)).cents\nc = (price * 2).cents\nd = (2 * price).cents\ne = (-price).cents\n");
        Assert.Equal(new BigInteger(1300), m.Globals["a"]);
        Assert.Equal(new BigInteger(1200), m.Globals["b"]);
        Assert.Equal(new BigInteger(2500), m.Globals["c"]);
        Assert.Equal(new BigInteger(2500), m.Globals["d"]);
        Assert.Equal(new BigInteger(-1250), m.Globals["e"]);
    }

    /// <summary>An operator the type does not support is the usual error, naming the host type.</summary>
    [Fact]
    public void UnsupportedOperatorsFailClearly()
    {
        var e = Assert.Throws<StarlarkException>(() => Run("x = price + 'a'\n"));
        Assert.Contains("money", e.RawMessage);
        Assert.Throws<StarlarkException>(() => Run("x = 1 - price\n"));
    }

    /// <summary>Equality, ordering, hashing (so it is a dict key and set member), and sorting.</summary>
    [Fact]
    public void EqualityOrderingAndHashing()
    {
        var m = Run("eq = money(5) == money(5)\nne = money(5) != money(6)\nlt = money(5) < money(6)\nd = {money(5): 'a'}\nhit = d[money(5)]\ns = sorted([money(9), money(3)])\nfirst = s[0].cents\n");
        Assert.Equal(true, m.Globals["eq"]);
        Assert.Equal(true, m.Globals["ne"]);
        Assert.Equal(true, m.Globals["lt"]);
        Assert.Equal("a", m.Globals["hit"]);
        Assert.Equal(new BigInteger(3), m.Globals["first"]);
        Assert.Throws<StarlarkException>(() => Run("x = price < 1\n"));
    }

    /// <summary>len(), indexing, iteration and the in operator follow the type's own definitions.</summary>
    [Fact]
    public void SequenceBehaviour()
    {
        var m = Run("n = len(price)\nfirst = price[0]\ndigits = [d for d in price]\nhas = 2 in price\nmissing = 9 in price\n");
        Assert.Equal(new BigInteger(4), m.Globals["n"]);
        Assert.Equal(new BigInteger(1), m.Globals["first"]);
        Assert.Equal("[1, 2, 5, 0]", Repr.Of(m.Globals["digits"]));
        Assert.Equal(true, m.Globals["has"]);
        Assert.Equal(false, m.Globals["missing"]);
    }

    /// <summary>Methods returned from <c>TryGetAttribute</c> are callable.</summary>
    [Fact]
    public void AttributesCanBeMethods() =>
        Assert.Equal(new BigInteger(2500), Run("x = price.doubled().cents\n").Globals["x"]);

    /// <summary>Unknown attributes give the usual error.</summary>
    [Fact]
    public void UnknownAttributeIsAnError() =>
        Assert.Contains("no attribute", Assert.Throws<StarlarkException>(() => Run("price.nope\n")).RawMessage);

    /// <summary>Values pass through <see cref="Interop.ToStar"/> unchanged and survive a trip back to the host.</summary>
    [Fact]
    public void ValuesRoundTripThroughTheHost()
    {
        var money = new Money(7);
        Assert.Same(money, Interop.ToStar(money));
        var m = Run("x = price\n");
        Assert.IsType<Money>(m.Globals["x"]);
    }

    /// <summary>A type without overrides behaves like an opaque object.</summary>
    [Fact]
    public void DefaultsAreOpaque()
    {
        var it = New();
        it.DefineValue("o", new Opaque());
        var m = it.Execute("t", "r = repr(o)\nb = bool(o)\nt = type(o)\n");
        Assert.Equal("<opaque>", m.Globals["r"]);
        Assert.Equal(true, m.Globals["b"]);
        Assert.Throws<StarlarkException>(() => it.Execute("u", "len(o)\n"));
        Assert.Throws<StarlarkException>(() => it.Execute("v", "for x in o:\n    pass\n"));
    }

    private sealed class Opaque : IHostValue
    {
        public string TypeName => "opaque";
    }
}
