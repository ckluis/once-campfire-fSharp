#!/usr/bin/env python3
"""Render the Phase 7 progress dashboard (target/phase7-dashboard/index.html) from bench/results/phase7-log.jsonl.

Run once after each logged tuning run; nothing rebuilds it on a timer.

F#'s throughput per workload as a share of Rust's stored numbers, over time. The Rust reference is
the median of the reviewed baseline's three Rust reps (bench/results/baseline-20261006/rust-*.json)
until bench/results/phase7-rust-reference.json exists. Writes target/phase7-dashboard/index.html,
which reloads itself every 30 s.
"""
import glob
import html
import json
import os
import statistics
from datetime import datetime, timezone

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
LOG = os.path.join(ROOT, "bench/results/phase7-log.jsonl")
OUT = os.path.join(ROOT, "target/phase7-dashboard/index.html")
WORKLOADS = [("room_show", "Room page"), ("messages_page", "Messages page"), ("sidebar", "Sidebar"),
             ("search", "Search"), ("post_message", "Post a message")]
CONCS = [("c16", 16), ("c1", 1)]


def rust_reference():
    ref = os.path.join(ROOT, "bench/results/phase7-rust-reference.json")
    if os.path.exists(ref):
        with open(ref) as f:
            return json.load(f), "bench/results/phase7-rust-reference.json"
    reps = {}
    for p in sorted(glob.glob(os.path.join(ROOT, "bench/results/phase7-start/rust-[0-9].json"))):
        with open(p) as f:
            for run in json.load(f)["http"]:
                if run.get("gzip"):
                    reps.setdefault((run["route"], run["conc"]), []).append(run["rps"])
    out = {}
    for w, _ in WORKLOADS:
        out[w] = {key: {"rps": statistics.median(reps[(w, c)])} for key, c in CONCS if (w, c) in reps}
    return out, "bench/results/phase7-start Rust medians, 3 reps with the long warm-up"


