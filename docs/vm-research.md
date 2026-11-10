# How other .NET script engines build their interpreters: research for vm-design.md

Status: research note, written 2026-11-10 to support `docs/vm-design.md`. It is read-only research. No code from these
projects has been copied into starlark-cs.

Sources were shallow clones taken on 2026-11-10:

| Project | Commit read | Licence |
|---|---|---|
| MoonSharp (`moonsharp-devs/moonsharp`) | `fbd0367` (2026-10-14) | BSD 3-clause style (Marco Mastropaolo, 2014-2016); some parts derived from KopiLua, and an MIT debugger part |
| Jint (`sebastienros/jint`) | `498c13a` (2026-11-10), `VersionPrefix` 5.0.0 | BSD 2-clause |
| DLR (`IronLanguages/dlr`) | `3c9ebf4` (2026-11-06) | Apache 2.0 (.NET Foundation) |
| IronPython 3 (`IronLanguages/ironpython3`) | `064ea75` | Apache 2.0 |
| dotnet/runtime, `System.Linq.Expressions/.../Interpreter` only | `9511d6b` | MIT |

All four licences are permissive. Code could be borrowed if the notices are kept (BSD needs the copyright line; Apache 2.0
needs the NOTICE and licence). In practice nothing here is worth copying verbatim. The ideas are what matter.

How to read this note: "**Read**" means I saw it in the source at the commit above. "**Inferred**" means a conclusion
I drew that I did not measure. None of these projects publishes benchmarks in-tree that I checked, so I make no
performance claims beyond what the code shows.

---

## 1. MoonSharp (Lua, bytecode VM)

**Instruction representation (read).** `src/MoonSharp.Interpreter/Execution/VM/Instruction.cs`: each instruction is a
*class* object with wide fields, not packed bytes:

```csharp
internal class Instruction {
    internal OpCode OpCode; internal SymbolRef Symbol; internal SymbolRef[] SymbolList;
    internal string Name; internal DynValue Value; internal int NumVal; internal int NumVal2;
    internal SourceRef SourceCodeRef;
}
```

A whole script, with all its functions, compiles into one `List<Instruction>` (`m_RootChunk.Code`). Functions are entry
points into that list.

**Dispatch (read).** `Execution/VM/Processor/Processor_InstructionLoop.cs`, `Processing_Loop`: a `while(true)` with a
`switch (i.OpCode)` that has 56 cases, inside one `try`. A comment says: "has a weird control flow and needs to be as fast
as possible ... don't complain about gotos". Each iteration:

```csharp
Instruction i = m_RootChunk.Code[instructionPtr];
if (m_Debug.DebuggerAttached != null) ListenDebugger(i, instructionPtr);
++executedInstructions;
if (canAutoYield && executedInstructions > AutoYieldCounter) { ...; return DynValue.NewForcedYieldReq(); }
++instructionPtr;
switch (i.OpCode) { ... case OpCode.Add: instructionPtr = ExecAdd(i, instructionPtr);
                         if (instructionPtr == YIELD_SPECIAL_TRAP) goto yield_to_calling_coroutine; break; ... }
```

Most cases call an `ExecXxx` helper that returns the new instruction pointer. A sentinel `-99` means "yield".
Cost: a `List<T>` indexer (bounds check plus an indirection), a pointer-chasing load of a heap object, a debugger null
check and a counter test per instruction. This is a "switch over an enum field of an object" design, between a packed
`byte[]` and object-per-instruction with virtual `Run`.

