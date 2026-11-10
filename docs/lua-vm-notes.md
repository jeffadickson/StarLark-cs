# Design note: what a Lua VM in C# would need beyond the Starlark VM

Status: **a note for later**, not a plan. It records what a register VM for Lua 5.4, written in C# for .NET 11, would need in
addition to, or instead of, the Starlark bytecode VM specified in `docs/requirements/vm.sdoc` and designed in
`docs/vm-design.md`. It was written while designing that VM, when the question came up whether Lua or Starlark is the better
fit for the owner's tools. That discussion concluded:

- **Starlark** fits rules, configuration and scripts that come from other people: it is deterministic and hermetic, and
  values are frozen.
- **Lua** fits long-running, stateful automation that the author writes themselves: it has coroutines, mutable state and
  real loops.
- **For testbenches in SharpTestBench, C# `async` already provides the coroutines,** so neither scripting language is
  needed there.

Nothing here has been prototyped or measured. Claims about Lua's semantics are from the Lua 5.4 reference manual and the
design of the reference interpreter (`lvm.c`, `lopcodes.h`), from knowledge rather than a fresh reading of the source.
Check them against the source before relying on them.

## 1. What carries over from the Starlark VM unchanged

| Starlark VM part | Use for Lua |
|---|---|
| Script frames on the heap (VM-EXE-001) | **Even more important.** Coroutines become nearly free: a suspended coroutine is just its own frame stack (section 4.8). |
| One opcode table driving compiler, verifier, disassembler and loop (VM-CODE-003) | Same. |
| Fixed-width operands in a `byte[]` (VM-CODE-002) | Replaced by a 32-bit word per instruction, see section 2. |
| Bytecode verified once, proved in Dafny (VM-VER-*) | Same, with the register-file rules of section 2. |
| Line table proved in Dafny (VM-CODE-005) | Same. |
| Step, time, memory and cancellation budgets with amortised polls (VM-STEP-*) | Same. Lua's `debug.sethook` count hook is the reference's version. |
| Railway contract: only documented exception types reach the host (VM-RAIL-*) | Same, with Lua's `error`/`pcall` added (section 4.10). |
| Typed C# interop (`Register`, `Expose`, the type checker reading C# signatures) | The interop carries over; there is no static checker for Lua. |
| Differential testing against a reference engine (VM-EQ-*) | Against the reference Lua 5.4 interpreter and its official test suite, instead of a tree walker. |
| Running in the browser and under native AOT (VM-FEAT-004) | Same: no dynamic code generation. |

## 2. The main structural change: registers instead of a stack

The Starlark VM is a **stack machine**: operands are pushed, then an instruction consumes them. Real Lua (5.x) is a
**register machine**: each instruction names the slots it reads and writes, for example `Add r3, r1, r2`. For Lua the
register design is the better choice:

- Lua functions are full of temporaries, and locals already live in numbered slots; registers save the loads and stores a
  stack machine needs around every operation.
- The same statement takes fewer instructions, so the dispatch loop runs fewer times.
- The Lua compiler's own design (expression descriptors, register allocation, jump lists) assumes registers, and decades of
  tuning went into it.

For Starlark the stack machine was chosen on purpose: it is simpler to compile and to verify, and Starlark's cost is in the
shared semantics (BigInteger ints, boxing) rather than in dispatch.

**Instruction format.** As in the reference, one 32-bit word per instruction: a 7-bit opcode and operands A (8 bits), B and
C (8 bits each, or a 17-bit Bx, or a signed sBx, or a 25-bit sJ for jumps). Each function has at most 255 registers. A k
flag on some instructions selects whether an operand is a register or a constant.

**Verifier changes.** Instead of the stack depth on every path, the verifier proves that every register an instruction
reads is below the function's register count, that every register is written before it is read on every path (or reads
nil by Lua's rules), that the "open" result ranges of calls and varargs (section 4.4) are consumed by the next instruction,
and that every jump lands on an instruction.

## 3. Instruction set

Names follow this project's convention: PascalCase verbs that say what the instruction does. The reference interpreter's
names are given in brackets for comparison. R[x] is register x, K[x] is constant x, U[x] is upvalue x.

### 3.1 Moves and constants

