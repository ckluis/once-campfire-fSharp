use campfire_richtext::dom::{Context, Dom};
use serde_json::{json, Value};
use std::io::{BufRead, Write};

fn main() {
    let stdin = std::io::stdin();
    let stdout = std::io::stdout();
    let mut out = std::io::BufWriter::new(stdout.lock());
    for line in stdin.lock().lines() {
        let line = line.unwrap();
        if line.is_empty() { continue; }
        let v: Value = serde_json::from_str(&line).unwrap();
        let html = v.as_str().unwrap();
        let mut results = Vec::new();
        for ctx in ["body", "table", "tr", "td", "select", "template", "head", "html", "colgroup", "tbody", "caption", "frameset", "title", "textarea", "script", "style", "plaintext", "noscript", "p", "a"] {
            let mut dom = Dom::new();
            let r = dom.parse_nodes(html, &Context::html(ctx));
            let s = match r {
                Ok(nodes) => json!({"ok": nodes.iter().map(|&n| dom.to_html(n)).collect::<String>()}),
                Err(e) => json!({"err": e.to_string()}),
            };
            results.push(s);
        }
        writeln!(out, "{}", serde_json::to_string(&results).unwrap()).unwrap();
    }
}
