#!/usr/bin/env python3
"""Unit 7.5: F# runs in DIR... against phase7-final's F# and Rust medians (req/s, CPU us/req, p99 ms), c=1 and c=16."""
import glob, json, statistics, sys
ROUTES = ["room_show", "messages_page", "sidebar", "search", "post_message"]
def load(pattern):
    cells = {}
    for f in sorted(glob.glob(pattern)):
        for r in json.load(open(f)).get("http", []):
            if r["route"] in ROUTES and r["conc"] in (1, 16):
                cells.setdefault((r["route"], r["conc"]), []).append(
                    (r["rps"], r["cost"]["server_cpu_us_per_req"], r["latency"]["p99_ms"]))
    return {k: tuple(statistics.median(x[i] for x in v) for i in range(3)) + (len(v),) for k, v in cells.items()}
final_f = load("bench/results/phase7-final/fsharp-*.json")
final_r = load("bench/results/phase7-final/rust-*.json")
runs = [(d, load(d + "/fsharp-*.json")) for d in sys.argv[1:]]
hdr = "| Workload | c | Rust req/s / CPU / p99 | F# final req/s / CPU / p99 | " + " | ".join(f"{d.rstrip('/').split('/')[-1]} req/s / CPU / p99 (n) | vs F# final req/s, CPU, p99" for d, _ in runs) + " |"
print(hdr); print("|" + "---|" * (hdr.count("|") - 1))
for route in ROUTES:
    for c in (1, 16):
        r, f = final_r[(route, c)], final_f[(route, c)]
        row = f"| {route} | {c} | {r[0]:,.0f} / {r[1]:.1f} / {r[2]:.2f} | {f[0]:,.0f} / {f[1]:.1f} / {f[2]:.2f} |"
        for _, cells in runs:
            x = cells.get((route, c))
            row += " – | – |" if not x else f" {x[0]:,.0f} / {x[1]:.1f} / {x[2]:.2f} ({x[3]}) | {x[0]/f[0]:.3f}, {x[1]/f[1]:.3f}, {x[2]/f[2]:.2f} (vs Rust {x[0]/r[0]:.2f}x) |"
        print(row)