| Opcode | Effect | Reference |
|---|---|---|
| `Move A B` | R[A] := R[B] | OP_MOVE |
| `LoadInt A sBx` | R[A] := integer sBx | OP_LOADI |
| `LoadFloat A sBx` | R[A] := float sBx | OP_LOADF |
| `LoadConst A Bx` | R[A] := K[Bx] | OP_LOADK, OP_LOADKX |
| `LoadFalse A`, `LoadTrue A` | R[A] := false or true | OP_LOADFALSE, OP_LOADTRUE |
| `LoadFalseSkip A` | R[A] := false, skip the next instruction (for comparisons that produce a value) | OP_LFALSESKIP |
| `LoadNil A B` | R[A] .. R[A+B] := nil | OP_LOADNIL |

### 3.2 Upvalues: captured variables that can be assigned

Lua closures may **assign** to captured variables, which Starlark forbids. A captured local lives in its register while its
block is active ("open"), and is moved to the heap when the block ends ("closed"), so every closure that captured it still
shares one variable.

| Opcode | Effect | Reference |
|---|---|---|
| `LoadUpvalue A B` | R[A] := U[B] | OP_GETUPVAL |
| `StoreUpvalue A B` | U[B] := R[A] | OP_SETUPVAL |
| `CloseUpvalues A` | close every open upvalue at register A or above (block exit, loop iteration, break, goto out of a block) | OP_CLOSE |

The verifier and the compiler must guarantee that every block exit that has captured locals passes a `CloseUpvalues`.
That is the Lua counterpart of the Starlark VM's rule that every loop exit releases its iterator guard.

### 3.3 Tables, the only data structure

Tables are arrays, dictionaries, objects, modules and namespaces at once.

| Opcode | Effect | Reference |
|---|---|---|
| `NewTable A B C` | R[A] := a new table with room for B array items and C hash items | OP_NEWTABLE |
| `LoadField A B C` | R[A] := R[B][K[C]] where K[C] is a string constant | OP_GETFIELD |
| `StoreField A B C` | R[A][K[B]] := R[C] or K[C] | OP_SETFIELD |
| `LoadIndex A B C` | R[A] := R[B][R[C]] | OP_GETTABLE |
| `StoreIndex A B C` | R[A][R[B]] := R[C] or K[C] | OP_SETTABLE |
| `LoadIndexInt A B C` | R[A] := R[B][C] for a small integer C | OP_GETI |
| `StoreIndexInt A B C` | R[A][B] := R[C] or K[C] | OP_SETI |
| `LoadGlobal A B` | R[A] := _ENV[K[B]] (globals are a table, the upvalue _ENV) | OP_GETTABUP |
| `StoreGlobal A B C` | _ENV[K[B]] := R[C] or K[C] | OP_SETTABUP |
| `SetList A B C` | R[A][C+i] := R[A+i] for i in 1..B (table constructors with a list part) | OP_SETLIST |

**Representation.** A Lua table has an array part (indexes 1..n, dense) and a hash part. In C# this is a `Value[]` plus an
open-addressing hash table keyed by `Value` with Lua's equality (floats with integer values equal the integer; NaN is never
a key). Growth must be charged to the memory budget, which is the main new robustness work (section 5).

### 3.4 Calls, varargs and multiple results

Lua functions take any number of arguments (`...`) and return any number of results. A call's results can be spread into
another call, a table constructor or a return.

| Opcode | Effect | Reference |
|---|---|---|
| `Call A B C` | R[A] .. R[A+C-2] := R[A](R[A+1] .. R[A+B-1]); B = 0 means "up to the top" (the open results of the previous call or vararg); C = 0 means "keep all results" | OP_CALL |
| `TailCall A B` | return R[A](R[A+1] .. R[A+B-1]), reusing the current frame | OP_TAILCALL |
| `Return A B` | return R[A] .. R[A+B-2]; B = 0 means "up to the top" | OP_RETURN, OP_RETURN0, OP_RETURN1 |
| `VarArgs A C` | R[A] .. R[A+C-2] := the vararg values; C = 0 means all | OP_VARARG |
| `PrepareVarArgs A` | adjust the frame of a vararg function on entry | OP_VARARGPREP |
| `SelfCall A B C` | R[A+1] := R[B]; R[A] := R[B][K[C]], for obj:method(args) | OP_SELF |
| `MakeClosure A Bx` | R[A] := a closure of nested prototype Bx, capturing its upvalues | OP_CLOSURE |

