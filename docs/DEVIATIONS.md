# Deliberate deviations and known gaps

This port follows the Starlark spec and the starlark-rust behaviour. Where it differs on purpose, it is listed here.

## Behaviour

- **String elements default to UTF-8 bytes**, as in Go and Rust: `len("😀")` is 4, and `len("é")` is 2. The spec allows 8-bit or
  16-bit elements. Slicing or indexing inside a multi-byte character is an error. Set
  `Interpreter.StringElements = StringElements.Utf16` for Java-compatible, .NET-native offsets (`len("😀")` is 2), or use the
  separate `utf16` type for one string at a time. **This default changed from UTF-16** on 2026-11-04; a host that relied on the
  old default must now set `Utf16` explicitly.
- **`dict.update(None)` is an error**, rather than a no-op.
- **Negative indexes work with `list.pop`** (`xs.pop(-1)`), as in Python and the reference implementations.
- **Recursion is an error**, per the spec, and call depth is also capped at 150. Set `Dialect.EnableRecursion` to allow it, as
  Go's `option:recursion` does: the cap then rises to 1000 (`Interpreter.MaxCallDepth` changes it either way), and a runaway
  recursion ends in the error "Starlark stack overflow: more than N nested calls" long before the process stack runs out.
  The thread's remaining stack is checked too, so on a small stack the run can end before the cap is reached: an ordinary
  1 MB thread holds about 600 nested calls of a simple function. **This differs from Go**, whose cap is 100,000 frames on
  goroutine stacks that grow to 1 GB. To go as deep, raise `MaxCallDepth` and set `Interpreter.ScriptStackSize` (for
  example to 256 MB), which runs each script on its own thread with that stack; 100,000 calls then take about 6 seconds.
- **Comparison depth is not capped at 10.** Go's `CompareLimit` makes comparing values nested more than 10 deep an error;
  here such data compares normally, and only nesting deep enough to threaten the stack is an error ("nested too deeply").
- **Nesting is counted in more places than Go counts it** (see the table below). Both stop at 1000 levels, but Go's parser
  counts a level per call of certain functions, so the depth it allows depends on the construct (75 parentheses, 987 unary
  minus, 493 lambdas), while here a level is one bracket, block, unary operator, lambda, `elif` and so on, so 1000 is 1000
  of anything. Go does not count `elif` chains or `.name`/`[i]`/`(call)` suffixes at all; here each counts, because each
  makes the tree one level deeper and every walker over the tree recurses through it.

## Limits

Every limit that keeps a script from crashing, hanging or exhausting the host, against Starlark-Go (`go.starlark.net`).
The parser-side limits throw `StarlarkSyntaxException`, the run-time ones `StarlarkException` unless noted, and the
budgets `ExecutionLimitException` (whose `Limit` names the budget). All errors are ordinary exceptions: none of these
limits can end the process.

