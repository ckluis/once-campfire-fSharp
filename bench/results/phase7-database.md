# Unit 7.1: the database path (2026-10-06)

F# against the stored Rust baseline (`phase7-start/`, medians of 3 Rust reps; Rust was not re-run in this unit, so every ratio is
**vs stored Rust baseline**). Tier 3 numbers: `campfire-fsharp:app` rebuilt from `df6ffd9` (`sha256:5b3be84342fe`), F# only, 3 reps,
a fresh container and seed copy per part, `SERVER_CPUS=0-3 LOADGEN_CPUS=4-7`, the long warm-up. Raw data and `bench/report`'s validity
tables: [`phase7-unit-7.1/`](phase7-unit-7.1/) (`vs-stored-rust.md` is the table below). Every one of the 30 runs: all responses 2xx, 0
connection errors, decoded bytes equal to Rust's on every route, rows written equal to the 2xx answers of every post phase.

## Before and after

Req/s and container CPU per request, median [min-max] of 3 reps. "start" is `phase7-start` (3 reps, the image of `16a7da2`).

| Workload | c | Rust stored | F# start | **F# after** | F# / Rust start → **after** | CPU us/req Rust / start / **after** |
|---|---|---|---|---|---|---|
| room page | 16 | 28,720 | 21,892 | **28,328** [27,778-28,640] | 0.76 → **0.99** | 121.6 / 173.0 / **135.8** |
| room page | 1 | 6,500 | 3,885 | **5,589** [5,503-5,707] | 0.60 → **0.86** | 141.6 / 453.2 / **258.1** |
| messages page | 16 | 30,230 | 23,346 | **31,066** [30,858-31,476] | 0.77 → **1.03** | 113.7 / 160.8 / **124.0** |
| messages page | 1 | 6,981 | 4,227 | **6,096** [5,889-6,101] | 0.61 → **0.87** | 135.7 / 449.3 / **243.1** |
| sidebar | 16 | 28,378 | 18,888 | **27,356** [26,739-27,691] | 0.67 → **0.96** | 122.8 / 199.5 / **142.1** |
| sidebar | 1 | 6,366 | 3,837 | **5,177** [5,096-5,258] | 0.60 → **0.81** | 145.8 / 442.7 / **277.6** |
| search | 16 | 26,606 | 20,026 | **27,801** [27,110-27,933] | 0.75 → **1.05** | 124.2 / 183.8 / **134.2** |
| search | 1 | 5,638 | 3,990 | **4,855** [4,551-4,955] | 0.71 → **0.86** | 157.9 / 467.5 / **363.6** |
| post a message | 16 | 7,140 | 6,046 | **7,004** [5,496-7,290] | 0.85 → **0.98** | 389.4 / 548.9 / **452.1** |
| post a message | 1 | 2,814 | 1,958 | **2,328** [2,154-2,370] | 0.70 → **0.83** | 399.2 / 997.8 / **783.3** |

What the table does and does not say:

- **Ahead of Rust on throughput at c=16 on the messages page (1.03) and search (1.05), about level on the room page (0.99) and post (0.98), close on the sidebar (0.96).
  Not ahead on CPU per request**: F# still spends 9% (messages), 8% (search), 12% (room), 16% (sidebar) and 16% (post) more CPU a request than Rust.
  The throughput lead exists because Rust does not use its four cores in these runs (stored baseline: 3.3-3.5 cores busy on the read pages, 2.8 on a post) while F# does
  (3.7-3.9, 3.2): the extra CPU is spent, not wasted, and the gap in CPU per request is what a later unit still has to close. I checked this before trusting 1.03 and 1.05.
- Post c=16 rep 3 read 5,496 (0.79 of its own warm-up tail, 6,923): the write path's settling period seen in `phase7-warmup.md`, once in three reps; the median is
  the other two reps' neighbour, and the spread is printed. Two cells' warm-ups reached the 90 s maximum (messages c=16 rep 3, post c=1 rep 3), both flat afterwards.
- c=1 is 0.81-0.87 on the pages, 0.83 on a post. What is left there is not the database: 258-364 us of CPU against 142-158 us for Rust is thread-pool wake-ups and spinning
  (one hop for the search's offloaded read and one back, the writer's two on a post), Kestrel and the cookie/session work.

## What changed (commits on `port`)

1. `5d2a8aa` **Statements through SQLitePCLRaw, not Microsoft.Data.Sqlite commands.** `Conn` keeps the Microsoft.Data.Sqlite connection (`Conn.Raw` is what
   `Campfire.Storage` is written against, and it opens and closes) but prepares (`sqlite3_prepare_v3`, persistent) once per connection and statement, binds by
   index (text encoded into a reused buffer) and reads columns by ordinal, with no command, parameter, boxing or reader objects; rows are a struct over the statement; NULL text
   is the null pointer `sqlite3_column_text` returns (one call, not `IsDBNull` plus `GetString`). The connection's extra retry loop (Microsoft.Data.Sqlite re-runs a busy statement
   for `CommandTimeout`) is gone: SQLite's busy handler is the only wait, as with rusqlite.
2. `286c2ca` **No per-connection mutex, no memory-statistics mutex**: `sqlite3_config(MULTITHREAD)` and `MEMSTATUS` off before the first connection, which is what rusqlite's
   `SQLITE_OPEN_NO_MUTEX` gives Rust. The flag itself cannot be passed because Microsoft.Data.Sqlite opens the connection that `Raw` must stay, so it is library-wide (every
   connection is used by one thread at a time, as in Rust). A test asserts `sqlite3_memory_used() = 0` (statistics are off).
