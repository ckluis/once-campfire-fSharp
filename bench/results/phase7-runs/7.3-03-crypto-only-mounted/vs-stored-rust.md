F# in bench/results/phase7-runs/7.3-03-crypto-only-mounted against the stored Rust baseline (bench/results/phase7-start, medians of 3 Rust reps) and the F# starting point

| Workload | c | F# req/s | F# start | vs start | Rust stored | **F# / Rust (vs stored Rust baseline)** | F# CPU us/req | start | Rust stored |
|---|---|---|---|---|---|---|---|---|---|
| room page | 16 | 33,436 | 21,892 | 1.527x | 28,720 | **1.164x** | 111.8 | 173.0 | 121.6 |
| sidebar | 16 | 33,614 | 18,888 | 1.780x | 28,378 | **1.185x** | 113.7 | 199.5 | 122.8 |
| search | 16 | 29,413 | 20,026 | 1.469x | 26,606 | **1.106x** | 129.3 | 183.8 | 124.2 |

Checks:
  responses: every one 2xx with 0 connection errors in 3 runs
  decoded bytes equal Rust's on every probed route; post rows written equal the 2xx answers in every phase
