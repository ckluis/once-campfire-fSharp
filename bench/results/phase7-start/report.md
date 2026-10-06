```
date: 2026-10-06T09:28:11-04:00
host: 6.8.0-117-generic, , 8 threads, 5GB
server cpus: 0-3 (nproc 4); loadgen cpus: 4-7; network: host
env: WEB_CONCURRENCY=3 JOB_CONCURRENCY=3 RAILS_MAX_THREADS=5 
rust extra env: 
fsharp extra env: 
user agent: (none)
rust image: campfire-rust:app sha256:d582a01d3a4ae9c5fd4bc9cd7b0262e1a5d4440cc0058d200b50ad9b27e1365c 2026-10-05T23:48:02.375329666-04:00 built from commit: 
fsharp image: campfire-fsharp:app sha256:76b5ca0fa4e027e404ae4061c17e4af2b723ffcc1334ba8a5eb717a721517941 2026-10-06T09:03:28.60734127-04:00 built from commit: e43ab7dfa0b5e5ae5c3c2910e50cb424fb6cdbc3
repo HEAD: e43ab7d (dirty: 0 files under src/ and rust/)
work dir (seed copies): /var/tmp/campfire-bench-work (ext4)
loadgen: /Users/clank/Desktop/projects/once-campfire-fsharp/target/rust/aarch64-unknown-linux-gnu/release/loadgen (2026-10-06 03:04:07), built with rust:1.98.1-trixie
schedule: apps rust,fsharp, reps 3; suites: http; http secs 8, concs 1 16, routes room_show messages_page sidebar search post_message (each rep run in two parts, routes 1-3 then 4-5, each part in a fresh container; MERGE_PART=1), warm-up 30s at each route and concentration, idle 5s
log retention: 2 x 10m (json-file)
```

Reps: rust 3, fsharp 3. Cells: median [min–max].

### Startup and memory

| Metric | Rust | F# | F# adv. |
|---|---|---|---|
| cold start: docker run → /up 200 (ms) | 102 [100–110] | 205 [197–250] | 0.50× |
| idle memory.current (MB) | 16.0 [16.0–16.0] | 28.0 [28.0–28.0] | 0.57× |
| idle anon (MB) | 15.0 [14.0–15.0] | 15.0 [15.0–15.0] | 1.00× |
| peak memory.current under load (MB) | 273 [269–299] | 371 [371–373] | 0.74× |
| peak anon under load (MB) | 99.0 [98.0–99.0] | 258 [252–258] | 0.38× |

### HTTP (signed in as david; keep-alive; c = concurrent connections)

`CPU µs/req` is the container's CPU (cgroup cpu.stat, user + system, unprofiled) over the run divided by its requests; for CPU a ratio below 1 means the first app spends more per request.

