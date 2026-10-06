```
date: 2026-10-06T15:20:35-04:00
host: 6.8.0-117-generic, , 8 threads, 5GB
server cpus: 0-3 (nproc 4); loadgen cpus: 4-7; network: host
env: WEB_CONCURRENCY=3 JOB_CONCURRENCY=3 RAILS_MAX_THREADS=5 
rust extra env: 
fsharp extra env: 
user agent: (none)
fsharp image: campfire-fsharp:app sha256:9e87cfde74c348be0b31c17bfdce42e2137cb8c08483e567ea076770fe27db2c 2026-10-06T15:20:10.915021924-04:00 built from commit: dcb803277c61caf8c5c24e86df82a5f39f58bc2e
repo HEAD: dcb8032 (dirty: 0 files under src/ and rust/)
work dir (seed copies): /var/tmp/campfire-bench-work (ext4)
loadgen: /Users/clank/Desktop/projects/once-campfire-fsharp/target/rust/aarch64-unknown-linux-gnu/release/loadgen (2026-10-06 03:04:07), built with rust:1.98.1-trixie
schedule: apps fsharp, reps 3; suites: http; http secs 8, concs 1 16, routes room_show messages_page sidebar search post_message (in parts, --part 1/2 is one slice of them), warm-up 30s at each route and concentration, idle 5s
log retention: 2 x 10m (json-file)
```

Reps: fsharp 3. Cells: median [min–max].

### Startup and memory

| Metric | F# |
|---|---|
| cold start: docker run → /up 200 (ms) | 265 [242–306] |
| idle memory.current (MB) | 28.0 [28.0–28.0] |
| idle anon (MB) | 15.0 [15.0–15.0] |
| peak memory.current under load (MB) | 396 [395–404] |
| peak anon under load (MB) | 208 [206–209] |

### HTTP (signed in as david; keep-alive; c = concurrent connections)

`CPU µs/req` is the container's CPU (cgroup cpu.stat, user + system, unprofiled) over the run divided by its requests; for CPU a ratio below 1 means the first app spends more per request.

| Metric | F# |
|---|---|
| room_show c=1 req/s | 5,474 [5,019–5,585] |
| room_show c=1 CPU µs/req | 162 [161–183] |
| room_show c=1 p50 ms | 0.18 [0.18–0.18] |
| room_show c=1 p99 ms | 0.23 [0.21–0.47] |
| room_show c=16 req/s | 28,416 [27,971–28,468] |
| room_show c=16 CPU µs/req | 120 [119–120] |
| room_show c=16 p50 ms | 0.47 [0.47–0.48] |
| room_show c=16 p99 ms | 1.85 [1.85–1.93] |
| messages_page c=1 req/s | 6,013 [6,012–6,071] |
| messages_page c=1 CPU µs/req | 148 [146–148] |
| messages_page c=1 p50 ms | 0.16 [0.16–0.16] |
| messages_page c=1 p99 ms | 0.20 [0.20–0.20] |
| messages_page c=16 req/s | 30,606 [28,500–30,789] |
| messages_page c=16 CPU µs/req | 109 [108–116] |
| messages_page c=16 p50 ms | 0.45 [0.45–0.48] |
| messages_page c=16 p99 ms | 1.67 [1.66–1.76] |
| sidebar c=1 req/s | 5,148 [5,125–5,242] |
| sidebar c=1 CPU µs/req | 177 [174–178] |
| sidebar c=1 p50 ms | 0.19 [0.19–0.19] |
| sidebar c=1 p99 ms | 0.23 [0.23–0.27] |
| sidebar c=16 req/s | 26,808 [26,784–26,980] |
| sidebar c=16 CPU µs/req | 129 [128–129] |
| sidebar c=16 p50 ms | 0.48 [0.48–0.48] |
| sidebar c=16 p99 ms | 2.16 [2.14–2.23] |
| search c=1 req/s | 4,721 [4,673–4,785] |
| search c=1 CPU µs/req | 191 [190–194] |
| search c=1 p50 ms | 0.21 [0.21–0.21] |
| search c=1 p99 ms | 0.26 [0.25–0.26] |
| search c=16 req/s | 26,078 [25,881–26,311] |
| search c=16 CPU µs/req | 126 [125–128] |
| search c=16 p50 ms | 0.57 [0.57–0.58] |
| search c=16 p99 ms | 1.47 [1.44–1.49] |
| post_message c=1 req/s | 2,322 [2,275–2,389] |
| post_message c=1 CPU µs/req | 515 [510–527] |
| post_message c=1 p50 ms | 0.41 [0.41–0.42] |
| post_message c=1 p99 ms | 0.68 [0.62–0.70] |
| post_message c=16 req/s | 7,323 [7,249–7,335] |
| post_message c=16 CPU µs/req | 411 [407–414] |
| post_message c=16 p50 ms | 1.94 [1.92–1.95] |
| post_message c=16 p99 ms | 6.26 [6.17–6.36] |

## Validity

### Load average before each run (1, 5, 15 minutes; the run waited for the 1-minute figure to fall below LOAD_MAX)

