F# in bench/results/phase7-runs/7.3-02-crypto-log-mounted-b against the stored Rust baseline (bench/results/phase7-start, medians of 3 Rust reps) and the F# starting point

| Workload | c | F# req/s | F# start | vs start | Rust stored | **F# / Rust (vs stored Rust baseline)** | F# CPU us/req | start | Rust stored |
|---|---|---|---|---|---|---|---|---|---|
| room page | 16 | 39,424 | 21,892 | 1.801x | 28,720 | **1.373x** | 95.7 | 173.0 | 121.6 |
| sidebar | 16 | 38,359 | 18,888 | 2.031x | 28,378 | **1.352x** | 99.3 | 199.5 | 122.8 |
| search | 16 | 32,737 | 20,026 | 1.635x | 26,606 | **1.230x** | 113.6 | 183.8 | 124.2 |

Checks:
  responses: every one 2xx with 0 connection errors in 3 runs
  decoded bytes equal Rust's on every probed route; post rows written equal the 2xx answers in every phase
