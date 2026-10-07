# Unit 7.4: posting a message and the remaining paths (2026-10-06)

F# against the stored Rust baseline (`phase7-start/`, medians of 3 Rust reps; **Rust was not re-run, so every ratio here is "vs stored Rust baseline"**).
Tier 3: `campfire-fsharp:app` rebuilt from `3e321ac` (image `sha256:052d89880648`, `src/` identical to `571d25a`; its `campfire.dll`, `Campfire.Kit.dll` and
`Campfire.Cable.dll` hash the same as the mounted tier 2 build), F# only, 3 reps in three parts each, `SERVER_CPUS=0-3 LOADGEN_CPUS=4-7`, the long warm-up.
Raw data and validity tables: [`phase7-unit-7.4/tier3/`](phase7-unit-7.4/tier3/) (`vs-stored-rust.md` is the table below, `report.md` the full validity output).
All 30 runs: every response 2xx, 0 connection errors, decoded bytes equal to Rust's on every route, rows written equal the 2xx answers of every post phase.
Ten warm-ups reached the 90 s maximum (messages and room c=16 three times each, sidebar c=16 and search c=1 twice each) and were flat afterwards, as in 7.1 to 7.3.

## Before and after

Req/s and container CPU per request, median [min-max] of 3 reps. "7.3" is the tier 3 of `phase7-crypto-logging.md` (`b1d55bf`).

| Workload | c | Rust stored | F# 7.3 | **F# 7.4** | F# / Rust 7.3 -> **7.4** | CPU us/req Rust / 7.3 / **7.4** |
|---|---|---|---|---|---|---|
| post a message | 16 | 7,140 | 8,108 | **8,892** [8,751-8,997] | 1.14 -> **1.245** | 389.4 / 396.5 / **352.9** [350-353] |
| post a message | 1 | 2,814 | 2,727 | **3,060** [2,994-3,082] | 0.97 -> **1.087** | 399.2 / 697.0 / **631.0** [628-635] |
| room page | 16 | 28,720 | 38,606 | 37,726 [37,233-39,136] | 1.34 -> 1.314 | 121.6 / 97.5 / 101.4 [98-103] |
| room page | 1 | 6,500 | 7,524 | 7,462 [7,419-7,608] | 1.16 -> 1.148 | 141.6 / 209.7 / 210.6 |
| messages page | 16 | 30,230 | 37,933 | 38,174 [37,556-38,237] | 1.25 -> 1.263 | 113.7 / 101.5 / 100.5 |
| messages page | 1 | 6,981 | 8,112 | 8,238 [8,222-8,269] | 1.16 -> 1.180 | 135.7 / 199.5 / 199.3 |
| sidebar | 16 | 28,378 | 36,659 | 37,900 [37,791-38,648] | 1.29 -> 1.336 | 122.8 / 104.2 / 100.8 |
| sidebar | 1 | 6,366 | 7,690 | 7,822 [7,595-7,837] | 1.21 -> 1.229 | 145.8 / 187.5 / 185.8 |
| search | 16 | 26,606 | 33,798 | 33,565 [33,406-33,685] | 1.27 -> 1.262 | 124.2 / 108.0 / 108.6 |
| search | 1 | 5,638 | 5,837 | 5,814 [5,745-6,042] | 1.04 -> 1.031 | 157.9 / 321.5 / 324.9 |

What it says:

- **Posting gained 10-12% in req/s and 9-11% in CPU per request.** At c=16 F# is 1.245x the stored Rust baseline in req/s and uses 9% less CPU per post (352.9 against 389.4 us);
  at c=1 it is 1.087x with 1.58x Rust's CPU per request (spinning workers, unchanged). The five read workloads did not move beyond run-to-run noise (-2.3% to +3.4%, ranges overlap
  7.3's); nothing in this unit touches them, which is what that shows.
- Caveats that keep this from being a claim about Rust today: Rust was not re-measured, and at c=16 it leaves about half of the 4 cores idle in the stored runs while F# post uses 3.1.

## Where the gain came from

| Step | Evidence | Effect on a post |
|---|---|---|
| The default host watched the content root (`reloadOnChange` on appsettings.json: a recursive `FileSystemWatcher`), so every SQLite write under `storage/` woke the "File Watch" thread, which lstat'ed the path | perf at c=16: File Watch thread 5.3% of the CPU, `Interop+Sys::LStat` 2.7% of the kernel time under it; gone after (`--hostBuilder:reloadConfigOnChange=false`) | 392 -> 356 us/req in the perf runs (`bench/breakdown perf`), 8,108 -> 8,796 req/s and 396.5 -> 357.8 us in the tier 2 run |
| The message partial reached the broadcast as a 20 KB string (UTF-8 decoded from the cached fragment) that `JsonStringWriter` encoded again; the unread ping built, encoded and transcoded a `Value` for each of 17 members; two body parses looked for mentions in a body that has no attachment | allocation trace (`bench/breakdown alloc`): 183,601 -> 154,081 bytes per post (-16%) | A/B, same session, three alternating pairs each (`phase7-unit-7.4/t2-postpath-ab/`): 8,747 -> 9,034 req/s (+3.3%, every pair the same sign), 356.9 -> 349.3 us (-2.1%) |

