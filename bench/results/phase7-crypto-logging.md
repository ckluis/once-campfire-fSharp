# Unit 7.3: signed cookies, tokens and the request log (2026-10-06)

F# against the stored Rust baseline (`phase7-start/`, medians of 3 Rust reps; **Rust was not re-run, so every ratio here is "vs stored Rust baseline"**).
Tier 3: `campfire-fsharp:app` rebuilt from `b1d55bf` (`sha256:53ce6520c2c2`, `src/` identical to `0b67670`), F# only, 3 reps, a fresh container and seed copy per part,
`SERVER_CPUS=0-3 LOADGEN_CPUS=4-7`, the long warm-up. Raw data and validity tables: [`phase7-unit-7.3/`](phase7-unit-7.3/) (`vs-stored-rust.md` is the table below). All 30
runs: every response 2xx, 0 connection errors, decoded bytes equal to Rust's on every route, rows written equal the 2xx answers of every post phase. Eight cells' warm-ups
reached the 90 s maximum (messages c=16 three times, sidebar c=16 three times, room c=16, post c=1) and were flat afterwards, as in 7.1 and 7.2.

## Before and after

Req/s and container CPU per request, median [min-max] of 3 reps. "7.2" is the tier 3 of `phase7-runtime.md` (`1d0f3b2`).

| Workload | c | Rust stored | F# 7.2 | **F# 7.3** | F# / Rust 7.2 -> **7.3** | CPU us/req Rust / 7.2 / **7.3** |
|---|---|---|---|---|---|---|
| room page | 16 | 28,720 | 30,672 | **38,606** [38,027-39,915] | 1.07 -> **1.34** | 121.6 / 123.7 / **97.5** [95-100] |
| room page | 1 | 6,500 | 5,654 | **7,524** [7,473-7,529] | 0.87 -> **1.16** | 141.6 / 252.4 / **209.7** |
| messages page | 16 | 30,230 | 33,229 | **37,933** [36,937-38,006] | 1.10 -> **1.25** | 113.7 / 115.8 / **101.5** |
| messages page | 1 | 6,981 | 6,293 | **8,112** [8,035-8,154] | 0.90 -> **1.16** | 135.7 / 229.4 / **199.5** |
| sidebar | 16 | 28,378 | 28,192 | **36,659** [36,588-37,228] | 0.99 -> **1.29** | 122.8 / 135.4 / **104.2** |
| sidebar | 1 | 6,366 | 5,336 | **7,690** [7,613-7,728] | 0.84 -> **1.21** | 145.8 / 265.7 / **187.5** |
| search | 16 | 26,606 | 28,713 | **33,798** [33,717-33,950] | 1.08 -> **1.27** | 124.2 / 128.5 / **108.0** |
| search | 1 | 5,638 | 4,695 | **5,837** [5,796-5,993] | 0.83 -> **1.04** | 157.9 / 374.7 / **321.5** |
| post a message | 16 | 7,140 | 7,186 | **8,108** [7,861-8,124] | 1.01 -> **1.14** | 389.4 / 441.1 / **396.5** |
| post a message | 1 | 2,814 | 2,275 | **2,727** [2,420-2,766] | 0.81 -> **0.97** | 399.2 / 809.0 / **697.0** |

What it says, and what it does not:

- **Every cell gained 13-43% in req/s and 7-23% in CPU per request over 7.2**, which is more than the cookie code alone could give, so I split it (below): the request log
  is about two thirds of the CPU saving at c=16 and most of the c=1 gain; the signed cookies and tokens are the rest, and on the sidebar and the room page the avatar tokens are most of that.
- **F# is ahead of the stored Rust baseline on nine of ten cells and at 0.97 on posting at one connection.** Two caveats that keep this from being a claim about Rust today: Rust was not
  re-measured, and at c=16 it leaves about half of the 4 cores idle in the stored runs (3.3-3.5 busy) while F# now uses 3.2 (post) to 3.85 (messages) of them. F# CPU per request is now below Rust's stored figure on the four read pages
  (room 0.80x, messages 0.89x, sidebar 0.85x, search 0.87x) and +2% on a post. Two of those savings are things Rust does not do: F# remembers avatar tokens and stream names (Rust signs each on every request), and batches
  the request log into 4 KiB writes where Rust's `tracing` writes each line under the stdout lock. Whether Rust should do the same is not this port's question; the re-baseline at the end of the phase will say where Rust is.
