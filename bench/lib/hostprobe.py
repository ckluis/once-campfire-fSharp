#!/usr/bin/env python3
"""How much CPU the VM's vCPUs are really getting right now: a fixed spin on every CPU of the benchmark, timed.

  hostprobe.py CPUS [BASELINE_FILE [MAX_RATIO]]      # CPUS like 0-7; prints one JSON object

Runs a fixed integer loop (about 40 ms when the vCPU is not shared) pinned to each CPU in turn, five times, and reports the median
time per CPU. The VM's own load average cannot see the Mac: another program on the host (a build, a test run) takes cores the
VM's vCPUs are scheduled on, the pinned server and load generator slow down, and the load average inside the VM stays low. A
run that starts while the spin takes noticeably longer than on a quiet machine is not comparable with one that does not.

BASELINE_FILE holds the fastest `spin_ms` (median over CPUs) ever seen here, updated whenever a probe beats it. `ratio` is this
probe over that baseline; with MAX_RATIO the exit status is 1 when the ratio is above it (bench/run's wait_for_quiet waits
and probes again).
"""
import json
import os
import statistics
import subprocess
import sys

SPIN = "import time\nt=time.perf_counter()\nx=0\nfor i in range(1200000):\n    x+=i*i\nprint(time.perf_counter()-t)"


def cpus(spec):
    out = []
    for part in spec.split(","):
        lo, _, hi = part.partition("-")
        out += range(int(lo), int(hi or lo) + 1)
    return out


def main():
    cpu_list = cpus(sys.argv[1])
    per_cpu = []
    for c in cpu_list:
        times = []
        for _ in range(5):
            r = subprocess.run(["taskset", "-c", str(c), sys.executable, "-c", SPIN], capture_output=True, text=True)
            times.append(float(r.stdout) * 1000)
        per_cpu.append(statistics.median(times))
    spin = statistics.median(per_cpu)
    out = {"spin_ms": round(spin, 2), "spin_ms_max_cpu": round(max(per_cpu), 2), "cpus": sys.argv[1]}
    if len(sys.argv) > 2:
        base_file = sys.argv[2]
        base = None
        if os.path.exists(base_file):
            try:
                base = float(open(base_file).read().strip())
            except ValueError:
                base = None
        if base is None or spin < base:
            base = spin
            open(base_file, "w").write(f"{base:.2f}\n")
        out["baseline_ms"] = round(base, 2)
        out["ratio"] = round(spin / base, 3)
        if len(sys.argv) > 3 and out["ratio"] > float(sys.argv[3]):
            print(json.dumps(out))
            sys.exit(1)
    print(json.dumps(out))


main()
