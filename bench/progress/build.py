#!/usr/bin/env python3
"""Render the Phase 7 progress dashboard (target/phase7-dashboard/index.html) from bench/results/phase7-log.jsonl.

One chart: F#'s throughput as a share of Rust's over time, one dot per logged run, each dot carrying everything
about that run in a hover card (CSS only: hover, keyboard focus or tap). Run once after each logged tuning run;
nothing rebuilds it on a timer. Rust's numbers are the stored medians of bench/results/phase7-start/rust-*.json;
the first point is F#'s own starting point from the same directory.
"""
import glob
import html
import json
import os
import statistics
from datetime import datetime, timezone

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
LOG = os.path.join(ROOT, "bench/results/phase7-log.jsonl")
START = os.path.join(ROOT, "bench/results/phase7-start")
OUT = os.path.join(ROOT, "target/phase7-dashboard/index.html")
WORKLOADS = [("room_show", "Room page"), ("messages_page", "Messages page"), ("sidebar", "Sidebar"),
             ("search", "Search"), ("post_message", "Post a message")]
CONCS = [("c16", 16), ("c1", 1)]
e = html.escape


def medians(app):
    """Median req/s and CPU µs/req per (workload, concurrency) over an app's reps in the starting point."""
    reps = {}
    for p in sorted(glob.glob(os.path.join(START, f"{app}-[0-9].json"))):
        with open(p) as f:
            for run in json.load(f)["http"]:
                if run.get("gzip"):
                    key = (run["route"], run["conc"])
                    reps.setdefault(key, {"rps": [], "cpu_us": []})["rps"].append(run["rps"])
                    cpu = (run.get("cost") or {}).get("server_cpu_us_per_req")
                    if cpu:
                        reps[key]["cpu_us"].append(cpu)
    out = {}
    for w, _ in WORKLOADS:
        for key, c in CONCS:
            v = reps.get((w, c))
            if v and v["rps"]:
                out.setdefault(w, {})[key] = {"rps": statistics.median(v["rps"]),
                                              "cpu_us": statistics.median(v["cpu_us"]) if v["cpu_us"] else None}
    return out