- **c=1 moved the most (+23 to +43%),** and it is the answer to what 7.1 and 7.2 called "thread wake-ups": the console logger wakes its writer thread for every line while it is idle, which at one connection is on every request.
  Batching the lines removed that; there is no wake-up per request left to hide in the CPU figure. F# CPU per request at c=1 is still 1.3-2.0x Rust's (spinning workers; unchanged).

## What the 40 s of CPU is: the split (tier 2, c=16, one rep each, mounted builds, same day, same schedule)

| Build | room req/s / CPU | sidebar req/s / CPU | search req/s / CPU |
|---|---|---|---|
| control: the 7.2 image (`1d0f3b2`) | 30,626 / 122.0 | 28,195 / 134.8 | 29,478 / 130.9 |
| crypto only (`a696844`, mounted) | 33,436 / 111.8 | 33,614 / 113.7 | 29,413 / 129.3 |
| crypto + batched log (`0b67670`, mounted), run a / run b | 40,066 / 93.6 and 39,424 / 95.7 | 36,712 / 104.1 and 38,359 / 99.3 | 32,982 / 115.9 and 32,737 / 113.6 |

So the crypto half is -10 us (room), -21 (sidebar), -2 (search) of CPU per request; the log half is a further -16 to -18 (room), -10 to -14 (sidebar), -14 to -16 (search). The sidebar's
crypto gain is the avatar tokens (17 users a request, 1.2 us each before, a table lookup now); search signs none. The tier 3 image and the mounted build have byte-identical `Campfire.Kit.dll` and
`Campfire.RailsCompat.dll` (sha256 compared), as 7.2 checked once for another build. One more tier 2 run at c=1 (both halves, mounted): room 7,405 / 213.3, sidebar 7,612 / 192.5, search 5,820 / 326.6.

## Tier 1 (in process, Linux, `bench/micro-linux crypto-micro|log-micro`; best of 5 windows on a slow pinned core, so ns here are about twice what the app sees; compare variants only)

| Call | before | after |
|---|---|---|
| `cookies.signed[:session_token]` read (verify) | 2,693 ns, 5,904 B | **1,037 ns, 1,152 B** |
| `cookies.encrypted[:_campfire_session]` read | 3,610 ns, 8,769 B | **1,947 ns, 2,712 B** |
| signed cookie write | 1,572 ns | 986 ns |
| session cookie write (encrypt) | 2,860 ns | 2,064 ns |
| `Turbo.signedStreamName` (room messages / rooms) | 1,238 / 1,004 ns | **136 / 63 ns** (remembered) |
| one avatar token | 1,241 ns, 3,064 B | **71 ns, 24 B** (remembered) |
| 40 distinct users' avatar tokens | 51,187 ns, 119,336 B | **2,847 ns, 960 B** |
| one request through `FrontHandler`, logging off / ILogger console logger / batched bytes (CPU of the whole process) | 3.6 us / 16.4 us / - | 3.6 us / - / **4.8 us** |

