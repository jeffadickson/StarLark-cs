# Futures for Starlark for .NET: design (revision 2, after concurrency review)

Requirements: `docs/requirements/futures.sdoc`. Revision 1 was reviewed by four specialists (memory model, scheduling,
language semantics, resource limits). They found it unsound as written. This revision records what changed and why.

## Findings that changed the design

| Revision 1 said | Problem found | Revision 2 |
|---|---|---|
| Freeze arguments and the closure's captured state on spawn. | Freezing is in place and irreversible, so `spawn(f, xs)` makes the caller's `xs` immutable. Freezing a closure freezes the whole `Frame` chain, including the caller's unrelated locals and globals. Rebinding a name is not a mutation, so a loop-captured lambda races on `Frame.Vars` (a plain `Dictionary`). | **Copy, don't freeze, the caller's values.** Arguments are deep-copied (aliasing preserved). A task gets a **snapshot** of exactly the free variables its function references, frozen in the snapshot only. Later rebinding by the spawner is invisible. |
| Any function may be spawned. | Closures over live frames are the race source. | v1 accepts only a **module-level function of a loaded module**, a builtin, or a host callable. A closure over a live frame is an error with a clear message. |
| `done()` and `first_of` exist. | They expose scheduling and break the determinism promise; the sdoc exempted `first_of` but not `done()`. | They exist only under a second flag, `EnableNondeterministicFutures`, off by default. |
| Futures are first-class and may escape. | Storing, returning or passing them creates cross-task wait graphs, deadlock and leaked work. | Futures are **non-escaping handles**: not hashable, no truth value, never stored in a global, never returned from the spawning call, never passed to `spawn`. All futures must be collected before the spawning call (or the module) ends. |
| A `SemaphoreSlim` caps concurrency. | A task holding a permit while blocked in `get()` deadlocks (cap 4, depth-5 tree). The thread pool also starves. | Tasks **cannot spawn or call `get()` in v1**, so the task graph has depth 1 and deadlock is impossible by construction. Script tasks run on a **dedicated bounded scheduler**, never the shared pool. |
| "Inherit" the step budget. | Ambiguous: could multiply the budget by the task count. A shared atomic counter is nondeterministic about which task trips. | Each task gets a **deterministic slice** of the remaining steps (`remaining / task count`, or an explicit argument). The parent is charged the steps actually used when the task is joined, in program order. |
| Host `Task` functions return futures implicitly. | The same host function would change its type depending on a flag. Thousands of concurrent IO operations become possible. | A host function returns a future only if registered with an explicit **`AsFuture`** option. Others keep blocking. In-flight host operations are capped. |
| Cancel uncollected futures at the end of a run. | Hides bugs, makes side effects timing-dependent, and leaks work. | A future still pending when the spawning call ends is an **error**. At run end all tasks are cancelled and awaited for a bounded grace period; faults are observed so none surface as unobserved task exceptions. |
| Thread-static string mode and memory budget. | Pool threads start with defaults, so a task silently runs with no memory limit and UTF-16 strings. | Run state lives in an explicit **`ExecutionContext`** passed to every call; no `[ThreadStatic]` run state. The task entry point installs it and fails closed if absent. |
| `print` from tasks. | Concurrent output interleaves; a host `Action<string>` is invoked from several threads. | A task's output is **buffered** and appended at join time in index order. |
| Errors from `all_of` come from whichever task fails first. | Nondeterministic. | `all_of` waits for all tasks, then raises the error of the **lowest-index** failure. A limit error in any task always fails the run and cannot be caught by the script. |

## Script-visible API (v1, deterministic)

```python
results = parallel_map(fn, items)          # list, in input order
f = spawn(fn, arg, key = v)                # future; must be collected before the spawning call returns
x = f.get()                                # blocks; result or the callee's error with its original location
rs = all_of([f, g])                        # results in argument order; lowest-index error wins
```

Under `EnableNondeterministicFutures` only: `f.done()` and `first_of(futures)`.

`parallel_map` is the primary primitive: no handle escapes, fully typable (`Callable[[T], U], Iterable[T] -> list[U]`),
trivially deterministic. `spawn`/`get`/`all_of` are sugar over the same machinery for heterogeneous work.

## Architecture

1. **`ExecutionContext`** (new): steps, deadline, depth, active-recursion set (copied from the spawner at spawn),
   string mode, memory budget, cancellation token, output buffer, task counters. Created per top-level call and per
   task. `Interpreter` keeps only immutable configuration and the universe. This also makes frozen modules callable
   from many threads (FUT-PRE-001/002).
2. **Run object** (`FutureRun`): owns the root `CancellationTokenSource`, the registry of live futures, counters
   (spawns, live futures, depth), the dedicated scheduler, the closing flag, and the teardown routine.
3. **`StarFuture`** (`IHostValue`): wraps a task; `get` rethrows a fresh exception each call (original span kept,
   `AggregateException` unwrapped); not hashable, no truth value, repr `<future>`; `Freeze` is an error.
4. **Snapshot builder**: computes a function's free variables (the resolver already knows them), copies each binding,
   freezes the copies, memoised by identity so aliasing and sharing survive.
5. **Deep copy of arguments**: identity-preserving, charged to the step counter and memory budget.
6. **Checker**: `future[T]`, `parallel_map`, `spawn`, `all_of` signatures. `spawn` call-site argument checking is
   limited by the type dialect's lack of variadic callables; `parallel_map` types fully.

## Limits shipped by default for untrusted hosts

| Setting | Default |
|---|---|
| Concurrent tasks per run | 4 |
| Live uncollected futures | 1,000 |
| Total spawns per run | 10,000 |
| Spawn depth | 1 (tasks cannot spawn) |
| Step cost of a spawn | 1,000 |
| Step cost of a host call returning a task | 100 |
| In-flight host operations | 8 |
| Total memory across tasks | 256 MiB |
| Grace period at run end | 5 s |
| `all_of` list length | 1,000 |

## Other rules

* `load()` and `Execute` cannot run in a task. The module cache is only touched at module top level.
* `Mutable` iteration counters are not written for frozen values.
* Host values handed to a task must be frozen or declared thread-safe; the universe is frozen when futures are on.
* Registered host functions called from tasks must be thread-safe, or be registered as serialised.
* `StarFunction` stops using `Owner`'s run state; it takes the context explicitly.

## Implementation order

1. Execution context refactor, with a stress test running one frozen module's functions from many threads (red first).
2. Snapshot builder, deep copy, `StarFuture`, `parallel_map`, `spawn`/`get`/`all_of`, dedicated scheduler, run object.
3. Limits, teardown, host `AsFuture`.
4. Checker and type dialect.
5. Examples, property tests, soak.
