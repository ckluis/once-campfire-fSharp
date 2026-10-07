# Phase 7 re-baseline: Rails, Rust and F# re-measured at the end of the tuning units, 2026-10-07

This is the one point in Phase 7 where Rust and Rails are re-measured (the process rule of 2026-10-06). It ran
`bench/run --apps reference,rust,fsharp --reps 3` with every suite (HTTP at c=1, 16 and 64 on eight routes, Cable fan-out, upload),
the long warm-up and capped logs. The F# image was rebuilt from HEAD. Raw data, one JSON per app and rep:
[`phase7-final/`](phase7-final/). `report.md` there is `bench/report`'s full output, with every validity table. The hand-run
review checks are in [`phase7-final/review/`](phase7-final/review/).

**Verdict.** F# now serves more requests per second than Rust on all five workloads at all three concurrencies. At 16 connections it is
1.24-1.33x on the five workloads (room page 1.33x at steady state; see "What I found"). At 64 connections it is 1.22-1.31x. At one
connection it is 1.07-1.29x. At 16 and 64 connections the lead comes with **less** CPU per request than Rust (0.77-0.92x). At one
connection it does not: F# spends 1.2-2.0x Rust's CPU per request there. It buys its lower latency with thread-pool spinning on
otherwise idle cores. Three of the 15 cells are wins of 10% or less: search c=1 (1.07x), post c=1 (1.10x) and room page c=1
(1.18x, but CPU 1.47x). Still behind Rust, and not among the five workloads' req/s: p99 latency at 16 and 64 connections on the
read pages, memory, cold start and Cable fan-out (0.60-0.80x Rust's sustained broadcast rate).

## The five workloads (as I would publish them)

Req/s, median of 3 reps, with the container's CPU µs per request (cgroup `cpu.stat`, unprofiled) in the next column.
Signed in, keep-alive, gzip. Same seed, `SERVER_CPUS=0-3`, `LOADGEN_CPUS=4-7`. Measured with a warm-up of at least 30 s at each
route and concurrency (90 s at most).

| Workload | c | Rails req/s | Rails CPU µs | Rust req/s | Rust CPU µs | F# req/s | F# CPU µs | F# / Rust req/s | F# / Rust CPU |
|---|---|---|---|---|---|---|---|---|---|
| room page | 1 | 107 | 9,514 | 6,481 | 142.1 | 7,617 | 208.7 | **1.18x** | 1.47x |
| room page | 16 | 239 | 12,236 | 28,623 | 122.4 | 38,000 ¹ | 100 ¹ | **1.33x** ¹ | 0.82x |
| room page | 64 | 222 | 13,022 | 29,072 | 120.5 | 36,654 | 97.0 | **1.26x** | 0.80x |
| messages page | 1 | 218 | 4,715 | 6,822 | 139.2 | 8,312 | 199.3 | **1.22x** | 1.43x |
| messages page | 16 | 381 | 7,359 | 30,063 | 114.2 | 37,836 | 102.2 | **1.26x** | 0.89x |
| messages page | 64 | 369 | 7,475 | 30,849 | 111.8 | 40,382 | 91.1 | **1.31x** | 0.81x |
| sidebar | 1 | 287 | 3,585 | 6,196 | 149.0 | 8,000 | 181.0 | **1.29x** | 1.21x |
| sidebar | 16 | 599 | 4,901 | 28,238 | 123.2 | 37,555 | 101.8 | **1.33x** | 0.83x |
| sidebar | 64 | 524 | 5,622 | 28,856 | 120.8 | 36,455 | 97.6 | **1.26x** | 0.81x |
| search | 1 | 193 | 5,228 | 5,646 | 158.3 | 6,055 | 315.1 | **1.07x** | 1.99x |
| search | 16 | 408 | 7,029 | 26,606 | 123.9 | 34,070 | 107.3 | **1.28x** | 0.87x |
| search | 64 | 367 | 7,771 | 32,782 | 109.3 | 42,413 | 84.3 | **1.29x** | 0.77x |
| post a message | 1 | 167 | 8,157 | 2,779 | 403.7 | 3,052 | 629.1 | **1.10x** | 1.56x |
| post a message | 16 | 330 | 10,044 | 7,174 | 385.6 | 8,905 | 353.0 | **1.24x** | 0.92x |
| post a message | 64 | 332 | 10,220 | 4,809 | 388.9 | 5,888 | 341.0 | **1.22x** | 0.88x |

¹ The `bench/run` median for room page c=16 is 40,422 req/s at 93.6 µs. That is 1.41x Rust, and it overstates the steady state.
Two of the three reps' warm-ups stopped at 30-40 s, on a plateau F# leaves after about 80 s, when the CLR thread pool adds workers
(38 → 43-46 threads; CPU per request rises from 93 to 100 µs). Every run longer than that reads about 38,000: rep 3 (90 s warm-up)
38,482, my hand run (90 s fixed warm-up) 37,862, and the 150 s curve's last 30 s 37,700-38,200. The table uses the steady state.
The other F# cells were measured either after a 90 s warm-up or after that route's previous concurrency had already run. The search
c=16 curve is flat for 150 s at 34,000-34,600, and the messages page c=16 curve over 180 s reads 36,700-38,500 (the median 37,836
falls inside that range).

Rows written equal the 2xx answers on every post phase of every app. Every response was 2xx with 0 connection errors. Decoded bytes are
byte for byte Rust's (below). Rails is the reference image, for scale: Rust is 15-131x Rails and F# 18-165x on these five workloads.

Latency, p50 / p99 ms, Rust and F# (medians of 3 reps):

| Workload | c=1 Rust | c=1 F# | c=16 Rust | c=16 F# | c=64 Rust | c=64 F# |
|---|---|---|---|---|---|---|
| room page | 0.15 / 0.18 | 0.13 / 0.17 | 0.54 / 1.07 | 0.31 / 1.64 | 2.13 / 3.99 | 1.51 / 5.01 |
| messages page | 0.14 / 0.18 | 0.12 / 0.17 | 0.51 / 0.99 | 0.35 / **3.19** | 2.02 / 3.62 | 1.44 / 4.66 |
| sidebar | 0.16 / 0.19 | 0.12 / 0.18 | 0.54 / 1.11 | 0.34 / 1.75 | 2.15 / 4.13 | 1.54 / 4.83 |
| search | 0.18 / 0.21 | 0.17 / 0.21 | 0.57 / 1.11 | 0.44 / 1.36 | 1.88 / 3.33 | 1.35 / 3.81 |
| post a message | 0.34 / 0.53 | 0.30 / 0.56 | 2.10 / 6.04 | 1.55 / 5.99 | 8.52 / 18.0 | 6.43 / 15.4 |

F# has the better median everywhere. Its p99 is worse on the four read pages at 16 and 64 connections: 1.2-1.6x Rust's, and 3.2x
on the messages page at c=16.

## Baseline, Phase 7 start and now

"Baseline" is plan step 5b (`baseline-20261006.md`, measured with the old 2 s warm-up). "Start" is unit 7.0 (`phase7-start.md`, c=1
and c=16 only, no Rails). "Now" is this run. F# CPU before → start → now, µs per request, where it was measured.

| Workload | c | Rails base → now | Rust base → start → now | F# base → start → now | F# / Rust base → start → now | F# CPU µs base → start → now |
|---|---|---|---|---|---|---|
| room page | 1 | 111 → 107 | 6,310 → 6,500 → 6,481 | 3,329 → 3,885 → 7,617 | 0.53 → 0.60 → **1.18** | 538 → 453 → 209 |
| room page | 16 | 227 → 239 | 28,135 → 28,720 → 28,623 | 21,003 → 21,892 → 38,000 ¹ | 0.75 → 0.76 → **1.33** | 174 → 173 → 100 |
| room page | 64 | 218 → 222 | 28,860 → – → 29,072 | 23,184 → – → 36,654 | 0.80 → – → **1.26** | – → – → 97 |
| messages page | 1 | 209 → 218 | 6,625 → 6,981 → 6,822 | 4,044 → 4,227 → 8,312 | 0.61 → 0.61 → **1.22** | 464 → 449 → 199 |
| messages page | 16 | 390 → 381 | 29,406 → 30,230 → 30,063 | 23,097 → 23,346 → 37,836 | 0.79 → 0.77 → **1.26** | 166 → 161 → 102 |
| messages page | 64 | 371 → 369 | 30,541 → – → 30,849 | 24,468 → – → 40,382 | 0.80 → – → **1.31** | – → – → 91 |
| sidebar | 1 | 268 → 287 | 6,116 → 6,366 → 6,196 | 3,738 → 3,837 → 8,000 | 0.61 → 0.60 → **1.29** | 465 → 443 → 181 |
| sidebar | 16 | 581 → 599 | 27,938 → 28,378 → 28,238 | 19,115 → 18,888 → 37,555 | 0.68 → 0.67 → **1.33** | 199 → 200 → 102 |
| sidebar | 64 | 575 → 524 | 28,494 → – → 28,856 | 20,916 → – → 36,455 | 0.73 → – → **1.26** | – → – → 98 |
| search | 1 | 189 → 193 | 5,491 → 5,638 → 5,646 | 3,922 → 3,990 → 6,055 | 0.71 → 0.71 → **1.07** | 488 → 468 → 315 |
| search | 16 | 410 → 408 | 26,049 → 26,606 → 26,606 | 19,411 → 20,026 → 34,070 | 0.75 → 0.75 → **1.28** | 189 → 184 → 107 |
| search | 64 | 402 → 367 | 32,578 → – → 32,782 | 23,208 → – → 42,413 | 0.71 → – → **1.29** | – → – → 84 |
| post a message | 1 | 160 → 167 | 2,727 → 2,814 → 2,779 | 1,909 → 1,958 → 3,052 | 0.70 → 0.70 → **1.10** | 1,024 → 998 → 629 |
| post a message | 16 | 310 → 330 | 6,082 → 7,140 → 7,174 | 6,186 → 6,046 → 8,905 | 1.02 → 0.85 → **1.24** | 546 → 549 → 353 |
| post a message | 64 | 310 → 332 | 5,901 → – → 4,809 | 5,321 → – → 5,888 | 0.90 → – → **1.22** | – → – → 341 |

Rust's CPU per request now against its stored Phase 7 start, µs: room page 142.1 / 122.4 against 141.6 / 121.6 (c=1 / c=16),
messages page 139.2 / 114.2 against 135.7 / 113.7, sidebar 149.0 / 123.2 against 145.8 / 122.8, search 158.3 / 123.9 against
157.9 / 124.2, post 403.7 / 385.6 against 399.2 / 389.4. **Rust re-measured within 2.7% of the stored baseline on every c=1 and c=16
cell, in both req/s and CPU.** So the stored baseline the tuning units compared against was sound, and the F# gains are not drift
in the host.

Post a message at c=64 is lower than the baseline's for both ports (Rust 5,901 → 4,809, F# 5,321 → 5,888 is up only because F# improved
more). The cause is in the method, not in either app. The long warm-up writes 450,000-600,000 rows at c=64 before the window opens,
and both ports' posting rate falls as the write room grows: Rust's falls from 7,400 to 4,700-5,300 during that warm-up, F#'s from
9,100 to 5,400-5,900. The warm-up hit its 90 s maximum here in 5 of 6 runs. My hand run, which posts at c=16 after 90 s of posting,
reads 4,587 for Rust and 5,486 for F# (1.20x). The ratio holds, and the absolute post numbers depend on how much has been written
before the window opens.