| Metric | Rust | F# | F# adv. |
|---|---|---|---|
| room_show c=1 req/s | 6,500 [6,483–6,535] | 3,885 [3,806–4,160] | 0.60× |
| room_show c=1 CPU µs/req | 142 [142–142] | 453 [430–459] | 0.31× |
| room_show c=1 p50 ms | 0.15 [0.15–0.15] | 0.25 [0.24–0.26] | 0.60× |
| room_show c=1 p99 ms | 0.18 [0.18–0.19] | 0.43 [0.42–0.51] | 0.42× |
| room_show c=16 req/s | 28,720 [28,438–28,822] | 21,892 [21,025–22,469] | 0.76× |
| room_show c=16 CPU µs/req | 122 [121–122] | 173 [168–178] | 0.70× |
| room_show c=16 p50 ms | 0.53 [0.53–0.54] | 0.67 [0.65–0.69] | 0.80× |
| room_show c=16 p99 ms | 1.08 [1.07–1.10] | 1.65 [1.60–1.86] | 0.65× |
| messages_page c=1 req/s | 6,981 [6,958–6,998] | 4,227 [4,131–4,448] | 0.61× |
| messages_page c=1 CPU µs/req | 136 [136–136] | 449 [429–458] | 0.30× |
| messages_page c=1 p50 ms | 0.14 [0.14–0.14] | 0.23 [0.22–0.24] | 0.61× |
| messages_page c=1 p99 ms | 0.17 [0.17–0.17] | 0.41 [0.40–0.44] | 0.42× |
| messages_page c=16 req/s | 30,230 [30,063–30,267] | 23,346 [23,082–23,694] | 0.77× |
| messages_page c=16 CPU µs/req | 114 [114–115] | 161 [159–162] | 0.71× |
| messages_page c=16 p50 ms | 0.51 [0.51–0.51] | 0.62 [0.62–0.64] | 0.81× |
| messages_page c=16 p99 ms | 0.98 [0.97–0.98] | 1.51 [1.43–1.58] | 0.65× |
| sidebar c=1 req/s | 6,366 [6,356–6,368] | 3,837 [3,750–3,986] | 0.60× |
| sidebar c=1 CPU µs/req | 146 [145–146] | 443 [433–455] | 0.33× |
| sidebar c=1 p50 ms | 0.15 [0.15–0.15] | 0.26 [0.25–0.26] | 0.60× |
| sidebar c=1 p99 ms | 0.18 [0.18–0.18] | 0.44 [0.43–0.46] | 0.42× |
| sidebar c=16 req/s | 28,378 [28,314–28,388] | 18,888 [18,724–19,410] | 0.67× |
| sidebar c=16 CPU µs/req | 123 [123–123] | 200 [193–200] | 0.62× |
| sidebar c=16 p50 ms | 0.54 [0.54–0.54] | 0.78 [0.77–0.80] | 0.69× |
| sidebar c=16 p99 ms | 1.11 [1.11–1.11] | 1.72 [1.62–1.74] | 0.65× |
| search c=1 req/s | 5,638 [5,451–5,679] | 3,990 [3,914–4,123] | 0.71× |
| search c=1 CPU µs/req | 158 [157–165] | 468 [459–471] | 0.34× |
| search c=1 p50 ms | 0.18 [0.17–0.18] | 0.24 [0.24–0.25] | 0.72× |
| search c=1 p99 ms | 0.20 [0.20–0.26] | 0.47 [0.45–0.47] | 0.44× |
| search c=16 req/s | 26,606 [23,838–26,814] | 20,026 [19,844–20,076] | 0.75× |
| search c=16 CPU µs/req | 124 [123–135] | 184 [184–186] | 0.68× |
| search c=16 p50 ms | 0.57 [0.57–0.61] | 0.74 [0.74–0.74] | 0.78× |
| search c=16 p99 ms | 1.11 [1.11–1.69] | 1.81 [1.78–1.82] | 0.62× |
| post_message c=1 req/s | 2,814 [2,687–2,837] | 1,958 [1,936–1,980] | 0.70× |
| post_message c=1 CPU µs/req | 399 [396–408] | 998 [994–1,016] | 0.40× |
| post_message c=1 p50 ms | 0.34 [0.34–0.35] | 0.49 [0.49–0.50] | 0.70× |
| post_message c=1 p99 ms | 0.53 [0.52–0.60] | 0.78 [0.76–0.84] | 0.69× |
| post_message c=16 req/s | 7,140 [7,050–7,151] | 6,046 [6,001–6,120] | 0.85× |
| post_message c=16 CPU µs/req | 389 [388–392] | 549 [546–551] | 0.71× |
| post_message c=16 p50 ms | 2.10 [2.08–2.11] | 2.37 [2.35–2.39] | 0.89× |
| post_message c=16 p99 ms | 5.98 [5.65–6.09] | 6.70 [6.56–6.97] | 0.89× |

## Validity

### Load average before each run (1, 5, 15 minutes; the run waited for the 1-minute figure to fall below LOAD_MAX)