What the signed-cookie read was spending (Linux): the regex timestamp parse 415 ns, `Json.parse` of the cookie envelope 400 ns, a `Seq.forall` blank check over the 190-character payload, three `Substring`s and the
`int[]` of the Base64 decoder, and a one-shot HMAC (780 ns against 340 with the keyed state reused; the same keyed-state cut is 990 -> 380 ns for `new AesGcm` + decrypt). The macOS numbers were misleading for this:
the first Mac run said the verify cost 1.7 us (Apple's CommonCrypto is quick at the one-shot) against 8 us on Linux, which is why tier 1 for crypto now runs in the toolchain image.

## What changed

- `KeyedCrypto` (`src/Campfire.RailsCompat/KeyedCrypto.fs`): an `IncrementalHash` HMAC (SHA-1 and SHA-256) and an `AesGcm` per (key, thread), reached through the `KeyGenerator.SharedKey` array (one instance
  per salt and length, no copy per call), with a content check on every hit so an array someone overwrites gets its own state. `GenerateKey` still returns a copy.
- `MessageVerifier`: the digest is checked on spans (the payload's UTF-8 in a thread's scratch buffer, the MAC compared with the hex characters in constant time); no `Substring`, `Seq` or hex string per verify.
- `Timestamps.tryParse`: a hand parser for `2046-01-01T12:00:00.000Z`, the one shape `iso8601Millis` writes; anything else, and any date the calendar refuses, goes to the regex (80,000 generated strings compared).
- `Metadata`: the cookie jars' legacy envelope is read without `Json.parse` when it is exactly what `serializeDumpedWithMetadata` writes (plain strings or null, in order); anything else falls to the parser (generated and damaged envelopes compared).
- `RailsEncoding`: both decoders over spans, one allocation (compared with the array decoder they replaced on 80,000 strings), `urlsafe` encoders on `Base64Url`.
- `Secrets.Tokens`: a bounded table (16,384 entries, emptied when full) of signed ids without an expiry (avatar tokens) and signed stream names, per `Secrets` instance. Tokens with an expiry are never remembered.
- Request log: `RequestLog.fs` writes the line as UTF-8 into a thread's buffer, appends it to a batch under a lock held for one copy, and a thread writes the batch every 10 ms in chunks of whole lines of at most 4 KiB (atomic on a pipe).
  The line is `SimpleConsoleFormatter`'s for `RequestLogEntry`; `RequestLogTests` compares them on the real console logger (IPv6, mapped IPv4, forwarded-for, non-ASCII, newlines in values, 5,000-character values).
  Only `Boot.serve` passes the batch (`Front.serveWithLines`); tests and tools keep the `ILogger` path. The logger's level still decides whether requests are logged. `FrontHandler` splits the request target once for the cache key and the log.
- Tooling: `bench/micro-linux` (tier 1 in the toolchain image), `bench/crypto-micro`, `bench/log-micro`, `bench/lib/logcount`, a log tail per rep in `bench/run`.

## Behavior kept

- `bin/verify` with `CAMPFIRE_REQUIRE_SEED=1`: 1,032 tests, 0 failed, 7 skipped (the Linux-only and media ones, as before). The first full run of it showed one failure in `Campfire.App.Tests` that I did not capture; 9 later runs of the App project (1 alone, 2 in `bin/verify App`
  and in the full run, 6 in `bin/verify App Kit`) were clean, so I cannot name it. Running `dotnet test` on the App project directly, without `bin/verify`'s libvips path, fails three media tests on this Mac whatever the code is.
- `bin/kit-differential` 0 differ on all 9 sections of 3,000; `bin/views-differential` 22,374 of 22,374 renders identical; `bin/richtext-differential` 0 differ on all four sections of 20,000.
- Every signature, cookie and token is byte for byte what it was (the micro prints the same avatar, Turbo and cookie strings before and after); the tier 2 and tier 3 runs compare decoded response bytes with Rust's.
- `bench/lib/logcount`: 20,000 and 8,000 requests at 16 connections, then SIGTERM: 20,001 and 8,001 `Request path=` lines in `docker logs` (the requests plus the readiness probe that answered).

## Costs and limits of the batched log

A line is written up to 10 ms after the request, so it can come after a line another logger wrote later (the console logger's other lines, such as "Server started"); a `kill -9` loses up to 10 ms of lines (SIGTERM and exit flush).
A stdout that blocks blocks the request that fills a 4 MiB backlog, as a full console-logger queue blocked requests. Timestamps are taken when the line is written, as the console logger took them.

## SageFs

0.6.904 starts (`sagefs --owner-pid`, stopped with `sagefs stop` before every tier 2 and 3 run), speaks MCP over stdio and has a project session (15-30 s to warm up), but this session has no MCP client to hold a session open, and a
tier 1 that matters here (OpenSSL, `write` syscalls) has to run on Linux, which SageFs on the Mac does not. It did not save time; the Linux harness (`bench/micro-linux`, 45-110 s a run) did the work.
