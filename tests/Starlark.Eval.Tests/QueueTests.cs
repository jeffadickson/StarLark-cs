using System.Numerics;
using Starlark.Eval;
using Starlark.Syntax;

namespace Starlark.Eval.Tests;

/// <summary>The bundled pure Starlark queue, <c>std/queue.star</c>: order, limits, errors, speed, and agreement with a model.</summary>
public class QueueTests
{
    private const string Load = "load('std/queue.star', 'queue', 'enqueue', 'dequeue', 'peek', 'queue_len', 'queue_empty', 'queue_clear', 'queue_items')\n";

    private static Interpreter New(long steps = 50_000_000) => new()
    {
        Dialect = Dialect.Standard with { EnableTopLevelStmt = true },
        MaxSteps = steps,
    };

    private static object? Run(string body) => New().Execute("t.star", Load + body).Globals["r"];

    private static string Show(object? v) => Repr.Of(v);

    [Fact]
    public void ItemsComeOutInTheOrderTheyWentIn()
    {
        Assert.Equal("[1, 2, 3, 4]", Show(Run("q = queue()\nfor i in range(1, 5):\n    enqueue(q, i)\nr = [dequeue(q) for _ in range(4)]\n")));
    }

    [Fact]
    public void StartingItemsKeepTheirOrder() =>
        Assert.Equal("[\"a\", \"b\", \"c\"]", Show(Run("q = queue(['a', 'b', 'c'])\nr = queue_items(q)\n")));

    [Fact]
    public void MixedPushesAndPopsStayFirstInFirstOut()
    {
        // Interleaving moves items between the two internal lists at different times.
        const string script = "q = queue()\nout = []\nenqueue(q, 1)\nenqueue(q, 2)\nout.append(dequeue(q))\nenqueue(q, 3)\nout.append(dequeue(q))\nenqueue(q, 4)\nenqueue(q, 5)\nout.append(dequeue(q))\nout.append(dequeue(q))\nout.append(dequeue(q))\nr = out\n";
        Assert.Equal("[1, 2, 3, 4, 5]", Show(Run(script)));
    }

    [Fact]
    public void PeekDoesNotRemove() =>
        Assert.Equal("[1, 1, 2, 1]", Show(Run("q = queue([1, 2])\nr = [peek(q), dequeue(q), peek(q), queue_len(q)]\n")));

    [Fact]
    public void LengthAndEmptyTrackTheContents() =>
        Assert.Equal("[True, 0, False, 2, True]", Show(Run("q = queue()\na = queue_empty(q)\nb = queue_len(q)\nenqueue(q, 'x')\nenqueue(q, 'y')\nc = queue_empty(q)\nd = queue_len(q)\nqueue_clear(q)\nr = [a, b, c, d, queue_empty(q)]\n")));

    [Fact]
    public void ItemsDoesNotDisturbTheQueue() =>
        Assert.Equal("[[1, 2, 3], 1, 3]", Show(Run("q = queue([1, 2, 3])\nitems = queue_items(q)\nr = [items, dequeue(q), queue_len(q) + 1]\n")));

    [Fact]
    public void AnyKindOfValueCanBeQueued() =>
        Assert.Equal("[None, [1], {\"a\": 1}, (2,), 1.5]", Show(Run("q = queue([None, [1], {'a': 1}, (2,), 1.5])\nr = queue_items(q)\n")));

    [Fact]
    public void ALimitStopsAnOverfullQueue()
    {
        var e = Assert.Throws<StarlarkException>(() => Run("q = queue(limit = 2)\nenqueue(q, 1)\nenqueue(q, 2)\nenqueue(q, 3)\nr = 0\n"));
        Assert.Contains("queue is full (limit 2)", e.RawMessage);
        // Taking one out makes room again.
        Assert.Equal("[2, 3]", Show(Run("q = queue([1, 2], limit = 2)\ndequeue(q)\nenqueue(q, 3)\nr = queue_items(q)\n")));
    }

    [Fact]
    public void ALimitOfZeroHoldsNothing() =>
        Assert.Contains("queue is full", Assert.Throws<StarlarkException>(() => Run("q = queue(limit = 0)\nenqueue(q, 1)\nr = 0\n")).RawMessage);

    [Theory]
    [InlineData("queue(limit = -1)")]
    [InlineData("queue(limit = 'x')")]
    [InlineData("queue(limit = 1.5)")]
    [InlineData("queue(5)")]
    [InlineData("queue([1, 2, 3], limit = 2)")]
    public void BadConstructorArgumentsAreErrors(string call) =>
        Assert.Throws<StarlarkException>(() => Run("r = " + call + "\n"));

    [Fact]
    public void EmptyQueuesCannotBeReadFrom()
    {
        Assert.Contains("queue is empty", Assert.Throws<StarlarkException>(() => Run("r = dequeue(queue())\n")).RawMessage);
        Assert.Contains("queue is empty", Assert.Throws<StarlarkException>(() => Run("r = peek(queue())\n")).RawMessage);
        Assert.Contains("queue is empty", Assert.Throws<StarlarkException>(() => Run("q = queue([1])\ndequeue(q)\nr = dequeue(q)\n")).RawMessage);
    }

