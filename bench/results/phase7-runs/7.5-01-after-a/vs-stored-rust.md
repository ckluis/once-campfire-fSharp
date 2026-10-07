F# in bench/results/phase7-runs/7.5-01-after-a against the stored Rust baseline (bench/results/phase7-start, medians of 3 Rust reps) and the F# starting point

| Workload | c | F# req/s | F# start | vs start | Rust stored | **F# / Rust (vs stored Rust baseline)** | F# CPU us/req | start | Rust stored |
|---|---|---|---|---|---|---|---|---|---|
| search | 16 | 33,914 | 20,026 | 1.693x | 26,606 | **1.275x** | 107.9 | 183.8 | 124.2 |
| search | 1 | 5,709 | 3,990 | 1.431x | 5,638 | **1.013x** | 327.4 | 467.5 | 157.9 |
| post a message | 16 | 8,775 | 6,046 | 1.451x | 7,140 | **1.229x** | 353.1 | 548.9 | 389.4 |
| post a message | 1 | 2,962 | 1,958 | 1.513x | 2,814 | **1.053x** | 641.6 | 997.8 | 399.2 |

Checks:
  responses: every one 2xx with 0 connection errors in 4 runs
  decoded bytes equal Rust's on every probed route; post rows written equal the 2xx answers in every phase