| App | Rep | Before | After |
|---|---|---|---|
| Rust | 1 | 1.25 2.14 2.41 | 3.15 2.74 2.56 |
| Rust | 2 | 1.28 2.98 3.02 | 4.19 3.39 3.15 |
| Rust | 3 | 1.28 2.65 2.91 | 4.08 3.30 3.06 |
| F# | 1 | 1.45 2.33 2.43 | 5.42 3.82 3.05 |
| F# | 2 | 1.28 2.84 2.77 | 5.74 4.07 3.34 |
| F# | 3 | 1.38 2.65 2.85 | 6.38 4.08 3.37 |

### Host CPU probe (a fixed spin timed on every pinned CPU, against the quietest this VM has shown; bench/lib/hostprobe.py)

The VM's load average cannot see another program on the Mac taking the cores its vCPUs run on; this can. A ratio of 1.00 is a quiet host, above 1.10 the pinned server and load generator were slowed by something outside the run. `After each window` probes the machine right after every measured run of the rep.

| App | Rep | Before the run | After the run | Windows probed above 1.10 | Highest after a window |
|---|---|---|---|---|---|
| Rust | 1 | 1.02 | 1.02 | 0/10 | 1.05 |
| Rust | 2 | 1.04 | 1.04 | 0/10 | 1.04 |
| Rust | 3 | 1.05 | 1.03 | 0/10 | 1.05 |
| F# | 1 | 1.05 | 1.02 | 0/10 | 1.05 |
| F# | 2 | 1.04 | 1.02 | 0/10 | 1.05 |
| F# | 3 | 1.04 | 1.01 | 0/10 | 1.06 |

### Responses by status class, every HTTP run of every rep

| App | Route | 2xx | 3xx | 4xx | 5xx | Errors (no response) |
|---|---|---|---|---|---|---|
| Rust | room_show | 844,036 | 0 | 0 | 0 | 0 |
| Rust | messages_page | 892,029 | 0 | 0 | 0 | 0 |
| Rust | sidebar | 833,408 | 0 | 0 | 0 | 0 |
| Rust | search | 752,266 | 0 | 0 | 0 | 0 |
| Rust | post_message | 237,529 | 0 | 0 | 0 | 0 |
| F# | room_show | 617,957 | 0 | 0 | 0 | 0 |
| F# | messages_page | 663,475 | 0 | 0 | 0 | 0 |
| F# | sidebar | 548,831 | 0 | 0 | 0 | 0 |
| F# | search | 575,841 | 0 | 0 | 0 | 0 |
| F# | post_message | 192,368 | 0 | 0 | 0 | 0 |

### Warm-up before each measured run (the app runs the route at the measured concurrency until its throughput is flat; bench/lib/warmup.py)

`measured / warm-up tail` is the measured run's requests a second over the mean of the last three 2 s warm-up windows: well above 1 would mean the app was still speeding up when measurement began, well below 1 that it was slowing down.

| App | Route | c | warm-up seconds | settled | measured / warm-up tail |
|---|---|---|---|---|---|
| Rust | room_show | 1 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| Rust | room_show | 16 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| Rust | messages_page | 1 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| Rust | messages_page | 16 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| Rust | sidebar | 1 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| Rust | sidebar | 16 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| Rust | search | 1 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| Rust | search | 16 | 30.0 [30.0–44.0] | 3/3 | 1.00 |
| Rust | post_message | 1 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| Rust | post_message | 16 | 30.0 [30.0–48.0] | 3/3 | 0.98 |
| F# | room_show | 1 | 30.0 [30.0–30.0] | 3/3 | 1.01 |
| F# | room_show | 16 | 30.0 [30.0–38.0] | 3/3 | 1.00 |
| F# | messages_page | 1 | 30.0 [30.0–76.0] | 3/3 | 1.00 |
| F# | messages_page | 16 | 30.0 [30.0–32.0] | 3/3 | 1.00 |
| F# | sidebar | 1 | 30.0 [30.0–30.0] | 3/3 | 1.01 |
| F# | sidebar | 16 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| F# | search | 1 | 30.0 [30.0–30.0] | 3/3 | 1.02 |
| F# | search | 16 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| F# | post_message | 1 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| F# | post_message | 16 | 30.0 [30.0–30.0] | 3/3 | 1.00 |

