#!/usr/bin/env python3
"""F# results against the stored Rust baseline, and the Phase 7 progress log.

  quicklog.py compare DIR                 table of the F# runs in DIR against bench/results/phase7-start (stored Rust medians,
                                          the F# starting point), with the validity checks
  quicklog.py log DIR --unit U --tier N --change "..." --kept true|false [--build mounted|DIGEST] [--commit SHA]
                                          append one line to bench/results/phase7-log.jsonl (raw F# numbers per workload), then rebuild
                                          index.html (the climb chart) with bench/progress/build.py

Rust is never measured here: its numbers are the medians of bench/results/phase7-start/rust-*.json (the same warm-up and harness).
Every ratio this prints is "vs stored Rust baseline".
"""
import argparse
import glob
import json
import os
import statistics
import subprocess
import sys
from datetime import datetime, timezone

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
START = os.path.join(ROOT, "bench/results/phase7-start")
LOG = os.path.join(ROOT, "bench/results/phase7-log.jsonl")
WORKLOADS = ["room_show", "messages_page", "sidebar", "search", "post_message"]
LABEL = {"room_show": "room page", "messages_page": "messages page", "sidebar": "sidebar", "search": "search", "post_message": "post a message"}


def load(directory, app):
    runs = []
    for p in sorted(glob.glob(os.path.join(directory, f"{app}-[0-9].json"))):
        with open(p) as f:
            runs.append(json.load(f))
    return runs


def cells(runs):
    """{(route, conc): {"rps": [..], "cpu": [..], "runs": [..]}} over the gzip runs of every rep."""
    out = {}
    for r in runs:
        for h in r["http"]:
            if not h.get("gzip"):
                continue
            c = out.setdefault((h["route"], h["conc"]), {"rps": [], "cpu": [], "runs": []})
            c["rps"].append(h["rps"])
            cpu = h.get("cost", {}).get("server_cpu_us_per_req")
            if cpu is not None:
                c["cpu"].append(cpu)
            c["runs"].append(h)
    return out


def med(v):
    return statistics.median(v) if v else None


def spread(v):
    return "" if len(v) < 2 else f" [{min(v):,.0f}-{max(v):,.0f}]"


def fmt(x, d=0):
    return "-" if x is None else f"{x:,.{d}f}"


def compare(directory):
    mine = cells(load(directory, "fsharp"))
    if not mine:
        sys.exit(f"no fsharp-N.json in {directory}")
    rust = cells(load(START, "rust"))
    start = cells(load(START, "fsharp"))
    print(f"F# in {directory} against the stored Rust baseline (bench/results/phase7-start, medians of 3 Rust reps) and the F# starting point\n")
    print("| Workload | c | F# req/s | F# start | vs start | Rust stored | **F# / Rust (vs stored Rust baseline)** | F# CPU us/req | start | Rust stored |")
    print("|---|---|---|---|---|---|---|---|---|---|")
    for w in WORKLOADS:
        for c in (16, 1):
            m = mine.get((w, c))
            if not m:
                continue
            r, s = rust.get((w, c)), start.get((w, c))
            mr, rr, sr = med(m["rps"]), med(r["rps"]) if r else None, med(s["rps"]) if s else None
            print(f"| {LABEL[w]} | {c} | {fmt(mr)}{spread(m['rps'])} | {fmt(sr)} | {mr / sr:.3f}x | {fmt(rr)} | **{mr / rr:.3f}x** | "
                  f"{fmt(med(m['cpu']), 1)}{spread(m['cpu']) if len(m['cpu']) > 1 else ''} | {fmt(med(s['cpu']), 1) if s else '-'} | {fmt(med(r['cpu']), 1) if r else '-'} |")
    # validity: statuses, errors, response bytes, rows written, warm-up, host noise
    print("\nChecks:")
    bad = 0
    for (w, c), m in sorted(mine.items()):
        for h in m["runs"]:
            non2xx = {k: v for k, v in h["statuses"].items() if not k.startswith("2")}
            if non2xx or h["errors"]:
                bad += 1
                print(f"  FAIL {w} c={c}: statuses {h['statuses']} errors {h['errors']}")
            if not h.get("warmup", {}).get("settled", True):
                print(f"  note {w} c={c}: warm-up did not settle ({h['warmup'].get('reason')})")
            ratio = h.get("host_probe_after", {}).get("ratio")
            if ratio and ratio > 1.10:
                print(f"  note {w} c={c}: host probe {ratio} after the window (noisy host)")
    print(f"  responses: every one 2xx with 0 connection errors in {sum(len(m['runs']) for m in mine.values())} runs" if not bad else f"  {bad} run(s) with errors")
    rust_probe = {p["route"]: p["gzip"]["decoded_bytes"] for p in (load(START, "rust") or [{"probe": []}])[0]["probe"]}
    for r in load(directory, "fsharp"):
        for p in r["probe"]:
            want = rust_probe.get(p["route"])
            if want is not None and p["gzip"]["decoded_bytes"] != want:
                print(f"  FAIL decoded bytes {p['route']}: {p['gzip']['decoded_bytes']} vs Rust {want}")
                bad += 1
        for row in r["post_rows"]:
            if row["rows"] != sum(v for k, v in row["statuses"].items() if int(k) < 400):
                print(f"  FAIL post rows {row}")
                bad += 1
    if not bad:
        print("  decoded bytes equal Rust's on every probed route; post rows written equal the 2xx answers in every phase")
    return bad


def log(args):
    mine = cells(load(args.dir, "fsharp"))
    workloads = {}
    for (w, c), m in mine.items():
        workloads.setdefault(w, {})[f"c{c}"] = {"rps": round(med(m["rps"]), 1), "cpu_us": round(med(m["cpu"]), 1) if m["cpu"] else None, "reps": len(m["rps"])}
    commit = args.commit or subprocess.run(["git", "-C", ROOT, "rev-parse", "--short", "HEAD"], capture_output=True, text=True).stdout.strip()
    entry = {"time": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"), "unit": args.unit, "tier": args.tier, "change": args.change,
             "commit": commit, "build": args.build, "kept": args.kept == "true", "workloads": workloads}
    with open(LOG, "a") as f:
        f.write(json.dumps(entry) + "\n")
    print("logged:", json.dumps(entry))
    subprocess.run([sys.executable, os.path.join(ROOT, "bench/progress/build.py")], check=True)


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    c = sub.add_parser("compare"); c.add_argument("dir")
    l = sub.add_parser("log"); l.add_argument("dir"); l.add_argument("--unit", required=True, choices=["database", "runtime", "crypto-logging", "post"])
    l.add_argument("--tier", type=int, required=True, choices=[2, 3]); l.add_argument("--change", required=True)
    l.add_argument("--kept", required=True, choices=["true", "false"]); l.add_argument("--build", default="mounted"); l.add_argument("--commit")
    a = ap.parse_args()
    if a.cmd == "compare":
        sys.exit(1 if compare(a.dir) else 0)
    log(a)
