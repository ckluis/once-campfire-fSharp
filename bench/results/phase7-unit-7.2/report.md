```
date: 2026-10-06T16:12:28-04:00
host: 6.8.0-117-generic, , 8 threads, 5GB
server cpus: 0-3 (nproc 4); loadgen cpus: 4-7; network: host
env: WEB_CONCURRENCY=3 JOB_CONCURRENCY=3 RAILS_MAX_THREADS=5 
rust extra env: 
fsharp extra env: 
user agent: (none)
fsharp image: campfire-fsharp:app sha256:e316ad01e8eda3ba9ab80450267a6349253fbd5efb900d0ddb924c2bee4e4734 2026-10-06T15:20:10.915021924-04:00 built from commit: 1d0f3b2756261c871661290ba76a8ad54860ba88
repo HEAD: 1d0f3b2 (dirty: 0 files under src/ and rust/)
work dir (seed copies): /var/tmp/campfire-bench-work (ext4)
loadgen: /Users/clank/Desktop/projects/once-campfire-fsharp/target/rust/aarch64-unknown-linux-gnu/release/loadgen (2026-10-06 03:04:07), built with rust:1.98.1-trixie
schedule: apps fsharp, reps 3; suites: http; http secs 8, concs 1 16, routes room_show messages_page sidebar search post_message (in parts, --part 1/2 is one slice of them), warm-up 30s at each route and concentration, idle 5s
log retention: 2 x 10m (json-file)
```

Reps: fsharp 3. Cells: median [min–max].

### Startup and memory

| Metric | F# |
|---|---|
| cold start: docker run → /up 200 (ms) | 228 [194–247] |
| idle memory.current (MB) | 28.0 [28.0–28.0] |
| idle anon (MB) | 15.0 [15.0–15.0] |
| peak memory.current under load (MB) | 413 [386–432] |
| peak anon under load (MB) | 210 [210–210] |

### HTTP (signed in as david; keep-alive; c = concurrent connections)

`CPU µs/req` is the container's CPU (cgroup cpu.stat, user + system, unprofiled) over the run divided by its requests; for CPU a ratio below 1 means the first app spends more per request.

| Metric | F# |
|---|---|
| room_show c=1 req/s | 5,654 [5,521–5,698] |
| room_show c=1 CPU µs/req | 252 [252–258] |
| room_show c=1 p50 ms | 0.18 [0.17–0.18] |
| room_show c=1 p99 ms | 0.24 [0.23–0.24] |
| room_show c=16 req/s | 30,672 [30,638–31,019] |
| room_show c=16 CPU µs/req | 124 [121–124] |
| room_show c=16 p50 ms | 0.42 [0.42–0.42] |
| room_show c=16 p99 ms | 2.00 [1.92–2.00] |
| messages_page c=1 req/s | 6,293 [6,232–6,368] |
| messages_page c=1 CPU µs/req | 229 [226–232] |
| messages_page c=1 p50 ms | 0.16 [0.16–0.16] |
| messages_page c=1 p99 ms | 0.22 [0.21–0.22] |
| messages_page c=16 req/s | 33,229 [33,172–33,248] |
| messages_page c=16 CPU µs/req | 116 [116–116] |
| messages_page c=16 p50 ms | 0.40 [0.40–0.40] |
| messages_page c=16 p99 ms | 1.98 [1.92–2.04] |
| sidebar c=1 req/s | 5,336 [5,214–5,397] |
| sidebar c=1 CPU µs/req | 266 [262–270] |
| sidebar c=1 p50 ms | 0.19 [0.18–0.19] |
| sidebar c=1 p99 ms | 0.24 [0.24–0.24] |
| sidebar c=16 req/s | 28,192 [27,914–29,208] |
| sidebar c=16 CPU µs/req | 135 [130–136] |
| sidebar c=16 p50 ms | 0.44 [0.42–0.47] |
| sidebar c=16 p99 ms | 2.30 [2.10–2.46] |
| search c=1 req/s | 4,695 [4,397–4,829] |
| search c=1 CPU µs/req | 375 [371–384] |
| search c=1 p50 ms | 0.21 [0.20–0.22] |
| search c=1 p99 ms | 0.27 [0.27–0.33] |
| search c=16 req/s | 28,713 [28,032–29,098] |
| search c=16 CPU µs/req | 128 [126–129] |
| search c=16 p50 ms | 0.50 [0.50–0.52] |
| search c=16 p99 ms | 1.53 [1.50–1.62] |
| post_message c=1 req/s | 2,275 [2,202–2,401] |
| post_message c=1 CPU µs/req | 809 [782–812] |
| post_message c=1 p50 ms | 0.42 [0.40–0.42] |
| post_message c=1 p99 ms | 0.67 [0.65–0.80] |
| post_message c=16 req/s | 7,186 [7,154–7,508] |
| post_message c=16 CPU µs/req | 441 [430–446] |
| post_message c=16 p50 ms | 1.96 [1.91–1.96] |
| post_message c=16 p99 ms | 6.25 [6.19–6.35] |

## Validity

### Load average before each run (1, 5, 15 minutes; the run waited for the 1-minute figure to fall below LOAD_MAX)