### Rows written by `post_message` (messages of the write room counted in the app's SQLite file around each run)

| App | Rep | Phase | Posts answered below 400 | Rows written | Match |
|---|---|---|---|---|---|
| Rust | 1 | warm-up c=1 | 84,561 | 84,561 | yes |
| Rust | 1 | run c=1 | 22,513 | 22,513 | yes |
| Rust | 1 | warm-up c=16 | 216,574 | 216,574 | yes |
| Rust | 1 | run c=16 | 56,412 | 56,412 | yes |
| Rust | 1 | **all** | 380,060 | 380,060 | yes |
| Rust | 2 | warm-up c=1 | 84,906 | 84,906 | yes |
| Rust | 2 | run c=1 | 22,699 | 22,699 | yes |
| Rust | 2 | warm-up c=16 | 219,477 | 219,477 | yes |
| Rust | 2 | run c=16 | 57,135 | 57,135 | yes |
| Rust | 2 | **all** | 384,217 | 384,217 | yes |
| Rust | 3 | warm-up c=1 | 82,709 | 82,709 | yes |
| Rust | 3 | run c=1 | 21,496 | 21,496 | yes |
| Rust | 3 | warm-up c=16 | 301,947 | 301,947 | yes |
| Rust | 3 | run c=16 | 57,274 | 57,274 | yes |
| Rust | 3 | **all** | 463,426 | 463,426 | yes |
| F# | 1 | warm-up c=1 | 57,429 | 57,429 | yes |
| F# | 1 | run c=1 | 15,490 | 15,490 | yes |
| F# | 1 | warm-up c=16 | 182,035 | 182,035 | yes |
| F# | 1 | run c=16 | 48,380 | 48,380 | yes |
| F# | 1 | **all** | 303,334 | 303,334 | yes |
| F# | 2 | warm-up c=1 | 58,689 | 58,689 | yes |
| F# | 2 | run c=1 | 15,840 | 15,840 | yes |
| F# | 2 | warm-up c=16 | 181,693 | 181,693 | yes |
| F# | 2 | run c=16 | 48,970 | 48,970 | yes |
| F# | 2 | **all** | 305,192 | 305,192 | yes |
| F# | 3 | warm-up c=1 | 58,543 | 58,543 | yes |
| F# | 3 | run c=1 | 15,667 | 15,667 | yes |
| F# | 3 | warm-up c=16 | 185,589 | 185,589 | yes |
| F# | 3 | run c=16 | 48,021 | 48,021 | yes |
| F# | 3 | **all** | 307,820 | 307,820 | yes |

### Response bytes, one request per route (gzip: wire / decoded; identity: bytes) and the average over the c=16 run

The probe is a single request after sign-in, before any load. Bodies carry per-request tokens and the clock, so sizes of two apps agree to within a few bytes when they render the same page. "c=16 avg" is what the load generator received per request on the wire (gzip unless the column says otherwise).

| Route | Rust gzip wire / decoded | F# gzip wire / decoded | Rust identity | F# identity | Rust c=16 avg | F# c=16 avg | F# vs Rust |
|---|---|---|---|---|---|---|---|
| room_show | 200 24,231 / 416,122 | 200 24,080 / 416,122 | 200 416,122 | 200 416,122 | 24,231 | 24,080 | identity +0 B; gzip wire -0.6% |
| messages_page | 200 16,158 / 383,844 | 200 15,967 / 383,844 | 200 383,844 | 200 383,844 | 16,158 | 15,967 | identity +0 B; gzip wire -1.2% |
| sidebar | 200 5,910 / 30,763 | 200 5,910 / 30,763 | 200 30,763 | 200 30,763 | 5,910 | 5,910 | identity +0 B; gzip wire +0.0% |
| search | 200 9,766 / 149,623 | 200 9,713 / 149,623 | 200 149,623 | 200 149,623 | 9,766 | 9,713 | identity +0 B; gzip wire -0.5% |
| post_message | – | – | – | – | 1,993 | 1,993 | c=16 avg +0 B |
