```
date: 2026-10-07T04:18:46-04:00
host: 6.8.0-117-generic, , 8 threads, 5GB
server cpus: 0-3 (nproc 4); loadgen cpus: 4-7; network: host
env: WEB_CONCURRENCY=3 JOB_CONCURRENCY=3 RAILS_MAX_THREADS=5 
rust extra env: 
fsharp extra env: 
user agent: (none)
fsharp image: campfire-fsharp:app sha256:421f335c3ff8ee640991228ffe18bb750f2a37a4bd5ad9da64e0a0da5cb1ae0c 2026-10-07T04:18:34.342866793-04:00 built from commit: 2e6c08e242191cb9c982ecc0f2c06e56d2e9855e
repo HEAD: 2e6c08e (dirty: 0 files under src/ and rust/)
work dir (seed copies): /var/tmp/campfire-bench-work (ext4)
loadgen: /Users/clank/Desktop/projects/once-campfire-fsharp/target/rust/aarch64-unknown-linux-gnu/release/loadgen (2026-10-06 03:04:07), built with rust:1.98.1-trixie
schedule: apps fsharp, reps 3; suites: http; http secs 8, concs 1 16, routes room_show messages_page sidebar search post_message (in parts, --part 1/2 is one slice of them), warm-up 30s at each route and concentration, idle 5s
log retention: 2 x 10m (json-file)
```

Reps: fsharp 3. Cells: median [min–max].

### Startup and memory

| Metric | F# |
|---|---|
| cold start: docker run → /up 200 (ms) | 203 [198–228] |
| idle memory.current (MB) | 27.0 [27.0–28.0] |
| idle anon (MB) | 15.0 [15.0–15.0] |
| peak memory.current under load (MB) | 428 [422–446] |
| peak anon under load (MB) | 215 [214–216] |

### HTTP (signed in as david; keep-alive; c = concurrent connections)

`CPU µs/req` is the container's CPU (cgroup cpu.stat, user + system, unprofiled) over the run divided by its requests; for CPU a ratio below 1 means the first app spends more per request.

| Metric | F# |
|---|---|
| room_show c=1 req/s | 7,391 [7,337–7,518] |
| room_show c=1 CPU µs/req | 209 [207–211] |
| room_show c=1 p50 ms | 0.14 [0.13–0.14] |
| room_show c=1 p99 ms | 0.18 [0.18–0.18] |
| room_show c=16 req/s | 39,144 [39,112–39,172] |
| room_show c=16 CPU µs/req | 91.0 [90.9–91.1] |
| room_show c=16 p50 ms | 0.38 [0.38–0.38] |
| room_show c=16 p99 ms | 1.08 [1.08–1.09] |
| messages_page c=1 req/s | 8,115 [8,107–8,120] |
| messages_page c=1 CPU µs/req | 202 [201–202] |
| messages_page c=1 p50 ms | 0.12 [0.12–0.12] |
| messages_page c=1 p99 ms | 0.17 [0.17–0.17] |
| messages_page c=16 req/s | 42,144 [41,883–42,350] |
| messages_page c=16 CPU µs/req | 84.4 [83.9–84.9] |
| messages_page c=16 p50 ms | 0.36 [0.36–0.36] |
| messages_page c=16 p99 ms | 1.01 [1.01–1.02] |
| sidebar c=1 req/s | 7,752 [7,688–7,836] |
| sidebar c=1 CPU µs/req | 185 [182–188] |
| sidebar c=1 p50 ms | 0.12 [0.12–0.12] |
| sidebar c=1 p99 ms | 0.18 [0.18–0.18] |
| sidebar c=16 req/s | 37,814 [37,803–37,993] |
| sidebar c=16 CPU µs/req | 95.4 [94.8–95.5] |
| sidebar c=16 p50 ms | 0.39 [0.39–0.39] |
| sidebar c=16 p99 ms | 1.06 [1.06–1.07] |
| search c=1 req/s | 5,841 [5,739–6,144] |
| search c=1 CPU µs/req | 326 [316–330] |
| search c=1 p50 ms | 0.17 [0.16–0.17] |
| search c=1 p99 ms | 0.21 [0.21–0.21] |
| search c=16 req/s | 34,002 [33,906–34,263] |
| search c=16 CPU µs/req | 108 [107–108] |
| search c=16 p50 ms | 0.44 [0.44–0.44] |
| search c=16 p99 ms | 1.30 [1.29–1.30] |
| post_message c=1 req/s | 2,976 [2,956–3,066] |
| post_message c=1 CPU µs/req | 645 [634–649] |
| post_message c=1 p50 ms | 0.31 [0.30–0.32] |
| post_message c=1 p99 ms | 0.56 [0.54–0.57] |
| post_message c=16 req/s | 8,738 [8,676–8,860] |
| post_message c=16 CPU µs/req | 358 [356–361] |
| post_message c=16 p50 ms | 1.57 [1.57–1.58] |
| post_message c=16 p99 ms | 5.81 [5.73–6.13] |

