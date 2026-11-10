# starlark-cs compared with starlark-rust (QA-PERF-002)

Measured 2026-11-10. starlark-cs at commit c0d18ca (the tree-walking interpreter; no bytecode VM yet).

## Summary

- On 13 workloads, starlark-cs (warm, in-process) takes **2.3x to 14x** as long as starlark-rust, geometric mean
  about **4x**. Bigint multiplication and parsing are at parity.
- The gap is largest on **function calls** (`calls` 13.8x, `closures` 11x) and **tight loops over ints with list
  indexing** (`quicksort` 8.7x), smallest where the work is in library code (sorting, string methods, bigint
  arithmetic, parsing: 0.9x to 3.4x).
- **Cold start** for one small program: starlark-rust about 60-75 ms per process, starlark-cs about 210-410 ms (JIT).

## Results: warm, in-process

Time per run, median of all runs in a 5 s window after 12 warm-up runs; p10-p90 in brackets. Ratio = cs / rust
(higher is slower for starlark-cs). A second pass (3 s window) gave medians within about 15% of these for every
workload, and the same ranking.

| Workload | What it does | starlark-cs ms | starlark-rust ms | Ratio |
|---|---|---|---|---|
| loops | 90,000 iterations of `t += i * j % 7` | 22.14 (21.55-25.91) | 4.09 (3.81-4.37) | **5.4** |
| calls | 60,000 calls of `f(a, b)` | 31.94 (29.72-39.53) | 2.32 (2.24-2.48) | **13.8** |
| strings | concat, `str`, `upper`, `%d`, `format` | 13.45 (12.51-17.03) | 4.01 (3.53-4.43) | **3.4** |
| lists | comprehension, `sorted`, slice, loop sum | 4.83 (4.49-5.95) | 2.07 (1.86-2.97) | **2.3** |
| dicts | 6,000 inserts, iteration, `items()` | 4.29 (3.38-5.88) | 0.86 (0.81-1.27) | **5.0** |
| records | 8,000 `record` constructions, field reads | 6.59 (6.37-7.63) | 2.37 (2.21-3.44) | **2.8** |
| closures | 75,000 lambda calls with a default arg | 45.72 (35.85-57.89) | 4.17 (3.93-5.74) | **11.0** |
| bigints | 599! and `len(str(...))` | 0.245 (0.222-0.375) | 0.217 (0.203-0.326) | **1.1** |
| floats | 120,000 float divides and compares | 83.46 (77.76-95.65) | 16.43 (15.42-22.55) | **5.1** |
| parse | lex and parse 400 lines, no execution | 0.924 (0.836-1.532) | 1.029 (0.982-1.458) | **0.9** |
| quicksort | iterative quicksort of 3,000 ints + BFS over 5,000 nodes | 65.23 (59.03-73.33) | 7.46 (7.08-9.60) | **8.7** |
| config | 600 build targets: dict merge, `struct(**kw)`, grouping, `json.encode` | 7.24 (5.90-9.81) | 1.99 (1.84-2.78) | **3.6** |
| textproc | build a 3,000-line log, split, count, reformat | 36.71 (34.58-42.87) | 8.00 (7.51-9.37) | **4.6** |

Geometric mean of the ratios: about 4.0. Both implementations produced the same value of `r` for every workload
(the harnesses print it; for example `quicksort` gives `(True, 11)`, `config` gives `(120, 1794, 9098)`).

## Results: cold start (one run, whole process)

Wall-clock time to spawn the process, evaluate one file and exit; median of 20 spawns [p10-p90].

| Program | loops | quicksort | config | trivial |
|---|---|---|---|---|
| starlark-rust CLI (`starlark.exe file.star`) | 69.7 [68.3-76.7] | 75.6 [73.8-76.5] | 69.7 [67.9-72.1] | 65.5 [64.8-68.5] (`-e 1`) |
| Rust harness `--once` (library, same globals as warm) | 73.8 [64.1-95.3] | 67.1 [63.6-75.0] | 59.5 [58.1-61.3] | |
| starlark-cs harness `--once` (JIT, .NET 11 RC 3) | 284.3 [274.8-289.7] | 410.4 [390.3-436.4] | 279.2 [273.1-292.1] | 211.1 [204.4-225.5] (`bigints`) |

The cold ratio is about 3-6x. For starlark-rust, almost all of the ~60 ms is process start-up (the trivial `-e 1` takes
65 ms; this is high for a native binary and probably includes Windows process creation and endpoint scanning, which
were not isolated). For starlark-cs, about 200 ms is runtime start-up plus JIT of the interpreter, and each workload adds
JIT-ing the paths it uses, at tier 0 (quicksort, which exercises more of the interpreter, is 410 ms). Native AOT or
ReadyToRun for the C# host was not measured; it would be expected to remove most of the JIT share.

## Method

