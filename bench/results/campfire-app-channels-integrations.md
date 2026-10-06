# Channels and integrations: numbers from unit 5.4 (2026-10-06)

Recorded, not tuned: Phase 5b's whole-app baseline (`bench/run --apps reference,rust,fsharp`) decides what to optimize.
Apple silicon Mac, .NET 10.0.401, colima VM (aarch64, 2 CPUs per container, reached through Docker's published port, so
every connection pays the port proxy); the clients ran on the same Mac, which was doing other work.

## What was checked first

With `campfire-fsharp:app` rebuilt (`parity/bin/candidate build`) and running under `parity/bin/candidate up --seed default
--time 2026-03-02T16:00:00Z --freeze`, signed in as David (`POST /session`), a WebSocket to `/cable` with the session cookie:

| Step | F# |
|---|---|
| upgrade, `Sec-WebSocket-Protocol: actioncable-v1-json` | 101 |
| first frame | `{"type":"welcome"}` |
| subscribe `RoomChannel`, `PresenceChannel`, `TypingNotificationsChannel` for All Talk (486777696, David is a member) | `confirm_subscription` each |
| subscribe `RoomMessagesChannel` with the `signed-stream-name` the room page carries | `confirm_subscription` |

Web Push is now built, so the app has a VAPID key pair (`parity/.env.reference`) and the pages carry the
`vapid-public-key` meta tag the Rust image renders. The room page (`/rooms/486777696`) and the sidebar
(`/users/me/sidebar`) from the F# image and the Rust image (`PARITY_CANDIDATE_APP=rust`) on the same seed with the clock
frozen are byte for byte the same once the port and the CSRF token are masked, with the tag in place of the mask unit 5.3 needed.

## Cable: connect and subscribe

A raw-socket client (Python) opened a connection, waited for the welcome, subscribed to `RoomChannel`, `PresenceChannel` (a
database write: `Membership#present`), `TypingNotificationsChannel`, `UnreadRoomsChannel` and `ReadRoomsChannel`, and closed
the socket (which writes the membership's `disconnected`). One session is one connect and five subscribes. Median and 99th
percentile latency of the connect (to the welcome) and of a subscribe (to its confirmation), sessions per second; F# against
Rust, each on a fresh copy of the `default` seed and a container of its own.

| Clients | F# connect | Rust connect | F# subscribe | Rust subscribe | F# sessions/s | Rust sessions/s |
|---|---|---|---|---|---|---|
| 1 (second run of 100) | 0.80 ms (3.5) | 0.58 ms (0.72) | 0.37 ms (0.74) | 0.28 ms (0.58) | 339 | 447 |
| 8 (40 sessions each) | 1.11 ms (26.1) | 0.84 ms (3.4) | 0.59 ms (27.9) | 0.48 ms (4.0) | 870 | 1,369 |

F# is 1.3-1.4x Rust's median here, the gap of the HTTP paths (unit 5.3), and the 99th percentile at 8 clients has the
pause the room pages showed at 16 (the first run of a process also pays JIT: its first 100 sessions had a connect p99 of
11 ms). Nothing here was profiled.

## Integrations: what one call costs

Measured in an F# script on the Release build (`dotnet fsi --optimize+`, 5,000 calls after 200 of warm-up; Rust has no
counterpart measured):

| Call | F# |
|---|---|
| encrypt a notification (RFC 8291: ECDH, HKDF, AES-GCM) | 333 us |
| a VAPID `Authorization` header (ES256) | 81 us |
| the opengraph attributes of a 100 KB page | 96 us |
| the opengraph attributes of 1 MB of `<meta>` tags | 7.8 ms |

A Web Push to a subscriber is the encryption and the signature (about 0.4 ms of CPU) and one TLS connection; the pool runs
50 at a time. The tests hold the limits (`WebPushTests`: 50 running, 10,000 waiting, the rest dropped; `OpengraphTests`: a
gigabyte of gzip stops at 5 MB inside 2 seconds; the trickling page gives up at its deadline).
