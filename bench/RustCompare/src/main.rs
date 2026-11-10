//! In-process timing of starlark-rust on the shared workloads in ../workloads.
//!
//!   rust_compare [--dir workloads] [--seconds 3] [--workload name] [--once name]
//!
//! For each workload: parse + evaluate the source in a fresh Module per run (the same unit of work the C# harness
//! times: Interpreter.Execute = parse, resolve, execute). Warm-up runs first, then runs until the time budget is spent;
//! prints median, p10, p90, min (ms) and run count as tab-separated values.
//! `--once name` evaluates one workload once and exits (used for cold-start process timing).

use std::path::PathBuf;
use std::time::{Duration, Instant};

use starlark::environment::{Globals, LibraryExtension, Module};
use starlark::eval::Evaluator;
use starlark::syntax::{AstModule, Dialect, DialectTypes};

const NAMES: &[&str] = &[
    "loops", "calls", "strings", "lists", "dicts", "records", "closures", "bigints", "floats", "parse", "quicksort",
    "config", "textproc",
];

fn dialect() -> Dialect {
    Dialect { enable_top_level_stmt: true, enable_types: DialectTypes::Enable, ..Dialect::Standard }
}

fn globals() -> Globals {
    Globals::extended_by(&[LibraryExtension::StructType, LibraryExtension::RecordType, LibraryExtension::Json])
}

fn parse_source() -> String {
    let mut s = String::new();
    for i in 0..100 {
        s.push_str(&format!(
            "def fn{i}(a, b = {i}, *rest, **kw):\n    x = [a + k for k in range(b) if k % 2 == 0]\n    d = {{'a': a, 'b': [b, {i}]}}\n    return len(x) + d['a']\n"
        ));
    }
    s
}

fn run_once(name: &str, src: &str, g: &Globals, d: &Dialect) -> Result<(), String> {
    let ast = AstModule::parse(name, src.to_owned(), d).map_err(|e| e.to_string())?;
    if name == "parse" {
        std::hint::black_box(&ast);
        return Ok(());
    }
    Module::with_temp_heap(|module| {
        let mut eval = Evaluator::new(&module);
        let v = eval.eval_module(ast, g).map_err(|e| e.to_string())?;
        std::hint::black_box(v);
        let r = module.get("r").map(|v| v.to_repr());
        std::hint::black_box(r);
        Ok(())
    })
}

fn result_repr(name: &str, src: &str, g: &Globals, d: &Dialect) -> String {
    if name == "parse" {
        return "-".into();
    }
    let ast = AstModule::parse(name, src.to_owned(), d).unwrap();
    Module::with_temp_heap(|module| {
        let mut eval = Evaluator::new(&module);
        eval.eval_module(ast, g).unwrap();
        module.get("r").map(|v| v.to_repr()).unwrap_or_default()
    })
}

fn main() {
    let args: Vec<String> = std::env::args().collect();
    let get = |n: &str| args.iter().position(|a| a == n).and_then(|i| args.get(i + 1)).cloned();
    let dir = PathBuf::from(get("--dir").unwrap_or_else(|| "workloads".into()));
    let seconds: f64 = get("--seconds").and_then(|s| s.parse().ok()).unwrap_or(3.0);
    let only = get("--workload");
    if let Some(i) = args.iter().position(|a| a == "--spawn") {
        // Cold-start timer: `--spawn N -- program args...` runs the program N times, reports wall-clock ms.
        let n: usize = args[i + 1].parse().expect("--spawn N");
        let sep = args.iter().position(|a| a == "--").expect("-- program");
        let (prog, rest) = (&args[sep + 1], &args[sep + 2..]);
        let mut times = Vec::new();
        for _ in 0..n {
            let t = Instant::now();
            let st = std::process::Command::new(prog).args(rest).stdout(std::process::Stdio::null()).status().expect("spawn");
            times.push(t.elapsed().as_secs_f64() * 1000.0);
            if !st.success() { eprintln!("exit {st}"); std::process::exit(1); }
        }
        times.sort_by(|a, b| a.partial_cmp(b).unwrap());
        let q = |p: f64| times[((times.len() - 1) as f64 * p).round() as usize];
        println!("median_ms\t{:.1}\tp10\t{:.1}\tp90\t{:.1}\tmin\t{:.1}\tn\t{}", q(0.5), q(0.1), q(0.9), times[0], n);
        return;
    }
    let g = globals();
    let d = dialect();
    let load = |n: &str| -> String {
        if n == "parse" { parse_source() } else { std::fs::read_to_string(dir.join(format!("{n}.star"))).expect("workload file") }
    };

    if let Some(n) = get("--once") {
        match run_once(&n, &load(&n), &g, &d) {
            Ok(()) => std::process::exit(0),
            Err(e) => { eprintln!("{e}"); std::process::exit(1) }
        }
    }

    println!("workload\tmedian_ms\tp10_ms\tp90_ms\tmin_ms\truns\tresult");
    for &n in NAMES {
        if only.as_deref().is_some_and(|o| o != n) { continue; }
        let src = load(n);
        if let Err(e) = run_once(n, &src, &g, &d) {
            println!("{n}\tERROR\t{}", e.replace('\n', " "));
            continue;
        }
        for _ in 0..12 { run_once(n, &src, &g, &d).unwrap(); }
        let budget = Duration::from_secs_f64(seconds);
        let start = Instant::now();
        let mut times = Vec::new();
        while start.elapsed() < budget || times.len() < 5 {
            let t = Instant::now();
            run_once(n, &src, &g, &d).unwrap();
            times.push(t.elapsed().as_secs_f64() * 1000.0);
        }
        times.sort_by(|a, b| a.partial_cmp(b).unwrap());
        let q = |p: f64| times[((times.len() - 1) as f64 * p).round() as usize];
        println!("{n}\t{:.4}\t{:.4}\t{:.4}\t{:.4}\t{}\t{}", q(0.5), q(0.1), q(0.9), times[0], times.len(), result_repr(n, &src, &g, &d));
    }
}