**Proper tail calls** are required by the language: a loop written as tail recursion must run in constant space.
`TailCall` replaces the current heap frame instead of pushing one, which heap frames make straightforward.

### 3.5 Arithmetic, bitwise operators and comparison

Lua 5.3 and later have two number subtypes: 64-bit **integers that wrap around** on overflow (unlike Starlark's arbitrary
precision), and 64-bit floats. `//` is floor division, `/` always gives a float, bitwise operators work on integers, and
converting a float without an exact integer value to an integer is an error.

| Group | Opcodes | Reference |
|---|---|---|
| Register-register | `Add`, `Subtract`, `Multiply`, `Modulo`, `Power`, `Divide`, `FloorDivide`, `BitAnd`, `BitOr`, `BitXor`, `ShiftLeft`, `ShiftRight` | OP_ADD .. OP_SHR |
| With a constant | `AddConst`, `SubtractConst`, ... (operand C is a constant) | OP_ADDK .. OP_BXORK |
| With a small integer | `AddInt A B sC`, `ShiftRightInt`, `ShiftLeftInt` | OP_ADDI, OP_SHRI, OP_SHLI |
| Metamethod fallback | `CallBinaryMetamethod A B C` placed after each arithmetic instruction; it runs only when the fast path fails (section 3.6) | OP_MMBIN, OP_MMBINI, OP_MMBINK |
| Unary | `Negate`, `BitNot`, `Not`, `Length` (`#`) | OP_UNM, OP_BNOT, OP_NOT, OP_LEN |
| Concatenation | `Concat A B`: R[A] := R[A] .. ... .. R[A+B-1], building the string once | OP_CONCAT |
| Comparison and jump | `JumpIfEqual`, `JumpIfLess`, `JumpIfLessOrEqual`, and forms against a constant or small integer, each followed by a `Jump` | OP_EQ, OP_LT, OP_LE, OP_EQK, OP_EQI, OP_LTI, OP_LEI, OP_GTI, OP_GEI |
| Truth tests | `Test A k`, `TestSet A B k` (and, or, if conditions) | OP_TEST, OP_TESTSET |

`#t` has the "border" semantics of Lua: for a table with holes, any border is a valid answer. The implementation must pick
one deterministically, so that differential tests are stable.

### 3.6 Metatables

Every table (and, through the string library, every string) may have a metatable whose fields override operations:
`__index` and `__newindex` (missing keys and assignments), `__add`, `__sub`, `__mul`, `__div`, `__mod`, `__pow`, `__unm`,
`__idiv`, `__band`, `__bor`, `__bxor`, `__shl`, `__shr`, `__bnot`, `__concat`, `__len`, `__eq`, `__lt`, `__le`, `__call`,
`__close`, `__tostring`, `__name`, `__metatable`, `__gc` (finalizers; see section 5) and `__mode` (weak tables).

Every table and arithmetic instruction therefore has a **fast path** (plain table, plain number) and a **metamethod
fallback** that calls Lua code. This is the same pattern as the small-int fast paths of the Starlark VM (VM-OPT-002), but
on every operation. Calling the metamethod must be a call request into the dispatch loop, never a C# recursion (the
Starlark VM's VM-EXE-005), because `__index` chains can be long.

Caching which metamethods a metatable lacks (the reference keeps a flags byte per table) makes the fast path one test.

### 3.7 Loops and goto

| Opcode | Effect | Reference |
|---|---|---|
| `ForPrepare A Bx` | prepare a numeric for (start, limit, step in R[A] .. R[A+2]; checks types and the zero step) | OP_FORPREP |
| `ForLoop A Bx` | advance the numeric for and jump back while it runs; integer loops cannot overflow (Lua 5.4 precomputes the iteration count) | OP_FORLOOP |
| `ForInPrepare A Bx` | prepare a generic for (iterator function, state, control, closing value) | OP_TFORPREP |
| `ForInCall A C` | call the iterator: R[A+4] .. R[A+3+C] := R[A](R[A+1], R[A+2]) | OP_TFORCALL |
| `ForInLoop A Bx` | if R[A+4] is not nil, copy it to the control variable and jump back | OP_TFORLOOP |
| `Jump sJ` | relative jump; also used for goto, with the compiler resolving labels and checking that a goto never jumps into the scope of a local | OP_JMP |