Both changes are in `571d25a` and `bc80d28`. What `571d25a` changes: `IPartials.Message` and `Boost` return the fragment's UTF-8 (`HtmlUtf8`, which the broadcast already supported and
`JsonStringWriter.AppendUtf8` escapes as bytes), `UnreadRoom` encodes `{"roomId":n}` once for all streams, and `AppRichText.MentionedUserIds` returns `[]` for a body with neither
`action-text-attachment` nor `data-trix-attachment` (case-insensitive: the two ways an attachment gets into a body) instead of parsing it. A log or error line for a body that fails to parse
for mentions would no longer be written for such a body (it has no attachments to fail on).

## Tried and reverted (logged with kept=false in `phase7-log.jsonl`)

- **Gzip a post's answer through the zlib shim** (`5c23899`, reverted in `f18616d`). A post's answer is 9,283 bytes of HTML that gzip to 1,976, and `libSystem.IO.Compression.Native` is 7.7% of
  the CPU (about 28 us), which is zlib-ng's level-6 compress, in `Deflater.gzipMember` (the post's body has no cached parts). A direct P/Invoke of the shim's `Deflate` with the smallest window that
  reaches back over the body gave byte-identical output at sizes from 1 to 60,000 bytes and was 27-30% faster per call in the micro-benchmark (`bench/micro-linux gzip-micro`: 14.9 -> 10.9 us at
  2 KB, 43.5 -> 29.4 us at 8 KB on the slow pinned core), but the tier 2 run showed nothing (8,852 req/s, 356.3 us against 8,796 and 357.8) and three perf runs read 360-362 us. Not worth a P/Invoke into
  the runtime's private exports. The remaining 28 us is the compress itself, which Rust also pays (38 us in its gzip layer): a lower level would shrink it and grow the wire size, which is a
  decision for the operator, not a tuning step.
- **Larger Kestrel pipe segments for big identity bodies** (`dcb5ffa`, reverted in `f849119`). A 416 KB body is a hundred 4 KB blocks. An `IMemoryPoolFactory<byte>` with 64 KB (and then 16 KB) pinned
  blocks and size-hinted body writes made the identity room page slower, not faster: 64 KB blocks 18,038 req/s and 205 us against 19,944 and 183 us on the same build before; in a same-build A/B (a toggle,
  two alternating pairs) 16 KB blocks gave 17,245 and 17,180 req/s (217, 218 us) against Kestrel's own blocks at 18,520 and 18,950 (197, 192 us). The copy is the same size, the working set of blocks in
  flight outgrows the cache, and the profile says where the identity cost is: `memmove` 12.5% (the copy into the pipe, 22 us), `__arch_copy_from_user` 9.3% (the kernel's copy, Rust pays it too),
  `SocketPal.SysSend` 22.8% in all. Removing the copy needs a path around Kestrel's output pipe (the raw socket), which I did not do: it would have to keep the framing, TLS and pipelining intact.
  Identity today (tier 2, mounted, one rep): room page c=16 19,944 req/s at 183 us against the baseline's Rust 19,574 at 171 us (that baseline used the old 2 s warm-up and is not in `phase7-start/`):
  1.02x in req/s, +7% CPU. Down from 14,015 and 263 us at the baseline, by the work of 7.1 to 7.3.

## Measurements that settle what was asked

- **File watching**: confirmed and removed (above). It appeared only on posts, because only a write under the watched tree produces an event.
- **Database work on the write path**: the SQLite engine is not the gap: 146 us per post across the reader and writer threads (the writer thread 64 us, `campfire-db-writer` 19% of the CPU) against Rust's 197 us.
  The rest of the database layer is 32 + 3 us. A post is one write and several reads (the room, the broadcast render, the response render, the push job, webhook candidates, the unread members), the shape of
  the Rails controller and of the Rust port this one follows; the bench seed's push subscriptions are real (neutered to a closed local port), so the push job does its work in a post.
- **Rich text per post**: 11.6 us (3.2% of the CPU) against Rust's 11.3. Parity; a post parses the body five or six times (canonicalize, search index, two views, mentions, the push payload).
- **Allocation**: 154 KB per post after (was 184 KB); `System.String` 25 KB, the rich text DOM about 45 KB, fragments 8.5 KB.

## Tier 1 and the progress log

`bench/micro-linux gzip-micro` (new, `5c23899`, removed with the revert) was the tier 1 harness of the gzip experiment. SageFs: I ran `sagefs check` only (the environment is fine) and did not use it: it starts
bare and wants a client (dashboard, editor or MCP) to create a session for the multi-project solution, which is more set-up than the Stopwatch harnesses and tier 2 runs took here, so it neither saved time nor
got in the way. Every tier 2 and 3 run is in `phase7-log.jsonl` (kept or not; the identity runs under `room_show_identity`, which the chart does not draw), and `index.html` was rebuilt with `bench/progress/build.py`
(it writes `index.html` at the repository root now, not `target/phase7-dashboard/`).

## Checks

`bin/verify` with `CAMPFIRE_REQUIRE_SEED=1`: build with warnings as errors, 1,032 tests, 1,025 passed, 0 failed, 7 skipped (among them the TLS session-ticket test, which needs Linux, and the Cable reference recorder). `bin/views-differential`: 22,374 of 22,374 renders identical. `bin/kit-differential`: 0 differ in every group (3,000 inputs each). `bin/richtext-differential`: 0 differ
(20,000 inputs per group). Decoded response bytes equal Rust's on every probed route in all 30 tier 3 runs.