    [Theory]
    [InlineData("dequeue({})")]
    [InlineData("dequeue([1])")]
    [InlineData("enqueue(None, 1)")]
    [InlineData("peek('q')")]
    [InlineData("queue_len(3)")]
    [InlineData("queue_empty({'kind': 'x'})")]
    [InlineData("queue_clear([])")]
    [InlineData("queue_items(None)")]
    public void OnlyRealQueuesAreAccepted(string call) =>
        Assert.Contains("want a queue", Assert.Throws<StarlarkException>(() => Run("r = " + call + "\n")).RawMessage);

    [Fact]
    public void QueuesAreIndependent() =>
        Assert.Equal("[[1], [2]]", Show(Run("a = queue([1])\nb = queue([2])\nr = [queue_items(a), queue_items(b)]\n")));

    [Fact]
    public void ABreadthFirstSearchWorksOnAGrid()
    {
        // Shortest path length from corner to corner of an open 8 x 8 grid is 14 steps.
        const string script = @"
def shortest(n):
    seen = {(0, 0): 0}
    q = queue([(0, 0)])
    for _ in range(100000):
        if queue_empty(q):
            break
        x, y = dequeue(q)
        for dx, dy in [(1, 0), (0, 1), (-1, 0), (0, -1)]:
            nx, ny = x + dx, y + dy
            if 0 <= nx and nx < n and 0 <= ny and ny < n and (nx, ny) not in seen:
                seen[(nx, ny)] = seen[(x, y)] + 1
                enqueue(q, (nx, ny))
    return seen[(n - 1, n - 1)]
r = shortest(8)
";
        Assert.Equal(new BigInteger(14), Run(script));
    }

    [Fact]
    public void DrainingIsLinearNotQuadratic()
    {
        // 20,000 items in and out. With list.pop(0) the pops alone would cost about 200 million item moves; here the work is a
        // few operations per item, which fits a step budget far below that.
        var it = New(steps: 3_000_000);
        var m = it.Execute("t.star", Load + "def drain():\n    q = queue()\n    for i in range(20000):\n        enqueue(q, i)\n    total = 0\n    for _ in range(20000):\n        total += dequeue(q)\n    return total\nr = drain()\n");
        Assert.Equal(new BigInteger(199_990_000), m.Globals["r"]);
    }

    [Fact]
    public void ItNeedsNoRecursion()
    {
        // The default dialect forbids recursion; every function above ran without it. Check the refill path as well.
        Assert.Equal("[1, 2, 3]", Show(Run("q = queue()\nenqueue(q, 1)\nenqueue(q, 2)\nenqueue(q, 3)\nr = [dequeue(q), dequeue(q), dequeue(q)]\n")));
    }

    [Fact]
    public void RandomOperationsAgreeWithAListModel()
    {
        // The same random sequence of enqueue, dequeue, peek and length run against the queue and against a plain list.
        for (int seed = 0; seed < 25; seed++)
        {
            var rng = new Random(seed);
            var script = new System.Text.StringBuilder(Load + "q = queue(limit = 40)\nmodel = []\nbad = []\n");
            for (int i = 0; i < 300; i++)
            {
                switch (rng.Next(4))
                {
                    case 0:
                        script.Append($"if len(model) < 40:\n    enqueue(q, {i})\n    model.append({i})\n");
                        break;
                    case 1:
                        script.Append("if model:\n    if dequeue(q) != model.pop(0):\n        bad.append(" + i + ")\n");
                        break;
                    case 2:
                        script.Append("if model:\n    if peek(q) != model[0]:\n        bad.append(" + i + ")\n");
                        break;
                    default:
                        script.Append("if queue_len(q) != len(model) or queue_items(q) != model:\n    bad.append(" + i + ")\n");
                        break;
                }
            }
            script.Append("r = bad\n");
            Assert.Equal("[]", Show(New().Execute("t.star", script.ToString()).Globals["r"]));
        }
    }

    [Fact]
    public void TheTypeCheckerAcceptsLoadsFromThePackage()
    {
        var typed = new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableTypes = DialectTypes.Enable } };
        Assert.Empty(typed.Typecheck("t.star", Load + "def f(xs: list[int]) -> int:\n    q = queue(xs)\n    return dequeue(q)\n").Errors);
    }

    [Fact]
    public void ThePackageIsBundledAndListed()
    {
        Assert.Contains("std/queue.star", StandardLibrary.Packages());
        Assert.NotNull(StandardLibrary.Read("std/queue.star"));
    }

    [Fact]
    public void InternalHelpersCannotBeLoaded() =>
        Assert.Throws<StarlarkException>(() => New().Execute("t.star", "load('std/queue.star', '_check')\n"));
}