## Validity

### Load average before each run (1, 5, 15 minutes; the run waited for the 1-minute figure to fall below LOAD_MAX)

| App | Rep | Before | After |
|---|---|---|---|
| F# | 1 | 1.29 2.65 3.08 | 5.48 3.71 3.33 |
| F# | 2 | 1.32 2.79 3.03 | 4.27 3.29 3.12 |
| F# | 3 | 1.44 2.64 2.91 | 5.02 3.39 3.09 |

### Host CPU probe (a fixed spin timed on every pinned CPU, against the quietest this VM has shown; bench/lib/hostprobe.py)

The VM's load average cannot see another program on the Mac taking the cores its vCPUs run on; this can. A ratio of 1.00 is a quiet host, above 1.10 the pinned server and load generator were slowed by something outside the run. `After each window` probes the machine right after every measured run of the rep.

| App | Rep | Before the run | After the run | Windows probed above 1.10 | Highest after a window |
|---|---|---|---|---|---|
| F# | 1 | 1.04 | 1.04 | 0/10 | 1.07 |
| F# | 2 | 1.04 | 1.03 | 0/10 | 1.07 |
| F# | 3 | 1.04 | 1.01 | 0/10 | 1.06 |

### Responses by status class, every HTTP run of every rep

| App | Route | 2xx | 3xx | 4xx | 5xx | Errors (no response) |
|---|---|---|---|---|---|---|
| F# | room_show | 1,117,445 | 0 | 0 | 0 | 0 |
| F# | messages_page | 1,205,810 | 0 | 0 | 0 | 0 |
| F# | sidebar | 1,095,145 | 0 | 0 | 0 | 0 |
| F# | search | 959,211 | 0 | 0 | 0 | 0 |
| F# | post_message | 282,224 | 0 | 0 | 0 | 0 |

### Warm-up before each measured run (the app runs the route at the measured concurrency until its throughput is flat; bench/lib/warmup.py)

`measured / warm-up tail` is the measured run's requests a second over the mean of the last three 2 s warm-up windows: well above 1 would mean the app was still speeding up when measurement began, well below 1 that it was slowing down.

| App | Route | c | warm-up seconds | settled | measured / warm-up tail |
|---|---|---|---|---|---|
| F# | room_show | 1 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| F# | room_show | 16 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| F# | messages_page | 1 | 30.0 [30.0–30.0] | 3/3 | 0.99 |
| F# | messages_page | 16 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| F# | sidebar | 1 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| F# | sidebar | 16 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| F# | search | 1 | 30.0 [30.0–30.0] | 3/3 | 1.01 |
| F# | search | 16 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| F# | post_message | 1 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| F# | post_message | 16 | 30.1 [30.0–38.0] | 3/3 | 0.98 |

### Rows written by `post_message` (messages of the write room counted in the app's SQLite file around each run)

| App | Rep | Phase | Posts answered below 400 | Rows written | Match |
|---|---|---|---|---|---|
| F# | 1 | warm-up c=1 | 88,309 | 88,309 | yes |
| F# | 1 | run c=1 | 23,807 | 23,807 | yes |
| F# | 1 | warm-up c=16 | 266,105 | 266,105 | yes |
| F# | 1 | run c=16 | 70,894 | 70,894 | yes |
| F# | 1 | **all** | 449,115 | 449,115 | yes |
| F# | 2 | warm-up c=1 | 87,850 | 87,850 | yes |
| F# | 2 | run c=1 | 23,653 | 23,653 | yes |
| F# | 2 | warm-up c=16 | 336,961 | 336,961 | yes |
| F# | 2 | run c=16 | 69,918 | 69,918 | yes |
| F# | 2 | **all** | 518,382 | 518,382 | yes |
| F# | 3 | warm-up c=1 | 90,552 | 90,552 | yes |
| F# | 3 | run c=1 | 24,530 | 24,530 | yes |
| F# | 3 | warm-up c=16 | 268,001 | 268,001 | yes |
| F# | 3 | run c=16 | 69,422 | 69,422 | yes |
| F# | 3 | **all** | 452,505 | 452,505 | yes |

### Response bytes, one request per route (gzip: wire / decoded; identity: bytes) and the average over the c=16 run

The probe is a single request after sign-in, before any load. Bodies carry per-request tokens and the clock, so sizes of two apps agree to within a few bytes when they render the same page. "c=16 avg" is what the load generator received per request on the wire (gzip unless the column says otherwise).

| Route | F# gzip wire / decoded | F# identity | F# c=16 avg | F# vs Rust |
|---|---|---|---|---|
| room_show | 200 24,080 / 416,122 | 200 416,122 | 24,080 | – |
| messages_page | 200 15,967 / 383,844 | 200 383,844 | 15,967 | – |
| sidebar | 200 5,910 / 30,763 | 200 30,763 | 5,910 | – |
| search | 200 9,713 / 149,623 | 200 149,623 | 9,713 | – |
| post_message | – | – | 1,993 | – |
