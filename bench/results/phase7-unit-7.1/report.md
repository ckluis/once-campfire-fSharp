```
date: 2026-10-06T12:44:09-04:00
host: 6.8.0-117-generic, , 8 threads, 5GB
server cpus: 0-3 (nproc 4); loadgen cpus: 4-7; network: host
env: WEB_CONCURRENCY=3 JOB_CONCURRENCY=3 RAILS_MAX_THREADS=5 
rust extra env: 
fsharp extra env: 
user agent: (none)
fsharp image: campfire-fsharp:app sha256:5b3be84342fe62190c8d66404a115886a9803d381920ce750089649020abd5aa 2026-10-06T12:43:31.451113631-04:00 built from commit: df6ffd96302a4655a38498f6e2179f6108fd8e95
repo HEAD: df6ffd9 (dirty: 0 files under src/ and rust/)
work dir (seed copies): /var/tmp/campfire-bench-work (ext4)
loadgen: /Users/clank/Desktop/projects/once-campfire-fsharp/target/rust/aarch64-unknown-linux-gnu/release/loadgen (2026-10-06 03:04:07), built with rust:1.98.1-trixie
schedule: apps fsharp, reps 3; suites: http; http secs 8, concs 1 16, routes room_show messages_page sidebar search post_message (in parts, --part 1/2 is one slice of them), warm-up 30s at each route and concentration, idle 5s
log retention: 2 x 10m (json-file)
```

Reps: fsharp 3. Cells: median [min–max].

### Startup and memory

| Metric | F# |
|---|---|
| cold start: docker run → /up 200 (ms) | 249 [233–327] |
| idle memory.current (MB) | 28.0 [28.0–28.0] |
| idle anon (MB) | 15.0 [15.0–15.0] |
| peak memory.current under load (MB) | 435 [434–464] |
| peak anon under load (MB) | 261 [257–283] |

### HTTP (signed in as david; keep-alive; c = concurrent connections)

`CPU µs/req` is the container's CPU (cgroup cpu.stat, user + system, unprofiled) over the run divided by its requests; for CPU a ratio below 1 means the first app spends more per request.

| Metric | F# |
|---|---|
| room_show c=1 req/s | 5,589 [5,503–5,707] |
| room_show c=1 CPU µs/req | 258 [256–266] |
| room_show c=1 p50 ms | 0.17 [0.17–0.18] |
| room_show c=1 p99 ms | 0.36 [0.35–0.36] |
| room_show c=16 req/s | 28,328 [27,778–28,640] |
| room_show c=16 CPU µs/req | 136 [135–138] |
| room_show c=16 p50 ms | 0.45 [0.44–0.46] |
| room_show c=16 p99 ms | 2.03 [2.02–2.03] |
| messages_page c=1 req/s | 6,096 [5,889–6,101] |
| messages_page c=1 CPU µs/req | 243 [242–252] |
| messages_page c=1 p50 ms | 0.16 [0.16–0.16] |
| messages_page c=1 p99 ms | 0.34 [0.33–0.35] |
| messages_page c=16 req/s | 31,066 [30,858–31,476] |
| messages_page c=16 CPU µs/req | 124 [123–126] |
| messages_page c=16 p50 ms | 0.41 [0.40–0.41] |
| messages_page c=16 p99 ms | 2.04 [1.96–2.12] |
| sidebar c=1 req/s | 5,177 [5,096–5,258] |
| sidebar c=1 CPU µs/req | 278 [274–278] |
| sidebar c=1 p50 ms | 0.19 [0.18–0.19] |
| sidebar c=1 p99 ms | 0.37 [0.37–0.38] |
| sidebar c=16 req/s | 27,356 [26,739–27,691] |
| sidebar c=16 CPU µs/req | 142 [140–144] |
| sidebar c=16 p50 ms | 0.46 [0.46–0.47] |
| sidebar c=16 p99 ms | 2.23 [2.20–2.25] |
| search c=1 req/s | 4,855 [4,551–4,955] |
| search c=1 CPU µs/req | 364 [360–376] |
| search c=1 p50 ms | 0.20 [0.20–0.21] |
| search c=1 p99 ms | 0.42 [0.40–0.45] |
| search c=16 req/s | 27,801 [27,110–27,933] |
| search c=16 CPU µs/req | 134 [134–138] |
| search c=16 p50 ms | 0.51 [0.51–0.52] |
| search c=16 p99 ms | 1.43 [1.39–1.50] |
| post_message c=1 req/s | 2,328 [2,154–2,370] |
| post_message c=1 CPU µs/req | 783 [783–829] |
| post_message c=1 p50 ms | 0.40 [0.40–0.43] |
| post_message c=1 p99 ms | 0.72 [0.68–0.78] |
| post_message c=16 req/s | 7,004 [5,496–7,290] |
| post_message c=16 CPU µs/req | 452 [441–462] |
| post_message c=16 p50 ms | 1.96 [1.92–1.98] |
| post_message c=16 p99 ms | 7.29 [6.13–7.35] |

## Validity

### Load average before each run (1, 5, 15 minutes; the run waited for the 1-minute figure to fall below LOAD_MAX)

