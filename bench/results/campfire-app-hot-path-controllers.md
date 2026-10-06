# The hot-path controllers: numbers from unit 5.3 (2026-10-06)

Recorded, not tuned: Phase 5b's whole-app baseline (`bench/run --apps reference,rust,fsharp`) decides what to optimize.
Apple silicon Mac, .NET 10.0.401, colima VM (aarch64, 2 CPUs per container, reached through Docker's published port, so
every request pays the port proxy); `wrk` ran on the same Mac, which was doing other work.

## What was checked first

With `campfire-fsharp:app` rebuilt (`parity/bin/candidate build`) and running under `parity/bin/candidate up --seed default`,
signed in as David (`POST /session`):

| Request | F# |
|---|---|
| `GET /rooms/486777696` (All Talk) | 200, 40 messages |
| `GET /rooms/486777696/messages?before=<a message id of that room>` | 200 (not 204), 40 messages |
| `GET /users/me/sidebar` (Turbo-Frame `user_sidebar`) | 200 |
| `GET /searches?q=the` | 200, 62 results |
| `POST /rooms/486777696/messages` (turbo stream) | 200, the message appended; the room page then shows it |

The post wrote its row: the bot API's `X-Total-Count` for the room went from 132 to 4585 over a `wrk` run of posts (a
count by the application, through its own reader).

The same requests, a room page, a messages page, the sidebar, a search, a refresh, the message, bot JSON, involvement
and room-form pages, and a posted message came back byte for byte the same from the Rust image (`PARITY_CANDIDATE_APP=rust`)
on the same seed with the clock frozen (`--time 2026-03-02T16:00:00Z --freeze`), once the port in absolute URLs and the
`vapid-public-key` meta tag (Web Push isn't built in F# yet, see unit 5.2's notes) were masked, and the CSRF tokens.

## Latency and throughput

`wrk` (`-t1 -c1` and `-t2 -c16`, 6 seconds a path, `Accept-Encoding: gzip`, a session cookie), F# against Rust, each on a
fresh copy of the `default` seed and a container of its own, one after the other. `target/wrk-run.sh` and
`target/wrk-post.lua` are throwaway scripts (`target/` is untracked). Median and 99th percentile latency, requests per
second.

| Path | F# c=1 | Rust c=1 | F# c=16 | Rust c=16 |
|---|---|---|---|---|
| room page | 0.58 ms (0.93) 1,665/s | 0.40 ms (0.48) 2,487/s | 1.70 ms (18.0) 9,130/s | 1.38 ms (2.5) 11,285/s |
| messages page (`?before=`) | 0.47 ms (0.76) 2,079/s | 0.37 ms (0.43) 2,673/s | 1.61 ms (17.9) 9,350/s | 1.25 ms (2.1) 12,452/s |
| sidebar | 0.43 ms (0.71) 2,263/s | 0.34 ms (0.40) 2,879/s | 1.42 ms (17.7) 10,404/s | 1.09 ms (2.0) 14,237/s |
| search (`?q=the`) | 0.57 ms (0.90) 1,722/s | 0.47 ms (0.54) 2,095/s | 2.38 ms (47.3) 4,321/s | 1.99 ms (38.0) 5,650/s |
| post a message | 3.11 ms (5.6) 311/s | 2.22 ms (3.4) 449/s | 38.1 ms (42.0) 427/s | 29.0 ms (33.4) 554/s |

F# is 1.2-1.5x Rust's latency everywhere and 0.77-0.8x its throughput, which is the gap unit 5.2 saw for the pages
around the templates (`bench/results/campfire-app-account-controllers.md`): the request path, not the pages. The room,
messages and sidebar pages at 16 connections show a 99th percentile of about 18 ms against Rust's 2 ms, which is a pause
(server GC, most likely) rather than work; the baseline's per-layer breakdown should look there, and at the post, whose
time is the single writer's (a transaction, the commit's fsync and what runs after it: the index entry, the broadcast).
Nothing here was profiled.
