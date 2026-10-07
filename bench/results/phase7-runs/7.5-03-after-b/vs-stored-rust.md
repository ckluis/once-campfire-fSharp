F# in bench/results/phase7-runs/7.5-03-after-b against the stored Rust baseline (bench/results/phase7-start, medians of 3 Rust reps) and the F# starting point

| Workload | c | F# req/s | F# start | vs start | Rust stored | **F# / Rust (vs stored Rust baseline)** | F# CPU us/req | start | Rust stored |
|---|---|---|---|---|---|---|---|---|---|
| search | 16 | 34,120 | 20,026 | 1.704x | 26,606 | **1.282x** | 107.0 | 183.8 | 124.2 |
| search | 1 | 6,103 | 3,990 | 1.530x | 5,638 | **1.083x** | 315.7 | 467.5 | 157.9 |
| post a message | 16 | 8,793 | 6,046 | 1.454x | 7,140 | **1.231x** | 354.9 | 548.9 | 389.4 |
| post a message | 1 | 3,050 | 1,958 | 1.558x | 2,814 | **1.084x** | 633.2 | 997.8 | 399.2 |

Checks:
  responses: every one 2xx with 0 connection errors in 4 runs
  decoded bytes equal Rust's on every probed route; post rows written equal the 2xx answers in every phase
