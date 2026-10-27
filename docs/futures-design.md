# Futures for Starlark for .NET: implementation plan (draft for review)

Requirements: `docs/requirements/futures.sdoc`. This document is the plan that satisfies them.

## Goal

An opt-in extension (dialect flag `EnableFutures`) that lets a script start work concurrently and collect results,
with C# doing the scheduling (`Task`, thread pool, `CancellationToken`). Standard programs must behave identically
with the flag off. Starlark's guarantees (hermetic, deterministic, safe to run untrusted) must survive.

## Script-visible API

```python
f = spawn(fn, arg1, key = v)   # starts now, returns a future immediately
f.get()                        # blocks; returns the result or re-raises the callee's error (original location)
f.done()                       # non-blocking bool
all_of([f, g])                 # results as a list, in argument order
first_of([f, g])               # (index, result) of the first to finish; documented as nondeterministic
```

Host side: with the flag on, a registered function that returns `Task`/`Task<T>`/`ValueTask<T>` returns a future
instead of blocking (today it blocks the script thread in `Interpreter.AwaitHostTask`).

Typing (type dialect): `future[T]`; `spawn` and `get` typed for the checker.

## Current architecture facts (read the code)

* `Interpreter` is one object holding: universe (`_universe`), module cache (`_moduleCache`), recursion set
  (`_active`), call depth (`_depth`), step counter (`_steps`), deadline, `_executeNesting`, poll counter, host
  signature tables, plus options (`MaxSteps`, `Timeout`, `Cancellation`, `MaxAllocatedBytes`, `StringElements`).
* `StarFunction` holds `Owner` (an `Interpreter`) and `Call` forwards to `Owner.CallFunction`, so a function from a
  loaded module always runs against its owner's mutable run state.
* `Strings.Mode` (string element size) and `MemoryBudget` are `[ThreadStatic]`, set by `Execute`/`CallValue`.
* Values: `StarList`/`StarDict`/`StarSet` (extend `Mutable`: `Frozen` flag and an `_iterating` counter, not
  thread-safe), `StarStruct`, `StarRecord`, `StarFunction` (captures `Frame` closures), `IHostValue`.
* Modules freeze their globals after execution (`Freezer.Freeze`), including closures a function captured.
* Host functions are .NET delegates and may touch arbitrary host state.

## Plan

1. **Per-call execution context (FUT-PRE-001/002).** Introduce `ExecutionContext` holding steps, deadline, depth,
   active-recursion set, poll counter, string mode, memory budget, cancellation token. `CallFunction`/`EvalExpr`
   receive it (or it is ambient via `AsyncLocal`/explicit parameter). `Interpreter` keeps only immutable
   configuration plus the universe. Frozen modules become callable from many threads.
2. **Isolation (FUT-LANG-005).** `spawn` freezes the function's captured state and every argument (`Freezer.Freeze`)
   before starting, and freezes the result before handing it back. A task mutating shared data hits the existing
   frozen-value error.
3. **Future value.** `StarFuture` wraps a `Task<object?>`; implements `IHostValue` (`get`, `done` attributes).
   `get()` waits on the task; an error is rethrown as a `StarlarkException` carrying the original span/codemap.
   `all_of` = `Task.WhenAll` in argument order; `first_of` = `Task.WhenAny`.
4. **Limits (FUT-HOST-002/003).** Child context gets the parent's remaining step budget (a share or shared atomic
   counter), the same deadline, a linked `CancellationTokenSource`; a `SemaphoreSlim` caps concurrent tasks.
   Futures not collected when the run ends are cancelled and awaited.
5. **Host tasks as futures (FUT-HOST-001).** `Interop.Invoke` returns a `StarFuture` when the flag is on and the
   delegate returns a task.
6. **Checker (FUT-VER-002).** `future[T]` type, `spawn`/`all_of`/`first_of` signatures.
7. **Tests.** Stress test on one frozen module from many threads; determinism; error propagation; cancellation;
   limits; equivalence property (Hedgehog + FsCheck): `spawn(f, x).get()` equals `f(x)`; many example programs;
   included in the long soak.

## Open questions for the reviewers

* Is freezing arguments/closures/results on spawn sufficient to rule out data races, given `Mutable._iterating`,
  closures sharing `Frame.Vars` dictionaries, and `StarFunction` defaults?
* Frames: a function's closure `Frame` has a mutable `Vars` dictionary. Is it safe for two tasks to run the same
  frozen function (each call creates its own frame, but the captured outer frame is shared)?
* Should the step budget be shared (atomic) or divided? What is the right semantics for the deadline?
* Deadlock risks: `get()` blocking a thread-pool thread while tasks wait for pool threads. Cap vs starvation.
* What happens to a future whose error is never observed? Is cancelling uncollected futures at the end of a run right?
* `first_of` nondeterminism vs the determinism requirement; should it exist at all?
* Memory model for results crossing threads (publication safety of frozen objects).
* Recursion detection (`_active`) and call depth across tasks.
* Host functions: reentrancy and the thread-bound `StringElements` mode.
