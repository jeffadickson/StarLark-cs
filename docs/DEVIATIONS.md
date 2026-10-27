# Deliberate deviations and known gaps

This port follows the Starlark spec and the starlark-rust behaviour. Where it differs on purpose, it is listed here.

## Behaviour

- **String elements default to UTF-16.** `len("😀")` is 2. The spec allows 8-bit or 16-bit elements. Set
  `Interpreter.StringElements = StringElements.Utf8` for Go/Rust-compatible offsets, or use the separate `utf16` type.
- **`dict.update(None)` is an error**, rather than a no-op.
- **Negative indexes work with `list.pop`** (`xs.pop(-1)`), as in Python and the reference implementations.
- **Recursion is an error**, per the spec, and call depth is also capped at 150.

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