The numeric for is the Lua counterpart of the Starlark VM's `RangeStart`/`RangeNext`, built into the language.

### 3.8 To-be-closed variables

| Opcode | Effect | Reference |
|---|---|---|
| `MarkToBeClosed A` | R[A] is a `<close>` local: its `__close` metamethod runs on every exit from its block, normal or by error | OP_TBC |

This is the same discipline as the Starlark VM's rule that every loop exit releases its iterator guard (VM-RAIL-005), but
it runs Lua code, so it needs the call-request mechanism and must handle an error raised inside `__close`.

## 4. Runtime features with no Starlark counterpart

### 4.1 Values

A tagged `Value` struct: nil, boolean, integer (`long`), float (`double`), light userdata, and a reference for strings,
tables, closures, userdata and threads (coroutines). Unlike the Starlark VM, which keeps boxed .NET objects, a Lua VM should
use an unboxed struct of 16 bytes (a tag and an 8-byte payload, plus an object reference). Numbers then need no allocation,
which removes the biggest cost measured in the Starlark comparison with Rust.

### 4.2 Strings

Lua strings are **byte strings**, not text: any byte, including zero, with `#s` counting bytes. In C# that means `byte[]`
(or `ReadOnlyMemory<byte>`) with interning for short strings, not `System.String`. The string library (`string.format`,
patterns, `string.rep`, `string.pack`) is large, and Lua patterns are not regular expressions; both need careful porting
and fuzzing.

### 4.3 Upvalue lifetime

See section 3.2. Open upvalues are tracked per frame, sorted by register, and closed in bulk by `CloseUpvalues`, on return,
and when a coroutine dies.

### 4.4 Open result ranges

When a call or `...` produces "all results", the count is known only at run time. The frame records the top of the
produced values, and the next instruction (`Call` with B = 0, `Return` with B = 0, `SetList` with B = 0) consumes up to it.
The verifier must check that an open range is always consumed by the very next instruction.

### 4.5 Coroutines

`coroutine.create`, `resume`, `yield`, `wrap`, `status`, `isyieldable`, `running`, `close`. **Heap frames make these
cheap**: a coroutine is its own frame stack and register file; `resume` switches the dispatch loop to it, and `yield` saves
its program counter and switches back. No .NET thread, `Task` or stack copying is needed. This is the main reason Lua suits
event-driven, testbench-style scripts.

The one hard case is a yield across a host (C#) function, for example a host iterator called from Lua that itself calls
back into Lua, which then yields. The reference interpreter handles it with continuation functions (`lua_yieldk`). Here the
choice is either the same continuation style for host functions that need it, or a clear error: "attempt to yield across a
host function".

### 4.6 Garbage collection, weak tables, finalizers

.NET's collector manages memory, so no separate collector is written (unlike starlark-rust). Two Lua features still need
work:

- **Weak tables** (`__mode = "k"`, `"v"` or `"kv"`): implemented with `ConditionalWeakTable` for weak keys and `WeakReference`
  for weak values, with Lua's rules for when entries disappear. Lua's ephemeron semantics for weak-key tables match
  `ConditionalWeakTable` closely.
- **`__gc` finalizers**: deterministic order is impossible on .NET's collector. Either they are not supported (an error when
  set), or they run from a queue at safe points. That is a documented deviation.

`collectgarbage` reports and controls collection; most of its options cannot map onto .NET and would be accepted and
ignored, or report approximate figures. This is also a documented deviation.

### 4.7 The debug library

`debug.traceback`, `getinfo` and `getlocal` map onto the frame stack and the line table, like `ScriptThread`. `sethook`
with a count maps onto the step budget. `setlocal`, `setupvalue`, `setmetatable` on arbitrary values and `getregistry`
break the sandbox and must be omitted from the default environment.

### 4.8 load and chunks

