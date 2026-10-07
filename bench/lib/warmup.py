#!/usr/bin/env python3
"""Warm an app up at the concurrency it is about to be measured at: bench/run's warm_up.

  warmup.py --min SECS --max SECS --window SECS -- LOADGEN-COMMAND...      # prints one JSON object

Runs the load generator (the command, to which `--duration WINDOW` is appended) back to back in windows of WINDOW seconds
until the app has settled, and prints the load generator's counters summed over the windows (`ok`, `statuses`,
`errors`: bench/run checks the rows a warm-up wrote against them) with a `warmup` object: seconds run, each window's
requests a second, whether it settled and why it stopped.

Why not just a fixed time: the CLR's tiered JIT reaches 0.9x of its steady throughput in 6-10 s of load and the last 5-8% only
after 25-45 s (its background compiler competes with the request threads for the four pinned CPUs; bench/warmup draws the
curves, results/phase7-warmup.md has them), and 2 s windows swing +-5-10% on this VM when another program is running on the Mac.
Rust and Rails are flat from the first window. The rule is the same for every app: after at least --min seconds, stop when the
mean of the last 3 windows is within 3% of the mean of the 3 before them (the throughput is not still climbing) and is at least
97% of the best 3-window mean so far (it has not fallen off a peak either); stop at --max regardless and say so (`settled: false` if the rule does not hold then either), which
bench/report flags. --max equal to --min is a fixed warm-up; --min 0 skips it.
"""
import json
import statistics
import subprocess
import sys


def settled(rps):
    if len(rps) < 6:
        return False
    last, prev = statistics.mean(rps[-3:]), statistics.mean(rps[-6:-3])
    means = [statistics.mean(rps[i:i + 3]) for i in range(len(rps) - 2)]
    return prev > 0 and abs(last - prev) / prev <= 0.03 and last >= 0.97 * max(means)


def main():
    argv = sys.argv[1:]
    cmd = argv[argv.index("--") + 1:]
    opt = lambda name, default: float(argv[argv.index(name) + 1]) if name in argv else default
    lo, hi, window = opt("--min", 30.0), opt("--max", 90.0), opt("--window", 2.0)
    hi = max(lo, hi)
    out = {"ok": 0, "statuses": {}, "errors": 0}
    rps, elapsed, reason = [], 0.0, "skipped"
    while lo > 0:
        proc = subprocess.run(cmd + ["--duration", str(window)], capture_output=True, text=True)
        if proc.returncode != 0:
            sys.stderr.write(proc.stderr)
            sys.exit(proc.returncode)
        r = json.loads(proc.stdout)
        out["ok"] += r["ok"]
        out["errors"] += r["errors"]
        for k, v in r["statuses"].items():
            out["statuses"][k] = out["statuses"].get(k, 0) + v
        rps.append(r["rps"])
        elapsed += r["secs"]
        if elapsed >= lo and settled(rps):
            reason = "settled"
            break
        if elapsed >= hi:
            reason = "reached the maximum" if hi > lo else "fixed"
            break
    out["warmup"] = {
        "secs": round(elapsed, 1), "windows_rps": rps, "reason": reason, "window_secs": window,
        "settled": None if reason == "skipped" else settled(rps),
        "tail_rps": round(statistics.mean(rps[-3:]), 1) if rps else None,
    }
    print(json.dumps(out))


main()
