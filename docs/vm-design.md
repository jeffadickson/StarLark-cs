# A bytecode VM for starlark-cs on .NET 11: design

Status: **proposal**, for after RC1. Tracked in a GitHub issue together with the WebAssembly sandbox idea. Nothing here is built yet.

## 1. Goals, in order

1. **No stack overflow, by construction.** Script calls must not use the .NET call stack, so recursion depth is limited
   by a budget and never by the thread's stack. This removes the class of crash that the audits keep finding: deep
   `load()` chains, deep recursion, and the "1 MB thread reaches only about 650 calls" limit.
2. **The same behaviour as the tree walker, exactly.** The same results, error messages, error locations, step counts
   and limits. The tree walker stays as the reference implementation, and every test runs on both engines (section 9).
3. **Faster.** Measured against the tree walker and against starlark-rust (requirement QA-PERF-002), not assumed.
4. **Everything else keeps working:** budgets, futures (threaded and inline), the type checker, interop, `ScriptThread`,
   browser WebAssembly and native AOT.

Not goals: changing the language, replacing the type checker, or a separate heap. Values stay ordinary .NET objects on
.NET's garbage collector. That is the one big difference from starlark-rust, which has its own heap and collector (see
section 11).

## 2. Baseline, measured on .NET 11 RC 3

`bench/Starlark.Profile`, Release:

| Workload | Time per run | Allocated per run | Rough cost per operation |
|---|---|---|---|
| `calls`: 60,000 calls to `f(a, b)` | 29.7 ms | 26.2 MB | about 500 ns and 450 B per call |
| `loops`: 90,000 iterations of `t += i * j % 7` | 22.8 ms | 8.4 MB | about 250 ns per iteration |
| `closures` | 51.5 ms | 35.0 MB | |
| `floats` | 71.7 ms | 30.9 MB | boxing every `double` |

Where the time goes in the tree walker:
- **Name lookups go through dictionaries:** `Frame.Vars` is a `Dictionary<string, object?>`, probed once per frame
  level.
- **Every call allocates** a `Frame`, a dictionary, an argument `List`, and boxes for the results.
- **Every node pays for a virtual type switch** (`switch (expr)` over about 30 node types) and an exception filter.

A VM removes most of this: slots in an array instead of dictionaries, no per-call dictionary, and a dense opcode switch.

## 3. Why not compile Starlark straight to CIL?

CIL (`System.Reflection.Emit`, `DynamicMethod` or `System.Linq.Expressions`) is the obvious alternative: let the .NET JIT
do the work. It would be the fastest option on a server. It is not the right *first* engine, for five reasons.

1. **It does not fix the stack.** Emitted methods call each other on the real .NET stack, so deep recursion overflows
   exactly as the tree walker does. Goal 1 needs script frames on the heap, which means an interpreter loop. CIL would
   have to be added on top, not instead.
2. **It is not available everywhere we run.** Dynamic code is unsupported under native AOT, and in browser WebAssembly
   it is interpreted, not JIT compiled, so it is slower than a plain VM there.
   `RuntimeFeature.IsDynamicCodeCompiled` is false in both cases, and the playground and AOT hosts would lose the engine.
3. **Starlark is dynamically typed.** Without type information, emitted code still boxes every value and calls
   `Ops.Binary(op, object, object)`, so most of the win is spent on the same helpers. The real gains need type
   specialization (guards and inline caches), which is a large compiler project.
4. **Budgets and guards must be emitted correctly.** Every step count, deadline poll, memory check and error location
   would have to be generated into the IL. An emitter bug gives `InvalidProgramException`, or silently skips a budget:
   worse failure modes than an interpreter bug.
5. **Collectible code has costs.** Generated code for short-lived scripts must be collectible, which means
   `AssemblyLoadContext` or `DynamicMethod` management, a JIT cost on each compile (milliseconds, more than many config
   scripts take to run), and memory per compiled script.

### And why not translate Starlark to C# and compile it with Roslyn?

This has all five problems above, and three more:
- **Start-up.** A Roslyn compile takes 100 ms to seconds and pulls in a large compiler, while most scripts run in milliseconds.
- **It cannot run where we need it.** Native AOT hosts have no compiler, and Roslyn in the browser would be many times the
  size of the whole playground.
