using System.Numerics;
using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>Automatic conversion between .NET objects and Starlark values, and typed host registration.</summary>
public class InteropTests
{
    private static Interpreter New() => new() { Dialect = Dialect.Standard with { EnableTopLevelStmt = true } };

    public sealed record Address(string City, int PostCode);

    public sealed class Customer
    {
        public string Name { get; set; } = "";
        public int LoyaltyPoints { get; set; }
        public Address Home { get; set; } = new("", 0);
        public List<string> Tags { get; set; } = [];
    }

    public enum Tier { Basic, Gold }

    public sealed record Order(string Id, decimal Total, Tier Tier, IReadOnlyList<string> Items);

    [Fact]
    public void SnakeCaseHandlesWordsAndAcronyms()
    {
        Assert.Equal("max_retry_count", Interop.SnakeCase("MaxRetryCount"));
        Assert.Equal("http_server", Interop.SnakeCase("HTTPServer"));
        Assert.Equal("id", Interop.SnakeCase("Id"));
        Assert.Equal("x2_value", Interop.SnakeCase("X2Value"));
    }

    [Fact]
    public void ObjectsBecomeStructsWithSnakeCaseFields()
    {
        var it = New();
        it.DefineValue("customer", new Customer { Name = "Ada", LoyaltyPoints = 7, Home = new("Oslo", 150), Tags = ["vip", "early"] });
        it.Print = _ => { };
        var m = it.Execute("t", "result = [customer.name, customer.loyalty_points, customer.home.city, customer.tags]\n");
        Assert.Equal("[\"Ada\", 7, \"Oslo\", [\"vip\", \"early\"]]", Repr.Of(m.Globals["result"]));
    }

    [Fact]
    public void ScriptsCannotChangeTheHostObject()
    {
        var c = new Customer { Name = "Ada" };
        var it = New();
        it.DefineValue("customer", c);
        var ex = Assert.Throws<StarlarkException>(() => it.Execute("t", "customer.name = 'Eve'\n"));
        Assert.Equal("Ada", c.Name);
        Assert.NotNull(ex);
    }

    [Fact]
    public void TypedFunctionsNeedNoArgumentHandling()
    {
        var it = New();
        it.Register("add", (int a, int b) => a + b);
        it.Register("greet", (string name, string greeting = "Hello") => $"{greeting}, {name}");
        var m = it.Execute("t", "x = add(2, b = 3)\ng = greet('Ada')\nh = greet('Ada', greeting = 'Hi')\n");
        Assert.Equal(new BigInteger(5), m.Globals["x"]);
        Assert.Equal("Hello, Ada", m.Globals["g"]);
        Assert.Equal("Hi, Ada", m.Globals["h"]);
    }

    [Fact]
    public void WrongArgumentTypesAreScriptErrors()
    {
        var it = New();
        it.Register("add", (int a, int b) => a + b);
        Assert.Contains("Cannot convert a value of type 'string'", Assert.Throws<StarlarkException>(() => it.Execute("t", "add('x', 1)\n")).RawMessage);
        Assert.Contains("Missing parameter `b`", Assert.Throws<StarlarkException>(() => it.Execute("u", "add(1)\n")).RawMessage);
        Assert.Contains("Unexpected parameter named `z`", Assert.Throws<StarlarkException>(() => it.Execute("v", "add(1, z = 2)\n")).RawMessage);
        Assert.Contains("does not fit in Int32", Assert.Throws<StarlarkException>(() => it.Execute("w", "add(1 << 40, 1)\n")).RawMessage);
    }

    [Fact]
    public void HostExceptionsBecomeScriptErrorsAndLimitsPassThrough()
    {
        var it = New();
        it.Register("boom", () => { if (true) throw new InvalidOperationException("nope"); return 0; });
        Assert.Contains("Host function", Assert.Throws<StarlarkException>(() => it.Execute("t", "boom()\n")).RawMessage);
    }