**Frames and calls (read).** `CallStackItem` is a class holding `BasePointer`, `ReturnAddress`, `LocalScope`
(`DynValue[]`), `ClosureScope`, and the CLR-callback, continuation and error-handler fields. There are two
`FastStack<T>`s, the value stack and the execution stack. Each is a plain array preallocated to
`STACK_SIZE = 131072` (`Processor.cs`), and `Push` is `m_Storage[m_HeadIdx++] = item` with no capacity check.
**Lua-to-Lua calls do not recurse in C#.** `Internal_ExecCall` pushes a `CallStackItem` and returns the callee's entry
address, so the same loop continues. **CLR callbacks are called directly** (`fn.Callback.Invoke(...)`) from inside the
loop. If a callback calls back into Lua, it enters a new `Processing_Loop` (C# recursion). MoonSharp works around this
for `pcall`, metamethods and so on with *tail-call requests*: a callback returns a `DynValue` of type `TailCallRequest`
or `YieldRequest`, and the loop performs the call itself (see `Internal_CheckForTailRequests`). This is how its core
library avoids nested loops.

Deep recursion (inferred from the code): when the call depth passes `TailCallOptimizationThreshold` (default 65536,
`Script.cs`), a call followed exactly by `RET 1` is turned into a tail call (`PerformTCO`). Otherwise the fixed-size stack
array is the only limit, and overflowing it would throw an `IndexOutOfRangeException` from `FastStack.Push`. There is no
designed "too deep" error. That is a crash path we must not copy.

**Closures (read).** `ClosureContext : List<DynValue>` plus symbol names. Captured locals are shared `DynValue`
instances (DynValue is a mutable class), so the cell is the value box itself.

**Values (read).** `DynValue` is a `sealed class`. `DynValue.NewNumber(double)` allocates a new object every time. There is
no small-number cache, so every arithmetic result allocates (`ExecAdd` pushes `DynValue.NewNumber(ln.Value + rn.Value)`).

**Limits and sandboxing (read).** There are no step, time or memory budgets. The only instruction counter is
`AutoYieldCounter`, which forces a coroutine to yield after N instructions (pre-emptive time slicing for coroutines,
not a budget). Sandboxing is done by *module selection*: `CoreModules.Preset_HardSandbox` / `Preset_SoftSandbox`
(`Modules/CoreModules.cs`) leave out io, os and load.

**Coroutines (read).** Each coroutine has its own `Processor` with its own stacks (`Processor_Coroutines.cs`), so a
yield is a plain return out of `Processing_Loop`, with `m_SavedInstructionPtr` kept for resuming. This is the same
mechanism that would let a heap-frame VM suspend a whole Starlark call chain.

**Lessons.** (1) Heap frames plus one loop is a proven way to keep script calls off the C# stack in .NET. (2) Re-entry
from host code is the remaining hole, and MoonSharp's "return a request to the loop" trick (`TailCallRequest`) is the
cleanest fix for built-ins that call script functions (sorted's `key=`, `map`-style helpers). (3) Do not preallocate
huge unchecked arrays. (4) Allocating a value per arithmetic result is MoonSharp's best-known slowness, and our
small-int cache already avoids it.

---

## 2. Jint (JavaScript, prepared-AST tree walker)

**Representation (read).** Jint is still a tree walker. It has no bytecode. Esprima AST nodes are wrapped once in
"prepared" nodes, `JintExpression` / `JintStatement` subclasses (`Jint/Runtime/Interpreter/Expressions/*.cs`, about 35
classes), and the result is cached on the AST node's `UserData`:

```csharp
protected internal static JintExpression Build(Expression expression) {
    if (expression.UserData is JintExpression preparedExpression) return preparedExpression; ...
    NodeType.BinaryExpression => JintBinaryExpression.Build((NonLogicalBinaryExpression) expression), ...
```

Dispatch is a virtual `EvaluateInternal(EvaluationContext)` per node. The prepared nodes hold *per-site caches*, which
is where Jint's speed comes from.

**Identifier resolution (read).** `JintIdentifierExpression.cs` holds several inline caches on the node itself:
- a slot cache, `_cachedSlotEnv` plus `_cachedSlotIndex`. It remembers which `DeclarativeEnvironment` and slot index
  the name resolved to, and validates the cache by walking up at most `MaxSlotCacheChainDepth = 4` environments
  comparing references;
- `SlotChainMemo` / `NestedChainMemo`, which pin up to 4 chain links plus an "injection epoch" for closure reads;
- a global inline cache, which caches the `PropertyDescriptor` and is valid while `_cachedGlobalShapeVersion` and
  `_cachedGlobalLexicalVersion` are unchanged.

So Jint gets slot-like speed *at run time*, because JavaScript (`eval`, `with`) prevents full static resolution.
Starlark is fully statically resolvable, so our resolver can give exact slot numbers at compile time, which is simpler
and strictly better. We need none of the cache validation machinery.

**Numbers (read).** `Native/JsNumber.cs`: `JsValue` is an abstract class, and numbers are `JsNumber` objects. A static
table `_intToJsValue` of `NumbersMax = 1024 * 10` preallocated `JsNumber`s covers 0..10239, plus named singletons
(`PositiveZero`, `NegativeZero`, `IntegerNegativeOne`, `DoubleNaN`, `PI`...). `Create(int)` does
`(uint)value < (uint)temp.Length ? temp[value] : new JsNumber(value)`. That is the same technique as our small-int cache,
with a larger range (we should check ours; see recommendation R6).

**Constraints, the most useful part (read).** `Jint/Constraint.cs` defines `abstract Check()`, `Reset()` and
`virtual bool IsAmortizable` (default `false`). The built-in constraints are in `Jint/Constraints/`:

| Constraint | `IsAmortizable` | What `Check` does |
|---|---|---|
| `MaxStatementsConstraint` | false | `if (MaxStatements > 0 && ++_statementsCount > MaxStatements) throw` |
| `TimeConstraint` | true | `Stopwatch.GetTimestamp() >= _deadline` (or a `TimeProvider`) |
| `CancellationConstraint` | true | the token check |
| `OperationDeadlineConstraint` | true | a deadline across async operations |
| `MemoryLimitConstraint` | false | `GC.GetAllocatedBytesForCurrentThread()` minus the segment start, plus accumulated segments |

`EvaluationContext.cs` decides the cadence:

```csharp
internal const int AmortizedConstraintCheckInterval = 64;
public void RunBeforeExecuteStatementChecks(StatementOrExpression statement) {
    if (_shouldRunPerStatementChecks) Engine.RunPerStatementChecks(statement);
    else ChargeStatement();                 // inline: _statementCounter?.Check()
    RunAmortizedConstraintChecks();         // --countdown <= 0 → check time/cancellation, reset to 64
}
```

So **exact** constraints (statement count, memory) are checked on every statement, and **observational** ones (clock,
cancellation) only every 64 statements. `Constraint.cs` explains when amortizing is sound: only when `Check` observes
external state without consuming anything. There is also a "host boundary" check
(`CheckAmortizedConstraintsAtHostBoundary` in `Engine.Constraints.cs`), so that a script calling host functions in a loop
cannot hide from the countdown.

The memory constraint is notable. It does not count Jint's own allocations. It measures
`GC.GetAllocatedBytesForCurrentThread()` across "segments", switching segments when execution changes thread or nests
(for async/await), and it throws `PlatformNotSupportedException` when that API is unavailable
(`MemoryLimitAccuracy.Unavailable`). It also charges large string concatenations *before* copying
(`CheckBeforeCopying`, `ChargeDeferredConcatenation`), so a single huge allocation is refused rather than detected after
the fact. Our design should check how our budget compares on WebAssembly, where that GC API may report 0 (inferred, not
checked).

**Recursion (read).** Jint does use the C# stack for JS calls. It has three defences:
- `Constraints.MaxRecursionDepth` (default -1, off): a JS call-depth count in `JintCallStack`.
- `StackGuard.cs`: `RuntimeHelpers.TryEnsureSufficientExecutionStack()` probes (`EnsureStackHeadroom`,
  `[MethodImpl(NoInlining)] ProbeStackHeadroom`) that throw a JS `RangeError` instead of crashing the process.
- `MaxExecutionStackCount`: when the stack runs low, `RunOnEmptyStack` **continues the evaluation on a fresh thread-pool
  thread** (`Task.Factory.StartNew(..., TaskScheduler.Default)`) and blocks on it. That is a hop to a new stack, not a
  removal of recursion.

Deep *syntax* nesting is handled separately: `JintStackProbeExpression` / `SyntaxNesting` insert probe nodes into deep
binary-operator chains at build time. The volume of comments in `StackGuard.cs` about ordering between the two "lanes"
shows how fragile stack-based recursion guarding is. That is good evidence for our goal 1.

**Lessons.** Per-site caching on prepared nodes; a large preallocated small-number table; exact counters checked every
time with clock and cancellation amortized; memory measured with the GC's per-thread counter and pre-charged for big
copies; and confirmation that guarding the native stack is a maze we avoid by design.

---

## 3. DLR light interpreter and IronPython 3

**Call sites (known design, consistent with what I read).** IronPython compiles Python to DLR expression trees. Each
dynamic operation becomes a `CallSite<T>` with a binder (`PythonBinaryOperationBinder`, `PythonGetMemberBinder`,
`PythonInvokeBinder` in `ironpython3/src/core/IronPython/Runtime/Binding/`). The site caches rules (a guard plus an
action) and rebinds on a miss. IronPython binders call `this.LightBind<T>(args, Context.Options.CompilationThreshold)`,
so even the *rules* first run in the light interpreter and are compiled only once hot
(`DynamicUtils.cs:73`, `GenericInterpretedBinder<T>`).

**Light interpreter (read).** `dlr/src/core/Microsoft.Dynamic/Interpreter/`:
- `Instructions/Instruction.cs`: abstract `Instruction` with `public abstract int Run(InterpretedFrame frame)`, which
  returns the instruction-pointer delta, plus `ConsumedStack` / `ProducedStack` metadata.
- `Interpreter.cs`, the whole dispatch loop:

```csharp
var instructions = Instructions.Instructions;
int index = frame.InstructionIndex;
while (index < instructions.Length) {
    index += instructions[index].Run(frame);
    frame.InstructionIndex = index;
}
```

  This is wrapped in `try/catch` with `HandleException` for try/finally regions.
- `InterpretedFrame.cs`: one `object[] Data` holds the locals followed by the evaluation stack
  (`Data = new object[StackIndex + interpreter.Instructions.MaxStackDepth]`), with `StackIndex` and
  `InstructionIndex`, and a thread-local `CurrentFrame` / `_parent` chain used only for tracebacks.
- The instructions are type-specialised singletons, for example `AddInstruction.AddInt32`:

```csharp
object? left = stack[index - 2];
stack[index - 2] = right == null ? null : ScriptingRuntimeHelpers.Int32ToObject(unchecked((int)left + (int)right));
```

  `LoadLocalInstruction.Run` is `frame.Data[frame.StackIndex++] = frame.Data[_index]; return +1;`.
- `ScriptingRuntimeHelpers.Int32ToObject` keeps a box cache for -100..999 (`MIN_CACHE`/`MAX_CACHE`). Its own comment says:
  "caches improves pystone by ~5-10% on MS .Net 1.1 ... There's evidence that it's harmful on .NET 3.5 and 4.0". It is
  old evidence, but a reminder to *measure* box caches on .NET 11.

**Tiering (read).** `LightLambda.cs`: each interpreted lambda counts calls down from
`LightCompiler.DefaultCompilationThreshold = 32`. At zero it compiles the expression tree to IL, either synchronously
or on a background `Task` (`new Task(_delegateCreator.Compile, null).Start()`), then swaps `_compiled` in. A comment
explains the deliberately racy counter: missed decrements only delay compilation. `LoopCompiler.cs` compiles hot loops
*inside* an interpreted method (on-stack replacement for loops).

**Calls and recursion (read).** Interpreted calls go through the delegate (`LightLambda.Run*`), which creates a new
`InterpretedFrame` and calls `Interpreter.Run` again, so **every call recurses in C#**. IronPython's recursion limit is a
plain counter: `PythonFunction.AddRecursionDepth` uses a small static array indexed by managed thread id
(`_depth_fast = new int[20]`, "hi-perf thread static data to avoid hitting a real thread static"), falling back to a
`[ThreadStatic] DepthSlow`, compared against `sys.setrecursionlimit`. It has no budgets of any kind for steps, memory or time.

**AOT (inferred plus read).** The DLR targets classic JIT .NET. Compilation needs `Reflection.Emit`. The light
interpreter itself is reflection-heavy (`CallInstruction.Generated.cs` builds delegates by reflection;
`DynamicInstructions.Generated.cs` is generic over many arities), so it is not trimming- or AOT-friendly as shipped.
I found no `IsDynamicCodeSupported` checks in either repository.

---

## 4. System.Linq.Expressions interpreter (dotnet/runtime)

**Read**, `src/libraries/System.Linq.Expressions/src/System/Linq/Expressions/Interpreter/`: the same design as the DLR
light interpreter, with the loop `index += instructions[index].Run(frame)` in `Interpreter.cs`, `Instruction`
subclasses, and `InterpretedFrame` with `_parent`. `LambdaExpression.Compile()` checks
`CanCompileToIL => RuntimeFeature.IsDynamicCodeSupported` and falls back to this interpreter. That is why expression
trees "work" on native AOT and WebAssembly. It has no tiering (it never compiles once on AOT), and nested lambda
invocations recurse in C#. It is the AOT fallback, not a fast engine. My understanding, from general knowledge and
not measured here, is that it is much slower than compiled delegates.

---

## 5. NLua and ClearScript (why not)

Not cloned. They are well known: NLua wraps native Lua (KeraLua, a P/Invoke to `lua54`), and ClearScript wraps V8,
JScript or VBScript through native libraries. Neither fits: native binaries cannot run in browser WebAssembly, they
complicate native AOT packaging per RID, the native heap sits outside our memory budget and the .NET GC, and a native
crash takes down the process, which breaks the no-crash policy. They also implement other languages, so nothing about
Starlark semantics is reusable.

---

## 6. Comparison

| | Representation and dispatch | Script calls on the C# stack? | Locals | Limits | Number boxing |
|---|---|---|---|---|---|
| MoonSharp | `Instruction` class objects in a `List`, `switch` on the opcode | **No** (heap `CallStackItem`); yes for host re-entry, partly avoided with tail-call requests | `DynValue[]` per frame | none (coroutine auto-yield counter only) | allocates every result |
| Jint | prepared AST nodes, virtual `EvaluateInternal` | **Yes**, with stack probes, a depth limit and a thread hop | env slots plus per-node inline caches | statements (exact), memory (exact, GC counter), time and cancellation (every 64) | cache 0..10239 |
| DLR light | `Instruction[]`, virtual `Run` returning an ip delta | **Yes** | slots in a shared `object[] Data` | none (recursion limit in IronPython) | cache -100..999 |
| S.L.Expressions | the same as the DLR | **Yes** | the same | none | none to speak of |
| starlark-cs VM (design) | `byte[]` with int32 operands, `switch` | **No** | slot arrays plus cells | steps, time, memory, depth | small-int cache |

---

## 7. Recommendations for docs/vm-design.md

### Adopt

**R1. Keep the `byte[]` switch loop; do not switch to object-per-instruction.** The DLR and S.L.Expressions chose
virtual `Run` because their instructions are generated from arbitrary expression trees and specialised per CLR type
(`AddInt32`, `AddDouble`...). That needs an open set of instruction *classes*. Starlark's set is closed (~70), so a dense
`switch` compiles to a jump table. Per instruction, a virtual call costs an object load, a method-table load and an
indirect call that the JIT cannot inline, and it splits the loop's state (`frame.StackIndex`, `frame.Data`) into memory
that each `Run` reloads and writes back. A switch keeps `pc`, `sp`, `code` and `stack` in registers (inferred; standard
interpreter folklore, not measured here). Object-per-instruction does have one real advantage: operands live in fields,
so nothing is decoded. To recover most of it, read operands with
`BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(pc))` or `Unsafe.ReadUnaligned<int>(ref code[pc])` after a single
bounds validation done at compile time (see R9). If profiling later shows that decoding matters, an `int[]` code array
(opcode and operands each one int) is a cheap switch, since it removes the unaligned reads at the cost of size.
MoonSharp's middle way, a `List<Instruction>` of fat objects, gets the costs of both and the benefits of neither: avoid it.

**R2. Hoist the loop state into locals and write it back only at calls, returns and exception points.** Both MoonSharp
(`instructionPtr` local) and the DLR (`index` local) do this for the pc. Do it for `pc`, `sp`, `code`, `stack`, `locals`
and `consts`. The design's `VmFrame.Sp/Pc` fields should be *spill slots*, not the working copies. Put the
write-back in one helper used before anything that can throw, so error locations stay exact (the DLR stores
`frame.InstructionIndex = index` after every instruction, which is simple but costs a store each time; we only need it
before something can throw. Since nearly any `Ops` call can throw, storing `frame.Pc = pc` before each non-trivial opcode
is acceptable; or keep a local and store it in a `catch` / exception filter, which costs nothing on the fast path).

**R3. Split budget checks into exact and amortized ones, as Jint does.** The step count must be exact (parity with the
tree walker), so keep `steps++` with a compare against a precomputed limit on each counted opcode. The deadline, the
cancellation token and memory polling can run every N steps (Jint uses 64) using a countdown, and also at every
host-call boundary, so that a loop of built-in calls cannot dodge them (Jint's `CheckAmortizedConstraintsAtHostBoundary`).
Make the exact check one compare: keep `remaining = limit - steps` in a local and do `if (--remaining < 0) StepLimitHit()`
with a `[MethodImpl(NoInlining)]` slow path, then reconstruct `LastRunSteps` from `remaining` on exit. Fold the amortized
countdown into the same counter: run the slow path when `remaining` reaches the next "poll point", so the hot path is
still one decrement and one branch. This replaces the design's `if (StepFlags[op]) ctx.Step()` (a table load, a branch and
a call per instruction). Better still, have the **compiler emit the step charge** as part of the opcodes that count
(for example `Call`, loop back-edges and statement starts), or as an explicit `Step` opcode where the tree walker counts,
so opcodes that never count pay nothing.

**R4. Let built-ins request calls instead of making them (MoonSharp's `TailCallRequest`).** Built-ins that call script
callbacks (`sorted(key=)`, `max/min(key=)`, the future helpers, user `CallValue`) are the remaining C# recursion. For
the common ones, give built-ins a way to return "call f with args, then continue me with this continuation" to the
loop, which pushes a VM frame. That keeps deep `sorted(key=lambda: recurse())` patterns on the heap. Keep the nested-loop
fallback with the existing host depth limit and `StackGuard` for general host code.

**R5. Use heap frames as the place to suspend.** MoonSharp's coroutines are just "return out of the loop with the saved
ip, resume later", which works because frames are on the heap. The same applies to futures in inline mode and to a
debugger step in `ScriptThread`: the VM can *suspend* instead of blocking a thread. It is worth noting as a future benefit
in section 8.

**R6. Check and maybe widen the small-int cache.** Jint caches 0..10239 and the DLR -100..999. Make sure `SmallInt` and
every arithmetic fast path return cached boxes for a range that covers typical loop counters and indices (say
-128..10239, about 10 K objects and about 240 KB once per process). Measure it, because the DLR's comment records that a box
cache once *hurt*.

**R7. Measure memory as Jint does, and pre-charge large allocations.** If the memory budget is not already based on
`GC.GetAllocatedBytesForCurrentThread()`, Jint's segment approach (record the start counter when entering the loop,
accumulate on exit or thread switch) is a cheap per-poll measurement that fits R3's amortized poll. More important for
the no-crash policy: charge big operations (string repeat, list `*`, join, concatenation) *before* allocating, as
`CheckBeforeCopying` does. Check what that GC API returns on browser WebAssembly before relying on it there (unverified).

**R8. Keep resolution static; skip runtime inline caches for names.** Jint's `JintIdentifierExpression` shows the cost
of not knowing scopes at compile time: several caches plus validation walks per identifier. Starlark's resolver already
knows every binding, so `LoadLocal`/`LoadCell`/`LoadGlobal` with slot numbers is the right design, as written.
Inline caches *are* worth considering later for `GetAttr` on host objects and `struct`-like values (Jint's member cache
and DLR call sites). They are a tier-2 topic.

**R9. Verify bytecode once, then trust it in the loop.** Neither MoonSharp nor the DLR checks operands at run time, and
MoonSharp's unchecked `FastStack` shows what happens when the static assumptions are wrong. Have `BytecodeCompiler`
compute and record `MaxStack` exactly (as the DLR's `Instructions.MaxStackDepth` does) and run a debug/test verifier
over each `CodeObject` (stack depth at each pc, jump targets in range, slot indices < `LocalCount`). The loop can then
use unchecked access (`Unsafe.Add`/`MemoryMarshal.GetArrayDataReference`) safely. If the owner prefers no `Unsafe`, keep
the bounds checks; they are cheap compared with `Ops` calls.

### Avoid

- **Object-per-instruction with virtual `Run`** (DLR, S.L.Expressions), for the reasons in R1. Its type specialisation
  depends on static CLR types, which Starlark does not have.
- **Recursing in C# per script call, plus stack probes and thread hops** (Jint, DLR, IronPython). Jint's `StackGuard`
  needs pages of comments about the ordering between probes. The heap-frame design is the right call.
- **Fixed, unchecked preallocated stacks** (MoonSharp `STACK_SIZE = 131072` with no capacity check). Grow the frame pool
  and per-frame stacks from verified `MaxStack`, and fail with the depth or memory budget, never with an index exception.
- **A debugger/hook check per instruction on the hot path** (MoonSharp checks `m_Debug.DebuggerAttached` every
  instruction). If `ScriptThread` needs stepping, use a separate loop instance or patch the budget counter so the
  existing slow path fires (the "poll point" from R3 can double as the debugger hook).
- **Expression trees or the DLR as the tier-2 vehicle on AOT.** S.L.Expressions silently falls back to its slow
  interpreter when `IsDynamicCodeSupported` is false, and the DLR is not AOT-ready. Tier 2 should stay gated on
  `RuntimeFeature.IsDynamicCodeCompiled`, as section 10 says. If it is built, the DLR's tiering pattern (count calls down from a
  threshold of 32, compile on a background `Task`, swap the delegate in, and compile hot loops separately) is a reasonable
  template, but the emitted code would still recurse in C#, which conflicts with goal 1. A tier-2 JIT must therefore
  compile *leaf* or non-recursive hot functions only, or run inside the frame model. Section 10 should state this.

### Possible mistakes or gaps in vm-design.md

1. **`if (StepFlags[op]) ctx.Step()` per instruction** (section 5) puts a table lookup and a method call on every
   opcode. Prefer compiler-emitted charges and the single-decrement scheme in R3. Same exact counts, less work.
2. **`VmFrame.Sp`/`Pc` as the working state** (section 7). Use locals in the loop, with frames as spill storage (R2).
3. **Host re-entry is described as "the only C# recursion left"**, but built-ins with callbacks are common in Starlark
   (`sorted`, `min`, `max` with `key=`). Without R4 the stack-overflow class survives through them, still bounded only by the
   host depth check. Consider the request/continuation protocol at least for the built-in callers.
4. **The per-frame `object?[] Stack` plus separate `Locals`** costs two allocations, or two pooled arrays. The DLR's
   single `Data` array (locals followed by the evaluation stack) halves that and lets `LoadLocal` and stack pushes share one
   base reference. A further step, used by CPython and Lua: one contiguous value stack for the whole thread, with each
   frame a window (`base`, `LocalCount + MaxStack`). Arguments are then passed in place (the caller's pushed arguments
   *become* the callee's first locals), which removes the argument copy per call. Worth doing for the `calls` workload.
5. **Amortized deadline and cancellation polling is not mentioned.** Polling `Stopwatch` or the token on every step
   would be needlessly costly. State the cadence (for example every 64 or 256 steps and at every host-call boundary), and
   note that it affects *when* a timeout fires but never the step count, so parity is kept.
