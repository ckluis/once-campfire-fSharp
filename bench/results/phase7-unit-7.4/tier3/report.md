```
date: 2026-10-06T20:03:30-04:00
host: 6.8.0-117-generic, , 8 threads, 5GB
server cpus: 0-3 (nproc 4); loadgen cpus: 4-7; network: host
env: WEB_CONCURRENCY=3 JOB_CONCURRENCY=3 RAILS_MAX_THREADS=5 
rust extra env: 
fsharp extra env: 
user agent: (none)
fsharp image: campfire-fsharp:app sha256:052d898806487332e82e239f9a7d55a89e22f991ce9fd7cfa2875ddec2a9c858 2026-10-06T20:03:10.89120568-04:00 built from commit: 3e321acffac98cdf4fb5b579639a64a7752e6c1a
repo HEAD: 3e321ac (dirty: 0 files under src/ and rust/)
work dir (seed copies): /var/tmp/campfire-bench-work (ext4)
loadgen: /Users/clank/Desktop/projects/once-campfire-fsharp/target/rust/aarch64-unknown-linux-gnu/release/loadgen (2026-10-06 03:04:07), built with rust:1.98.1-trixie
schedule: apps fsharp, reps 3; suites: http; http secs 8, concs 1 16, routes room_show messages_page sidebar search post_message (in parts, --part 1/3 is one slice of them), warm-up 30s at each route and concentration, idle 5s
log retention: 2 x 10m (json-file)
```

Reps: fsharp 3. Cells: median [min–max].

### Startup and memory

| Metric | F# |
|---|---|
| cold start: docker run → /up 200 (ms) | 227 [218–277] |
| idle memory.current (MB) | 27.0 [27.0–28.0] |
| idle anon (MB) | 15.0 [15.0–15.0] |
| peak memory.current under load (MB) | 439 [426–454] |
| peak anon under load (MB) | 216 [213–217] |

### HTTP (signed in as david; keep-alive; c = concurrent connections)

`CPU µs/req` is the container's CPU (cgroup cpu.stat, user + system, unprofiled) over the run divided by its requests; for CPU a ratio below 1 means the first app spends more per request.

| Metric | F# |
|---|---|
| room_show c=1 req/s | 7,462 [7,419–7,608] |
| room_show c=1 CPU µs/req | 211 [209–211] |
| room_show c=1 p50 ms | 0.13 [0.13–0.13] |
| room_show c=1 p99 ms | 0.18 [0.17–0.18] |
| room_show c=16 req/s | 37,726 [37,233–39,136] |
| room_show c=16 CPU µs/req | 101 [98–103] |
| room_show c=16 p50 ms | 0.35 [0.33–0.36] |
| room_show c=16 p99 ms | 1.64 [1.64–1.65] |
| messages_page c=1 req/s | 8,238 [8,222–8,269] |
| messages_page c=1 CPU µs/req | 199 [199–201] |
| messages_page c=1 p50 ms | 0.12 [0.12–0.12] |
| messages_page c=1 p99 ms | 0.17 [0.16–0.17] |
| messages_page c=16 req/s | 38,174 [37,556–38,237] |
| messages_page c=16 CPU µs/req | 100 [100–103] |
| messages_page c=16 p50 ms | 0.35 [0.35–0.35] |
| messages_page c=16 p99 ms | 2.79 [2.54–3.24] |
| sidebar c=1 req/s | 7,822 [7,595–7,837] |
| sidebar c=1 CPU µs/req | 186 [185–187] |
| sidebar c=1 p50 ms | 0.12 [0.12–0.12] |
| sidebar c=1 p99 ms | 0.18 [0.18–0.32] |
| sidebar c=16 req/s | 37,900 [37,791–38,648] |
| sidebar c=16 CPU µs/req | 101 [99–101] |
| sidebar c=16 p50 ms | 0.34 [0.33–0.34] |
| sidebar c=16 p99 ms | 1.79 [1.77–1.81] |
| search c=1 req/s | 5,814 [5,745–6,042] |
| search c=1 CPU µs/req | 325 [316–327] |
| search c=1 p50 ms | 0.17 [0.17–0.17] |
| search c=1 p99 ms | 0.21 [0.21–0.22] |
| search c=16 req/s | 33,565 [33,406–33,685] |
| search c=16 CPU µs/req | 109 [108–109] |
| search c=16 p50 ms | 0.44 [0.44–0.45] |
| search c=16 p99 ms | 1.37 [1.37–1.39] |
| post_message c=1 req/s | 3,060 [2,994–3,082] |
| post_message c=1 CPU µs/req | 631 [628–635] |
| post_message c=1 p50 ms | 0.30 [0.30–0.31] |
| post_message c=1 p99 ms | 0.57 [0.54–0.61] |
| post_message c=16 req/s | 8,892 [8,751–8,997] |
| post_message c=16 CPU µs/req | 353 [350–353] |
| post_message c=16 p50 ms | 1.55 [1.53–1.55] |
| post_message c=16 p99 ms | 5.95 [5.82–6.00] |

## Validity

### Load average before each run (1, 5, 15 minutes; the run waited for the 1-minute figure to fall below LOAD_MAX)