def start_entry():
    t = "2026-10-06T14:00:00Z"
    p = os.path.join(START, "fsharp-3.json")
    if os.path.exists(p):
        t = datetime.fromtimestamp(os.path.getmtime(p), timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    return {"time": t, "unit": "start", "tier": 3, "change": "Phase 7 starting point, before any tuning (3 reps, image build)",
            "commit": "5cc5ba4", "build": "image", "kept": True, "workloads": medians("fsharp")}


def entries():
    rows = [start_entry()]
    if os.path.exists(LOG):
        with open(LOG) as f:
            for line in f:
                line = line.strip()
                if line:
                    try:
                        rows.append(json.loads(line))
                    except json.JSONDecodeError:
                        pass
    return rows


def when(r):
    try:
        return datetime.fromisoformat(str(r["time"]).replace("Z", "+00:00"))
    except (KeyError, ValueError):
        return None


def carry(rows):
    """Kept runs with every workload they didn't measure filled in from the latest earlier kept run that did.
    Each value remembers whether it was measured in that run. Reverted runs are filled from the same state but
    don't change it."""
    state, out = {}, []
    for r in rows:
        measured = {(w, c) for w, v in r.get("workloads", {}).items() for c in v}
        view = {w: {c: dict(v) for c, v in cs.items()} for w, cs in state.items()}
        for w, cs in r.get("workloads", {}).items():
            for c, v in cs.items():
                view.setdefault(w, {})[c] = dict(v)
        if r.get("kept", True):
            state = {w: {c: dict(v) for c, v in cs.items()} for w, cs in view.items()}
        out.append((r, view, measured))
    return out


def ratio(view, rust, w, c):
    try:
        return view[w][c]["rps"] / rust[w][c]["rps"]
    except (KeyError, TypeError, ZeroDivisionError):
        return None


def overall(view, rust, c="c16"):
    qs = [ratio(view, rust, w, c) for w, _ in WORKLOADS]
    if any(q is None for q in qs):
        return None
    prod = 1.0
    for q in qs:
        prod *= q
    return prod ** (1 / len(qs))


def num(v, fmt="{:,.0f}"):
    return fmt.format(v) if isinstance(v, (int, float)) else "–"


def card(r, view, measured, rust, ov):
    t = when(r)
    rows = []
    for w, label in WORKLOADS:
        cells = []
        for c, _ in CONCS:
            v = view.get(w, {}).get(c, {})
            q = ratio(view, rust, w, c)
            cls = "win" if q and q >= 1 else ("close" if q and q >= 0.9 else "")
            carried = (w, c) not in measured and v
            cells.append('<td class="%s">%s<br><b class="%s">%s</b><br><small>%s µs</small></td>'
                         % ("carried" if carried else "", num(v.get("rps")), cls, ("%.2f×" % q) if q else "–", num(v.get("cpu_us"))))
        rows.append("<tr><th>%s</th>%s</tr>" % (e(label), "".join(cells)))
    kept = r.get("kept", True)
    return ('<div class="card" role="tooltip"><p class="when">%s · %s · tier %s%s</p><p class="what">%s</p>'
            '<p class="meta">commit <code>%s</code> · %s build%s</p>'
            '<table><tr><th></th><th>16 conn.</th><th>1 conn.</th></tr>%s</table>'
            '<p class="foot">req/s · F#/Rust · CPU µs per request. Greyed values were carried from an earlier run.</p></div>'
            % (e(t.strftime("%H:%M UTC") if t else "?"), e(str(r.get("unit", ""))), e(str(r.get("tier", ""))),
               "" if kept else " · reverted", e(str(r.get("change", ""))), e(str(r.get("commit", ""))),
               e(str(r.get("build", ""))[:19]), (" · overall %.2f× of Rust" % ov) if ov else "", "".join(rows)))


def page():
    rust = medians("rust")
    rows = carry(entries())
    pts = [(when(r), overall(v, rust), r, v, m) for r, v, m in rows]
    pts = [p for p in pts if p[0] and p[1] is not None]
    kept = [p for p in pts if p[2].get("kept", True)]
    W, H, L, R, T, B = 1000, 420, 56, 80, 28, 56
    if pts:
        t0 = min(p[0].timestamp() for p in pts)
        span = max(max(p[0].timestamp() for p in pts) - t0, 3600)
        vals = [p[1] for p in pts]
        lo, hi = min(0.6, min(vals) - 0.04), max(1.15, max(vals) + 0.04)
    else:
        t0, span, lo, hi = 0, 3600, 0.6, 1.15
    X = lambda t: L + (W - L - R) * (t.timestamp() - t0) / span
    Y = lambda v: H - B - (H - T - B) * (v - lo) / (hi - lo)

    svg = ['<svg viewBox="0 0 %d %d" aria-hidden="true">' % (W, H)]
    g = 0.6
    while g <= hi + 1e-9:
        if g >= lo:
            svg.append('<line x1="%d" x2="%d" y1="%.1f" y2="%.1f" class="grid"/><text x="10" y="%.1f" class="axis">%.1f×</text>'
                       % (L, W - R, Y(g), Y(g), Y(g) + 4, g))
        g = round(g + 0.1, 2)
    svg.append('<line x1="%d" x2="%d" y1="%.1f" y2="%.1f" class="rust"/><text x="%d" y="%.1f" class="rustlabel">Rust</text>'
               % (L, W - R, Y(1), Y(1), W - R + 10, Y(1) + 5))
    if kept:
        line = " ".join("%.1f,%.1f" % (X(t), Y(v)) for t, v, *_ in kept)
        svg.append('<polygon class="area" points="%.1f,%d %s %.1f,%d"/>' % (X(kept[0][0]), H - B, line, X(kept[-1][0]), H - B))
        svg.append('<polyline class="climb" points="%s"/>' % line)
        for t, v, *_ in kept[:: max(1, len(kept) // 8)] + [kept[-1]]:
            svg.append('<text x="%.1f" y="%d" class="tick" text-anchor="middle">%s</text>' % (X(t), H - B + 20, t.strftime("%H:%M")))
    svg.append("</svg>")

    dots = []
    for i, (t, v, r, view, measured) in enumerate(pts):
        isk = r.get("kept", True)
        last = isk and kept and r is kept[-1][2]
        left, top = X(t) / W * 100, Y(v) / H * 100
        side = "flip" if left > 58 else ""
        dots.append('<span class="dot %s %s %s" tabindex="0" role="button" style="left:%.2f%%;top:%.2f%%" aria-label="%s, %.2f times Rust">%s</span>'
                    % ("kept" if isk else "dropped", "last" if last else "", side, left, top,
                       e(r.get("change", "")), v, card(r, view, measured, rust, v)))

    if kept:
        first, latest = kept[0], kept[-1]
        gain = (latest[1] / first[1] - 1) * 100
        hours = (latest[0] - first[0]).total_seconds() / 3600
        rate = gain / hours if hours > 0.05 else 0.0
        to_go = (1 / latest[1] - 1) * 100
        stats = ('<div class="stats"><div><b>%.2f×</b><span>of Rust now</span></div><div><b>%+.1f%%</b><span>since tuning began</span></div>'
                 '<div><b>%+.1f%%/h</b><span>rate</span></div><div><b>%.0f%%</b><span>%s</span></div></div>'
                 % (latest[1], gain, rate, abs(to_go), "to go to match Rust" if to_go > 0 else "ahead of Rust"))
    else:
        stats = '<p class="sub">No runs yet.</p>'
    now = datetime.now(timezone.utc).strftime("%H:%M UTC")
    return f"""<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1"><meta http-equiv="refresh" content="30">
<title>F# vs Rust</title><style>
:root{{--bg:#f7f5f0;--card:#fff;--ink:#1c1a17;--mute:#6d675d;--line:#e3ded3;--acc:#c2410c;--win:#15803d;--close:#b45309;--drop:#a8a29e}}
@media (prefers-color-scheme:dark){{:root:not([data-theme="light"]){{--bg:#14120f;--card:#221e19;--ink:#f1ede5;--mute:#a29c90;--line:#36312a;--acc:#fb923c;--win:#4ade80;--close:#fbbf24;--drop:#78716c}}}}
:root[data-theme="dark"]{{--bg:#14120f;--card:#221e19;--ink:#f1ede5;--mute:#a29c90;--line:#36312a;--acc:#fb923c;--win:#4ade80;--close:#fbbf24;--drop:#78716c}}
*{{box-sizing:border-box}} body{{margin:0;background:var(--bg);color:var(--ink);font:15px/1.45 -apple-system,system-ui,sans-serif}}
main{{max-width:1100px;margin:0 auto;padding:28px 16px 48px}} h1{{font-size:30px;margin:0;letter-spacing:-.02em}}
.sub{{color:var(--mute);margin:4px 0 18px}}
.stats{{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:12px;margin-bottom:14px}}
.stats div{{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:12px 14px}}
.stats b{{display:block;font-size:26px;letter-spacing:-.02em}} .stats span{{color:var(--mute);font-size:13px}}
.chart{{position:relative;background:var(--card);border:1px solid var(--line);border-radius:14px;aspect-ratio:1000/420}}
.chart svg{{position:absolute;inset:0;width:100%;height:100%}}
.grid{{stroke:var(--line)}} .axis,.tick{{fill:var(--mute);font-size:12px}}
.rust{{stroke:var(--ink);stroke-width:2;stroke-dasharray:8 6;opacity:.7}} .rustlabel{{fill:var(--ink);font-weight:700;font-size:15px}}
.climb{{fill:none;stroke:var(--acc);stroke-width:4;stroke-linejoin:round;stroke-linecap:round}} .area{{fill:var(--acc);opacity:.1}}
.dot{{position:absolute;width:16px;height:16px;margin:-8px 0 0 -8px;border-radius:50%;border:3px solid var(--acc);background:var(--card);padding:0;cursor:pointer}}
.dot.last{{background:var(--acc);width:20px;height:20px;margin:-10px 0 0 -10px}} .dot.dropped{{border-color:var(--drop);width:11px;height:11px;margin:-5.5px 0 0 -5.5px;border-width:2px}}
.dot{{display:block}} .dot .card{{display:none;position:absolute;left:18px;top:-12px;z-index:5;width:330px;max-width:80vw;text-align:left;background:var(--card);color:var(--ink);
  border:1px solid var(--line);border-radius:12px;padding:12px 14px;box-shadow:0 12px 32px rgba(0,0,0,.18);font:13px/1.4 -apple-system,system-ui,sans-serif;cursor:default}}
.dot.flip .card{{left:auto;right:18px}} .dot:hover .card,.dot:focus .card,.dot:focus-within .card{{display:block}}
.card p{{margin:0 0 4px}} .when{{color:var(--mute);font-size:12px}} .what{{font-weight:650;font-size:14px}} .meta{{color:var(--mute);font-size:12px}}
.card table{{width:100%;border-collapse:collapse;margin-top:8px;font-variant-numeric:tabular-nums}} .card th,.card td{{padding:5px 4px;border-top:1px solid var(--line);text-align:right;vertical-align:top}}
.card th:first-child{{text-align:left;font-weight:600}} .card tr:first-child th{{border-top:0;color:var(--mute);font-weight:600}}
.card .carried{{opacity:.45}} .card small{{color:var(--mute)}} .win{{color:var(--win)}} .close{{color:var(--close)}} .foot{{color:var(--mute);font-size:11px;margin-top:8px}}
@media (max-width:640px){{.stats{{grid-template-columns:repeat(2,minmax(0,1fr))}} .chart{{aspect-ratio:auto;height:300px}}}}
</style></head><body><main>
<h1>F# climbing toward Rust</h1>
<p class="sub">Phase 7 tuning. Each dot is a measured run; hover or tap it for everything about that run. The line is the geometric mean of F#/Rust across the five workloads at 16 connections against the stored Rust numbers; hollow grey dots are reverted experiments. Built {now}.</p>
{stats}
<div class="chart">{"".join(svg)}{"".join(dots)}</div>
</main></body></html>"""


def main():
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    tmp = OUT + ".tmp"
    with open(tmp, "w") as f:
        f.write(page())
    os.replace(tmp, OUT)


if __name__ == "__main__":
    main()
