# Phase 7 starting point (unit 7.0), 2026-10-06

F# against Rust on the five workloads at c=1 and c=16, measured with the trustworthy method this unit built (`bench/quick`, which is
`bench/run` restricted to Rust and F#, plus CPU per request from the container's cgroup). Nothing in `src/` changed in this unit.
Raw data, one JSON per app and rep: [`phase7-start/`](phase7-start/) (`report.md` there is `bench/report`'s full output with every
validity table; `breakdown-perf/` is the perf breakdown below). What the unit changed in the tooling and the warm-up evidence:
[`phase7-warmup.md`](phase7-warmup.md).

**Where F# stands.** At 16 connections F# serves 0.67-0.77x Rust's requests a second on the four read pages (room 0.76, messages
0.77, sidebar 0.67, search 0.75) and 0.85x on posting a message; at one connection 0.60-0.71x. It spends 1.41-1.62x Rust's CPU per
request at c=16 and 2.9-3.3x at c=1 (3.2x on the room page, 2.5x on a post). Nothing is faster than Rust yet; the aim is open on all
five workloads. The read pages are within 25-33% on throughput, posting within 15%.

## Starting point

Req/s and container CPU per request, median [min-max] of 3 reps, alternating app order (rust, fsharp / fsharp, rust / rust, fsharp),
a fresh container and a fresh copy of `parity/.seed/default` per app and rep, `SERVER_CPUS=0-3`, `LOADGEN_CPUS=4-7` in the colima VM.
"before" is the whole-app baseline (`baseline-20261006.md`, measured with a 2 s warm-up); "start" is this unit's method (a warm-up
of at least 30 s at each route and concurrency, until flat). F# / Rust is of medians.

| Workload | c | Rust req/s | F# before | F# start | F# / Rust before → start | Rust CPU µs/req | F# CPU µs/req before → start |
|---|---|---|---|---|---|---|---|
| room page | 16 | 28,720 [28,438-28,822] | 21,003 [20,376-21,477] | 21,892 [21,025-22,469] | 0.75× → **0.76×** | 121.6 [121.3-122.3] | 173.8 [171.8-177.0] → 173.0 [167.5-177.8] |
| room page | 1 | 6,500 [6,483-6,535] | 3,329 [3,199-3,441] | 3,885 [3,806-4,160] | 0.53× → **0.60×** | 141.6 [141.5-141.8] | 538.4 [534.7-559.5] → 453.2 [429.7-459.4] |
| messages page | 16 | 30,230 [30,063-30,267] | 23,097 [22,661-23,160] | 23,346 [23,082-23,694] | 0.79× → **0.77×** | 113.7 [113.7-114.6] | 166.2 [165.4-166.9] → 160.8 [159.1-162.3] |
| messages page | 1 | 6,981 [6,958-6,998] | 4,044 [4,020-4,065] | 4,227 [4,131-4,448] | 0.61× → **0.61×** | 135.7 [135.6-136.1] | 463.5 [454.7-473.8] → 449.3 [429.2-457.7] |
| sidebar | 16 | 28,378 [28,314-28,388] | 19,115 [18,321-19,125] | 18,888 [18,724-19,410] | 0.68× → **0.67×** | 122.8 [122.7-123.3] | 199.2 [196.0-202.3] → 199.5 [192.7-199.5] |
| sidebar | 1 | 6,366 [6,356-6,368] | 3,738 [3,661-3,776] | 3,837 [3,750-3,986] | 0.61× → **0.60×** | 145.8 [145.4-145.8] | 465.3 [455.5-474.9] → 442.7 [432.9-455.4] |
| search | 16 | 26,606 [23,838-26,814] | 19,411 [19,394-19,728] | 20,026 [19,844-20,076] | 0.75× → **0.75×** | 124.2 [122.7-135.4] | 188.5 [186.4-189.2] → 183.8 [183.5-185.9] |
| search | 1 | 5,638 [5,451-5,679] | 3,922 [3,887-3,940] | 3,990 [3,914-4,123] | 0.71× → **0.71×** | 157.9 [157.3-165.1] | 488.2 [468.0-493.4] → 467.5 [459.1-471.3] |
| post a message | 16 | 7,140 [7,050-7,151] | 6,186 [6,034-6,264] | 6,046 [6,001-6,120] | 1.02× → **0.85×** | 389.4 [387.6-392.1] | 545.9 [540.1-554.2] → 548.9 [546.0-551.4] |
| post a message | 1 | 2,814 [2,687-2,837] | 1,909 [1,890-1,919] | 1,958 [1,936-1,980] | 0.70× → **0.70×** | 399.2 [396.5-408.2] | 1,024.3 [1,007.9-1,054.3] → 997.8 [993.7-1,016.5] |

