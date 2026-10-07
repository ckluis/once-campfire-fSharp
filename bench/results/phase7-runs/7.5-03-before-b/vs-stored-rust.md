F# in bench/results/phase7-runs/7.5-03-before-b against the stored Rust baseline (bench/results/phase7-start, medians of 3 Rust reps) and the F# starting point

| Workload | c | F# req/s | F# start | vs start | Rust stored | **F# / Rust (vs stored Rust baseline)** | F# CPU us/req | start | Rust stored |
|---|---|---|---|---|---|---|---|---|---|
| search | 16 | 32,922 | 20,026 | 1.644x | 26,606 | **1.237x** | 110.8 | 183.8 | 124.2 |
| search | 1 | 5,974 | 3,990 | 1.497x | 5,638 | **1.060x** | 319.8 | 467.5 | 157.9 |
| post a message | 16 | 8,606 | 6,046 | 1.423x | 7,140 | **1.205x** | 366.9 | 548.9 | 389.4 |
| post a message | 1 | 3,055 | 1,958 | 1.560x | 2,814 | **1.086x** | 625.9 | 997.8 | 399.2 |

Checks:
  note search c=16: warm-up did not settle (reached the maximum)
  responses: every one 2xx with 0 connection errors in 4 runs
  decoded bytes equal Rust's on every probed route; post rows written equal the 2xx answers in every phase
