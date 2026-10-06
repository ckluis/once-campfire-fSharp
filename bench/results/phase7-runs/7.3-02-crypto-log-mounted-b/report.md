```
date: 2026-10-06T17:41:43-04:00
host: 6.8.0-117-generic, , 8 threads, 5GB
server cpus: 0-3 (nproc 4); loadgen cpus: 4-7; network: host
env: WEB_CONCURRENCY=3 JOB_CONCURRENCY=3 RAILS_MAX_THREADS=5 
rust extra env: 
fsharp extra env: 
user agent: (none)
fsharp image: campfire-fsharp:app sha256:e316ad01e8eda3ba9ab80450267a6349253fbd5efb900d0ddb924c2bee4e4734 2026-10-06T15:20:10.915021924-04:00 built from commit: 1d0f3b2756261c871661290ba76a8ad54860ba88
fsharp build: MOUNTED /var/tmp/campfire-mounted over /opt/campfire (0b67670 (dirty files under src/: 0) at 2026-10-06T21:32:57Z)
repo HEAD: 0b67670 (dirty: 0 files under src/ and rust/)
work dir (seed copies): /var/tmp/campfire-bench-work (ext4)
loadgen: /Users/clank/Desktop/projects/once-campfire-fsharp/target/rust/aarch64-unknown-linux-gnu/release/loadgen (2026-10-06 03:04:07), built with rust:1.98.1-trixie
schedule: apps fsharp, reps 1; suites: http; http secs 8, concs 16, routes room_show sidebar search, warm-up 30s at each route and concentration, idle 5s
log retention: 2 x 10m (json-file)
```

Reps: fsharp 1. Cells: median [min–max].

### Startup and memory

| Metric | F# |
|---|---|
| cold start: docker run → /up 200 (ms) | 235 |
| idle memory.current (MB) | 28.0 |
| idle anon (MB) | 15.0 |
| peak memory.current under load (MB) | 132 |
| peak anon under load (MB) | 108 |

### HTTP (signed in as david; keep-alive; c = concurrent connections)

`CPU µs/req` is the container's CPU (cgroup cpu.stat, user + system, unprofiled) over the run divided by its requests; for CPU a ratio below 1 means the first app spends more per request.

| Metric | F# |
|---|---|
| room_show c=16 req/s | 39,424 |
| room_show c=16 CPU µs/req | 95.7 |
| room_show c=16 p50 ms | 0.32 |
| room_show c=16 p99 ms | 1.64 |
| sidebar c=16 req/s | 38,359 |
| sidebar c=16 CPU µs/req | 99.3 |
| sidebar c=16 p50 ms | 0.33 |
| sidebar c=16 p99 ms | 1.82 |
| search c=16 req/s | 32,737 |
| search c=16 CPU µs/req | 114 |
| search c=16 p50 ms | 0.43 |
| search c=16 p99 ms | 2.18 |

## Validity

### Load average before each run (1, 5, 15 minutes; the run waited for the 1-minute figure to fall below LOAD_MAX)

| App | Rep | Before | After |
|---|---|---|---|
| F# | 1 | 1.33 4.35 3.27 | 10.91 7.02 4.39 |

### Host CPU probe (a fixed spin timed on every pinned CPU, against the quietest this VM has shown; bench/lib/hostprobe.py)

The VM's load average cannot see another program on the Mac taking the cores its vCPUs run on; this can. A ratio of 1.00 is a quiet host, above 1.10 the pinned server and load generator were slowed by something outside the run. `After each window` probes the machine right after every measured run of the rep.

| App | Rep | Before the run | After the run | Windows probed above 1.10 | Highest after a window |
|---|---|---|---|---|---|
| F# | 1 | 1.04 | 1.02 | 0/3 | 1.06 |

### Responses by status class, every HTTP run of every rep

| App | Route | 2xx | 3xx | 4xx | 5xx | Errors (no response) |
|---|---|---|---|---|---|---|
| F# | room_show | 315,408 | 0 | 0 | 0 | 0 |
| F# | sidebar | 306,889 | 0 | 0 | 0 | 0 |
| F# | search | 261,912 | 0 | 0 | 0 | 0 |

### Warm-up before each measured run (the app runs the route at the measured concurrency until its throughput is flat; bench/lib/warmup.py)

`measured / warm-up tail` is the measured run's requests a second over the mean of the last three 2 s warm-up windows: well above 1 would mean the app was still speeding up when measurement began, well below 1 that it was slowing down.

| App | Route | c | warm-up seconds | settled | measured / warm-up tail |
|---|---|---|---|---|---|
| F# | room_show | 16 | 30.0 | 1/1 | 1.00 |
| F# | sidebar | 16 | 30.0 | 1/1 | 1.00 |
| F# | search | 16 | 30.0 | 1/1 | 0.99 |

### Rows written by `post_message` (messages of the write room counted in the app's SQLite file around each run)

| App | Rep | Phase | Posts answered below 400 | Rows written | Match |
|---|---|---|---|---|---|

### Response bytes, one request per route (gzip: wire / decoded; identity: bytes) and the average over the c=16 run

The probe is a single request after sign-in, before any load. Bodies carry per-request tokens and the clock, so sizes of two apps agree to within a few bytes when they render the same page. "c=16 avg" is what the load generator received per request on the wire (gzip unless the column says otherwise).

| Route | F# gzip wire / decoded | F# identity | F# c=16 avg | F# vs Rust |
|---|---|---|---|---|
| room_show | 200 24,080 / 416,122 | 200 416,122 | 24,080 | – |
| sidebar | 200 5,910 / 30,763 | 200 30,763 | 5,910 | – |
| search | 200 9,713 / 149,623 | 200 149,623 | 9,713 | – |
| post_message | – | – | – | – |
