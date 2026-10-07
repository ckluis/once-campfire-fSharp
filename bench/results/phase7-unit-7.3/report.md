```
date: 2026-10-06T17:57:20-04:00
host: 6.8.0-117-generic, , 8 threads, 5GB
server cpus: 0-3 (nproc 4); loadgen cpus: 4-7; network: host
env: WEB_CONCURRENCY=3 JOB_CONCURRENCY=3 RAILS_MAX_THREADS=5 
rust extra env: 
fsharp extra env: 
user agent: (none)
fsharp image: campfire-fsharp:app sha256:53ce6520c2c2b6ec6c4c85985059c413ff1b0958f97983ee294df1a56442b09e 2026-10-06T17:56:54.059565692-04:00 built from commit: b1d55bf59da053f1697bee5229b0bdfea5989cbb
repo HEAD: b1d55bf (dirty: 0 files under src/ and rust/)
work dir (seed copies): /var/tmp/campfire-bench-work (ext4)
loadgen: /Users/clank/Desktop/projects/once-campfire-fsharp/target/rust/aarch64-unknown-linux-gnu/release/loadgen (2026-10-06 03:04:07), built with rust:1.98.1-trixie
schedule: apps fsharp, reps 3; suites: http; http secs 8, concs 1 16, routes room_show messages_page sidebar search post_message (in parts, --part 1/2 is one slice of them), warm-up 30s at each route and concentration, idle 5s
log retention: 2 x 10m (json-file)
```

Reps: fsharp 3. Cells: median [min–max].

### Startup and memory

| Metric | F# |
|---|---|
| cold start: docker run → /up 200 (ms) | 228 [213–247] |
| idle memory.current (MB) | 28.0 [28.0–28.0] |
| idle anon (MB) | 15.0 [15.0–15.0] |
| peak memory.current under load (MB) | 407 [402–453] |
| peak anon under load (MB) | 216 [211–216] |

### HTTP (signed in as david; keep-alive; c = concurrent connections)

`CPU µs/req` is the container's CPU (cgroup cpu.stat, user + system, unprofiled) over the run divided by its requests; for CPU a ratio below 1 means the first app spends more per request.

| Metric | F# |
|---|---|
| room_show c=1 req/s | 7,524 [7,473–7,529] |
| room_show c=1 CPU µs/req | 210 [209–210] |
| room_show c=1 p50 ms | 0.13 [0.13–0.13] |
| room_show c=1 p99 ms | 0.18 [0.18–0.18] |
| room_show c=16 req/s | 38,606 [38,027–39,915] |
| room_show c=16 CPU µs/req | 97.5 [94.9–100.4] |
| room_show c=16 p50 ms | 0.32 [0.32–0.35] |
| room_show c=16 p99 ms | 1.66 [1.65–1.70] |
| messages_page c=1 req/s | 8,112 [8,035–8,154] |
| messages_page c=1 CPU µs/req | 200 [199–200] |
| messages_page c=1 p50 ms | 0.12 [0.12–0.12] |
| messages_page c=1 p99 ms | 0.17 [0.17–0.17] |
| messages_page c=16 req/s | 37,933 [36,937–38,006] |
| messages_page c=16 CPU µs/req | 102 [101–104] |
| messages_page c=16 p50 ms | 0.35 [0.35–0.35] |
| messages_page c=16 p99 ms | 3.17 [3.14–3.47] |
| sidebar c=1 req/s | 7,690 [7,613–7,728] |
| sidebar c=1 CPU µs/req | 188 [186–192] |
| sidebar c=1 p50 ms | 0.12 [0.12–0.13] |
| sidebar c=1 p99 ms | 0.18 [0.18–0.18] |
| sidebar c=16 req/s | 36,659 [36,588–37,228] |
| sidebar c=16 CPU µs/req | 104 [103–105] |
| sidebar c=16 p50 ms | 0.36 [0.35–0.36] |
| sidebar c=16 p99 ms | 1.76 [1.74–1.78] |
| search c=1 req/s | 5,837 [5,796–5,993] |
| search c=1 CPU µs/req | 322 [319–324] |
| search c=1 p50 ms | 0.17 [0.17–0.17] |
| search c=1 p99 ms | 0.21 [0.21–0.22] |
| search c=16 req/s | 33,798 [33,717–33,950] |
| search c=16 CPU µs/req | 108 [108–109] |
| search c=16 p50 ms | 0.44 [0.44–0.44] |
| search c=16 p99 ms | 1.38 [1.36–1.39] |
| post_message c=1 req/s | 2,727 [2,420–2,766] |
| post_message c=1 CPU µs/req | 697 [694–744] |
| post_message c=1 p50 ms | 0.34 [0.34–0.36] |
| post_message c=1 p99 ms | 0.64 [0.60–1.46] |
| post_message c=16 req/s | 8,108 [7,861–8,124] |
| post_message c=16 CPU µs/req | 396 [394–399] |
| post_message c=16 p50 ms | 1.72 [1.72–1.74] |
| post_message c=16 p99 ms | 6.09 [5.98–6.09] |

## Validity

### Load average before each run (1, 5, 15 minutes; the run waited for the 1-minute figure to fall below LOAD_MAX)

