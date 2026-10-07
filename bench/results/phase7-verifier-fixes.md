# Phase 7 correctness verifier: fixes (2026-10-07)

Four minor findings, none a correctness break of a response; all fixed in `69dc486`, each with a test that fails without it
(checked by reverting the fix and running the test).

| Finding | Fix | Test that catches it |
|---|---|---|
| Request-log bytes equal the console logger's only off a TTY; request lines no longer share a queue with other log lines | `Boot.configureConsole` sets `ColorBehavior = Disabled` (the app's other lines are plain on a TTY too); the `RequestLog.fs` module comment records the up-to-10 ms reordering | `AppTests`: "the console logger never colours its lines" (the options, and a factory's output has no ESC) |
| Mention shortcut dropped the `mentioned_users raised` error log for attachment-free bodies that fail to parse (Rust logs it) | The skip applies only to bodies of at most 800 UTF-16 units, which cannot reach the parse limits (400 deep, 400 attributes; at 3 units for the shortest element that is 266 elements plus a few dozen the tree builder re-opens, and 401 attributes take 802); longer bodies parse and log as before | `MiscTests`: "a body without attachments that fails to parse still logs the mentions error" (500 nested divs, 450 attributes), "a plain body parses nothing and logs nothing", "no body of the safe length fails to parse" (worst shapes cut to 800 all parse) |
| `sqlite3_bind_text`/`blob` with `SQLITE_TRANSIENT` ran under `SuppressGCTransition` for the whole malloc and copy | Two imports of each: `_short` (suppressed) for values of at most 1,024 bytes, the plain one above that. `column_text` stays suppressed: it returns a pointer for the text columns every caller reads, and a conversion only happens for a scalar read as text | `SqlTests`: "binding a long value does not suppress the GC transition" (reflection on the imports), "text and blobs bind and read back at every size around the short threshold" (0 B to 8 MB) |
| A `Campfire.App.Tests` host orphaned for 20 h (PID 70301) | Killed before measuring | none (environment) |

## Re-measure (tier 2, mounted, F# only, `SERVER_CPUS=0-3 LOADGEN_CPUS=4-7`, 2 alternating runs each, logged)

The fixes touch every bind (search, post) and the post path's mention check. Before is the tree at `896278f` published the same way
(its `REVISION` file says `69dc486` because it was published from a checkout of that commit's `src/`).

| Workload | c | before req/s | after req/s | before CPU us/req | after CPU us/req | after vs stored Rust baseline |
|---|---|---|---|---|---|---|
| search | 16 | 33,437 [32,922-33,951] | 34,017 [33,914-34,120] | 109.3 | 107.5 | 1.28x |
| search | 1 | 5,930 [5,885-5,975] | 5,906 [5,709-6,103] | 322.4 | 321.6 | 1.05x |
| post a message | 16 | 8,840 [8,606-9,074] | 8,784 [8,775-8,793] | 356.9 | 354.0 | 1.23x |
| post a message | 1 | 3,059 [3,055-3,063] | 3,007 [2,963-3,051] | 630.5 | 637.4 | 1.07x |

Everything is within the run-to-run spread (post c=16 swings 3% between two runs of the same build), so the fixes cost nothing measurable;
every response 2xx, rows written equal answers, decoded bytes equal Rust's. No tier 3 (no image rebuild): this is a fix-up of unit 7.4, not a new unit.

## Parity kept

`CAMPFIRE_REQUIRE_SEED=1 bin/verify`: 1,038 tests, 0 failed, 7 skipped (the storage media vectors' libvips/ffmpeg version checks and
Linux-only tests, as before). `bin/views-differential` 22,374 of 22,374 identical, `bin/kit-differential` 0 differ in every group,
`bin/richtext-differential` 0 differ in every group.
