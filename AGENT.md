# starlark-cs: orientation for AI agents and people

Planned repo move: this project is to become its own repository (`dev\starlark-cs`). When that happens,
`starlark-spec` moves to `docs\spec`, and `starlark-rust-src` stays an ignored reference copy of
https://github.com/facebook/starlark-rust. Today all three are siblings inside the `clipforge` git repo
(`..\starlark-spec`, `..\starlark-rust-src`); paths below are relative to `starlark-cs\` unless stated.

## 1. What this is

A C# (net10.0) implementation of the Starlark language, split into two libraries:

- `src/Starlark.Syntax` (15 .cs files): a port of the starlark-rust lexer, parser, AST, dialects, validation and
  lint suppressions (`Lexer.cs`, `RawLexer.cs`, `Parser.cs`, `Ast.cs`, `AstModule.cs`, `Dialect.cs`, `Validation.cs`,
  `TypeExprUnpack.cs`, `CodeMap.cs`, `Diagnostics.cs`, ...).
- `src/Starlark.Eval`: a tree-walking interpreter written from the spec, not a port of the Rust VM. Contents:
  `Interpreter.cs` (entry point: `Execute`, `Print`, `SourceLoader` for `load()`, `StringElements`, `MaxSteps`,
  `Timeout`, `Cancellation`), `Resolver.cs` (static name resolution), `Builtins.cs`, `Methods.cs`, `Ops.cs`,
  `StringFormat.cs`, `Values.cs`, `Json.cs` (json module), `TypeValues.cs`/`Ty.cs`/`UserTypes.cs` (typing: type values,
  annotations, `record`, `enum`, `struct`), `TypeCheck/` (static type checker: `TypeChecker.cs`, `Intersects.cs`,
  `Signatures.cs`), `Utf16String.cs` (the separate `utf16` string type, an extension), `ExecutionLimits.cs`,
  `Interop.cs` (CLR <-> Starlark conversion; currently untracked in git, as is `stryker-config.json`).
- Design decisions recorded in memory notes (2026-10-22): ints are BigInteger; strings default to UTF-16 elements
  (spec K=16; `Interpreter.StringElements = StringElements.Utf8` gives Go/Rust offsets); floats print like Go/Rust.

References (read-only): `..\starlark-rust-src` (snapshot of main, exact commit not determined) and `..\starlark-spec`
(`spec.md` plus `conformance/{go,java,rust}`, `starlark_test.py`, `util.bzl`; copy of bazelbuild/starlark).

### Layout
- `Starlark.slnx`: solution. Projects: `src/Starlark.Eval`, `src/Starlark.Syntax`, `tests/Starlark.Eval.Tests`,
  `tests/Starlark.Syntax.Tests`, `tests/Starlark.Fuzz.Tests`, `tools/CoverageReport`, `tools/ExampleSync`.
  `bench/Starlark.Bench` exists but is NOT in the .slnx.
- `examples/programs/*.star` (90 files): example programs that double as tests (headers `# title:`, `# output:`,
  `# error:`, `# typecheck:`, `# dialect:`); run by `tests/Starlark.Eval.Tests/ExampleProgramTests.cs`.
- `docs/DEVIATIONS.md`: deliberate deviations and known gaps (the only design doc).
- `coverage.md`, `coverage.html`, `TestResults/`: generated, git-ignored.

### Build, test, benchmark
Only these are verifiable from the files; I did not run them.
- Build/test: standard `dotnet build Starlark.slnx` and `dotnet test` (no scripts or CI files found; no README).
- Coverage: `dotnet test --collect:"XPlat Code Coverage"` then
  `CoverageReport <results-dir> [--out report.md] [--html report.html] [--min-line N]` (`tools/CoverageReport/Program.cs`;
  merges Cobertura files, exits 1 if line coverage < N).
- Examples: `ExampleSync <examples-dir> [--write] [file ...]` (`tools/ExampleSync/Program.cs`) prints/rewrites the
  `# output:` block; a human must review every change.
- Benchmark: `dotnet run -c Release --project bench/Starlark.Bench -- <dir-of-.star-files>` (lex and lex+parse only;
  the usage line is in `bench/Starlark.Bench/Program.cs`). Memory note: ~48 MiB/s lex, ~42 MiB/s lex+parse on the corpus.
- Fuzz tests are deterministic: env `STARLARK_FUZZ_ITERATIONS` (default 2000) and `STARLARK_FUZZ_SEED` (default 1).
- Mutation testing: `stryker-config.json` (Stryker.NET, targets Starlark.Eval via the Eval tests; thresholds
  high 90 / low 75 / break 60). No recorded mutation result found.

### Test counts
Not re-run for this document. Last recorded (memory note, 2026-10-22): 622 Eval tests and 277 Syntax tests passing;
both are stale, since later commits added type checker, example, limit, Go-suite and Utf16 tests. Counting
`[Fact]`/`[Theory]` attributes (theories expand to more cases): Eval.Tests 246, Syntax.Tests 110, Fuzz.Tests 50.
Recorded coverage (`coverage.md`, generated 2026-10-27): 94.4% lines (7293/7727), 88.1% branches (5565/6320);
the last commit message says the 96% line target is not yet reached.

### Test framework (standing decision)
The user's general preference is NUnit, but for starlark-cs the user explicitly decided to stay on xunit ("stay where
you are"). New tests in this project are written in xunit (xunit 2.9.3; Hedgehog 2.0.4 is used in the Fuzz tests'
property tests). All test projects use xunit. Tests carry XML doc comments; keep doing that.

## 2. Requirements and conformance

There is no StrictDoc / sdoc requirements file and no README in this project (searched; none found). Requirements are
implicit in these places:
- The spec: `..\starlark-spec\spec.md` (future `docs\spec`).
- `docs/DEVIATIONS.md`: where behaviour intentionally differs (UTF-16 default, `dict.update(None)` is an error,
  `list.pop` accepts negative indexes, recursion is an error with call depth capped at 150) plus embedding rules
  (Interpreter not thread-safe; `StringElements` is thread-static during a run; no memory limit).
- Upstream conformance: `tests/Starlark.Eval.Tests/Conformance/{go,java,rust}` hold 11 + 21 + 7 = 39 `.star` files plus
  `util.bzl`; file names match `..\starlark-spec\conformance` one to one (counts equal, contents not diffed by me).
  `ConformanceTests.cs` follows `starlark_test.py`: chunks split at `---`, `### regex` marks an expected error,
  and the `rust` expectation wins where implementations differ. All chunks are theory cases, no skip list.
- Go suites with a host `asserts` module: `tests/Starlark.Eval.Tests/GoSuites/{float,set}.star` via `GoSuiteTests.cs`.
  Errors that differ only in wording from Go are collected and reported by a separate test, not failed.
- Parser/lexer fidelity to Rust: `tests/Starlark.Syntax.Tests/Goldens/{def,grammar,lexer}` (golden files produced from
  Rust behaviour) and `Testcases/` (167 corpus files; also the bench corpus candidates).
- Spec examples: `SpecExampleTests.cs`. Type system and checker: `TypeSystemTests.cs`, `TypeChecker*Tests.cs`.
- There is no automated tracking of upstream drift (no script comparing `starlark-rust-src` or `starlark-spec` to the
  copied testdata); tracking is by hand.

## 3. Open work (verified)

- TODO/FIXME/NotImplemented/Skip markers in `*.cs`, `*.csproj`, `*.md`, `*.json`: none found. The only "TODO" hit is
  in `docs/DEVIATIONS.md` (spec marks bytes methods as TODO).
- `bytes` supports only what the spec lists (`elems`, indexing, slicing, `+`, `in`, comparisons); no string-like methods
  (`docs/DEVIATIONS.md`).
- No memory limit for untrusted scripts (`docs/DEVIATIONS.md`).
- Deviations to revisit if strict conformance is wanted: `dict.update(None)` errors (spec says None allowed; Go/Rust
  error), `list.pop` negative indexes.
- Coverage below its stated 96% target. Lowest files by line coverage in `coverage.md`: `Dialect.cs` 75.0%,
  `AstModule.cs` 78.5%, `Ty.cs` 83.8%, `TypeValues.cs` 85.5%, `CodeMap.cs` 85.7%, `Intersects.cs` 86.8%, `Values.cs` 87.2%.
- Memory note gaps ("no typing, no struct/JSON modules") are outdated: git log shows json, typing, struct/record/enum,
  type checker and utf16 type added after 2026-10-22.
- Housekeeping: `src/Starlark.Eval/Interop.cs` and `stryker-config.json` are untracked; `bench/Starlark.Bench` is not in
  `Starlark.slnx`; no README, CI, or requirements documents exist yet (needed before the repo split).
- Not determined: which starlark-rust commit the reference copy is, and any unimplemented spec sections beyond the
  above (no systematic spec-section audit found in the repo).

## History (git -C .. log -- starlark-cs, 13 commits)
Initial import b22689a; "Rework Starlark interpreter to follow the spec" 1490e25; UTF-16/UTF-8 element unit a22a03f;
json + type scaffolding 50bf472; fuzz tests and coverage tool efdf80a; json/typing usable from code b6407a8; lexer
hang fix and Hedgehog b8ed49c; types/records/enums/structs wired in a954ea5; static type checker 3814e20 and 043d248;
utf16 type 1fa6ee2; 90 example programs + ExampleSync 5b39b4b; execution limits, int-to-float rounding, frozen set
no-ops, Go float/set suites 5145308.
