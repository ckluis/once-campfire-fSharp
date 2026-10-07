//! Reads one JSON case per line on stdin and writes what the Rust views crate renders for it, one JSON
//! line each; the F# tool in `fsharp/` answers the same lines and `bin/views-differential` compares the
//! two byte for byte. The first line may be `{"shared": {...}}`: values every case's `ctx` falls back to
//! (`importmap_tags`, `stylesheet_tags`), so they are not repeated on every line.
//!
//! A case is `{"id", "op", "ctx"?, "args"?}` and the answer `{"id", "out", "fragments"?}` (`out` is the
//! rendered text; `fragments` lists a recorded page's `[offset, length]` of each fragment), or
//! `{"id", "error"}`. Adding a template or helper is a branch of `run` here and one in
//! `fsharp/Operations.fs`, and cases for it in `generate.py`.
//!
//! Every operation's arguments are described where `run` reads them.

mod ops;
mod pages;
mod rest;

// The templates this tool declares (`ops.rs`, `pages.rs`) name `crate::messages` as the views crate's own do.
pub use campfire_views::messages;

use std::io::{BufRead, Write};

use serde_json::{Value, json};

fn main() {
    let args: Vec<String> = std::env::args().collect();
    let stdin = std::io::stdin();
    let mut shared = Value::Null;
    let out = std::io::stdout();
    let mut out = std::io::BufWriter::new(out.lock());
    let bench: Option<usize> = (args.get(1).map(String::as_str) == Some("bench")).then(|| args[2].parse().unwrap());
    let mut cases: Vec<Value> = Vec::new();
    for line in stdin.lock().lines() {
        let line = line.unwrap();
        if line.trim().is_empty() {
            continue;
        }
        let case: Value = serde_json::from_str(&line).unwrap();
        if case.get("shared").is_some() {
            shared = case["shared"].clone();
            continue;
        }
        cases.push(case);
    }
    if let Some(rounds) = bench {
        ops::bench(&cases, &shared, rounds);
        ops::bench_page(&cases, &shared, rounds * 50);
        ops::bench_hot_pages(&cases, &shared, rounds * 50);
        rest::bench_pages(&cases, &shared, rounds * 50);
        ops::bench_cache(rounds * 500);
        return;
    }
    for case in &cases {
        let id = case["id"].clone();
        let answer = match ops::run(case, &shared) {
            Ok(mut answer) => {
                answer["id"] = id;
                answer
            }
            Err(error) => json!({"id": id, "error": error}),
        };
        writeln!(out, "{}", serde_json::to_string(&answer).unwrap()).unwrap();
    }
}