| App | Rep | Before | After |
|---|---|---|---|
| F# | 1 | 1.34 4.58 4.85 | 4.70 4.32 4.77 |
| F# | 2 | 1.31 3.32 4.38 | 4.99 3.99 4.34 |
| F# | 3 | 1.39 3.07 3.99 | 6.39 4.76 4.62 |

### Host CPU probe (a fixed spin timed on every pinned CPU, against the quietest this VM has shown; bench/lib/hostprobe.py)

The VM's load average cannot see another program on the Mac taking the cores its vCPUs run on; this can. A ratio of 1.00 is a quiet host, above 1.10 the pinned server and load generator were slowed by something outside the run. `After each window` probes the machine right after every measured run of the rep.

| App | Rep | Before the run | After the run | Windows probed above 1.10 | Highest after a window |
|---|---|---|---|---|---|
| F# | 1 | 1.05 | 1.02 | 0/10 | 1.05 |
| F# | 2 | 1.03 | 1.03 | 0/10 | 1.09 |
| F# | 3 | 1.04 | 1.04 | 0/10 | 1.05 |

### Responses by status class, every HTTP run of every rep

| App | Route | 2xx | 3xx | 4xx | 5xx | Errors (no response) |
|---|---|---|---|---|---|---|
| F# | room_show | 807,510 | 0 | 0 | 0 | 0 |
| F# | messages_page | 864,018 | 0 | 0 | 0 | 0 |
| F# | sidebar | 768,757 | 0 | 0 | 0 | 0 |
| F# | search | 739,647 | 0 | 0 | 0 | 0 |
| F# | post_message | 231,213 | 0 | 0 | 0 | 0 |

### Warm-up before each measured run (the app runs the route at the measured concurrency until its throughput is flat; bench/lib/warmup.py)

`measured / warm-up tail` is the measured run's requests a second over the mean of the last three 2 s warm-up windows: well above 1 would mean the app was still speeding up when measurement began, well below 1 that it was slowing down.

| App | Route | c | warm-up seconds | settled | measured / warm-up tail |
|---|---|---|---|---|---|
| F# | room_show | 1 | 30.0 [30.0–34.0] | 3/3 | 1.00 |
| F# | room_show | 16 | 90.0 [62.0–90.0] | 1/3 **←** | 1.00 |
| F# | messages_page | 1 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| F# | messages_page | 16 | 66.0 [30.0–90.0] | 2/3 **←** | 1.00 |
| F# | sidebar | 1 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| F# | sidebar | 16 | 90.0 [46.0–90.0] | 1/3 **←** | 1.00 |
| F# | search | 1 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| F# | search | 16 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| F# | post_message | 1 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| F# | post_message | 16 | 38.0 [36.0–40.0] | 3/3 | 1.01 |

### Rows written by `post_message` (messages of the write room counted in the app's SQLite file around each run)

| App | Rep | Phase | Posts answered below 400 | Rows written | Match |
|---|---|---|---|---|---|
| F# | 1 | warm-up c=1 | 67,478 | 67,478 | yes |
| F# | 1 | run c=1 | 18,203 | 18,203 | yes |
| F# | 1 | warm-up c=16 | 252,906 | 252,906 | yes |
| F# | 1 | run c=16 | 58,033 | 58,033 | yes |
| F# | 1 | **all** | 396,620 | 396,620 | yes |
| F# | 2 | warm-up c=1 | 69,083 | 69,083 | yes |
| F# | 2 | run c=1 | 18,580 | 18,580 | yes |
| F# | 2 | warm-up c=16 | 242,021 | 242,021 | yes |
| F# | 2 | run c=16 | 58,687 | 58,687 | yes |
| F# | 2 | **all** | 388,371 | 388,371 | yes |
| F# | 3 | warm-up c=1 | 70,300 | 70,300 | yes |
| F# | 3 | run c=1 | 19,113 | 19,113 | yes |
| F# | 3 | warm-up c=16 | 264,081 | 264,081 | yes |
| F# | 3 | run c=16 | 58,597 | 58,597 | yes |
| F# | 3 | **all** | 412,091 | 412,091 | yes |

### Response bytes, one request per route (gzip: wire / decoded; identity: bytes) and the average over the c=16 run

The probe is a single request after sign-in, before any load. Bodies carry per-request tokens and the clock, so sizes of two apps agree to within a few bytes when they render the same page. "c=16 avg" is what the load generator received per request on the wire (gzip unless the column says otherwise).

| Route | F# gzip wire / decoded | F# identity | F# c=16 avg | F# vs Rust |
|---|---|---|---|---|
| room_show | 200 24,080 / 416,122 | 200 416,122 | 24,080 | – |
| messages_page | 200 15,967 / 383,844 | 200 383,844 | 15,967 | – |
| sidebar | 200 5,910 / 30,763 | 200 30,763 | 5,910 | – |
| search | 200 9,713 / 149,623 | 200 149,623 | 9,713 | – |
| post_message | – | – | 1,993 | – |