## Other suites (F# against Rust; Rails for scale)

| | Rails | Rust | F# | F# / Rust | at the baseline |
|---|---|---|---|---|---|
| cold start, docker run → `/up` (ms) | 2,127 | 123 | 253 | 2.1x slower | 1.8x slower |
| idle memory.current (MB) | 303 | 16 | 28 | 1.75x more | 1.6x more |
| peak anon under the whole run (MB) | 1,435 | 269 | 717 [633-925] | 2.7x more | 2.3x more |
| Cable, 100 clients: msgs/s delivered to all | 99.0 | 3,247 | 2,611 | 0.80x | 0.72x |
| Cable, 500 clients | 30.0 | 1,044 | 695 | 0.67x | 0.61x |
| Cable, 1,000 clients | 15.5 | 578 | 349 | 0.60x | 0.56x |
| Cable, 1,000 clients: paced post → all p50 / p99 (ms) | 76.9 / 113 | 13.1 / 18.2 | 16.7 / 20.9 | 1.27x / 1.15x slower | 1.2x / 1.4x slower |
| Cable, 1,000 clients saturated: app Pss (MB) | 892 | 125 | 670 | 5.4x more | 4.5x more |
| upload + thumbnail, POST → thumb served (ms) | 61.8 | 28.3 | 28.3 | level | 1.07x slower |
| avatar c=16 req/s (CPU µs) | 69,481 (38.9) | 140,426 (15.7) | 174,192 (20.4) | 1.24x (CPU 1.30x) | 0.89x |
| CSS file c=16 req/s (CPU µs) | 98,776 (28.8) | 149,556 (14.8) | 190,580 (19.0) | 1.27x (CPU 1.28x) | 0.91x |
| `/up` c=16 req/s (CPU µs) | 4,229 (641) | 117,356 (22.4) | 147,639 (20.5) | 1.26x (CPU 0.91x) | 0.69x |