`load` compiles a string or function into a chunk at run time. It must go through the same compiler, verifier and budgets,
reject precompiled binary chunks (mode "t" only, because binary chunks bypass the compiler's guarantees), and charge
compilation to the budget.

### 4.9 Integer and float rules

Integer division and modulo by zero raise errors. Float division by zero gives inf or nan. String-to-number coercion in
arithmetic ("10" + 1 is 11) and number-to-string coercion in concatenation are language rules and must follow the manual
exactly. `math.tointeger`, `math.type` and `string.format("%d", 3.0)` all have exact rules.

### 4.10 Errors

`error(value, level)` raises any Lua value, not just a string. `pcall` and `xpcall` catch errors **inside the script**: a
feature Starlark does not have. For the railway contract this means:

- A Lua error caught by `pcall` stays inside the script, as a value on the script's own failure track.
- An uncaught Lua error reaches the host as a single documented exception type (say `LuaException`), carrying the Lua error
  value and a traceback.
- **Budget errors (steps, time, memory) and host cancellation must not be catchable by `pcall`.** Otherwise a script could
  swallow its own time limit in a loop around `pcall`. The reference interpreter has the same rule for memory errors in
  hooks. This is the single most important rule for running untrusted Lua.

## 5. Sandbox and robustness

Lua's standard library is designed for trusted scripts. A safe default environment would:

| Library | Treatment |
|---|---|
| `os` | Removed, except `os.time`, `os.clock` and `os.date` if the host wants them (as host-provided functions, so determinism is the host's choice). |
| `io` | Removed. |
| `package`, `require`, `dofile`, `loadfile` | Replaced by a host loader, as `load()` is in the Starlark interpreter's `SourceLoader`. |
| `load` | Text chunks only, through the verifier and budgets (section 4.8). |
| `debug` | Read-only subset (section 4.7). |
| `string.rep`, `string.format` widths, `table.concat`, `string.pack` | Bounded by the memory budget before allocating, exactly as the Starlark interpreter bounds repetition. |
| `collectgarbage` | Restricted (section 4.6). |
| `coroutine` | Kept; coroutines count towards the memory budget, and the number alive is capped. |
| Metatable of strings | Kept but protected (`__metatable` set), so scripts cannot change string behaviour for others. |

The same audit discipline as the Starlark interpreter's would apply: no input may crash, hang or exhaust the host;
exhaustive small-alphabet tests for the lexer and patterns; fuzzing; and a soak under a memory cap.

## 6. Interop with C#

The typed interop of starlark-cs carries over: `Register` for delegates, `Expose` for objects (as userdata with a
metatable whose `__index` maps to [Expose]d members), and conversion of results. Lua adds:

- **Multiple return values:** a C# method returning a tuple, `ValueTuple` or `out` parameters can return several Lua values.
- **Lua functions as C# delegates:** a host can call a Lua function as a `Func<...>` through a call request.
- **Async host functions as coroutine yields:** a host `Task` can suspend the calling coroutine until it completes,
  integrating cleanly with the coroutine model of section 4.5. This is the Lua counterpart of the Starlark interpreter's
  host futures, and arguably more natural.

## 7. Testing

- The official Lua 5.4 test suite (`lua-5.4.x-tests`), which is large and exercises the corner cases above.
- Differential runs against the reference `lua` executable: the same program on both, comparing output, errors and, where
  defined, results.
- Fuzzing of the lexer, the parser, patterns and `string.format`.
- The same verifier, line-table and railway tests as the Starlark VM.

## 8. Size and comparison

**Size.** Roughly comparable to the Starlark interpreter and VM together, plus the string library and patterns, which are
the biggest single parts. Lua's grammar is smaller than Starlark's; its runtime semantics (metatables, multiple values,
coroutines, coercions, the string library) are larger.

**Existing .NET options**, from `docs/vm-research.md`:

- **MoonSharp:** a C# Lua 5.2 interpreter. It has no budgets, a known crash path in its fixed stacks, and has been
  unmaintained for years.
- **NLua / KeraLua:** bindings to the native Lua 5.4. Fast and current, but native code, with no WebAssembly and no .NET
  memory budget.

A C# Lua VM along these lines would be the first with the robustness guarantees of starlark-cs. Whether it is worth the
effort depends on whether the owner's tools need scripts that are long-running and stateful, rather than rules and
configuration. For testbenches, C# `async` already covers it.