| App | Rep | Before | After |
|---|---|---|---|
| F# | 1 | 1.27 3.90 4.42 | 4.78 5.10 5.33 |
| F# | 2 | 1.25 3.87 4.87 | 4.85 4.33 4.85 |
| F# | 3 | 1.27 3.31 4.44 | 4.74 5.22 5.41 |

### Host CPU probe (a fixed spin timed on every pinned CPU, against the quietest this VM has shown; bench/lib/hostprobe.py)

The VM's load average cannot see another program on the Mac taking the cores its vCPUs run on; this can. A ratio of 1.00 is a quiet host, above 1.10 the pinned server and load generator were slowed by something outside the run. `After each window` probes the machine right after every measured run of the rep.

| App | Rep | Before the run | After the run | Windows probed above 1.10 | Highest after a window |
|---|---|---|---|---|---|
| F# | 1 | 1.02 | 1.07 | 0/10 | 1.06 |
| F# | 2 | 1.02 | 1.06 | 0/10 | 1.07 |
| F# | 3 | 1.04 | 1.03 | 0/10 | 1.06 |

### Responses by status class, every HTTP run of every rep

| App | Route | 2xx | 3xx | 4xx | 5xx | Errors (no response) |
|---|---|---|---|---|---|---|
| F# | room_show | 873,702 | 0 | 0 | 0 | 0 |
| F# | messages_page | 948,406 | 0 | 0 | 0 | 0 |
| F# | sidebar | 810,156 | 0 | 0 | 0 | 0 |
| F# | search | 798,172 | 0 | 0 | 0 | 0 |
| F# | post_message | 229,868 | 0 | 0 | 0 | 0 |

### Warm-up before each measured run (the app runs the route at the measured concurrency until its throughput is flat; bench/lib/warmup.py)

`measured / warm-up tail` is the measured run's requests a second over the mean of the last three 2 s warm-up windows: well above 1 would mean the app was still speeding up when measurement began, well below 1 that it was slowing down.

| App | Route | c | warm-up seconds | settled | measured / warm-up tail |
|---|---|---|---|---|---|
| F# | room_show | 1 | 30.0 [30.0–30.0] | 3/3 | 1.01 |
| F# | room_show | 16 | 30.0 [30.0–90.0] | 2/3 **←** | 1.00 |
| F# | messages_page | 1 | 30.0 [30.0–30.0] | 3/3 | 0.99 |
| F# | messages_page | 16 | 90.0 [90.0–90.0] | 0/3 **←** | 0.99 |
| F# | sidebar | 1 | 30.0 [30.0–30.0] | 3/3 | 0.99 |
| F# | sidebar | 16 | 30.0 [30.0–90.0] | 2/3 **←** | 1.00 |
| F# | search | 1 | 30.0 [30.0–30.0] | 3/3 | 1.01 |
| F# | search | 16 | 30.0 [30.0–40.0] | 3/3 | 1.00 |
| F# | post_message | 1 | 30.0 [30.0–90.2] | 2/3 **←** | 0.99 |
| F# | post_message | 16 | 32.0 [30.1–40.0] | 3/3 | 1.01 |

### Rows written by `post_message` (messages of the write room counted in the app's SQLite file around each run)

| App | Rep | Phase | Posts answered below 400 | Rows written | Match |
|---|---|---|---|---|---|
| F# | 1 | warm-up c=1 | 71,169 | 71,169 | yes |
| F# | 1 | run c=1 | 19,212 | 19,212 | yes |
| F# | 1 | warm-up c=16 | 294,696 | 294,696 | yes |
| F# | 1 | run c=16 | 60,078 | 60,078 | yes |
| F# | 1 | **all** | 445,155 | 445,155 | yes |
| F# | 2 | warm-up c=1 | 191,946 | 191,946 | yes |
| F# | 2 | run c=1 | 17,620 | 17,620 | yes |
| F# | 2 | warm-up c=16 | 220,757 | 220,757 | yes |
| F# | 2 | run c=16 | 57,251 | 57,251 | yes |
| F# | 2 | **all** | 487,574 | 487,574 | yes |
| F# | 3 | warm-up c=1 | 67,024 | 67,024 | yes |
| F# | 3 | run c=1 | 18,200 | 18,200 | yes |
| F# | 3 | warm-up c=16 | 219,476 | 219,476 | yes |
| F# | 3 | run c=16 | 57,507 | 57,507 | yes |
| F# | 3 | **all** | 362,207 | 362,207 | yes |

### Response bytes, one request per route (gzip: wire / decoded; identity: bytes) and the average over the c=16 run

The probe is a single request after sign-in, before any load. Bodies carry per-request tokens and the clock, so sizes of two apps agree to within a few bytes when they render the same page. "c=16 avg" is what the load generator received per request on the wire (gzip unless the column says otherwise).

| Route | F# gzip wire / decoded | F# identity | F# c=16 avg | F# vs Rust |
|---|---|---|---|---|
| room_show | 200 24,080 / 416,122 | 200 416,122 | 24,080 | – |
| messages_page | 200 15,967 / 383,844 | 200 383,844 | 15,967 | – |
| sidebar | 200 5,910 / 30,763 | 200 30,763 | 5,910 | – |
| search | 200 9,713 / 149,623 | 200 149,623 | 9,713 | – |
| post_message | – | – | 1,992 | – |