| Limit | Value | Error text | Compared with Go |
|---|---|---|---|
| Nesting of source text (`SyntaxLimits.MaxNesting`) | 1000 levels: brackets, blocks, `elif`, lambdas, unary operators, `not`, right operands, conditionals, f-strings, suffixes | "Parse error: excessive nesting: ..." | Same limit and phrase; Go counts differently (see above). A chain of one operator (`1 + 1 + ...`) is not nesting and is unlimited, as in Go. |
| Open brackets in the lexer | 1000 | "Parse error: excessive nesting: more than 1000 open brackets, parentheses or braces" | Same as Go's scanner. |
| Stack left while parsing or walking a tree | what the thread's stack allows | parser: "excessive nesting: the thread ran out of stack"; run time: "Starlark stack overflow" (in a call) or "Program is nested too deeply" | Go has growable stacks and needs no such check. The deepest tree the parser accepts parses, resolves, runs and type checks on a 1 MB thread. |
| Repetition count (`s * n`, `b * n`, `l * n`, `t * n`) | must fit in an int32 | "repeat count N too large" | Same. |
| Repetition result (`Ops.MaxRepeatElements`) | fewer than 2^30 elements: UTF-16 chars, bytes, or items | "excessive repeat (L * N elements)" | Same (Go counts string bytes; .NET strings are UTF-16). Checked after `MaxAllocatedBytes`, so a host that sets it gets a memory error first. |
| Largest string built piecemeal (`MemoryBudget.MaxStringChars`) | 2^30 - 1 chars | "String result is too large" | Go has no separate limit. |
| Left shift count | 0 to 511 | "shift count too large: N", "negative shift count: N" | Same. A right shift count has no upper limit (as in Go: any count past the int's size gives 0 or -1). |
| Int size (`Ops.MaxIntBits`) | 2^20 bits (about 315,000 digits) | "Integer overflow in OP: the result would have more than 1048576 bits" | Go has no limit (its ints grow until memory runs out). Bounds multiplication, which the shift limit does not. |
| Call depth (`Interpreter.MaxCallDepth`) | 150; 1000 with `EnableRecursion` | "Maximum call depth exceeded"; with recursion "Starlark stack overflow: more than N nested calls" | Go: no cap without recursion (recursion itself is an error), 100,000 with it. See `ScriptStackSize` above. |
| Call depth in a task (`FutureLimits.MaxTaskCallDepth`) | 100 | "Task call depth exceeded: ..." | Go has no futures. |
| Comparison, printing, hashing, freezing of nested values | what the thread's stack allows; cycles print as `[...]`/`{...}` | "Value is nested too deeply: maximum recursion depth reached" | Go caps comparison at depth 10 (`CompareLimit`) and prints cycles as `...`; the cap is deliberately not adopted. |
| JSON encoding depth | 128 | "Value is nested too deeply to serialize to JSON" | Go: no depth limit, a cycle is an error. |
| Host conversion depth | 32 levels from a host object into a script, 200 from a script value out to a host | "...: nesting is too deep (cyclic?)" | Go has no reflection-based conversion. |
| Expanding an iterable (`Ops.MaxMaterialized`) | 100,000,000 elements | "Cannot expand ... elements: the limit is 100000000" | Go: none. |
| Steps (`Interpreter.MaxSteps`) | none by default | `ExecutionLimitException` "steps" | Go's `Thread.SetMaxExecutionSteps`. |
| Time, cancellation (`Timeout`, `Cancellation`) | none by default | `ExecutionLimitException` "time"; `OperationCanceledException` | Go's `Thread.Cancel`. |
| Largest single object (`MaxAllocatedBytes`) | none by default | `ExecutionLimitException` "memory" | Go has none. Usually stricter than the repetition ceiling. |
| Futures (`FutureLimits`) | 4 workers, 10,000 spawns, 1,000 live futures, 8 host operations, 16 MB buffered output | `ExecutionLimitException` "tasks", "host-operations", "memory" | Go has no futures. |
- **Compared with Starlark-Go** (checked against its own test data, `starlark/testdata`): `chr`, `ord`, `string.codepoints()`,
  `codepoint_ords()`, `elem_ords()`, the `module` type (`json` and `typing` are modules), `json.encode_indent`, positional-only
  `dict.get`, `pop` and `setdefault`, and `ScriptThread` (Go's `Thread`) are all provided. Still different: `assert` is a
  reserved word here (Go allows it), a tab is not accepted as indentation (Go accepts it), `while` is not supported.
  `x[1, 2] = 3` (assigning to a tuple-indexed element) follows Go: it stores the key `(1, 2)`, and `x[1, 2] += 1` works too.
  **This differs from Rust**, which reports "left-hand-side of assignment must take the form `a`, `a.b` or `a[b]`"; the shared
  conformance chunk `go/dict.star:89` and a grammar test were changed to expect Go's behaviour. A subscript with three or more
  elements is still a syntax error. A lambda is accepted as a comprehension condition (`[x for x in y if lambda: 0]`), as in Go. Error
  messages are worded differently in many places (the same difference as from the Rust and Java suites), and `bytes` has none of
  the string-like methods. A string cut inside a UTF-8 character cannot exist here (slicing it is an error), so Go's
  `ord("Й"[1:]) == 0xFFFD` has no equivalent; a lone UTF-16 surrogate reads as U+FFFD.

## Not implemented

- `bytes` has the operations the spec lists (`elems`, indexing, slicing, `+`, `in`, comparisons) but not the
  extra string-like methods that the spec marks as TODO.

## Embedding

- `Interpreter` is not thread-safe; use one instance per thread.
- `StringElements` is held in a thread-static while a script runs, so do not move a running script between threads
  (for example across an `await` inside a host function).
- Untrusted scripts: set `MaxSteps`, `Timeout`, `MaxAllocatedBytes` and/or `Cancellation`. Exceeding a step, time or
  memory budget throws `ExecutionLimitException` (`Limit` is `steps`, `time` or `memory`); cancellation throws
  `OperationCanceledException`. Neither can be caught by a script. `MaxAllocatedBytes` caps each single string, list,
  tuple or bytes value one operation builds; it does not limit the total memory a run uses, so combine it with
  `MaxSteps`, `Timeout` and a process-level memory cap.
