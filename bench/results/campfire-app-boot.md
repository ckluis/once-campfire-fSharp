# The app booted: numbers from unit 5.1 (2026-10-06)

Recorded, not tuned. Phase 5b's whole-app baseline (`bench/run --apps reference,rust,fsharp`) decides what to
optimize; these are the cheap measurements the boot, wiring and image unit could take on the way. Apple silicon
Mac, .NET 10.0.401, colima VM (aarch64, 2 CPUs per container); other work was running on the host.

## Boot and footprint

`docker run` of the production image on a copy of the `default` parity seed, with `parity/.env.reference`, 2 CPUs,
until `GET /up` first answers 200 (polled every 50 ms), then the container's memory after 3 idle seconds
(`docker stats`). Three runs each, alternating; `target/boot-time.sh` was the throwaway script.

| | F# (`campfire-fsharp:app`) | Rust (`campfire-rust:app`) |
|---|---|---|
| Boot to first `/up` | 213, 218, 222 ms | 136, 140, 146 ms |
| Idle memory | 26.2-26.4 MiB | 16.1 MiB |
| Image, content size | 113 MB | 70 MB |
| Image, on disk | 419 MB | 267 MB |

The F# image is self-contained (the ~70 MB runtime, ReadyToRun-compiled app assemblies; see the `Dockerfile` header for
why). The route table (177 regexes, `RegexOptions.Compiled`) builds in about 10 ms on first use; the rest of the 70-80 ms
over Rust is the runtime starting and the first requests' JIT (the image is ReadyToRun-compiled). Not tuned.

## Route recognition

`RouteTable.recognize` on `GET`, median of 9 samples of 200,000 calls per path, `CAMPFIRE_TIMING=1 dotnet test ...
--filter-method "*times recognition*"` in Release (`RouteTableTests`). Rust's are from its
`bench/results/routing-20260930` (`RegexSet` per verb, a different machine).

| Path | F# | Rust (RegexSet) |
|---|---|---|
| room_show `/rooms/12` | 426 ns (min 342, max 1,075) | 246 ns |
| messages_page `/rooms/12/messages` | 219 ns | 271 ns |
| search `/searches` | 127 ns | 211 ns |
| `/up` | 127 ns | 194 ns |
| 404 `/wp-login.php` | 20 ns | 47 ns |
| Active Storage representation, 310 bytes | 906 ns | 2,801 ns |
| Active Storage blob, 2.3 KB filename | 16.9 us | 32.6 us |

Routes are indexed by the first segment of their pattern, and a path tries only the regexes of its segment, in table
order (`recognizes like a first match scan` checks the answer is the first-match scan's over a corpus of about
100,000 paths and every verb).

## Static files

`Boot.staticResponse` (ActionDispatch::Static: the lookup, the headers and the response, no socket) for an identity
GET, median of 7 runs of 20,000 calls (`CAMPFIRE_TIMING=1`, `AppTests` "static response timing"). Rust's
(`bench/results/static-body-20260930`, `static_response`, after the no-copy change) are 487-522 ns for the same files.

| File | F# |
|---|---|
| /robots.txt (99 B) | 2,880 ns (first run warms up: 857 ns at the fastest run) |
| _reset.css (1,218 B) | 971 ns |
| lexxy.js (922,910 B) | 960 ns |
| lexxy.js.map (2,179,664 B) | 1,075 ns |

The cost is independent of the size, as in Rust (the body is the embedded bytes, not a copy); the F# response is about
2x Rust's per call, in the header list and the `Response` it builds. Not tuned.

## What is not measured yet

Requests per second of the five benchmarked workloads: the controllers behind them are the next units'. Until then
`/up` and `/session/new` are the only pages the app serves, and the baseline would measure the kit and the layout, not
Campfire.
