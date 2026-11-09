# Starlark for .NET

`starlark-cs` is an implementation of the [Starlark](https://github.com/bazelbuild/starlark) language in C# for .NET 11.
Starlark is a small, Python-like language for configuration and for extending applications: deterministic, hermetic,
and safe to run code you did not write.

```csharp
var it = new Interpreter();
it.Register("add", (int a, int b) => a + b);
var module = it.Execute("main.star", "total = add(2, 3) * 10\n");
// module.Globals["total"] is 50
```

What it adds to the language:

- **Futures**: `parallel_map`, `spawn` and `all_of` run work in parallel and still give deterministic results; host
  functions that return a `Task` can hand the script a future.
- **A static type checker** that knows records, enums, structs, the bundled packages and the C# signatures of host functions.
- **Budgets** for steps, wall-clock time and cancellation, plus a per-allocation memory cap, which no script can catch.
- **Typed interop**: `Register`, `Expose` (with `[Expose]` and `[Hide]`) and `DefineValue`.
- **WebAssembly**: the same library runs in a browser.

It passes the conformance data of the Go, Java and Rust implementations, and its robustness rule is that no input may
crash, hang or exhaust the host process.

## Documentation

- [Manual](docs/MANUAL.md): embedding, host functions, limits, dialects, futures, types, packages, reference tables.
- [Implementation deep dive](docs/site/deep-dive.html), with live examples and a playground that run in the browser (build
  the site first, see below).
- [Deviations](docs/DEVIATIONS.md) from the specification and the other implementations.
- [Futures design](docs/futures-design.md) and the [requirements](docs/requirements) (StrictDoc).

## Build and test

```
dotnet build Starlark.slnx
dotnet test Starlark.slnx
```

Build the documentation site, with the WebAssembly playground, and check it in a headless browser (Edge or Chrome):

```
dotnet run --project tools/BuildSite -- --check
dotnet run --project tools/Serve -- site-out 8766
```

Then open http://localhost:8766/deep-dive.html.

## Layout

| Folder | Contents |
|---|---|
| `src/Starlark.Syntax` | Lexer, parser, syntax tree, dialects (a port of the starlark-rust front end). |
| `src/Starlark.Eval` | Interpreter, values, built-ins, futures, type checker, interop, bundled `std/` packages. |
| `tests/` | Syntax, evaluation, fuzz and property tests, and the sample applications. |
| `apps/` | Ten sample programs, including a TRS-80 Level II BASIC interpreter written in Starlark. |
| `examples/programs` | 122 example programs that double as tests. |
| `web/Playground` | The WebAssembly build. |
| `tools/` | C# helper programs: soak testing, traceability, comment checking, the site builder and checker. |
| `docs/` | Manual, deep dive, design notes, requirements. |