3. `b7f020f` **Direct calls for the per-row functions**: step, reset, binds and column reads call `e_sqlite3` (the library SQLitePCLRaw ships, which Microsoft.Data.Sqlite
   loads) by C name with the statement as a pointer, without `SafeHandle` counting, and the ones that return at once with `SuppressGCTransition`. Text is decoded with its
   length; a datetime in the form Rails writes is parsed from the UTF-8 bytes (`Timestamp.ParseDbUtf8`, tested against the string parser on random input).
4. `15b1376` **A read that found a free connection no longer `Task.Yield`s.** Rust's `yield_now` re-queues on the same worker; .NET's queues to the pool and wakes another worker to
   look for it: about 38 us of CPU each at c=1, five or six per page. This was the c=1 cost: room page 3,885 → 5,405 req/s, sidebar 3,837 → 5,175 on the tier 2 run that isolated it
   (CPU per request 453 → 264 and 443 → 275). `Read` still completes synchronously with its connection returned first, and a read that raises still faults its caller's task.
5. `c9964b3` **Statement cache lookup hashes the length and sixteen characters**, not all of a 300-500 character `SELECT` (270 ns of every statement), and the row list is built
   without an enumerator.

Behavior is unchanged: the same SQL, the same transactions, writer topology and callback order, `Raw` still the Microsoft.Data.Sqlite connection. `Campfire.Db.Tests` gained
tests for the quoting of `?`, empty text and blobs binding as text and blobs (not NULL), required accessors refusing NULL while a stored 0 reads as 0, and the UTF-8 datetime path.

## Where the SQLite time went

SQL per request is the same as Rust's: the sidebar compared function by function with `rust/crates/campfire/src/controllers/users/sidebars.rs` and `presenters/accounts.rs`
(identical queries and order); the F# side traced for the other three pages (room 8 statements, messages page 8, sidebar 9, search 7, each with 4-6 reads, search one of them
offloaded, as in Rust). The engine's "extra work" was not extra SQL; it was Microsoft.Data.Sqlite's API calls per row and column, SQLite's own connection and allocation
mutexes, and the per-call transitions. `bench/breakdown perf` (c=16, its 15 s warm-up, so its unprofiled CPU is not the table's), Rust / F# start → F# after, us per request:

| Layer | room | messages | sidebar | search | post |
|---|---|---|---|---|---|
| SQLite engine | 40.1 / 43.6 → **24.2** | 42.1 / 39.6 → **24.4** | 42.2 / 69.0 → **34.2** | 58.1 / 73.4 → **34.1** | 197.3 / 238.4 → **165.4** |
| Around the engine (F#: Microsoft.Data.Sqlite stubs + Campfire.Db; Rust: db crate) | 6.8 / 32.7 → **13.4** | 7.0 / 31.0 → **14.2** | 6.2 / 28.5 → **16.5** | 5.9 / 26.1 → **16.0** | 19.6 / 76.7 → **38.6** |

The engine is now below Rust's on every route (the mutexes are off and the call count is down; I did not compare the two SQLite builds' compile options, so I can't say how much of the margin is the build).
The part around it is still twice Rust's: row mapping into records and options, the lists, the read queue's lock.

Tier 1 (`bench/db-micro`, one reader connection on the seed, best of 5, this Mac): room page 58.4 → 37.4 us, messages 49.2 → 28.7, sidebar 47.4 → 20.7, search 25.1 → 14.5, a bare
statement 2.0 → 1.1; bytes allocated per room page 20.1 KB → 12.3 KB. Of the 28 us of the room page's last-40-messages query, 28 us is SQLite sorting the room's rows (the plan is the same as Rust's).

## Process notes

- `bin/publish-mounted` + `bench/quick --mounted DIR` is the tier 2 loop (publish 40 s, one cell about 40-90 s); `bench/quick compare|log` print the table against the stored baseline and append to
  `phase7-log.jsonl`. Mounted and image builds agree: the same schedule gave room/messages/sidebar/search c=16 of 18.4/21.5/17.6/18.4k mounted and 20.7/18.7/17.6/18.5k image (unchanged app, one rep
  each: tier 2's noise is about ±8% on a cell, so only changes above that are read from it); both ran 3-8% under the stored start on the same day, so tier 2 A/Bs use a same-day run of the unchanged app.
- A tier 2 run that mixes routes in one container is not the schedule of the stored numbers (c=1 first, two containers per rep): post c=16 read 0.5-0.6x of its start in the first two such runs
  and 0.97x in the next. Compare cells measured the same way.
- Checks run after the last code change (`d7ce54c`): `bin/verify` per project with `CAMPFIRE_REQUIRE_SEED=1` (Db 176 passed/4 skipped, Storage 41, Ruby 20, RailsCompat 37, Routes 5, Assets 14, Kit 225/1 skipped,
  Views 137, RichText 78, Cable 64/1 skipped, App 195/1 skipped, all Release with warnings as errors), `bin/views-differential` (22,374 of 22,374 renders identical), `bin/kit-differential` (0 differ in every group),
  `bin/richtext-differential` (0 differ), `bin/db-differential` (schema identity, the reference boots on the F#-written database).