- **Trust.** The generated C# is full-trust code. A translator bug could produce a program that skips budgets or calls
  any .NET API. An interpreter can only do what its opcodes do.

It does make sense in one narrow case: **ahead-of-time compilation of trusted scripts**. A host ships a fixed set of its
own `.star` files and wants them fast. A C# source generator could compile them into the host's assembly at build time,
with no runtime compiler. That would be an optional build-time tool, using the same bytecode-level semantics, listed under
"later" with the CIL tier. It is not the engine for scripts that arrive at run time.

**Plan:** build the bytecode VM (this document). Later, as **tier 2**, add an optional CIL JIT for *hot functions only*
on platforms where `RuntimeFeature.IsDynamicCodeCompiled` is true. That is the same tiering .NET uses for itself. The
bytecode is the input to that JIT, so nothing in this design is thrown away (section 10).

## 4. Architecture

```
source ─► Lexer/Parser (unchanged) ─► AST ─► Resolver (unchanged, extended with slot numbers)
       ─► BytecodeCompiler ─► CodeObject per function/module ─► VM.Run (one loop, heap frames)
```

New code lives in `src/Starlark.Eval/Vm/`:

| File | Contents |
|---|---|
| `Opcode.cs` | `enum Opcode : byte` (section 5). |
| `CodeObject.cs` | Compiled code for one function, lambda, comprehension or module (section 6). |
| `BytecodeCompiler.cs` | AST to `CodeObject`. One pass per function after resolution. |
| `VmFrame.cs` | A frame on the VM's heap stack (section 7). |
| `Vm.cs` | The interpreter loop. |
| `Disassembler.cs` | Text dump for tests, debugging and the deep dive. |

The `Interpreter` API does not change. A new property chooses the engine:

```csharp
public enum Engine { TreeWalker, Bytecode }
public Engine Engine { get; set; } = Engine.TreeWalker;   // Bytecode becomes the default once it passes everything
```

## 5. Instruction set

The VM is a stack machine, with operands in a per-frame `object?[]` evaluation stack. Instructions are a `byte` opcode
followed by little-endian `int32` operands, in a `byte[]` per code object, so they are compact and cache-friendly, as in
Go's starlark compiler.

| Group | Opcodes | Notes |
|---|---|---|
| Constants | `Const k`, `None`, `True`, `False`, `SmallInt n` | `SmallInt` uses the shared small-int box cache, so no allocation. |
| Locals | `LoadLocal s`, `StoreLocal s`, `LoadCell s`, `StoreCell s`, `LoadFree s` | Slot numbers from the resolver; cells only for captured variables. |
| Globals | `LoadGlobal s`, `StoreGlobal s`, `LoadUniversal k` | Module globals in an array; predeclared names resolved at compile time. |
| Stack | `Pop`, `Dup`, `Dup2`, `Swap`, `Rot3` | |
| Operators | `Add`, `Sub`, `Mul`, `Div`, `FloorDiv`, `Mod`, `Shl`, `Shr`, `BitAnd`, `BitOr`, `BitXor`, `Neg`, `Plus`, `BitNot`, `Not`, `Eq`, `Ne`, `Lt`, `Le`, `Gt`, `Ge`, `In`, `NotIn`, `Percent` | Each calls the existing `Ops` method, so semantics are shared, not duplicated. |
| Fast paths | `AddInt`, `LtInt`, `IncLocal s, k` | Emitted only where the compiler cannot prove types; each checks both operands are small ints and falls back to `Ops`. |
| Control | `Jump t`, `JumpIfFalse t`, `JumpIfTrue t`, `JumpIfFalseOrPop t`, `JumpIfTrueOrPop t` | The last two implement `and`/`or`, which yield the deciding operand. |
| Iteration | `IterPush`, `IterNext t`, `IterPop` | The iterator stack is per frame, and it also locks the container against mutation while iterating, as the spec requires. |
| Containers | `MakeList n`, `MakeTuple n`, `MakeDict n`, `ListAppend`, `DictInsert`, `SetIndex`, `Index`, `Slice`, `GetAttr k`, `SetAttr k` | `MakeDict` checks duplicate keys exactly as today. |
| Calls | `Call n, kwmask`, `CallKw n, k`, `CallStar flags`, `Return`, `MakeFunction k, ndefaults` | Starlark calls push a VM frame; built-ins and host functions are called directly (section 7). |
| Modules | `Load k` | Loads go through the same `LoadModule` path and the same cache. |
| Misc | `FormatF k`, `Unpack n`, `Fail`, `Nop` | `FormatF` builds an f-string; `Unpack` handles `a, b = x` with the existing errors. |

