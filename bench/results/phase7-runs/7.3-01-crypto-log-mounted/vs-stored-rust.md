F# in bench/results/phase7-runs/7.3-01-crypto-log-mounted against the stored Rust baseline (bench/results/phase7-start, medians of 3 Rust reps) and the F# starting point

| Workload | c | F# req/s | F# start | vs start | Rust stored | **F# / Rust (vs stored Rust baseline)** | F# CPU us/req | start | Rust stored |
|---|---|---|---|---|---|---|---|---|---|
| room page | 16 | 40,066 | 21,892 | 1.830x | 28,720 | **1.395x** | 93.6 | 173.0 | 121.6 |
| sidebar | 16 | 36,712 | 18,888 | 1.944x | 28,378 | **1.294x** | 104.1 | 199.5 | 122.8 |
| search | 16 | 32,982 | 20,026 | 1.647x | 26,606 | **1.240x** | 115.9 | 183.8 | 124.2 |

Checks:
  responses: every one 2xx with 0 connection errors in 3 runs
  decoded bytes equal Rust's on every probed route; post rows written equal the 2xx answers in every phase