Latency at c=16, p50 / p99 ms (Rust / F#): room page 0.53 / 1.08 and 0.67 / 1.65; messages page 0.51 / 0.98 and 0.62 / 1.51; sidebar
0.54 / 1.11 and 0.78 / 1.72; search 0.57 / 1.11 and 0.74 / 1.81; post 2.10 / 5.98 and 2.37 / 6.70. At c=1 the F# p99 is 0.42-0.47 ms
against Rust's 0.17-0.20 (post 0.78 against 0.53).

What moved against the baseline, and why:

- **Two numbers are not comparable with the baseline's, for a reason that is in the method, not in either app.** F# at c=1 gains
  8-17% in req/s and 3-16% in CPU per request (the room page, the first route measured, most: 538 to 453 µs): the baseline's 8 s
  window held the JIT's ramp. And Rust's posting rate at c=16 is 17% higher than the baseline's (7,140 against 6,082) while F#'s is
  2% lower, which turns 1.02x into 0.85x. I found out why before accepting it, because 17% on one app looked too good: a
  back-to-back A/B of Rust's post c=16 gives 7,121 with the new warm-up and 6,333 and 6,605 with the old 2 s one on the same image
  (`phase7-warmup.md`); the write path has a settling period of its own, and the old warm-up measured Rust inside it.
- Everything else is within 1-4% of the baseline at c=16 (the read pages are where the baseline said they were), which is the check
  that nothing else drifted: same decoded bytes, same status classes, same CPU per request.
- Cold start and memory (not the point of this unit, only seen on the way): cold start 102 ms Rust against 205 ms F#, idle
  `memory.current` 16 against 28 MB, peak anon under this run 99 against 258 MB (the baseline's cable-heavy peak was higher).

## Method and validity

| | |
|---|---|
| Images | Rust `campfire-rust:app` `sha256:d582a01d3a4a...` (rust/ pinned at `ccece30`, not rebuilt). F# `campfire-fsharp:app` rebuilt from HEAD before measuring with `CAMPFIRE_REVISION=$(git rev-parse HEAD) parity/bin/candidate build`: `sha256:76b5ca0fa4e0...`, label `org.opencontainers.image.revision=e43ab7dfa0b5e5ae5c3c2910e50cb424fb6cdbc3` (the HEAD the matrix ran at). Docker's layer cache hit through the publish stage because `src/`, the props and `reference/`'s embedded parts are byte for byte what the baseline image was built from (`git diff dd5ae60 HEAD -- src` is empty); the digest differs from the baseline's by that label and nothing else. The perf breakdown below ran on `sha256:abc70784a0f4...` from `16a7da2` (the same `src/`) |
| Disk | `df -h /Users/clank` before every call: 29-31 GB free throughout; `bin/clean-docker` after the rebuilds |
| Logs | json-file capped at 2 x 10 MB for every app container; request logging stays on in both apps |
| Schedule | 2 apps x 3 reps, order rust, fsharp (rep 1), fsharp, rust (rep 2), rust, fsharp (rep 3); each rep in two parts (routes 1-3, then 4-5) in a fresh container each, merged: a rep of F# can outrun a foreground call (`bench/quick --part`). Before each part the harness waits for the 1-minute load average below 1.5 and for the host probe within 1.10 of the quietest on this VM |
| Per (route, c) | warm-up at that route and concurrency (30 s minimum, until three 2 s windows are within 3% of the three before, 90 s at most, the same rule for both apps), then one 8 s measured run through `cpurun.py` (cgroup `cpu.stat` for CPU per request), then a host probe |
| Status | 3,559,268 Rust and 2,598,472 F# responses over the measured runs: every one 2xx, 0 connection errors, for every route and concurrency (`report.md`) |
| Rows written | "post a message" rows counted in the app's SQLite file around every warm-up and every run, against the answers below 400: 0 mismatches in 30 phases per app (Rust 380,060 / 384,217 / 463,426 posts in the three reps, F# 303,334 / 305,192 / 307,820, every one a row) |
| Response bytes | decoded bytes identical to Rust's on every route (room page 416,122, messages page 383,844, sidebar 30,763, search 149,623; post answer 1,993); gzip on the wire 0.0 to -1.2% for F# on the big pages (a different deflate), the same as the baseline |
| Host noise | the host probe (a fixed spin timed on each pinned CPU, 57-60 ms on a quiet host): 1.00-1.06 before and after every window of all six reps, none above 1.10; the VM's load average at each start 1.25-1.45 |
| Warm-up | every warm-up ended settled; measured / last-warm-up-windows 0.98-1.02 in every cell (`phase7-warmup.md`) |
| Known limits | one VM on a laptop, three reps; spreads are 1-3% except Rust search c=16 (23,838-26,814: rep 3's warm-up took 44 s and its run read 7% under the tail, with the host probe at 1.03, so not host noise I can see; the median is used). The Cable, upload, identity and `/up`, CSS and avatar suites were not re-run in this unit; their baseline figures stand and were taken with the old warm-up |

## Where the CPU goes, unprofiled-scaled (c=16)

`bench/breakdown perf`, one run per app and route after the warm-up: an unprofiled window (cgroup CPU per request) and a window under
`perf record -g`, and every layer is its perf share times the unprofiled CPU, so each column adds up to the unprofiled total. perf's
own cost this time: +4 to +17% on F#, -4 to +8% on Rust (the old unscaled figures were not comparable between apps). Full tables,
the layer lists of both apps and the CLR's native time by activity: [`phase7-start/breakdown-perf/report.md`](phase7-start/breakdown-perf/report.md).
Host probe after every window: highest 1.06.

Rust / F# CPU µs per request:

| Group | room page | messages page | sidebar | search | post a message |
|---|---|---|---|---|---|
| SQLite engine | 40.1 / 43.6 | 42.1 / 39.6 | 42.2 / 69.0 | 58.1 / 73.4 | 197.3 / 238.4 |
| Database access around the engine (wrapper, rows, pool) | 6.8 / 32.7 | 7.0 / 31.0 | 6.2 / 28.5 | 5.9 / 26.1 | 19.6 / 76.7 |
| cookies, session, HMAC, key derivation | 5.5 / 13.3 | 3.9 / 9.0 | 17.7 / 24.9 | 3.1 / 10.5 | 25.9 / 16.5 |
| templates, escaping, fragment cache | 14.3 / 12.8 | 11.7 / 10.1 | 9.8 / 12.5 | 6.2 / 9.3 | 4.8 / 9.1 |
| gzip and the page splice | 4.7 / 3.4 | 2.8 / 1.9 | 0.5 / 0.2 | 2.9 / 1.9 | 38.1 / 36.1 |
| HTTP server and request plumbing | 29.0 / 36.6 | 31.3 / 35.1 | 26.6 / 34.8 | 28.4 / 38.8 | 58.5 / 65.6 |
| controllers, presenters, authentication, rich text | 14.5 / 6.4 | 11.0 / 4.7 | 13.3 / 6.2 | 14.5 / 8.2 | 34.0 / 45.7 |
| allocation and reclamation (F#: CLR helpers + GC; Rust: jemalloc; both: libc malloc/free) | 7.4 / 23.4 | 5.1 / 22.4 | 8.6 / 22.9 | 7.0 / 20.3 | 21.2 / 36.5 |
| runtime, other (F#: CLR/BCL with no Campfire caller: thread-pool dispatch, spinning, stubs) | 0.0 / 2.3 | 0.0 / 11.9 | 0.0 / 1.4 | 0.0 / 15.8 | 0.0 / 64.5 |
| **total (unprofiled)** | 122.3 / 174.6 | 114.9 / 165.6 | 124.9 / 200.3 | 126.1 / 204.2 | 399.3 / 589.2 |

The F# minus Rust gap in the same groups, µs per request (what each layer would have to give back for CPU parity):

| Group | room page | messages page | sidebar | search | post a message |
|---|---|---|---|---|---|
| **Database access around the engine** | **+25.9** | **+23.9** | **+22.3** | **+20.1** | **+57.1** |
| SQLite engine (more calls, the connection mutex, column access) | +3.5 | -2.5 | +26.8 | +15.3 | +41.0 |
| **Allocation and reclamation** | **+16.0** | **+17.4** | **+14.2** | **+13.2** | **+15.4** |
| runtime, other (thread-pool dispatch and spinning, stubs) | +2.3 | +11.9 | +1.4 | +15.8 | +64.5 |
| HTTP server and request plumbing | +7.7 | +3.8 | +8.2 | +10.4 | +7.1 |
| cookies, session, HMAC, key derivation | +7.8 | +5.0 | +7.2 | +7.4 | -9.3 |
| controllers, presenters, authentication, rich text | -8.1 | -6.3 | -7.1 | -6.3 | +11.7 |
| templates, gzip and the splice | -2.9 | -2.5 | +2.4 | +2.2 | +2.3 |
| **total gap** | +52.3 | +50.7 | +75.4 | +78.1 | +189.9 |

Allocation, attributed the same way for both apps (the unit's step 4), Rust / F# µs per request: the runtime allocator (F#'s CLR
allocation helpers; Rust's jemalloc) 6.9 / 10.0 on the room page, 4.6 / 7.5, 7.2 / 9.3, 5.2 / 8.3 and 14.9 / 19.8 on a post;
reclamation (F#'s GC; Rust has none) 0 / 11.9, 0 / 13.8, 0 / 10.5, 0 / 9.0 and 0 / 10.3; libc malloc and free (both apps' native
allocations) 0.5 / 1.6 up to 6.2 / 6.3. Together 7.4 / 23.4 on the room page: **F# spends 13% of its CPU on allocating and
reclaiming against Rust's 6%**, and two thirds of the difference is the GC (the server-GC threads and their join spins), not the
allocation helpers. With libcoreclr's symbols the CLR's own native time is no longer "[unknown]": per request on the room page 9.4 µs
reclaiming, 4.8 allocating, 3.6 in monitors and spinning (`ThreadNative_SpinWait`, `ObjHeader::EnterObjMonitorHelperSpin`), 2.0 in
P/Invoke transitions, 1.1 in type checks and write barriers, 0.0 in the JIT (the warm-up did its job). On a post the monitors and spinning
are 31.5 µs and the "runtime, other" row 64.5.

One caution on the post column: the same flow gave F# post unprofiled CPU of 741, 589 and (alone, once) 543 µs/req in three runs,
against `bench/quick`'s 549 [546-551] in three reps. The perf flow runs five routes in one container with perf data being written
to disk beside a fsync-heavy route; the layer shares of the post column are right for the window they were taken in, but quote its
µs from `bench/quick`.

## Next target

The largest remaining gap, measured, is **database access around SQLite's engine**: 20-26 µs per page request and 57 µs per post, a
third to a half of the F# - Rust difference on every route (the Rust db crate costs 6-7 µs there; F#'s `Microsoft.Data.Sqlite`
reader, the P/Invoke stubs with SafeHandle add-ref/release, `Campfire.Db`'s row mapping and the reader pool's lock and thread
hand-off cost 26-33). It is item 1 of the baseline's ranked targets and nothing in this unit moved it. Behind it, in order:
allocation and GC (13-17 µs a request on every route, about 30% of the gap), the SQLite engine itself on sidebar, search and post
(+15 to +41, the connection mutex and per-column calls), and on post and search the thread-pool dispatch and spinning
(+16 to +65). The Falco layer, the request log line (7-13 µs) and the JIT are not the problem.

## Reproducing

All inside colima (`colima ssh -- bash -c 'cd /Users/clank/Desktop/projects/once-campfire-fsharp && ...'`), `SERVER_CPUS=0-3
LOADGEN_CPUS=4-7`:

```sh
CAMPFIRE_REVISION=$(git rev-parse HEAD) parity/bin/candidate build          # the F# image from HEAD, labelled with the commit
bench/quick --only rust:1 --part 1/2 --out bench/results/phase7-start       # then 2/2, fsharp:1 (1/2, 2/2), fsharp:2, rust:2, rust:3, fsharp:3
bench/quick report bench/results/phase7-start
bench/breakdown perf --apps fsharp --routes room_show,messages_page,sidebar,search,post_message --out bench/results/phase7-start/breakdown-perf   # and --apps rust
python3 bench/lib/breakdown_report.py DIR --perf DIR                        # tables from the perf directory
```