That is about 70 opcodes, comparable to Go (about 75) and fewer than Rust (about 83 instruction types).

**Step counting.** The VM charges one step at exactly the points where the tree walker does, so `LastRunSteps` and
every step limit match between engines. Each opcode carries a "counts a step" flag in a static table; the loop does
`if (StepFlags[op]) ctx.Step()`. That keeps the budgets, deadlines and cancellation identical to today.

## 6. Code objects and the resolver

`CodeObject` (immutable, shared by all calls, safe across threads like a frozen module):
- `byte[] Code`
- `object?[] Constants`: frozen literals, names, nested code objects.
- `int LocalCount`, `int CellCount`, `int[] FreeVarSlots`.
- `int MaxStack`, `int MaxIterators`: computed by the compiler, so frames are allocated once at the right size.
- `ParamSpec`: names, defaults, `*args` and `**kwargs` positions, keyword-only; built once, reused by every call.
- **Line table:** a compressed map from instruction offset to `Span`, as in Python's `co_linetable`. Used only when an
  error is raised, so error locations are exact and cost nothing on the happy path.

The resolver already classifies every name as local, global, predeclared or universal. It is extended to assign **slot
numbers** and to mark **captured** locals (read by an inner function), which become cells. This is where most of the
speed comes from: `LoadLocal 3` is an array read instead of a dictionary probe up a chain of frames.

## 7. Frames on the heap, and calls

```csharp
sealed class VmFrame
{
    public CodeObject Code;
    public object?[] Locals;       // LocalCount slots
    public Cell[] Cells;           // captured variables
    public object?[] Stack;        // MaxStack slots, reused
    public int Sp, Pc;
    public VmFrame? Caller;
    public StarFunction? Function;
}
```

- **Calls never recurse in C#.** A Starlark-to-Starlark call pushes a new `VmFrame` and continues the same loop, and
  `Return` pops back to `Caller`. The .NET stack depth stays constant whatever the script does, so recursion is limited
  only by `MaxCallDepth` and the memory budget: Go's 100,000 frames, on any thread, without `ScriptStackSize`.
- **Frames are pooled** per execution context, so a call allocates nothing in the common case.
- **Built-ins and host functions** are called directly from the loop with a pooled argument buffer. A host function
  that calls back into Starlark (`CallValue`) starts a nested VM loop. That is the only C# recursion left, and it is
  bounded by the existing host call-depth check and `StackGuard`.
- **Errors** are still `StarlarkException`. When one escapes an instruction, the loop records the location from the
  line table, walks the frame chain to build the same traceback as today, and rethrows. The railway contract is
  unchanged: the same four exception types reach the host.
- **`ScriptThread`** reads the frame chain (`CallStack`, `CallFrame(depth)`), which is easier than with today's side stack.

## 8. How each existing feature fits

| Feature | In the VM |
|---|---|
| Freezing | unchanged: values are the same objects. |
| Futures | A task runs its function in its own `ExecutionContext` with its own VM loop. Inline mode just runs that loop on the caller's thread. Deep copy and snapshots are unchanged. |
| Budgets | The same `ExecutionContext.Step()`, deadline poll and `MemoryBudget` calls, at the same points. |
| Type checker | Works on the AST, before compilation. Unchanged. |
| Interop (`Register`, `Expose`, `DefineValue`) | unchanged: built-ins are `ICallable`. |
| `load()` | the same `LoadModule` and module cache; a loaded module is compiled once and its `CodeObject` cached with it. |
| Worker mode | the worker runs whichever engine is configured. |
| WebAssembly and AOT | fully supported: the VM is plain C# with no dynamic code. |
| Comprehensions | compiled to inline loops with their own slots (their scoping rules are already resolved). |

