```
date: 2026-10-06T12:11:12-04:00
host: 6.8.0-117-generic, , 8 threads, 5GB
server cpus: 0-3 (nproc 4); loadgen cpus: 4-7; network: host
env: WEB_CONCURRENCY=3 JOB_CONCURRENCY=3 RAILS_MAX_THREADS=5 
rust extra env: 
fsharp extra env: 
user agent: (none)
fsharp image: campfire-fsharp:app sha256:abc70784a0f4bc576141757010b789e1957d390c43ca95438289b9d441cd64c2 2026-10-06T09:03:28.60734127-04:00 built from commit: 16a7da254aa2a6097cbd1d8f4dc2a9303b142cae
fsharp build: MOUNTED /var/tmp/campfire-mounted over /opt/campfire (b7f020f (dirty files under src/: 0) at 2026-10-06T16:11:12Z)
repo HEAD: b7f020f (dirty: 0 files under src/ and rust/)
work dir (seed copies): /var/tmp/campfire-bench-work (ext4)
loadgen: /Users/clank/Desktop/projects/once-campfire-fsharp/target/rust/aarch64-unknown-linux-gnu/release/loadgen (2026-10-06 03:04:07), built with rust:1.98.1-trixie
schedule: apps fsharp, reps 1; suites: http; http secs 8, concs 16, routes room_show messages_page sidebar search, warm-up 30s at each route and concentration, idle 5s
log retention: 2 x 10m (json-file)
```

Reps: fsharp 1. Cells: median [min–max].

### Startup and memory

| Metric | F# |
|---|---|
| cold start: docker run → /up 200 (ms) | 280 |
| idle memory.current (MB) | 27.0 |
| idle anon (MB) | 15.0 |
| peak memory.current under load (MB) | 102 |
| peak anon under load (MB) | 79.0 |

### HTTP (signed in as david; keep-alive; c = concurrent connections)

`CPU µs/req` is the container's CPU (cgroup cpu.stat, user + system, unprofiled) over the run divided by its requests; for CPU a ratio below 1 means the first app spends more per request.

| Metric | F# |
|---|---|
| room_show c=16 req/s | 25,851 |
| room_show c=16 CPU µs/req | 141 |
| room_show c=16 p50 ms | 0.55 |
| room_show c=16 p99 ms | 1.57 |
| messages_page c=16 req/s | 28,704 |
| messages_page c=16 CPU µs/req | 126 |
| messages_page c=16 p50 ms | 0.49 |
| messages_page c=16 p99 ms | 1.46 |
| sidebar c=16 req/s | 24,993 |
| sidebar c=16 CPU µs/req | 151 |
| sidebar c=16 p50 ms | 0.53 |
| sidebar c=16 p99 ms | 2.16 |
| search c=16 req/s | 26,034 |
| search c=16 CPU µs/req | 146 |
| search c=16 p50 ms | 0.52 |
| search c=16 p99 ms | 1.89 |

## Validity

### Load average before each run (1, 5, 15 minutes; the run waited for the 1-minute figure to fall below LOAD_MAX)

| App | Rep | Before | After |
|---|---|---|---|
| F# | 1 | 1.39 1.41 1.86 | 8.44 4.31 2.88 |

### Host CPU probe (a fixed spin timed on every pinned CPU, against the quietest this VM has shown; bench/lib/hostprobe.py)

The VM's load average cannot see another program on the Mac taking the cores its vCPUs run on; this can. A ratio of 1.00 is a quiet host, above 1.10 the pinned server and load generator were slowed by something outside the run. `After each window` probes the machine right after every measured run of the rep.

| App | Rep | Before the run | After the run | Windows probed above 1.10 | Highest after a window |
|---|---|---|---|---|---|
| F# | 1 | 1.03 | 1.02 | 0/4 | 1.04 |

### Responses by status class, every HTTP run of every rep

| App | Route | 2xx | 3xx | 4xx | 5xx | Errors (no response) |
|---|---|---|---|---|---|---|
| F# | room_show | 206,836 | 0 | 0 | 0 | 0 |
| F# | messages_page | 229,646 | 0 | 0 | 0 | 0 |
| F# | sidebar | 199,953 | 0 | 0 | 0 | 0 |
| F# | search | 208,293 | 0 | 0 | 0 | 0 |

### Warm-up before each measured run (the app runs the route at the measured concurrency until its throughput is flat; bench/lib/warmup.py)

`measured / warm-up tail` is the measured run's requests a second over the mean of the last three 2 s warm-up windows: well above 1 would mean the app was still speeding up when measurement began, well below 1 that it was slowing down.

| App | Route | c | warm-up seconds | settled | measured / warm-up tail |
|---|---|---|---|---|---|
| F# | room_show | 16 | 30.0 | 1/1 | 1.01 |
| F# | messages_page | 16 | 34.0 | 1/1 | 1.02 |
| F# | sidebar | 16 | 40.0 | 1/1 | 1.04 |
| F# | search | 16 | 32.0 | 1/1 | 1.00 |

### Rows written by `post_message` (messages of the write room counted in the app's SQLite file around each run)

| App | Rep | Phase | Posts answered below 400 | Rows written | Match |
|---|---|---|---|---|---|

### Response bytes, one request per route (gzip: wire / decoded; identity: bytes) and the average over the c=16 run

The probe is a single request after sign-in, before any load. Bodies carry per-request tokens and the clock, so sizes of two apps agree to within a few bytes when they render the same page. "c=16 avg" is what the load generator received per request on the wire (gzip unless the column says otherwise).

| Route | F# gzip wire / decoded | F# identity | F# c=16 avg | F# vs Rust |
|---|---|---|---|---|
| room_show | 200 24,080 / 416,122 | 200 416,122 | 24,080 | – |
| messages_page | 200 15,967 / 383,844 | 200 383,844 | 15,967 | – |
| sidebar | 200 5,910 / 30,763 | 200 30,763 | 5,910 | – |
| search | 200 9,713 / 149,623 | 200 149,623 | 9,713 | – |
| post_message | – | – | – | – |