| App | Rep | Before | After |
|---|---|---|---|
| F# | 1 | 1.11 3.12 3.51 | 4.16 3.94 3.87 |
| F# | 2 | 1.37 3.13 3.59 | 5.43 4.41 4.12 |
| F# | 3 | 1.39 3.33 3.76 | 4.98 4.43 4.37 |

### Host CPU probe (a fixed spin timed on every pinned CPU, against the quietest this VM has shown; bench/lib/hostprobe.py)

The VM's load average cannot see another program on the Mac taking the cores its vCPUs run on; this can. A ratio of 1.00 is a quiet host, above 1.10 the pinned server and load generator were slowed by something outside the run. `After each window` probes the machine right after every measured run of the rep.

| App | Rep | Before the run | After the run | Windows probed above 1.10 | Highest after a window |
|---|---|---|---|---|---|
| F# | 1 | 1.04 | 1.02 | 0/10 | 1.05 |
| F# | 2 | 1.06 | 1.03 | 0/10 | 1.05 |
| F# | 3 | 1.04 | 1.00 | 0/10 | 1.04 |

### Responses by status class, every HTTP run of every rep

| App | Route | 2xx | 3xx | 4xx | 5xx | Errors (no response) |
|---|---|---|---|---|---|---|
| F# | room_show | 812,434 | 0 | 0 | 0 | 0 |
| F# | messages_page | 891,959 | 0 | 0 | 0 | 0 |
| F# | sidebar | 778,598 | 0 | 0 | 0 | 0 |
| F# | search | 777,716 | 0 | 0 | 0 | 0 |
| F# | post_message | 215,268 | 0 | 0 | 0 | 0 |

### Warm-up before each measured run (the app runs the route at the measured concurrency until its throughput is flat; bench/lib/warmup.py)

`measured / warm-up tail` is the measured run's requests a second over the mean of the last three 2 s warm-up windows: well above 1 would mean the app was still speeding up when measurement began, well below 1 that it was slowing down.

| App | Route | c | warm-up seconds | settled | measured / warm-up tail |
|---|---|---|---|---|---|
| F# | room_show | 1 | 30.0 [30.0–30.0] | 3/3 | 1.01 |
| F# | room_show | 16 | 34.0 [30.0–44.0] | 3/3 | 1.00 |
| F# | messages_page | 1 | 30.0 [30.0–30.0] | 3/3 | 0.99 |
| F# | messages_page | 16 | 42.0 [30.0–90.0] | 2/3 **←** | 1.00 |
| F# | sidebar | 1 | 30.0 [30.0–40.0] | 3/3 | 1.00 |
| F# | sidebar | 16 | 30.0 [30.0–40.0] | 3/3 | 1.00 |
| F# | search | 1 | 30.0 [30.0–30.0] | 3/3 | 1.01 |
| F# | search | 16 | 30.0 [30.0–32.0] | 3/3 | 1.01 |
| F# | post_message | 1 | 40.0 [30.0–90.0] | 2/3 **←** | 1.00 |
| F# | post_message | 16 | 30.0 [30.0–32.0] | 3/3 | 0.94 |

### Rows written by `post_message` (messages of the write room counted in the app's SQLite file around each run)

| App | Rep | Phase | Posts answered below 400 | Rows written | Match |
|---|---|---|---|---|---|
| F# | 1 | warm-up c=1 | 90,639 | 90,639 | yes |
| F# | 1 | run c=1 | 18,623 | 18,623 | yes |
| F# | 1 | warm-up c=16 | 206,131 | 206,131 | yes |
| F# | 1 | run c=16 | 58,331 | 58,331 | yes |
| F# | 1 | **all** | 373,724 | 373,724 | yes |
| F# | 2 | warm-up c=1 | 69,835 | 69,835 | yes |
| F# | 2 | run c=1 | 18,964 | 18,964 | yes |
| F# | 2 | warm-up c=16 | 220,944 | 220,944 | yes |
| F# | 2 | run c=16 | 56,051 | 56,051 | yes |
| F# | 2 | **all** | 365,794 | 365,794 | yes |
| F# | 3 | warm-up c=1 | 193,428 | 193,428 | yes |
| F# | 3 | run c=1 | 17,237 | 17,237 | yes |
| F# | 3 | warm-up c=16 | 199,649 | 199,649 | yes |
| F# | 3 | run c=16 | 46,062 | 46,062 | yes |
| F# | 3 | **all** | 456,376 | 456,376 | yes |

### Response bytes, one request per route (gzip: wire / decoded; identity: bytes) and the average over the c=16 run

The probe is a single request after sign-in, before any load. Bodies carry per-request tokens and the clock, so sizes of two apps agree to within a few bytes when they render the same page. "c=16 avg" is what the load generator received per request on the wire (gzip unless the column says otherwise).

| Route | F# gzip wire / decoded | F# identity | F# c=16 avg | F# vs Rust |
|---|---|---|---|---|
| room_show | 200 24,080 / 416,122 | 200 416,122 | 24,080 | – |
| messages_page | 200 15,967 / 383,844 | 200 383,844 | 15,967 | – |
| sidebar | 200 5,910 / 30,763 | 200 30,763 | 5,910 | – |
| search | 200 9,713 / 149,623 | 200 149,623 | 9,713 | – |
| post_message | – | – | 1,992 | – |
