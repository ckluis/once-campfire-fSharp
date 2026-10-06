# Unit 7.2: allocation, GC and the thread pool (2026-10-06)

F# against the stored Rust baseline (`phase7-start/`, medians of 3 Rust reps; Rust was not re-run, so every ratio is **vs stored Rust baseline**).
Tier 3: `campfire-fsharp:app` rebuilt from `1d0f3b2` (`sha256:e316ad01e8ed`), F# only, 3 reps, a fresh container and seed copy per part,
`SERVER_CPUS=0-3 LOADGEN_CPUS=4-7`, the long warm-up. Raw data and `bench/report`'s validity tables: [`phase7-unit-7.2/`](phase7-unit-7.2/)
(`vs-stored-rust.md` is the table below). All 30 runs: every response 2xx, 0 connection errors, decoded bytes equal to Rust's on every route, rows
written equal to the 2xx answers of every post phase. Six cells' warm-ups reached the 90 s maximum (messages c=16 three times, room and sidebar c=16,
post c=1) and were flat afterwards, as in 7.1.

## Before and after

Req/s and container CPU per request, median [min-max] of 3 reps. "7.1" is the tier 3 of `phase7-database.md` (`df6ffd9`).

| Workload | c | Rust stored | F# 7.1 | **F# 7.2** | F# / Rust 7.1 -> **7.2** | CPU us/req Rust / 7.1 / **7.2** |
|---|---|---|---|---|---|---|
| room page | 16 | 28,720 | 28,328 | **30,672** [30,638-31,019] | 0.99 -> **1.07** | 121.6 / 135.8 / **123.7** |
| room page | 1 | 6,500 | 5,589 | **5,654** [5,521-5,698] | 0.86 -> **0.87** | 141.6 / 258.1 / **252.4** |
| messages page | 16 | 30,230 | 31,066 | **33,229** [33,172-33,248] | 1.03 -> **1.10** | 113.7 / 124.0 / **115.8** |
| messages page | 1 | 6,981 | 6,096 | **6,293** [6,232-6,368] | 0.87 -> **0.90** | 135.7 / 243.1 / **229.4** |
| sidebar | 16 | 28,378 | 27,356 | **28,192** [27,914-29,208] | 0.96 -> **0.99** | 122.8 / 142.1 / **135.4** |
| sidebar | 1 | 6,366 | 5,177 | **5,336** [5,214-5,397] | 0.81 -> **0.84** | 145.8 / 277.6 / **265.7** |
| search | 16 | 26,606 | 27,801 | **28,713** [28,032-29,098] | 1.05 -> **1.08** | 124.2 / 134.2 / **128.5** |
| search | 1 | 5,638 | 4,855 | **4,695** [4,397-4,829] | 0.86 -> **0.83** | 157.9 / 363.6 / **374.7** |
| post a message | 16 | 7,140 | 7,004 | **7,186** [7,154-7,508] | 0.98 -> **1.01** | 389.4 / 452.1 / **441.1** |
| post a message | 1 | 2,814 | 2,328 | **2,275** [2,202-2,401] | 0.83 -> **0.81** | 399.2 / 783.3 / **809.0** |

What it says, and what it does not:

- **c=16 gains 3-8% in req/s and 3-12% in CPU per request.** F# is ahead of the stored Rust baseline on the room page (1.07), messages (1.10), search (1.08) and
  posting (1.006) and level on the sidebar (0.99). Each of these cells is Rust at 3.3-3.5 busy cores against F# at 3.4-3.6; the CPU per request is still above
  Rust's: room +2%, messages +2%, search +3%, sidebar +10%, post +13%.
- **c=1 did not move beyond noise** (+1 to +3% on three workloads, -3% on search and post, whose spreads cover that). What is left at one connection (0.81-0.90) is
  thread wake-ups, not allocation or the GC; see below.
- Most of the c=16 gain is the gen0 budget (below), not the allocation cuts: the allocation cuts alone were worth 1-2% CPU per request on the pages (control and
  cut run, same day, tier 2: 119.7 -> 117.4, 111.4 -> 109.7, 130.7 -> 129.1, 130.0 -> 129.0 us), which is inside tier 2's noise, though the bytes allocated did fall.

## 1. Where the allocation came from (the trace, c=16)

`bench/breakdown alloc` (new): dotnet-trace collects the runtime's GC allocation-tick events (about one per 100 KB, with the type and a stack) for 8 s under
load after the warm-up, and `bench/alloc-analyze` (TraceEvent, built once in the toolchain image) sums them by type and by the nearest Campfire frame. All
frames resolved (0.0% unresolved). Bytes per request, before -> after the cuts:

