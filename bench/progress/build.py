#!/usr/bin/env python3
"""Render the Phase 7 progress dashboard (target/phase7-dashboard/index.html) from bench/results/phase7-log.jsonl.

One chart: F#'s throughput as a share of Rust's over time, one dot per logged run, each dot carrying everything
about that run in a hover card (CSS only: hover, keyboard focus or tap). The chart itself is bench/progress/climb.py,
which the story page (site/build.py) draws too. Run once after each logged tuning run; nothing rebuilds it on a
timer. After writing the dashboard it re-runs site/build.py, when there is one, so index.html shows the same climb.
"""
import os
import subprocess
import sys
from datetime import datetime

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import climb  # noqa: E402

ROOT = climb.ROOT
OUT = os.path.join(ROOT, "target/phase7-dashboard/index.html")
SITE_BUILD = os.path.join(ROOT, "site/build.py")


def page():
    c = climb.climb()
    now = datetime.now().astimezone().strftime("%H:%M %Z")
    return f"""<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1"><meta http-equiv="refresh" content="30">
<title>F# vs Rust</title><style>
:root{{--bg:#f7f5f0;--ink:#1c1a17;--mute:#6d675d}}
@media (prefers-color-scheme:dark){{:root:not([data-theme="light"]){{--bg:#14120f;--ink:#f1ede5;--mute:#a29c90}}}}
:root[data-theme="dark"]{{--bg:#14120f;--ink:#f1ede5;--mute:#a29c90}}
*{{box-sizing:border-box}} body{{margin:0;background:var(--bg);color:var(--ink);font:15px/1.45 -apple-system,system-ui,sans-serif;overflow-x:clip}}
main{{max-width:1100px;margin:0 auto;padding:28px 16px 48px}} h1{{font-size:30px;margin:0;letter-spacing:-.02em}}
.sub{{color:var(--mute);margin:4px 0 18px}}
{climb.CSS}
</style></head><body><main>
<h1>F# climbing toward Rust</h1>
<p class="sub">Phase 7 tuning. Each dot is a measured run; hover or tap it for everything about that run. The line is the geometric mean of F#/Rust across the five workloads at 16 connections; dots are spaced evenly in the order they ran, labelled with time and day; hollow grey dots are reverted experiments. Built {now}.</p>
{c["prelude"]}
{c["stats"]}
{c["chart"]}
</main></body></html>"""


def main():
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    tmp = OUT + ".tmp"
    with open(tmp, "w") as f:
        f.write(page())
    os.replace(tmp, OUT)
    # Every logged run refreshes the story page too; a failure there never fails the dashboard.
    if os.path.exists(SITE_BUILD):
        try:
            r = subprocess.run([sys.executable, SITE_BUILD], capture_output=True, text=True, timeout=120)
            if r.returncode != 0:
                sys.stderr.write("site/build.py failed (dashboard written):\n" + r.stderr[-2000:])
        except (OSError, subprocess.SubprocessError) as ex:
            sys.stderr.write("site/build.py did not run (dashboard written): %s\n" % ex)


if __name__ == "__main__":
    main()
