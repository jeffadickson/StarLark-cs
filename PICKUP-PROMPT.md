# Pickup prompt - Starlark in C# (dev\starlark-cs)

Paste this into a new session opened in `C:\Users\dickson\dev\starlark-cs`.

---

You are continuing work on a C# (net10.0) implementation of the Starlark language: `Starlark.Syntax` (port of the
starlark-rust lexer/parser) and `Starlark.Eval` (a tree-walking interpreter written from the spec). It was moved on 2026-11-02
from `dev\clipforge\starlark-cs` to `dev\starlark-cs`. It is a git repo (branch `main`) with a GitHub remote,
https://github.com/jeffadickson/StarLark-cs. Do not push without the user saying so.

1. Another session may be working here: run `git status` first. At the time of writing `bench/Starlark.Profile/` was
   untracked. Do not touch uncommitted files you did not create, and do not reset or clean the tree.
2. Read `AGENT.md`. Reference material: the spec and conformance tests are in `docs/spec`; the Rust source is an ignored
   sibling copy at `..\starlark-rust-src` (from https://github.com/facebook/starlark-rust, exact commit unknown).
3. Build and test with the standard `dotnet build Starlark.slnx` and `dotnet test Starlark.slnx`. Report the real counts you see.
   Note `bench/Starlark.Bench` is not in the solution.
4. Open work from `AGENT.md` section 3: bytes string-like methods, no memory limit for untrusted scripts, two deviations
   (`dict.update(None)`, negative `list.pop`), coverage below the 96% target (lowest files `Dialect.cs`, `AstModule.cs`, `Ty.cs`),
   and no README. Add a README that says how to build, test and run.

Rules: tests stay on **xunit** (the user decided this explicitly; do not offer NUnit again). Comments are XML doc comments.
Property tests use FsCheck and Hedgehog; mutation testing uses Stryker.NET (`stryker-config.json` exists, untracked until the
user commits it). Avoid `sed` on C# files with `\n` escapes; use Edit or Write. Decisions to keep: UTF-16 strings, BigInteger ints,
spec-style hash(). Do not claim a test passed that you did not run.