- **Unit of work, both sides:** parse + compile/resolve + execute the source in a fresh module. starlark-cs: a new
  `Interpreter`, then `Execute(name, source)`. starlark-rust: `AstModule::parse`, then `Evaluator::eval_module` in
  `Module::with_temp_heap` (this includes starlark-rust's compilation to its bytecode and freeing its heap). Then the
  global `r` is read. The `parse` workload does only the parse on both sides.
- **Dialect:** standard plus top-level statements and types enabled (needed for `record`). starlark-rust globals:
  standard plus `StructType`, `RecordType` and `Json` extensions. starlark-cs default globals (which include these).
- **Timing:** per run with a monotonic clock (`Stopwatch` / `Instant`); 12 warm-up runs; then runs until the time
  budget is spent (minimum 5); median, p10, p90, min reported. One thread, sequential, one implementation at a time.
- **.NET:** Release build, workstation non-concurrent GC, tiered PGO on (as `bench/Starlark.Profile`),
  `DOTNET_GCHeapHardLimit=0x80000000` (2 GB). **Rust:** `--release`, opt-level 3, thin LTO, codegen-units 1.
- **Cold start:** the Rust harness's `--spawn` mode starts the process 20 times and times each from spawn to exit,
  stdout discarded.
- The machine was otherwise idle apart from the desktop session; other Claude agents may have been running in other
  worktrees (one was building a VM in parallel), so some noise from them cannot be excluded. p90 values are where that
  shows.

## Machine and versions

- Dell workstation, Intel Xeon W-2255 @ 3.70 GHz, 10 cores / 20 threads, 64 GB RAM, Windows 11 Enterprise 10.0.26100.
- .NET SDK 11.0.100-rc.3.26522.148 (runtime .NET 11 RC 3), x64, JIT.
- rustc 1.99.1 (2026-10-13); starlark crate 0.14.2 from the starlark-rust `main` checkout at
  `C:\Users\dickson\dev\starlark-rust-src\starlark-rust-main`.

## Workloads and dialect differences

The source text is the same for both implementations; it is in `bench/RustCompare/workloads/*.star` (the `parse`
source is generated in both harnesses by the same loop). The nine `bench/Starlark.Profile` workloads are copied
verbatim with one change; the three new ones (`quicksort`, `config`, `textproc`) were written to the common subset.
Differences found, all in starlark-rust's support for things starlark-cs accepts:

1. **`str.format` format specs:** starlark-rust rejects `'{:>6}'.format(i)` ("Key `:>6` was not found"). In
   `strings`, changed to `'{}'.format(i)` for both. So `strings` here is not identical to `bench/Starlark.Profile`.
2. **`%02d`:** starlark-rust rejects flags and widths in `%` formatting ("Unsupported format character: '0'"). In
   `textproc`, written as `%d`.
3. **`sum`:** not a starlark-rust builtin. In `config`, written as a loop.
4. **`while` and recursion** are not used (the Starlark spec forbids both by default); `quicksort` uses an
   explicit stack and bounded `for` loops.
5. `record` is supported by both (with types enabled), so `records` is included.

The `parse` workload measures slightly different things: starlark-rust's `AstModule::parse` also builds its code map;
starlark-cs's `AstModule.Parse` builds its own source index. Both copy the source.

## Analysis

What was measured is above; what follows is inference from the numbers and from reading both code bases, not profiling
of starlark-rust.

**Where the gap is largest.** Calls (13.8x, closures 11x) and interpreter-bound loops over small ints (quicksort 8.7x,
loops 5.4x, floats 5.1x). Where most of the time is in library code written in the host language (sorting, string
methods, bigint multiply, the parser) starlark-cs is at 0.9x-3.4x, and on bigints and parsing it is at parity: .NET's
`BigInteger` and string code are competitive with Rust's `num-bigint` and string code.

**Likely reasons**, consistent with the profile in `docs/vm-design.md` section 2:

- **Tree walker vs bytecode.** starlark-rust compiles to a compact bytecode with resolved slots and specialised
  instructions; starlark-cs walks the AST with a type switch per node. This is a per-operation constant cost and shows
  on every interpreter-bound workload.
- **Allocation per call.** starlark-cs allocates a `Frame`, a `Dictionary<string, object?>` for locals, an argument
  list and boxed results for every call (about 450 B per call by `bench/Starlark.Profile`); starlark-rust uses
  pre-sized frames on its own bump heap. This matches calls and closures being the worst ratios.
- **Dictionary lookups for locals.** Every local read and write in starlark-cs is a string-keyed hash probe;
  starlark-rust uses array slots. Hits `loops`, `quicksort`, `floats`.
- **Boxing and BigInteger ints.** Every starlark-cs int is a boxed `System.Numerics.BigInteger` and every float a boxed
  `double`, so each arithmetic result allocates and goes through `BigInteger` arithmetic even for small values;
  starlark-rust stores small ints unboxed inside the tagged value word and only allocates for big ones.
  `floats` (5.1x) is mostly this: no calls, little else to do.
- **GC model.** starlark-rust frees a whole module heap at once; .NET's generational GC has to trace survivors. With
  short-lived garbage this is cheap, but not free.

**What a C# bytecode VM could plausibly recover** (inference; see `docs/vm-design.md`). Slots instead of dictionaries
and no per-call dictionary attack the two biggest causes in `calls`, `closures` and `loops`; a dense opcode loop removes
the node switch. A 2-3x gain on the call- and loop-bound workloads would bring them to roughly 3-6x of starlark-rust,
and the library-bound ones would barely move. Getting close to parity on arithmetic loops would additionally need a
small-int representation that does not allocate (for example a cached or unboxed `long` fast path, with `BigInteger`
only on overflow), which is a value-representation change, not only a VM change. Cold start is a separate problem
(JIT), better addressed by ReadyToRun or native AOT than by the VM. These are expectations to be checked by re-running
this comparison against the VM, not results.

## Re-running

See `bench/RustCompare/README.md`. Harnesses: `bench/RustCompare` (Rust, cargo) and `bench/Starlark.RustCompare` (C#).
