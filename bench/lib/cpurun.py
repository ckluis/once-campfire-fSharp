#!/usr/bin/env python3
"""Run a command and say what it cost: the container's CPU (cgroup cpu.stat) and the command's own.

  cpurun.py CGROUP_DIR -- cmd arg...      # the command prints one JSON object on stdout

Prints that object with `cost` added: wall seconds, the container's usage/user/system CPU microseconds
over the command's run, the command's own user and system CPU (rusage of the child), and, when the
object has `n` requests under `latency`, the container's and the command's CPU per request in
microseconds. bench/breakdown uses it to put a server's CPU per request next to the load generator's:
a load generator that burns as much as the server is the limit of the run, not the server.
"""
import json, os, resource, subprocess, sys, time


def stat(cg):
    out = {}
    for line in open(os.path.join(cg, "cpu.stat")):
        k, v = line.split()
        out[k] = int(v)
    return out


cg = sys.argv[1]
cmd = sys.argv[sys.argv.index("--") + 1:]
before, r0, t0 = stat(cg), resource.getrusage(resource.RUSAGE_CHILDREN), time.time()
proc = subprocess.run(cmd, capture_output=True, text=True)
wall = time.time() - t0
after, r1 = stat(cg), resource.getrusage(resource.RUSAGE_CHILDREN)
if proc.returncode != 0:
    sys.stderr.write(proc.stderr)
    sys.exit(proc.returncode)
res = json.loads(proc.stdout)
cost = {
    "wall_s": round(wall, 2),
    "server_usage_us": after["usage_usec"] - before["usage_usec"],
    "server_user_us": after["user_usec"] - before["user_usec"],
    "server_system_us": after["system_usec"] - before["system_usec"],
    "loadgen_user_us": round((r1.ru_utime - r0.ru_utime) * 1e6),
    "loadgen_system_us": round((r1.ru_stime - r0.ru_stime) * 1e6),
}
n = res.get("latency", {}).get("n", 0)
if n:
    cost["n"] = n
    cost["server_cpu_us_per_req"] = round(cost["server_usage_us"] / n, 1)
    cost["server_user_us_per_req"] = round(cost["server_user_us"] / n, 1)
    cost["server_system_us_per_req"] = round(cost["server_system_us"] / n, 1)
    cost["loadgen_cpu_us_per_req"] = round((cost["loadgen_user_us"] + cost["loadgen_system_us"]) / n, 1)
    cost["server_cores_busy"] = round(cost["server_usage_us"] / (wall * 1e6), 2)
    cost["loadgen_cores_busy"] = round((cost["loadgen_user_us"] + cost["loadgen_system_us"]) / (wall * 1e6), 2)
res["cost"] = cost
print(json.dumps(res))