| App | Rep | Before | After |
|---|---|---|---|
| F# | 1 | 1.37 2.59 2.64 | 4.68 4.47 4.34 |
| F# | 2 | 1.31 3.43 3.98 | 3.59 4.04 4.56 |
| F# | 3 | 1.40 3.32 4.28 | 3.70 4.02 4.66 |

### Host CPU probe (a fixed spin timed on every pinned CPU, against the quietest this VM has shown; bench/lib/hostprobe.py)

The VM's load average cannot see another program on the Mac taking the cores its vCPUs run on; this can. A ratio of 1.00 is a quiet host, above 1.10 the pinned server and load generator were slowed by something outside the run. `After each window` probes the machine right after every measured run of the rep.

| App | Rep | Before the run | After the run | Windows probed above 1.10 | Highest after a window |
|---|---|---|---|---|---|
| F# | 1 | 1.03 | 1.01 | 0/10 | 1.07 |
| F# | 2 | 1.02 | 1.02 | 0/10 | 1.04 |
| F# | 3 | 1.05 | 1.03 | 0/10 | 1.06 |

### Responses by status class, every HTTP run of every rep

| App | Route | 2xx | 3xx | 4xx | 5xx | Errors (no response) |
|---|---|---|---|---|---|---|
| F# | room_show | 1,092,742 | 0 | 0 | 0 | 0 |
| F# | messages_page | 1,109,626 | 0 | 0 | 0 | 0 |
| F# | sidebar | 1,100,812 | 0 | 0 | 0 | 0 |
| F# | search | 946,112 | 0 | 0 | 0 | 0 |
| F# | post_message | 286,269 | 0 | 0 | 0 | 0 |

### Warm-up before each measured run (the app runs the route at the measured concurrency until its throughput is flat; bench/lib/warmup.py)

`measured / warm-up tail` is the measured run's requests a second over the mean of the last three 2 s warm-up windows: well above 1 would mean the app was still speeding up when measurement began, well below 1 that it was slowing down.

| App | Route | c | warm-up seconds | settled | measured / warm-up tail |
|---|---|---|---|---|---|
| F# | room_show | 1 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| F# | room_show | 16 | 90.0 [90.0–90.0] | 0/3 **←** | 1.00 |
| F# | messages_page | 1 | 30.0 [30.0–30.0] | 3/3 | 1.01 |
| F# | messages_page | 16 | 90.0 [90.0–90.0] | 0/3 **←** | 1.00 |
| F# | sidebar | 1 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| F# | sidebar | 16 | 90.0 [50.0–90.0] | 1/3 **←** | 1.00 |
| F# | search | 1 | 90.0 [30.0–90.0] | 1/3 **←** | 1.00 |
| F# | search | 16 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| F# | post_message | 1 | 30.0 [30.0–36.0] | 3/3 | 1.00 |
| F# | post_message | 16 | 36.1 [30.0–38.0] | 3/3 | 1.01 |

### Rows written by `post_message` (messages of the write room counted in the app's SQLite file around each run)

| App | Rep | Phase | Posts answered below 400 | Rows written | Match |
|---|---|---|---|---|---|
| F# | 1 | warm-up c=1 | 85,739 | 85,739 | yes |
| F# | 1 | run c=1 | 24,482 | 24,482 | yes |
| F# | 1 | warm-up c=16 | 319,611 | 319,611 | yes |
| F# | 1 | run c=16 | 70,038 | 70,038 | yes |
| F# | 1 | **all** | 499,870 | 499,870 | yes |
| F# | 2 | warm-up c=1 | 86,000 | 86,000 | yes |
| F# | 2 | run c=1 | 24,660 | 24,660 | yes |
| F# | 2 | warm-up c=16 | 268,228 | 268,228 | yes |
| F# | 2 | run c=16 | 71,984 | 71,984 | yes |
| F# | 2 | **all** | 450,872 | 450,872 | yes |
| F# | 3 | warm-up c=1 | 101,893 | 101,893 | yes |
| F# | 3 | run c=1 | 23,953 | 23,953 | yes |
| F# | 3 | warm-up c=16 | 329,341 | 329,341 | yes |
| F# | 3 | run c=16 | 71,152 | 71,152 | yes |
| F# | 3 | **all** | 526,339 | 526,339 | yes |

### Response bytes, one request per route (gzip: wire / decoded; identity: bytes) and the average over the c=16 run

The probe is a single request after sign-in, before any load. Bodies carry per-request tokens and the clock, so sizes of two apps agree to within a few bytes when they render the same page. "c=16 avg" is what the load generator received per request on the wire (gzip unless the column says otherwise).

| Route | F# gzip wire / decoded | F# identity | F# c=16 avg | F# vs Rust |
|---|---|---|---|---|
| room_show | 200 24,080 / 416,122 | 200 416,122 | 24,080 | – |
| messages_page | 200 15,967 / 383,844 | 200 383,844 | 15,967 | – |
| sidebar | 200 5,910 / 30,763 | 200 30,763 | 5,910 | – |
| search | 200 9,713 / 149,623 | 200 149,623 | 9,713 | – |
| post_message | – | – | 1,993 | – |
