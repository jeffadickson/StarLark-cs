# Deliberate deviations and known gaps

This port follows the Starlark spec and the starlark-rust behaviour. Where it differs on purpose, it is listed here.

## Behaviour

- **String elements default to UTF-16.** `len("😀")` is 2. The spec allows 8-bit or 16-bit elements. Set
  `Interpreter.StringElements = StringElements.Utf8` for Go/Rust-compatible offsets, or use the separate `utf16` type.
- **`dict.update(None)` is an error**, rather than a no-op.
- **Negative indexes work with `list.pop`** (`xs.pop(-1)`), as in Python and the reference implementations.
- **Recursion is an error**, per the spec, and call depth is also capped at 150. Set `Dialect.EnableRecursion` to allow it, as
  Go's `option:recursion` does: the cap then rises to 1000 (`Interpreter.MaxCallDepth` changes it either way), and a runaway
  recursion ends in the error "Starlark stack overflow" long before the process stack runs out.
- **Compared with Starlark-Go** (checked against its own test data, `starlark/testdata`): `chr`, `ord`, `string.codepoints()`,
  `codepoint_ords()`, `elem_ords()`, the `module` type (`json` and `typing` are modules), `json.encode_indent`, positional-only
  `dict.get`, `pop` and `setdefault`, and `ScriptThread` (Go's `Thread`) are all provided. Still different: `assert` is a
  reserved word here (Go allows it), a tab is not accepted as indentation (Go accepts it), `while` is not supported, error
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
- Untrusted scripts: set `MaxSteps`, `Timeout` and/or `Cancellation`. Exceeding a step or time budget throws
  `ExecutionLimitException`; cancellation throws `OperationCanceledException`. Neither can be caught by a script.
  There is no memory limit.
