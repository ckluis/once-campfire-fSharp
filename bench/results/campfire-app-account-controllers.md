# The account-side controllers: numbers from unit 5.2 (2026-10-06)

Recorded, not tuned: Phase 5b's whole-app baseline (`bench/run --apps reference,rust,fsharp`) decides what to
optimize. Apple silicon Mac, .NET 10.0.401, colima VM (aarch64, 2 CPUs per container); other work was running on the host.

## Request latency

One sequential keep-alive connection to the production image of each app (`parity/bin/candidate up`, the `default`
parity seed, `PARITY_CANDIDATE_APP=rust` for the Rust port), signed in as David, 20 warm-up requests and then 300
timed ones per path; median and, in brackets, 95th percentile of the time from sending the request to reading its
whole response (`target/bench_account.py`, a throwaway script). These are single-request latencies including the
loopback and the VM's network, not throughput.

| Path | F# | Rust | F# / Rust |
|---|---|---|---|
| GET /qr_code/:id | 0.22 ms (0.26) | 0.14 ms (0.18) | 1.6x |
| GET /account/edit | 0.76 ms (0.93) | 0.28 ms (0.31) | 2.7x |
| GET /users/me/profile | 0.77 ms (0.99) | 0.28 ms (0.31) | 2.8x |
| GET /users/me/sidebar | 0.62 ms (0.80) | 0.26 ms (0.31) | 2.4x |
| GET /autocompletable/users | 0.54 ms (0.66) | 0.24 ms (0.27) | 2.2x |
| GET /account/bots | 0.66 ms (0.86) | 0.28 ms (0.31) | 2.3x |
| GET /webmanifest.json | 0.28 ms (0.32) | 0.16 ms (0.18) | 1.8x |
| GET /session/new (signed out) | 0.41 ms (0.47) | 0.20 ms (0.22) | 2.1x |

Nothing here is tuned or profiled. The pages render through the same templates the views differential checks byte for
byte against Rust, so the gap is in the request path around them. `/qr_code/:id` and `/webmanifest.json` need no session
and no database read, and sit about 0.1 ms over Rust: that is roughly the floor of Kestrel and the kit against hyper.
The signed-in and signed-out pages add about 0.2-0.4 ms more (a session lookup on a reader, the layout's reads, a larger
response), which is where the baseline's per-layer breakdown should look first.

## QR code

`Rqrcode.svgBytes` is a port of rqrcode_core's encoder, byte for byte (the gem's own vectors, 30+ inputs from empty to
version 40, are in `RqrcodeTests`). The row above for `/qr_code/:id` is the join URL's code: 0.22 ms at the median against
Rust's 0.14, of which the request path accounts for about 0.1.