## 9. How we know it is right

- **Dual-engine test runs.** A test-wide switch runs every Eval, Fuzz and Apps test on both engines: the three
  conformance suites, the 122 example programs, the manual samples, the BASIC interpreter and the examples.
- **Differential fuzzing.** The existing program fuzzers run each program on both engines and require the same output,
  the same error message and location, and the same `LastRunSteps`. Any difference is a bug in one of them.
- **No-crash.** Every crash regression test from the two audits runs on the VM. New tests check that 100,000-deep
  recursion works on a 1 MB thread, and that a deep `load()` chain ends in a clean error.
- **Disassembly golden tests** for representative functions, so changes to code generation are visible in review.
- **Mutation testing** (Stryker.NET) on `Vm.cs` and `BytecodeCompiler.cs`.

## 10. Later: tier 2, a CIL JIT for hot functions

Once the VM is the default, a function called more than N times (say 1,000) is compiled from its bytecode to a
`DynamicMethod`, but only where `RuntimeFeature.IsDynamicCodeCompiled` is true:
- The emitted method uses **the same frame and value layout**, so it can call and be called by interpreted frames, and
  it can **deoptimize** (fall back to the interpreter) when a type guard fails.
- **Inline caches** on `GetAttr`, `Call` and operators record the types seen, and specialize to them (small-int add,
  string concatenation, dict lookup by string).
- **Budgets are kept:** emitted code calls `Step()` at loop back-edges and calls, so limits still hold, with fewer
  checks per operation.
- **Recursion stays on the heap:** a JIT-compiled function calling another Starlark function returns to the VM loop,
  which pushes the frame. Only leaf work runs as native code.

This is a separate project, measured against the VM, and optional per host (`Interpreter.Jit = true`).

## 11. Comparison with starlark-rust and Go

| | starlark-rust | starlark-go | starlark-cs VM |
|---|---|---|---|
| Execution | bytecode VM, about 83 instructions | bytecode VM, about 75 opcodes | bytecode VM, about 70 opcodes; optional CIL tier later |
| Values | own heap, own GC, frozen and unfrozen heaps | Go's GC | .NET's GC |
| Call stack | interpreter stack, bounded | Go's growable stacks | heap frames: constant .NET stack |
| Step budget | yes | yes | yes, identical to the tree walker |
| Memory budget | no | no | yes (`MaxAllocatedBytes`) |
| Runs in a browser | via its own WebAssembly build | via its own WebAssembly build | the same library, unchanged |

## 12. Plan and size

| Phase | Work | Size |
|---|---|---|
| 1 | Resolver slot numbers; `CodeObject`, `Opcode`, compiler for expressions and simple statements; disassembler | about 1 week |
| 2 | VM loop with heap frames, calls, closures and cells, iteration, comprehensions, `load()` | about 1 to 2 weeks |
| 3 | Error locations from line tables, tracebacks, `ScriptThread`; dual-engine test switch; differential fuzzing | about 1 week |
| 4 | Futures, interop and worker mode on the VM; frame pooling; fast paths; benchmarks against the tree walker and Rust | about 1 week |
| 5 | Make `Engine.Bytecode` the default once every test passes on both engines and benchmarks justify it | |

The tree walker is kept as the reference, and it is the fallback for any feature the VM has not reached yet.

## 13. Risks

- **Two engines to keep in step.** That is mitigated by sharing all semantics (`Ops`, `Methods`, `Builtins` and
  `ExecutionContext` are called by both) and by differential fuzzing on every build.
- **Error locations.** The line table must reproduce the tree walker's spans exactly; golden tests on error messages
  catch any drift.
- **The gain may be smaller than hoped.** Values stay boxed `object`s. Expect a 2 to 4 times speed-up on call-heavy and
  loop-heavy scripts, and less on string and big-int work, which already runs in `Ops`. Measure in phase 4 before
  making the VM the default.
