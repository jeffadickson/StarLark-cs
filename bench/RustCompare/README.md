# starlark-cs vs starlark-rust comparison (QA-PERF-002)

Two harnesses time the same `.star` files in `workloads/` in-process and print the same tab-separated table
(median, p10, p90, min in ms, run count, and the repr of the global `r` so results can be checked to agree).
The report is `docs/perf-rust-comparison.md`.

- `bench/RustCompare` (this folder): Rust, uses the `starlark` crate from a local starlark-rust checkout as a path
  dependency (edit the path in `Cargo.toml` if yours is not at `C:/Users/dickson/dev/starlark-rust-src/starlark-rust-main`).
  It also has a `--spawn N -- program args...` mode used to time process cold starts.
- `bench/Starlark.RustCompare`: C#, references `src/Starlark.Eval`.

Run from the repository root (PowerShell):

```powershell
cd C:\Users\dickson\dev\starlark-cs
cargo build --release --manifest-path bench/RustCompare/Cargo.toml --target-dir $env:TEMP\rustcompare-target
& "$env:TEMP\rustcompare-target\release\rust_compare.exe" --dir bench/RustCompare/workloads --seconds 5

$env:DOTNET_ROOT = 'C:\Users\dickson\.dotnet11'; $env:DOTNET_GCHeapHardLimit = '0x80000000'
dotnet build -c Release bench/Starlark.RustCompare
bench\Starlark.RustCompare\bin\Release\net11.0\Starlark.RustCompare.exe --dir bench/RustCompare/workloads --seconds 5
```

Options for both: `--workload name` (one workload), `--seconds s` (time budget per workload, default 3),
`--once name` (evaluate once and exit; for cold-start timing).

Cold start, 20 spawns each (the starlark-rust CLI is `cargo build --release -p starlark_bin` in the starlark-rust tree):

```powershell
cd C:\Users\dickson\dev\starlark-cs
$t = "$env:TEMP\rustcompare-target\release\rust_compare.exe"
& $t --spawn 20 -- path\to\starlark.exe bench/RustCompare/workloads/loops.star
& $t --spawn 20 -- $t --dir bench/RustCompare/workloads --once loops
& $t --spawn 20 -- bench\Starlark.RustCompare\bin\Release\net11.0\Starlark.RustCompare.exe --dir bench/RustCompare/workloads --once loops
```