def baseline_fsharp():
    """The Phase 7 starting point (same harness and warm-up as every tuning run), so the chart starts where tuning began."""
    reps = {}
    for p in sorted(glob.glob(os.path.join(ROOT, "bench/results/phase7-start/fsharp-[0-9].json"))):
        with open(p) as f:
            for run in json.load(f)["http"]:
                if run.get("gzip"):
                    reps.setdefault((run["route"], run["conc"]), []).append(run["rps"])
    w = {wk: {key: {"rps": statistics.median(reps[(wk, c)])} for key, c in CONCS if (wk, c) in reps} for wk, _ in WORKLOADS}
    start = os.path.join(ROOT, "bench/results/phase7-start/fsharp-3.json")
    t = datetime.fromtimestamp(os.path.getmtime(start), timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ") if os.path.exists(start) else "2026-10-06T14:00:00Z"
    return {"time": t, "unit": "start", "tier": "start", "change": "Phase 7 starting point, before any tuning",
            "commit": "16a7da2", "build": "image", "kept": True, "workloads": w}


def entries():
    rows = [baseline_fsharp()]
    if not os.path.exists(LOG):
        return rows
    with open(LOG) as f:
        for line in f:
            line = line.strip()
            if line:
                try:
                    rows.append(json.loads(line))
                except json.JSONDecodeError:
                    pass
    return rows


def ratio(entry, rust, w, c):
    try:
        return entry["workloads"][w][c]["rps"] / rust[w][c]["rps"]
    except (KeyError, TypeError, ZeroDivisionError):
        return None


def chart(rows, rust, c):
    """Inline SVG: one polyline per workload, F#/Rust over the sequence of runs, with a parity line."""
    w_, h_, pad = 760, 260, 36
    pts = [(i, r) for i, r in enumerate(rows)]
    if not pts:
        return '<p class="empty">No runs logged yet.</p>'
    vals = [ratio(r, rust, w, c) for _, r in pts for w, _ in WORKLOADS]
    vals = [v for v in vals if v is not None] + [1.0]
    lo, hi = min(0.5, min(vals) - 0.05), max(1.2, max(vals) + 0.05)
    n = max(1, len(pts) - 1)
    x = lambda i: pad + (w_ - 2 * pad) * (i / n if len(pts) > 1 else 0.5)
    y = lambda v: h_ - pad - (h_ - 2 * pad) * (v - lo) / (hi - lo)
    colors = ["var(--c1)", "var(--c2)", "var(--c3)", "var(--c4)", "var(--c5)"]
    parts = ['<svg viewBox="0 0 %d %d" role="img" aria-label="F# throughput as a share of Rust, c=%s">' % (w_, h_, c[1:])]
    for g in [0.6, 0.8, 1.0, 1.2]:
        if lo <= g <= hi:
            parts.append('<line x1="%d" x2="%d" y1="%.1f" y2="%.1f" class="%s"/><text x="4" y="%.1f" class="axis">%.1f×</text>'
                         % (pad, w_ - pad, y(g), y(g), "parity" if g == 1.0 else "grid", y(g) + 4, g))
    for (wk, label), col in zip(WORKLOADS, colors):
        seq = [(x(i), y(v)) for i, r in pts if (v := ratio(r, rust, wk, c)) is not None]
        if seq:
            parts.append('<polyline fill="none" stroke="%s" stroke-width="2.5" points="%s"/>' % (col, " ".join("%.1f,%.1f" % p for p in seq)))
            parts.append('<circle cx="%.1f" cy="%.1f" r="4" fill="%s"><title>%s</title></circle>' % (seq[-1][0], seq[-1][1], col, html.escape(label)))
    parts.append("</svg>")
    legend = "".join('<span><i style="background:%s"></i>%s</span>' % (col, html.escape(label)) for (_, label), col in zip(WORKLOADS, colors))
    return "".join(parts) + '<div class="legend">%s</div>' % legend


def when(r):
    try:
        return datetime.fromisoformat(str(r["time"]).replace("Z", "+00:00"))
    except (KeyError, ValueError):
        return None


def overall(r, rust, c="c16"):
    """Geometric mean of F#/Rust across the five workloads; None unless all five were measured."""
    qs = [ratio(r, rust, w, c) for w, _ in WORKLOADS]
    if any(q is None for q in qs):
        return None
    prod = 1.0
    for q in qs:
        prod *= q
    return prod ** (1 / len(qs))


def headline(rows, rust):
    """F# climbing toward Rust: one point per kept version on a real time axis, labelled with its time."""
    pts = [(when(r), overall(r, rust), r) for r in rows]
    pts = [p for p in pts if p[0] and p[1] is not None]
    if not pts:
        return '<p class="empty">No versions measured yet.</p>'
    w_, h_, padl, padr, padt, padb = 1000, 380, 54, 70, 30, 64
    t0, t1 = pts[0][0].timestamp(), max(p[0].timestamp() for p in pts)
    span = max(t1 - t0, 3600)
    vals = [p[1] for p in pts]
    lo, hi = min(0.6, min(vals) - 0.05), max(1.15, max(vals) + 0.05)
    x = lambda t: padl + (w_ - padl - padr) * (t.timestamp() - t0) / span
    y = lambda v: h_ - padb - (h_ - padt - padb) * (v - lo) / (hi - lo)
    o = ['<svg viewBox="0 0 %d %d" role="img" aria-label="F# overall throughput as a share of Rust over time">' % (w_, h_)]
    g = 0.6
    while g <= hi + 1e-9:
        if g >= lo:
            o.append('<line x1="%d" x2="%d" y1="%.1f" y2="%.1f" class="grid"/><text x="8" y="%.1f" class="axis">%.1f×</text>'
                     % (padl, w_ - padr, y(g), y(g), y(g) + 4, g))
        g = round(g + 0.1, 2)
    o.append('<line x1="%d" x2="%d" y1="%.1f" y2="%.1f" class="rustline"/><text x="%d" y="%.1f" class="rustlabel">Rust</text>'
             % (padl, w_ - padr, y(1.0), y(1.0), w_ - padr + 8, y(1.0) + 5))
    line = " ".join("%.1f,%.1f" % (x(t), y(v)) for t, v, _ in pts)
    area = "%.1f,%.1f %s %.1f,%.1f" % (x(pts[0][0]), h_ - padb, line, x(pts[-1][0]), h_ - padb)
    o.append('<polygon points="%s" class="area"/>' % area)
    o.append('<polyline points="%s" class="climb"/>' % line)
    for i, (t, v, r) in enumerate(pts):
        last = i == len(pts) - 1
        o.append('<circle cx="%.1f" cy="%.1f" r="%d" class="%s"><title>%s · %.2f× · %s</title></circle>'
                 % (x(t), y(v), 7 if last else 5, "dot last" if last else "dot", t.strftime("%H:%M UTC"), v, html.escape(str(r.get("change", "")))))
        o.append('<text x="%.1f" y="%d" class="tick" text-anchor="middle">%s</text>' % (x(t), h_ - padb + 18, t.strftime("%H:%M")))
        if last or i == 0:
            o.append('<text x="%.1f" y="%.1f" class="val" text-anchor="middle">%.2f×</text>' % (x(t), y(v) - 14, v))
    o.append("</svg>")
    first, latest = pts[0], pts[-1]
    gain = (latest[1] / first[1] - 1) * 100
    hours = max((latest[0] - first[0]).total_seconds() / 3600, 1e-9)
    rate = gain / hours if len(pts) > 1 else 0.0
    gap = (1 / latest[1] - 1) * 100 if latest[1] < 1 else -(latest[1] - 1) * 100
    stats = ('<div class="stats"><div><b>%.2f×</b><span>of Rust now</span></div><div><b>%+.1f%%</b><span>since tuning began</span></div>'
             '<div><b>%+.1f%%/h</b><span>rate of improvement</span></div><div><b>%s</b><span>%s</span></div></div>'
             % (latest[1], gain, rate, "%.1f%%" % abs(gap), "still to go to match Rust" if latest[1] < 1 else "ahead of Rust"))
    return stats + "".join(o) + '<p class="sub">Each dot is a kept version, placed at the time it was measured: geometric mean of F#/Rust across the five workloads at 16 connections, against the stored Rust numbers.</p>'


def latest_table(rows, rust):
    kept = [r for r in rows if r.get("kept", True)]
    last = kept[-1] if kept else None
    head = "<tr><th>Workload</th><th>Rust c=16</th><th>F# c=16</th><th>F#/Rust</th><th>F# CPU µs/req</th><th>Rust c=1</th><th>F# c=1</th><th>F#/Rust</th></tr>"
    body = []
    for w, label in WORKLOADS:
        def cell(c):
            rr = rust.get(w, {}).get(c, {}).get("rps")
            fr = (last or {}).get("workloads", {}).get(w, {}).get(c, {}) if last else {}
            q = ratio(last, rust, w, c) if last else None
            cls = "win" if q and q >= 1.0 else ("close" if q and q >= 0.9 else "")
            return (("%s" % f"{rr:,.0f}") if rr else "–", f"{fr['rps']:,.0f}" if fr.get("rps") else "–",
                    '<b class="%s">%.2f×</b>' % (cls, q) if q else "–", f"{fr['cpu_us']:,.0f}" if fr.get("cpu_us") else "–")
        a, b = cell("c16"), cell("c1")
        body.append("<tr><td>%s</td><td>%s</td><td>%s</td><td>%s</td><td>%s</td><td>%s</td><td>%s</td><td>%s</td></tr>"
                    % (html.escape(label), a[0], a[1], a[2], a[3], b[0], b[1], b[2]))
    return "<table>%s%s</table>" % (head, "".join(body))


def log_list(rows):
    items = []
    for r in reversed(rows[-40:]):
        items.append('<li class="%s"><time>%s</time> <span class="tag">%s · tier %s</span> %s <code>%s</code></li>'
                     % ("" if r.get("kept", True) else "dropped", html.escape(str(r.get("time", ""))[11:16] + " UTC"),
                        html.escape(str(r.get("unit", ""))), html.escape(str(r.get("tier", ""))),
                        html.escape(str(r.get("change", ""))) + ("" if r.get("kept", True) else " (reverted)"),
                        html.escape(str(r.get("commit", "")))))
    return "<ol class=\"log\">%s</ol>" % "".join(items) if items else '<p class="empty">Waiting for the first tuning run.</p>'


def main():
    rust, source = rust_reference()
    rows = entries()
    kept = [r for r in rows if r.get("kept", True)]
    now = datetime.now(timezone.utc).strftime("%H:%M:%S UTC")
    page = f"""<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1"><meta http-equiv="refresh" content="30">
<title>Phase 7 Progress</title><style>
:root{{--bg:#f7f5f0;--card:#fff;--ink:#1c1a17;--mute:#6d675d;--line:#e3ded3;--c1:#c2410c;--c2:#2563eb;--c3:#15803d;--c4:#9333ea;--c5:#b45309;--win:#15803d;--close:#b45309}}
@media (prefers-color-scheme:dark){{:root:not([data-theme="light"]){{--bg:#14120f;--card:#1e1b17;--ink:#f1ede5;--mute:#a29c90;--line:#36312a;--c1:#fb923c;--c2:#60a5fa;--c3:#4ade80;--c4:#c084fc;--c5:#fbbf24;--win:#4ade80;--close:#fbbf24}}}}
:root[data-theme="dark"]{{--bg:#14120f;--card:#1e1b17;--ink:#f1ede5;--mute:#a29c90;--line:#36312a}}
body{{margin:0;background:var(--bg);color:var(--ink);font:15px/1.5 -apple-system,system-ui,sans-serif}}
main{{max-width:1080px;margin:0 auto;padding:28px 16px 60px}} h1{{font-size:28px;margin:0 0 4px;letter-spacing:-.02em}}
.sub{{color:var(--mute);margin:0 0 20px}} .card{{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:18px;margin-bottom:18px;overflow-x:auto}}
h2{{font-size:16px;margin:0 0 10px}} svg{{width:100%;height:auto}} .grid{{stroke:var(--line)}} .parity{{stroke:var(--ink);stroke-dasharray:5 4;opacity:.6}}
.axis{{fill:var(--mute);font-size:11px}} .legend{{display:flex;flex-wrap:wrap;gap:14px;font-size:13px;color:var(--mute);margin-top:6px}}
.legend i{{display:inline-block;width:12px;height:12px;border-radius:3px;margin-right:6px;vertical-align:-1px}}
table{{border-collapse:collapse;width:100%;font-size:14px;font-variant-numeric:tabular-nums}} td,th{{padding:6px 8px;border-bottom:1px solid var(--line);text-align:right}}
td:first-child,th:first-child{{text-align:left}} th{{color:var(--mute);font-weight:600}} .win{{color:var(--win)}} .close{{color:var(--close)}}
.log{{list-style:none;padding:0;margin:0;font-size:14px}} .log li{{padding:6px 0;border-bottom:1px solid var(--line)}} .log .dropped{{opacity:.55}}
.tag{{color:var(--mute);font-size:12px}}
.hero h2{{font-size:20px}} .rustline{{stroke:var(--ink);stroke-width:2;stroke-dasharray:8 6;opacity:.7}} .rustlabel{{fill:var(--ink);font-size:14px;font-weight:700}}
.climb{{fill:none;stroke:var(--c1);stroke-width:4;stroke-linejoin:round;stroke-linecap:round}} .area{{fill:var(--c1);opacity:.10}}
.dot{{fill:var(--card);stroke:var(--c1);stroke-width:3}} .dot.last{{fill:var(--c1)}} .tick{{fill:var(--mute);font-size:12px}}
.val{{fill:var(--ink);font-size:14px;font-weight:700}}
.stats{{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:12px;margin:4px 0 14px}} .stats div{{border:1px solid var(--line);border-radius:10px;padding:10px 12px}}
.stats b{{display:block;font-size:24px;letter-spacing:-.02em}} .stats span{{color:var(--mute);font-size:13px}}
@media (max-width:640px){{.stats{{grid-template-columns:repeat(2,minmax(0,1fr))}}}} time{{color:var(--mute);font-variant-numeric:tabular-nums}} .empty{{color:var(--mute)}}
</style></head><body><main>
<h1>F# vs Rust: Phase 7 tuning</h1>
<p class="sub">{len(kept)} kept change(s), {len(rows) - len(kept)} reverted. F# measured alone against the stored Rust numbers ({html.escape(source)}); Rust is re-measured only at the final re-baseline. Refreshed {now}, reloads every 30 s.</p>
<div class="card hero"><h2>F# climbing toward Rust</h2>{headline(kept, rust)}</div>
<div class="card"><h2>Latest kept run</h2>{latest_table(rows, rust)}</div>
<div class="card"><h2>F# throughput as a share of Rust, 16 connections</h2>{chart(kept, rust, "c16")}</div>
<div class="card"><h2>F# throughput as a share of Rust, 1 connection</h2>{chart(kept, rust, "c1")}</div>
<div class="card"><h2>Every run, newest first</h2>{log_list(rows)}</div>
</main></body></html>"""
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    tmp = OUT + ".tmp"
    with open(tmp, "w") as f:
        f.write(page)
    os.replace(tmp, OUT)


if __name__ == "__main__":
    main()
