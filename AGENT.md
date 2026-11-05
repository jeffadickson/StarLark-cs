# starlark-cs: orientation for AI agents and people

This project is its own repository (`dev\starlark-cs`, remote https://github.com/jeffadickson/StarLark-cs). It was
moved out of the old `clipforge` container repo on 2026-11-02. The spec lives in `docs\spec`; `starlark-rust-src`
is an ignored reference copy of https://github.com/facebook/starlark-rust kept as a sibling (`..\starlark-rust-src`).
Paths below are relative to `starlark-cs\` unless stated. Facts here were checked against the tree on 2026-11-05; when
in doubt, the files win.

## 1. What this is

A C# (net10.0) implementation of the Starlark language, split into two libraries:

- `src/Starlark.Syntax` (14 .cs files): a port of the starlark-rust lexer, parser, AST, dialects, validation and
  lint suppressions (`Lexer.cs`, `RawLexer.cs`, `Parser.cs`, `Ast.cs`, `AstModule.cs`, `Dialect.cs`, `Validation.cs`,
  `TypeExprUnpack.cs`, `CodeMap.cs`, `Diagnostics.cs`, ...).
- `src/Starlark.Eval` (28 .cs files at the top level, plus `Futures/`, `TypeCheck/` and `Lib/`): a tree-walking
  interpreter written from the spec, not a port of the Rust VM. Main files: `Interpreter.cs` (entry point: `Execute`,
  `Call<T>`, `Eval`, `Typecheck`, `Register`/`Expose`/`DefineValue`/`Define`, `SourceLoader`, the limits),
  `ExecutionContext.cs` (per-run state), `ExecutionLimits.cs` (step/time/memory budgets), `Resolver.cs`, `Builtins.cs`,
  `Methods.cs`, `Ops.cs`, `StringFormat.cs`, `Values.cs`, `Json.cs`, `TypeValues.cs`/`Ty.cs`/`UserTypes.cs` (typing,
  `record`, `enum`, `struct`), `Interop.cs`/`HostTypes.cs`/`HostValue.cs`/`HostJson.cs` (CLR <-> Starlark conversion),
  `ScriptThread.cs` (Go-style thread API), `StackGuard.cs`, `Utf16String.cs` (the separate `utf16` string type).
  `Futures/` holds `FutureRun.cs`, `StarFuture.cs`, `Isolation.cs`, `FutureBuiltins.cs`; `TypeCheck/` the static checker;
  `Lib/` the bundled `std/approx_math.star` and `std/queue.star`.
- Design decisions: ints are BigInteger; strings default to **UTF-8** elements, as in Go and Rust
  (`Interpreter.StringElements = StringElements.Utf16` gives Java/.NET offsets; the default changed from UTF-16 on
  2026-11-04, see `docs/DEVIATIONS.md`); floats print like Go/Rust.

References (read-only): `..\starlark-rust-src` (snapshot of main, exact commit not determined) and `docs\spec`
(`spec.md` plus `conformance/{go,java,rust}`, `starlark_test.py`, `util.bzl`; copy of bazelbuild/starlark).

### Layout
- `Starlark.slnx`: solution with 23 projects:
  - src: `Starlark.Eval`, `Starlark.Syntax`;
  - tests: `Starlark.Apps.Tests`, `Starlark.Eval.Tests`, `Starlark.Fuzz.Tests`, `Starlark.Syntax.Tests`;
  - apps: `Adventure`, `BuildRules`, `ConfigInspector`, `GameAI`, `PolicyEngine`, `Basic`, `Mandelbrot`,
    `ParallelBuilds`, `RayTracer`, `ReportPipeline`;
  - tools: `CoverageReport`, `Compress`, `Trace`, `CommentCheck`, `PrettyPrint`, `ExampleSync`, `Serve`, `WebCheck`,
    `BuildSite`.
  Not in the .slnx: `tools/Soak`, `tools/MutantReport`, `web/Playground`, `bench/Starlark.Bench`, `bench/Starlark.Profile`.
- `examples/programs/*.star` (122 files): example programs that double as tests (headers `# title:`, `# output:`,
  `# error:`, `# typecheck:`, `# dialect:`); run by `tests/Starlark.Eval.Tests/ExampleProgramTests.cs`.
- `web/Playground`: the interpreter and the BASIC machine compiled to WebAssembly (`wwwroot/index.html`,
  `selftest.html`, `starlark.js`).
- Documentation: `README.md`; `docs/MANUAL.md` (user manual; its marked samples are repeated by
  `tests/Starlark.Eval.Tests/ManualTests.cs`); `docs/site/` (HTML implementation deep dive with live examples, built
  into `site-out/` by `tools/BuildSite`); `docs/DEVIATIONS.md` (deliberate deviations and known gaps);
  `docs/futures-design.md`; `docs/requirements/*.sdoc` (StrictDoc requirements: futures, host_interface, language,
  quality); `docs/comment-specs/` (inputs for comment work on the BASIC sources).
- Generated and git-ignored: `coverage.md`, `coverage.html`, `TestResults/`, `StrykerOutput/`, `site-out/`, `bin/`, `obj/`.
- Everything else, including `Interop.cs` and `stryker-config.json`, is tracked in git.

### Build, test, tools
- Build/test: `dotnet build Starlark.slnx` and `dotnet test Starlark.slnx`. There are no CI files in the repository.
- Site: `dotnet run --project tools/BuildSite -- --check` publishes the playground, assembles `site-out/` and checks
  the playground self test and the deep dive in headless Edge or Chrome (must end with "site check passed").
- Tools (all C# programs under `tools/`; each `Program.cs` starts with a usage comment):
  - `Trace`: requirements-to-tests traceability over `docs/requirements/*.sdoc`.
  - `CommentCheck`: `same <git-ref> <file>...` proves a change to Starlark files touched comments only;
    `coverage` lists uncommented functions.
  - `PrettyPrint`: prints the BASIC interpreter's Starlark source as a PDF.
  - `Compress`: turns on NTFS compression for build and test artifact folders.
  - `Serve`: static localhost file server for the playground and the site (`Serve <folder> [port]`).
  - `WebCheck`: loads a page in headless Edge/Chrome and checks the text it ends with (`--expect TEXT`).
  - `BuildSite`: builds and optionally checks the documentation site (above).
  - `Soak`: hours-long fuzz/property runs in child processes with memory and disk guards.
  - `MutantReport`: summarises a Stryker.NET report (survivors by file and kind).
  - `CoverageReport <results-dir> [--out report.md] [--html report.html] [--min-line N]`: merges Cobertura files,
    exits 1 if line coverage is below N.
  - `ExampleSync <examples-dir> [--write] [file ...]`: prints or rewrites the `# output:` block; a human must review
    every change.
- Benchmarks: `bench/Starlark.Bench` (lex and lex+parse throughput) and `bench/Starlark.Profile` (interpreter workloads).
- Fuzz tests are deterministic: env `STARLARK_FUZZ_ITERATIONS` (default 2000) and `STARLARK_FUZZ_SEED` (default 1).
- Mutation testing: `stryker-config.json` (Stryker.NET 5.0.0 via `dotnet-tools.json`; targets Starlark.Eval with the
  Eval tests; thresholds high 90 / low 75 / break 0).

### Test counts
As counted on 2026-11-05: Syntax.Tests 340, Eval.Tests 1854 (3 of them skipped), Fuzz.Tests 81, Apps.Tests 334; more
than 2,600 in all. The three skipped tests are in `NondeterministicFuturesTests.cs` (`DonePollingCostsSteps`,
`FirstOfRaisesAFailure`, `FirstOfHonoursTheDeadline`), marked "Intermittently aborts the test host when a held worker
outlives the run; under investigation". Re-count with `dotnet test tests/<project> --list-tests` rather than trusting
these numbers.

### Test framework (standing decision)
The user's general preference is NUnit, but for starlark-cs the user explicitly decided to stay on xunit ("stay where
you are"). New tests in this project are written in xunit (xunit 2.9.3; the Fuzz tests also use FsCheck.Xunit 3.3.2 and
Hedgehog 2.0.4 for property tests). Tests carry XML doc comments; keep doing that.

## 2. Requirements and conformance

- Requirements: `docs/requirements/{futures,host_interface,language,quality}.sdoc` (StrictDoc). Each Implemented
  requirement names its tests in a VERIFICATION line; `tools/Trace` checks that they exist.
- The spec: `docs\spec\spec.md`. Deliberate differences: `docs/DEVIATIONS.md` (UTF-8 default, `dict.update(None)` is an
  error, `list.pop` accepts negative indexes, recursion is an error unless `EnableRecursion`, call depth capped at 150
  or 1000 with recursion) plus embedding rules (Interpreter not thread-safe; `StringElements` is thread-static during a
  run; `MaxSteps`, `Timeout`, `MaxAllocatedBytes` and `Cancellation` budgets, where `MaxAllocatedBytes` caps each single
  allocation, not the total).
- Upstream conformance: `tests/Starlark.Eval.Tests/Conformance/{go,java,rust}` hold 11 + 21 + 7 = 39 `.star` files plus
  `util.bzl`, matching `docs\spec\conformance`. `ConformanceTests.cs` follows `starlark_test.py`: chunks split at `---`,
  `### regex` marks an expected error, and the `rust` expectation wins where implementations differ.
- Go suites with a host `asserts` module: `tests/Starlark.Eval.Tests/GoSuites/{float,set}.star` via `GoSuiteTests.cs`.
  Errors that differ only in wording from Go are collected and reported by a separate test, not failed.
- Parser/lexer fidelity to Rust: `tests/Starlark.Syntax.Tests/Goldens/{def,grammar,lexer}` and `Testcases/` (167 corpus
  files).
- Spec examples: `SpecExampleTests.cs`. Type system and checker: `TypeSystemTests.cs`, `TypeChecker*Tests.cs`.
  Manual samples: `ManualTests.cs`.
- There is no automated tracking of upstream drift (no script comparing `starlark-rust-src` or `docs\spec` to the
  copied testdata); tracking is by hand.

## 3. Open work

- The three skipped futures tests above.
- `bytes` supports only what the spec lists (`elems`, indexing, slicing, `+`, `in`, comparisons); no string-like methods
  (`docs/DEVIATIONS.md`).
- `MaxAllocatedBytes` bounds single allocations only; there is no cap on the total memory a run uses.
- Deviations to revisit if strict conformance is wanted: `dict.update(None)` errors (spec says None allowed; Go/Rust
  error), `list.pop` negative indexes.
- Requirements still marked Open in `docs/requirements/*.sdoc` (for example QA-PKG-001, packaging).
- Not determined: which starlark-rust commit the reference copy is.

## History
`git log` is the record. Milestones: initial import, the spec-following interpreter rework, json/typing, the static type
checker, the `utf16` type, example programs with `ExampleSync`, execution limits, futures and host futures, the sample
apps and the BASIC machine, the UTF-8 default (2026-11-04), the WebAssembly playground, and the README, manual and deep
dive for release candidate 1.