    [Fact]
    public void ExposedObjectsGetLiveMethods()
    {
        var log = new Logger();
        var it = New();
        it.Expose("log", log);
        it.Execute("t", "log.write('a')\nlog.write('b', level = 2)\nn = log.count()\n");
        Assert.Equal(["a:1", "b:2"], log.Lines);
    }

    public sealed class Logger
    {
        public List<string> Lines { get; } = [];
        public void Write(string message, int level = 1) => Lines.Add($"{message}:{level}");
        public int Count() => Lines.Count;
    }

    [Fact]
    public void ScriptValuesBindToRecordsAndClasses()
    {
        var it = New();
        var m = it.Execute("t", "o = struct(id = 'A1', total = 12.5, tier = 'Gold', items = ['x', 'y'])\nd = {'city': 'Oslo', 'post_code': 150}\n");
        var order = (Order)Interop.FromStar(m.Globals["o"], typeof(Order))!;
        Assert.Equal(new Order("A1", 12.5m, Tier.Gold, ["x", "y"]).Id, order.Id);
        Assert.Equal(12.5m, order.Total);
        Assert.Equal(Tier.Gold, order.Tier);
        Assert.Equal(["x", "y"], order.Items);
        var addr = (Address)Interop.FromStar(m.Globals["d"], typeof(Address))!;
        Assert.Equal(new Address("Oslo", 150), addr);
    }

    [Fact]
    public void MissingFieldsAreReported()
    {
        var it = New();
        var m = it.Execute("t", "d = {'city': 'Oslo'}\n");
        Assert.Contains("missing field `post_code`", Assert.Throws<StarlarkException>(() => Interop.FromStar(m.Globals["d"], typeof(Address))).RawMessage);
    }

    [Fact]
    public void CollectionsConvertBothWays()
    {
        var star = Interop.ToStar(new Dictionary<string, int[]> { ["a"] = [1, 2] });
        Assert.Equal("{\"a\": [1, 2]}", Repr.Of(star));
        var back = (Dictionary<string, List<long>>)Interop.FromStar(star, typeof(Dictionary<string, List<long>>))!;
        Assert.Equal([1L, 2L], back["a"]);
        var natural = (Dictionary<object, object?>)Interop.FromStarNatural(star)!;
        Assert.IsType<List<object?>>(natural["a"]);
    }

    [Fact]
    public void NullablesAndPrimitivesRoundTrip()
    {
        Assert.Null(Interop.FromStar(null, typeof(int?)));
        Assert.Equal(5, Interop.FromStar(new BigInteger(5), typeof(int?)));
        Assert.Throws<StarlarkException>(() => Interop.FromStar(null, typeof(int)));
        Assert.Equal(2.0, Interop.FromStar(new BigInteger(2), typeof(double)));
        Assert.Equal("Gold", Interop.ToStar(Tier.Gold));
        Assert.Equal(new BigInteger(300), Interop.ToStar((byte)200 + 100));
        Assert.Equal(1.5, Interop.ToStar(1.5f));
        Assert.Equal(90.0, Interop.ToStar(TimeSpan.FromMinutes(1.5)));
    }

    [Fact]
    public void CallingScriptFunctionsFromTheHost()
    {
        var it = New();
        var m = it.Execute("t", "def discount(customer, total):\n    if customer.loyalty_points > 5:\n        return total * 0.9\n    return total\n");
        double v = it.Call<double>(m, "discount", new Customer { LoyaltyPoints = 7 }, 100.0);
        Assert.Equal(90.0, v, 6);
        Assert.Throws<StarlarkException>(() => it.Call<int>(m, "nothing"));
    }

    [Fact]
    public void CyclicObjectsAreRejectedNotLooped()
    {
        var a = new Node(); a.Next = a;
        Assert.Contains("too deep", Assert.Throws<StarlarkException>(() => Interop.ToStar(a)).RawMessage);
    }

    public sealed class Node { public Node? Next { get; set; } }
}