On the three cheap routes F# now has the higher throughput at every concurrency, but on avatar and the CSS file it spends 1.3x Rust's
CPU per request at c=16 and 2.6-2.8x at c=1. The identity (`Accept-Encoding: identity`) twins and `bench/front` were not part of this run,
so the "identity bodies through Kestrel" and front-server gates in the plan are not re-measured here.

## The review (assume it is wrong until shown otherwise)

| Check | How | Result |
|---|---|---|
| Image provenance | `env.txt`; `docker image inspect` | F# `campfire-fsharp:app` `sha256:82ebcbc734ec…`, label `revision=c3e0eca` (HEAD; `src/` last changed in 69dc486), rebuilt before the run, `bin/clean-docker` after. Rust `sha256:d582a01d3a4a…`, the same image as the baseline and `phase7-start`. Rails `sha256:d2ca3b5d7a7f…`, the same as the baseline. `git status` clean under `src/` and `rust/` |
| Statuses | `report.md` | 2xx only, with 0 connection errors, over 8.6M Rails, 29.3M Rust and 37.3M F# responses |
| Rows written | SQLite count around every warm-up and run | equal to the 2xx answers in all 54 post phases (Rails 93,810, Rust 2,399,863, F# 3,222,781 rows) |
| Decoded bytes | (1) `bench/run`'s probe; (2) [`review/bytes.py`](phase7-final/review/bytes.py): both parity images on the same seed with the clock frozen at 2026-03-02T16:00Z, the same port, unmasked | (1) equal sizes on every route. (2) **byte-identical** SHA-256 for the room page (gzip-decoded and identity), messages page, sidebar, search, the post answer and the room page after the post ([`bytes-sha256.txt`](phase7-final/review/bytes-sha256.txt)). Gzip on the wire differs (F# 0.6-1.2% smaller: another deflate) |
| Counts the load generator could get wrong | [`review/manual.sh`](phase7-final/review/manual.sh): my own run of room page and post at c=16 and c=1 per app, outside `bench/run`. A fixed warm-up (90 s at c=16, 45 s at c=1), then a 15 s window. CPU read straight from `cpu.stat`. Requests also counted from the server's own request log within the window | Rust room page c=16 28,960 req/s, 121.1 µs; F# 37,862, 100.7 µs. Rust post c=16 4,587, 390 µs; F# 5,486, 352 µs. c=1: Rust 6,564 / 2,746, F# 7,491 / 3,046. **Server log lines equal the load generator's count** for Rust exactly. For F# they are 19-42 short at the window's end, which is the batched log's 10 ms flush. `bench/lib/logcount 40000` then confirmed no lost line (40,001 of 40,001) |
| CPU per request | cgroup, both the harness and by hand | hand-run values within 1% of `bench/run`'s, except room page c=16 (100.7 against 93.6, the plateau in note ¹). F# at c=16 keeps 3.8 cores busy, Rust 3.5: both are CPU-bound, so the lead at c=16 is CPU per request |
| Load average | waited for the 1-min figure < 1.5 before every part (up to 150 s) | 1.17-1.49 at every start |
| Host noise | `hostprobe` after every window | ≤ 1.08 after all 216 windows of the final data. Rails rep 1's avatar c=16 window read 2.84 (a spike on the Mac), so I re-ran avatar for that rep in the next part and kept the clean run |
| Warm-up | `report.md` | Every run settled except the 90 s maximum on F# messages page, sidebar and post c=64 (all reps), room page c=16 (rep 3), `/up` c=1 (rep 2) and Rust post c=64 (2 reps). Measured / tail 0.99-1.04 throughout. Room page c=16 is the one cell where "settled" was premature (note ¹) |
| Leftovers | `ps` on the Mac | the orphaned test host from the verifier's note was already gone. Idle MSBuild nodes were shut down before measuring |

What I found and changed in the reported numbers: the room page c=16 median. Rust's request log is a coloured `tracing` line per request
(ANSI escapes included, about 1.5x the bytes of F#'s). F# batches plain lines (unit 7.3). That is part of F#'s lead and it is legitimate:
both apps log every request, and the Rust image is the unmodified upstream port. But it means some of the gap is Rust's logger, not
Rust's request path.

## Where F# stands, plainly

- **Beats Rust (more than 10%)**, req/s: all five workloads at c=16 (1.24-1.33x) and c=64 (1.22-1.31x), plus room page, messages page
  and sidebar at c=1 (1.18-1.29x). At c=16 and c=64 F# also spends 8-23% less CPU per request than Rust.
- **Within 10% (ahead, but close):** search c=1 (1.07x) and post a message c=1 (1.10x).
- **Behind Rust:**
  - CPU per request at one connection, on every workload: 1.21x (sidebar) to 1.99x (search). The extra is the thread pool spinning
    while it waits for the next request. It costs CPU on an idle machine and pays for itself only in latency.
  - p99 latency at 16 and 64 connections on the read pages: 1.2-1.6x, and 3.2x on the messages page at c=16. GC pauses are the
    likely cause: the p99 drifts up over minutes of running.
  - Room page c=16 loses about 6% after about 80 s, when the thread pool injects threads.
  - Memory: idle 1.75x, peak anon 2.7x, and Pss under Cable load 5.4x.
  - Cold start: 2.1x.
  - Cable fan-out: 0.60-0.80x Rust's sustained broadcast rate; it barely moved from the baseline's 0.56-0.72x.
  - Cheap static routes: CPU per request 1.3x at c=16, though throughput is ahead.
- **Not re-measured in this run:** identity bodies, the front server (`bench/front`) and `bench/cable/run`. Their gaps in the plan stay open.

## Reproducing

All inside colima, `SERVER_CPUS=0-3 LOADGEN_CPUS=4-7 WORK=/var/tmp/campfire-bench-work`. Each app's rep runs in parts: one container
per part, merged into the rep's file by `MERGE_PART=1`, so that every part fits one foreground call. Rails and Rust run three routes
a part. F# runs one heavy route a part. Cable and upload run in the rep's last part:

```sh
CAMPFIRE_REVISION=$(git rev-parse HEAD) parity/bin/candidate build && bin/clean-docker   # (bin/clean-docker from macOS)
MERGE_PART=1 HTTP_ROUTES="room_show messages_page" SUITES=http bench/run --apps reference,rust,fsharp --reps 3 --only reference:1 --out bench/results/phase7-final
#   ... the remaining parts; the order is reference, rust, fsharp in reps 1 and 3, reversed in rep 2
MERGE_PART=1 HTTP_ROUTES="static_css up" SUITES="http cable upload" bench/run ... --only fsharp:3 --out bench/results/phase7-final
bench/report bench/results/phase7-final > bench/results/phase7-final/report.md
bench/results/phase7-final/review/manual.sh rust   # and fsharp
bench/results/phase7-final/review/curve.sh fsharp room_show 75   # the 150 s room page curve
bench/quick log bench/results/phase7-final --unit final --tier 3 --kept true --build sha256:82ebcbc734ec --commit c3e0eca --change "..."
```

`bin/verify` and the differentials were not re-run for this unit. It changes nothing under `src/`. The last run of them was the
verifier fix-up at 69dc486, which passed, and HEAD's `src/` is identical to it.
