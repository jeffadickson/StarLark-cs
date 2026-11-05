# Starlark for .NET: manual

This manual covers using `starlark-cs`: running Starlark programs, embedding the interpreter in a .NET application, the
language extensions it adds, and its limits. For how it is built inside, read the
[implementation deep dive](site/deep-dive.html). For the places where it deliberately differs from other Starlark
implementations, see [DEVIATIONS.md](DEVIATIONS.md).

Each sample marked as a manual sample has a matching test in `tests/Starlark.Eval.Tests/ManualTests.cs` that repeats it
and checks the result. The test copies the code by hand, so a change to a sample here must be made in its test too; a
check only ensures that every marked sample has a test of the same name.

## Contents

1. [What Starlark is](#1-what-starlark-is)
2. [Getting started](#2-getting-started)
3. [Embedding: the Interpreter](#3-embedding-the-interpreter)
4. [Giving scripts host functions and objects](#4-giving-scripts-host-functions-and-objects)
5. [Running untrusted scripts safely](#5-running-untrusted-scripts-safely)
6. [Errors](#6-errors)
7. [Dialects and extensions](#7-dialects-and-extensions)
8. [Futures: parallel work in a deterministic language](#8-futures-parallel-work-in-a-deterministic-language)
9. [Types and the static checker](#9-types-and-the-static-checker)
10. [The standard library packages](#10-the-standard-library-packages)
11. [Strings: UTF-8 and UTF-16](#11-strings-utf-8-and-utf-16)
12. [Running in the browser](#12-running-in-the-browser)
13. [The sample applications](#13-the-sample-applications)
14. [Building and testing the project](#14-building-and-testing-the-project)
15. [Reference: interpreter settings](#15-reference-interpreter-settings)

## 1. What Starlark is

Starlark is a small, Python-like language designed for configuration and for extending applications. Bazel uses it for
build files. The language guarantees properties that make it safe to run code you did not write:

- **Deterministic.** The same program with the same inputs always gives the same result. There is no clock, no random
  numbers, no file or network access, unless the host provides them.
- **Hermetic.** A script can only reach the values and functions the host gives it.
- **Frozen after loading.** When a module finishes running, its values are frozen (immutable), so several threads can
  share a loaded module safely.
- **No recursion, no `while`.** Every loop iterates over something finite, which makes runaway programs easy to bound.

`starlark-cs` implements the [Starlark specification](spec/spec.md) in C# for .NET 10. It passes the official
conformance data of the Go, Java and Rust implementations (with the wording differences recorded in the test harness).

## 2. Getting started

The library is two assemblies: `Starlark.Syntax` (lexer, parser, syntax tree) and `Starlark.Eval` (interpreter, type
checker, standard library). Reference both from a project:

```xml
<ItemGroup>
  <ProjectReference Include="path\to\starlark-cs\src\Starlark.Eval\Starlark.Eval.csproj" />
  <ProjectReference Include="path\to\starlark-cs\src\Starlark.Syntax\Starlark.Syntax.csproj" />
</ItemGroup>
```

The smallest program that runs a script:

<!-- manual-sample: hello -->
```csharp
var it = new Interpreter();
var printed = new List<string>();
it.Print = printed.Add;
it.Execute("hello.star", "def greet(name):\n    return 'hello, ' + name\nprint(greet('world'))\n");
// printed: ["hello, world"]
```

`Execute` parses, resolves and runs the source, and returns the module. Read the module's globals to get results back:

<!-- manual-sample: globals -->
```csharp
var it = new Interpreter();
var module = it.Execute("config.star", "replicas = 3\nname = 'web-' + str(replicas)\n");
var name = (string)module.Globals["name"]!;      // "web-3"
```

Starlark values are ordinary CLR objects: `int` is `System.Numerics.BigInteger`, `float` is `double`, `string` is `string`,
`bool` is `bool`, `None` is `null`. Lists, dicts, tuples and sets are `StarList`, `StarDict`, `StarTuple` and `StarSet`.

To call a Starlark function from C#, use `Call<T>`, which converts the arguments and the result:

<!-- manual-sample: call -->
```csharp
var it = new Interpreter();
var module = it.Execute("price.star", "def price(qty, unit):\n    return qty * unit\n");
int total = it.Call<int>(module, "price", 4, 25);   // 100
```

## 3. Embedding: the Interpreter

An `Interpreter` holds configuration (the dialect, limits, the host functions), the cache of modules loaded with `load()`
(see below) and the statistics of the last run (`LastRunStats`). Each call to `Execute` gets a fresh execution context:
steps, deadline and call depth start again. A frozen module returned by `Execute` can be called from several
threads at once; one `Interpreter` should still be configured from one thread.

### The top level

By default the top level of a file may only contain definitions, assignments and `load` statements, as the specification
says. Most embedding hosts want scripts with statements at the top level (`for` loops, `if`, `print`), so they set
`EnableTopLevelStmt`:

<!-- manual-sample: toplevel -->
```csharp
var it = new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true } };
var printed = new List<string>();
it.Print = printed.Add;
it.Execute("loop.star", "for i in range(3):\n    print(i * i)\n");
// printed: ["0", "1", "4"]
```

### Loading other files

`load("name.star", "symbol")` asks the interpreter's `SourceLoader` for the text of `name.star`. The host decides what a
name means: a file next to the script, an embedded resource, a database row. Bundled `std/...` packages are found
without a loader (see [section 10](#10-the-standard-library-packages)).

<!-- manual-sample: load -->
```csharp
var files = new Dictionary<string, string>
{
    ["lib.star"] = "def double(x):\n    return 2 * x\n",
};
var it = new Interpreter { SourceLoader = id => files.GetValueOrDefault(id) };
var module = it.Execute("main.star", "load('lib.star', 'double')\nresult = double(21)\n");
// module.Globals["result"] is 42
```

A loaded module is run once per `Interpreter` and cached for that interpreter's lifetime: later `Execute` calls reuse the
first result, even if `SourceLoader` would now return different text. Create a new `Interpreter` to load fresh source. A
load that fails is not cached, and a cycle of loads is an error.

## 4. Giving scripts host functions and objects

A script can only do what the host lets it. There are three ways to give it more.

**`Register`** predeclares a typed .NET function. Arguments are converted by parameter type, by position or by name (the
C# parameter name, or its snake_case form), and C# default values are honoured:

<!-- manual-sample: register -->
```csharp
var it = new Interpreter();
it.Register("add", (int a, int b) => a + b);
it.Register("greet", (string name, string greeting = "hello") => $"{greeting}, {name}");
var m = it.Execute("t.star", "x = add(2, 3)\ny = greet('ada', greeting = 'hi')\n");
// m.Globals["x"] is 5, m.Globals["y"] is "hi, ada"
```

**`Expose`** predeclares a host object as a struct: its public properties become fields and its public methods become
callable. Mark members with `[Expose]` to show only those, or with `[Hide]` to hide one:

<!-- manual-sample: expose -->
```csharp
public sealed class Inventory
{
    private readonly Dictionary<string, int> _stock = new() { ["apple"] = 3 };
    [Expose] public int Count(string item) => _stock.GetValueOrDefault(item);
    [Expose] public string Owner { get; init; } = "shop";
    public void Wipe() => _stock.Clear();          // not marked, so scripts cannot call it
}

var it = new Interpreter();
it.Expose("inventory", new Inventory());
var m = it.Execute("t.star", "n = inventory.count('apple')\nwho = inventory.owner\n");
// m.Globals["n"] is 3; calling inventory.wipe() would be an error
```

**`DefineValue`** predeclares any .NET value, converted to Starlark (lists, dictionaries, records and so on).

Host functions are visible to the static type checker: the C# signature becomes the Starlark signature, so
`add("two", 3)` is reported before the script runs (see [section 9](#9-types-and-the-static-checker)).

## 5. Running untrusted scripts safely

A Starlark script cannot touch the file system, the network or the clock unless you give it a function that does. What
it can still do is use time and memory. Set budgets for both:

<!-- manual-sample: limits -->
```csharp
var it = new Interpreter
{
    Dialect = Dialect.Standard with { EnableTopLevelStmt = true },
    MaxSteps = 1_000_000,                       // abstract computation steps
    Timeout = TimeSpan.FromSeconds(2),          // wall-clock time
    MaxAllocatedBytes = 64L * 1024 * 1024,      // largest single object a script may build
};
try
{
    it.Execute("spin.star", "def spin():\n    for i in range(1000000000):\n        pass\nspin()\n");
}
catch (ExecutionLimitException e)
{
    // e.Limit is "steps"
}
```

`MaxAllocatedBytes` is a per-allocation cap, not a total: it is checked before one operation (repetition, concatenation,
`join`) builds a string, list, tuple or bytes value larger than the limit. It does not bound the sum of all a run
allocates, so a script can still use a lot of memory in many smaller pieces; combine it with `MaxSteps`, `Timeout` and a
process-level memory cap.

`ExecutionLimitException.Limit` names the budget that ran out (`steps`, `time`, `memory`, or a futures limit such as
`tasks` or `host-operations`). A script cannot catch it. `Cancellation` takes a `CancellationToken` for host-driven
cancellation, which ends the run with `OperationCanceledException`.

The project's robustness rule (requirement QA-ROB-001) is that **no input may crash, hang or exhaust the host process**:
every user-input path is fuzzed, exhaustively tested over small alphabets, and soak-tested for hours under a memory cap.
Deep nesting, cyclic values and huge numbers end in a Starlark error, never a stack overflow.

## 6. Errors

Three exception types reach the host:

| Exception | When |
|---|---|
| `StarlarkSyntaxException` | The source does not parse. |
| `StarlarkException` | An unresolved name (reported before the program runs), or a runtime error: a failed `fail()`, a type error, a missing key, recursion. |
| `ExecutionLimitException` | A budget ran out (section 5). |

`StarlarkException.Message` includes a source snippet with the failing line marked; `RawMessage` is the bare text, and
`Span` and `CodeMap` give the location for tools:

<!-- manual-sample: errors -->
```csharp
var it = new Interpreter();
try
{
    it.Execute("bad.star", "d = {'a': 1}\nx = d['b']\n");
}
catch (StarlarkException e)
{
    // e.RawMessage mentions the missing key "b"; e.Message also shows bad.star:2 with the line marked
}
```

## 7. Dialects and extensions

`Dialect` switches language features on and off. `Dialect.Standard` is the specification; build other dialects with
`Dialect.Standard with { ... }` (`Dialect.Extended`, which enables keyword-only arguments, types and top-level
statements, is deprecated). The switches:

| Switch | Default | What it allows |
|---|---|---|
| `EnableTopLevelStmt` | off | Statements (`for`, `if`, expressions) at the top level of a file. |
| `EnableTypes` | `Disable` | Type annotations, `record`, `enum`, `field`, `isinstance` with types, `typing.*`. |
| `EnableFStrings` | off | `f"..."` strings. |
| `EnableKeywordOnlyArguments` | off | `def f(a, *, b)`. |
| `EnablePositionalOnlyArguments` | off | `def f(a, /, b)`. |
| `EnableRecursion` | off | A function may call itself (Go's `option:recursion`); the depth cap rises from 150 to 1000, but the thread's remaining stack can end a run sooner. |
| `EnableFutures` | off | `parallel_map`, `spawn`, `all_of` (section 8). |
| `EnableNondeterministicFutures` | off | `f.done()` and `first_of` (results then depend on timing). |
| `EnableDef`, `EnableLambda`, `EnableLoad` | on | Turn off `def`, `lambda` or `load` for very restricted configuration files. |

Besides the dialect, the library adds a handful of built-ins beyond the specification: `json` (with `encode_indent`
and `indent`), `struct`, `chr`/`ord`, `string.codepoints()` and the Go compatibility surface, the elementary math
functions (`sin` ... `isinf`, section 10), and the `utf16` string type.

## 8. Futures: parallel work in a deterministic language

Starlark is single-threaded by design. `starlark-cs` adds **futures**, which let a script run work in parallel without
giving up determinism. Enable them with `EnableFutures`:

<!-- manual-sample: futures -->
```csharp
var it = new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableFutures = true } };
var printed = new List<string>();
it.Print = printed.Add;
it.Execute("par.star", """
    def score(word):
        return len(word) * 10
    print(parallel_map(score, ["fig", "apple", "kiwi"]))
    f = spawn(score, "banana")
    print(f.get())
    """);
// printed: ["[30, 50, 40]", "60"]
```

The script-visible API:

| Call | Result |
|---|---|
| `parallel_map(fn, items)` | A list of `fn(item)`, **in input order**, computed in parallel. |
| `spawn(fn, *args, **kwargs)` | A future. The work starts at once. |
| `f.get()` | Waits for the result, or raises the task's error with its original location. |
| `all_of(futures)` | Waits for all, returns results in argument order; if several failed, the **lowest-index** error wins. |

How the result stays deterministic:

- **Tasks cannot see the caller's mutable state.** Arguments are deep-copied (aliasing between arguments is preserved), so
  `spawn(f, xs)` followed by `xs.append(...)` in the caller does not race. A function that captures a running function's
  variables cannot be spawned at all; pass the values as arguments instead.
- **Results are frozen.** A task's return value is immutable when the caller receives it.
- **Output is ordered.** What a task prints is buffered and appended when the future is collected, in program order.
- **No hidden scheduling.** Tasks cannot spawn tasks, so there is no wait graph and no deadlock. A future that is never
  collected is an error, not a silent cancellation.
- **Budgets divide predictably.** Each task gets a fixed slice of the remaining step budget.

**Host operations as futures.** A host function that returns a `Task` can be registered with `asFuture: true`. A script
call then starts the operation and returns a future at once, so a script can have several slow operations (HTTP calls,
builds, queries) in flight and wait for them together. The number in flight is capped by `FutureLimits.MaxHostOperations`:

<!-- manual-sample: hostfutures -->
```csharp
var it = new Interpreter
{
    Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableFutures = true },
    FutureLimits = new FutureLimits { MaxHostOperations = 4 },
};
it.Register("fetch", async (string key) => { await Task.Delay(50); return key.ToUpperInvariant(); },
            threadSafe: true, asFuture: true);
var m = it.Execute("io.star", "fs = [fetch(k) for k in ['a', 'b', 'c']]\nr = all_of(fs)\n");
// m.Globals["r"] is ["A", "B", "C"]; the three waits overlap, so this takes about 50 ms, not 150 ms
```

The limits (`FutureLimits`) default to the "untrusted host" profile: 4 tasks at once, 1,000 uncollected futures,
10,000 spawns per run, 8 host operations in flight. On a platform without threads (browser WebAssembly), tasks run
inline on the calling thread: results, errors and output are identical, only the overlap is lost
(`FutureLimits.RunInline`).

The full design, with the review findings that shaped it, is in [futures-design.md](futures-design.md).

## 9. Types and the static checker

With `EnableTypes`, functions and variables may carry annotations, checked when the function is called:

<!-- manual-sample: types -->
```csharp
var it = new Interpreter { Dialect = Dialect.Standard with { EnableTopLevelStmt = true, EnableTypes = DialectTypes.Enable } };
var m = it.Execute("t.star", """
    Point = record(x = int, y = int)
    def norm1(p: Point) -> int:
        return abs(p.x) + abs(p.y)
    r = norm1(Point(x = 3, y = -4))
    """);
// m.Globals["r"] is 7; norm1("x") would fail with "does not match the type annotation"
```

The **static checker** finds type errors without running the program, including errors in code that would only run in
rare cases. It knows the built-ins, the bundled packages and the signatures of registered host functions:

<!-- manual-sample: typecheck -->
```csharp
var it = new Interpreter { Dialect = Dialect.Standard with { EnableTypes = DialectTypes.Enable } };
it.Register("add", (int a, int b) => a + b);
var result = it.Typecheck("t.star", "def f() -> str:\n    return add('two', 3)\n");
// result.Errors has two entries: the argument 'two' is not an int, and the int result is not a str
```

Set `TypecheckBeforeExecute = true` to refuse to run any program the checker rejects.

## 10. The standard library packages

Packages bundled with the library are loaded with `load("std/...")`; a host's `SourceLoader` is asked first, so a host can
replace or hide them.

**Elementary math.** The interpreter predeclares fast native functions backed by `System.Math`: `sin`, `cos`, `tan`, `asin`,
`acos`, `atan`, `atan2`, `exp`, `log` (natural, or `log(x, base)`), `log2`, `sqrt`, `pow`, `hypot`, `floor`, `ceil`, `trunc`,
`round`, `degrees`, `radians`, `isnan` and `isinf`. `floor`, `ceil`, `trunc` and `round` return ints (`round` sends halves to
the even neighbour, as Python does). Special values follow the C library: `log(0)` is `-inf`, `sqrt(-1)` is `nan`.

The same functions exist in pure Starlark in `std/approx_math.star`, computed with series and argument reduction, for hosts
that want results that are identical on every platform, or to show how it is done. Loading a name replaces the native one
in that file:

```python
load("std/approx_math.star", "sqrt", "pi")   # this file now uses the Starlark sqrt
```

**Queue.** `std/queue.star` is a first-in first-out queue with amortized constant time (a list's `pop(0)` moves every
element, so draining a list that way is quadratic):

```python
load("std/queue.star", "queue", "enqueue", "dequeue", "queue_empty")

def bfs(start, neighbours):
    seen = {start: 0}
    q = queue([start])
    for _ in range(100000):            # Starlark has no while loop
        if queue_empty(q):
            break
        node = dequeue(q)
        for n in neighbours(node):
            if n not in seen:
                seen[n] = seen[node] + 1
                enqueue(q, n)
    return seen
```

Also available: `queue(items, limit = n)`, `peek`, `queue_len`, `queue_clear` and `queue_items`.

## 11. Strings: UTF-8 and UTF-16

String length, indexing and slicing count **UTF-8 bytes** by default, as Go and Rust do: `len("héllo")` is 6. Indexing or
slicing inside a multi-byte character is an error rather than a broken string. A host that wants .NET's own offsets (UTF-16
code units: `len("héllo")` is 5) sets `StringElements = StringElements.Utf16`. The Level II BASIC sample does this, because
BASIC counts characters.

## 12. Running in the browser

`web/Playground` compiles the same `Starlark.Eval` library to WebAssembly. The page downloads about 1.5 MB (compressed) and
then runs Starlark entirely on the visitor's machine: no server executes code. The [deep dive](site/deep-dive.html) embeds
it, so its examples are live.

```
dotnet publish web/Playground -c Release -o site-out/playground
dotnet run --project tools/Serve -- site-out/playground/wwwroot 8766
```

`tools/WebCheck` loads a page in headless Edge or Chrome and checks what it shows; `selftest.html` runs a fixed set of
programs (futures, recursion limits, the type checker, BASIC) and must report `ALL PASSED`.

## 13. The sample applications

`apps/` holds ten programs that show embedding patterns. Each is run by a test (`tests/Starlark.Apps.Tests`).

| App | Shows |
|---|---|
| `BuildRules` | A Bazel-like rule engine: targets, dependency order, cycle errors with source locations. |
| `PolicyEngine` | Business rules in scripts, step limits, a static type check before running. |
| `ConfigInspector` | Configuration files validated by the checker, with a CI-style exit code. |
| `ReportPipeline` | Data aggregation and JSON output. |
| `GameAI` | Player strategies as scripts, hot reload, deterministic replay, a misbehaving script stopped by its budget. |
| `Adventure` | A text adventure whose whole world and rules are Starlark data and functions. |
| `ParallelBuilds` | Host operations as futures: three builds in flight at once, a capped number in flight. |
| `Mandelbrot`, `RayTracer` | `parallel_map` over image rows; the picture is identical for any number of workers. |
| `Basic` | A TRS-80 Level II BASIC interpreter written entirely in Starlark (about 2,600 lines), with the real `EDIT` command. |

Run one with `dotnet run --project apps/Basic` (an interactive BASIC prompt) or `dotnet run --project apps/Mandelbrot`.

## 14. Building and testing the project

```
dotnet build Starlark.slnx
dotnet test Starlark.slnx
```

The test projects:

| Project | What it covers |
|---|---|
| `Starlark.Syntax.Tests` | Lexer, parser, dialects, a corpus of 167 real-world Bazel and Starlark files. |
| `Starlark.Eval.Tests` | The interpreter: the Go, Java and Rust conformance data, 122 example programs, futures, types, the checker. |
| `Starlark.Fuzz.Tests` | Seeded fuzzers (lexer, parser, interpreter, checker, BASIC) and FsCheck/Hedgehog properties. |
| `Starlark.Apps.Tests` | The sample applications and the BASIC machine. |

Tools under `tools/` are C# programs: `Soak` (hours of fuzzing in child processes with memory and disk guards), `Trace`
(checks every requirement in `docs/requirements` is covered by a test), `CommentCheck` (proves a change touched comments
only), `MutantReport` (summarises Stryker.NET mutation runs), `PrettyPrint`, `Serve` and `WebCheck`.

## 15. Reference: interpreter settings

| Property | Default | Meaning |
|---|---|---|
| `Dialect` | `Dialect.Standard` | Language features (section 7). |
| `Print` | `Console.WriteLine` | Where `print()` goes. |
| `SourceLoader` | none | Resolves `load()` names to source text. |
| `MaxSteps` | 0 (none) | Step budget per run. |
| `Timeout` | none | Wall-clock budget per run. |
| `Cancellation` | none | Host cancellation token. |
| `MaxAllocatedBytes` | 0 (none) | Largest single string, list, tuple or bytes one operation may build; not a total. |
| `MaxCallDepth` | 150 (1000 with recursion) | Nested calls before "Maximum call depth exceeded" ("Starlark stack overflow" with recursion); the thread's remaining stack can end a run sooner. |
| `StringElements` | `Utf8` | String offsets in UTF-8 bytes or UTF-16 code units. |
| `AllowGlobalReassign` | false | Let a file assign a global twice. |
| `TypecheckBeforeExecute` | false | Run the static checker first and refuse programs with errors. |
| `FutureLimits` | untrusted profile | Concurrency, spawn and host-operation caps; `RunInline`. |
| `ThreadName`, `ThreadLocals` | empty | Go-style thread name and locals, visible to host functions through `ScriptThread`. |
| `LastRunStats` | null | After a run that started tasks or host futures: steps, spawns, peak concurrency, host calls; null otherwise. |