| App | Rep | Before | After |
|---|---|---|---|
| F# | 1 | 1.39 2.56 3.45 | 4.98 4.80 4.59 |
| F# | 2 | 1.42 3.73 4.23 | 3.77 4.32 4.92 |
| F# | 3 | 1.30 3.48 4.59 | 5.47 5.02 5.13 |

### Host CPU probe (a fixed spin timed on every pinned CPU, against the quietest this VM has shown; bench/lib/hostprobe.py)

The VM's load average cannot see another program on the Mac taking the cores its vCPUs run on; this can. A ratio of 1.00 is a quiet host, above 1.10 the pinned server and load generator were slowed by something outside the run. `After each window` probes the machine right after every measured run of the rep.

| App | Rep | Before the run | After the run | Windows probed above 1.10 | Highest after a window |
|---|---|---|---|---|---|
| F# | 1 | 1.03 | 1.01 | 0/10 | 1.05 |
| F# | 2 | 1.02 | 1.03 | 0/10 | 1.04 |
| F# | 3 | 1.02 | 1.01 | 0/10 | 1.05 |

### Responses by status class, every HTTP run of every rep

| App | Route | 2xx | 3xx | 4xx | 5xx | Errors (no response) |
|---|---|---|---|---|---|---|
| F# | room_show | 1,112,668 | 0 | 0 | 0 | 0 |
| F# | messages_page | 1,097,475 | 0 | 0 | 0 | 0 |
| F# | sidebar | 1,068,109 | 0 | 0 | 0 | 0 |
| F# | search | 952,777 | 0 | 0 | 0 | 0 |
| F# | post_message | 256,078 | 0 | 0 | 0 | 0 |

### Warm-up before each measured run (the app runs the route at the measured concurrency until its throughput is flat; bench/lib/warmup.py)

`measured / warm-up tail` is the measured run's requests a second over the mean of the last three 2 s warm-up windows: well above 1 would mean the app was still speeding up when measurement began, well below 1 that it was slowing down.

| App | Route | c | warm-up seconds | settled | measured / warm-up tail |
|---|---|---|---|---|---|
| F# | room_show | 1 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| F# | room_show | 16 | 30.0 [30.0–90.0] | 2/3 **←** | 0.98 |
| F# | messages_page | 1 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| F# | messages_page | 16 | 90.0 [90.0–90.0] | 0/3 **←** | 1.00 |
| F# | sidebar | 1 | 30.0 [30.0–30.0] | 3/3 | 1.02 |
| F# | sidebar | 16 | 90.0 [90.0–90.0] | 0/3 **←** | 0.99 |
| F# | search | 1 | 30.0 [30.0–30.0] | 3/3 | 1.01 |
| F# | search | 16 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| F# | post_message | 1 | 32.0 [30.0–90.0] | 2/3 **←** | 0.99 |
| F# | post_message | 16 | 32.0 [30.0–36.0] | 3/3 | 1.00 |

### Rows written by `post_message` (messages of the write room counted in the app's SQLite file around each run)

| App | Rep | Phase | Posts answered below 400 | Rows written | Match |
|---|---|---|---|---|---|
| F# | 1 | warm-up c=1 | 86,433 | 86,433 | yes |
| F# | 1 | run c=1 | 21,814 | 21,814 | yes |
| F# | 1 | warm-up c=16 | 226,116 | 226,116 | yes |
| F# | 1 | run c=16 | 64,877 | 64,877 | yes |
| F# | 1 | **all** | 399,240 | 399,240 | yes |
| F# | 2 | warm-up c=1 | 233,778 | 233,778 | yes |
| F# | 2 | run c=1 | 19,357 | 19,357 | yes |
| F# | 2 | warm-up c=16 | 231,148 | 231,148 | yes |
| F# | 2 | run c=16 | 62,899 | 62,899 | yes |
| F# | 2 | **all** | 547,182 | 547,182 | yes |
| F# | 3 | warm-up c=1 | 82,023 | 82,023 | yes |
| F# | 3 | run c=1 | 22,128 | 22,128 | yes |
| F# | 3 | warm-up c=16 | 243,079 | 243,079 | yes |
| F# | 3 | run c=16 | 65,003 | 65,003 | yes |
| F# | 3 | **all** | 412,233 | 412,233 | yes |

### Response bytes, one request per route (gzip: wire / decoded; identity: bytes) and the average over the c=16 run

The probe is a single request after sign-in, before any load. Bodies carry per-request tokens and the clock, so sizes of two apps agree to within a few bytes when they render the same page. "c=16 avg" is what the load generator received per request on the wire (gzip unless the column says otherwise).

| Route | F# gzip wire / decoded | F# identity | F# c=16 avg | F# vs Rust |
|---|---|---|---|---|
| room_show | 200 24,080 / 416,122 | 200 416,122 | 24,080 | – |
| messages_page | 200 15,967 / 383,844 | 200 383,844 | 15,967 | – |
| sidebar | 200 5,910 / 30,763 | 200 30,763 | 5,910 | – |
| search | 200 9,713 / 149,623 | 200 149,623 | 9,713 | – |
| post_message | – | – | 1,992 | – |