| Workload | before | after | largest sources before (bytes per request) |
|---|---|---|---|
| room page | 146,190 | **112,688** | `RenderSize.Render` text copy 36 KB (the page's exact-size array), `PageParts.Gzip` 28 KB (the joined gzip member), `lastPage` 7 KB, preload-link header 3.5 KB, `Integer.toI128` 2 KB (boxed Int128) |
| messages page | 121,272 | **81,975** | `cacheKeyWithVersion` 25 KB (a 256-char key buffer per message), Gzip 18 KB, `pageBefore` 9 KB, the index action's lists 9 KB |
| sidebar | 140,141 | **132,436** | `RenderSize.Render` 35 KB, `Attrs` arrays 18 KB (`WithDefaultData`, `Set`, ctor), `MessageVerifier.sign` 5 KB, `Json.generate` 4 KB |
| search | 99,069 | **81,159** | `RenderSize.Render` 28 KB, Gzip 9 KB, `searchReachable` 4 KB, preload header 3.5 KB |
| post a message | 220,233 | (not re-traced) | message partial string 20 KB, rich text parse 18 KB, `sanitizeTags` 16 KB (a `Set` built per call), `MessagesCached.message` 15 KB, `Dom.Clone` 10 KB, `Json.write` 8 KB |

Cut (commit `2e0f663`, `dcb8032`): the gzip member of a page in parts is written piece by piece into the response pipe (`PageParts.WriteGzip`, tested equal to
`Gzip`); `String#to_i` of a plain number is parsed in int64 (a test compares it with the Int128 path on 200,000 random strings); `cacheKeyWithVersion` and
`cacheVersion` use the thread's key buffer; `assetPath` is remembered per source (bounded) and `appendPreloadLinks` remembers its last answer for the layout's
immutable list; `sanitizeTags`' allowlist is built once. Not cut: `RenderSize.Render`'s exact copy of the page text (25-29% of the room, sidebar and search bytes
now; it needs the pooled buffer to live until the response is written, through Views, Kit and the adapter), the `Attrs` arrays, the post path's rich text.
`Guid.NewGuid` for request ids and the task state machines were not among the top sources of any page (`ActionBuilder.Bind` 1.3-2.2%), so were left.

## 2. Runtime settings, each on its own (tier 2, F# only, same container schedule, room page / sidebar / post unless noted)

CPU per request us, c=16 / c=1, from the same day's unchanged image as control (room 144.9 / 271.7, sidebar 159.7 / 286.7, post 449.0 / 811.6;
throughput room 26,423 / 5,266, sidebar 23,842 / 4,912, post 7,387 / 2,252). Tier 2 noise is about +-8% on a cell.

| Setting | Result | Decision |
|---|---|---|
| `DOTNET_ThreadPool_UnfairSemaphoreSpinLimit=0` | CPU per request -34% at c=1 (room 272 -> 179, sidebar 287 -> 187, post 812 -> 527) and -4..-10% at c=16, but **c=1 throughput unchanged** (workers sleeping instead of spinning costs the latency the spin saved) and, A/B on the final image with the default (46 hex = 70) in alternating runs, **messages 30.5k vs 33.8k and search 25.0-25.6k vs 29.0-29.2k at c=16**, CPU per request equal | **not set**: it trades throughput for CPU and loses 10-14% on two workloads. It would be the choice for a host where CPU is billed and latency is not |
| `DOTNET_GCgen0size` 16 MiB / **64 MiB** / 256 MiB (on top of spin 0) | room c=16 CPU 139 -> 124 / **121** / 120, sidebar 144 -> 132 / **132** / 131, one-connection p99 0.41 -> 0.22-0.25 ms (Rust 0.17-0.20); peak anon 229 / **234** / 450 MB (251 MB without) | **set, 64 MiB**: 256 MiB gains nothing more for 200 MB. A per-heap budget, so memory scales with the core count |
| workstation GC (`DOTNET_gcServer=0`, with gen0 64 MiB) | CPU and throughput the same as server GC within noise (room 120.4 vs 121.0, sidebar 132.2 vs 131.6) | not changed: server GC stays |
| `DOTNET_TieredPGO=0` | much worse: sidebar c=16 CPU 131 -> 193, post c=1 +24% | PGO stays on |
| `DOTNET_ReadyToRun=0` (everything JITted) | no gain (room 140, sidebar 133), post c=16 warm-up never settled | ReadyToRun stays |
| `DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS=1` | no change over spin 0 | no |
| Kestrel `IOQueueCount=0` (mounted experiment) | no change (room c=1 5,528 vs 5,475) | no |
| Kestrel `UnsafePreferInlineScheduling` + 4 socket engine threads (mounted experiment) | c=1 +5% (room 5,752 vs 5,475, sidebar 5,408 vs 5,116), but room c=16 CPU 123 -> 134 and throughput -7% | no (the experiment code was reverted, not committed) |

Not run: `gcConcurrent=0` (the background GC thread is only created for gen2 collections, which this workload does not trigger), `GCHeapCount` and
`GCHeapAffinitizeMask` (the app runs on the one cpuset that was measured), `TieredCompilation` thresholds (the JIT costs 0.0 us in the steady state after the warm-up).
Mounted and image builds agree: the same environment gave room/sidebar/post c=16 of 27.8/26.5/7.4k (image) and 27.7/27.0/7.2k (mounted).

## What shipped

- `Dockerfile`: `ENV DOTNET_GCgen0size=0x4000000`, with a comment of what was measured and what was not set.
- The allocation cuts above; `bench/breakdown alloc` and `bench/alloc-analyze`.
- Behavior unchanged: `bin/verify` with `CAMPFIRE_REQUIRE_SEED=1` (1,000 tests, 993 passed, 7 skipped as before), `bin/views-differential` (22,374 of 22,374 renders
  identical), `bin/kit-differential` (0 differ in every group), `bin/richtext-differential` (0 differ), decoded response bytes equal Rust's in all 30 runs.
  `bin/db-differential` was not re-run: nothing in `Campfire.Db` changed.

## Process notes

- SageFs was not used: the changes were small and every tier 1 question (does the fast `to_i` agree with the slow one, does `WriteGzip` equal `Gzip`) was a unit test, so there was no
  loop for a live REPL to speed up. I can't say whether it would have saved time.
- The first tier 3 (`phase7-unit-7.2-spin0/`, image `9e87cfde74c3`) shipped spin 0 and showed CPU per request down 10-45% with throughput flat to lower; the A/B above found why,
  and the setting was dropped and the image rebuilt. Both runs are in `phase7-log.jsonl` (the first with `kept: false`).
- An EventPipe session with GC allocation ticks and stacks costs CPU, so only the bytes of that run are read, never a rate.
