F# in bench/results/phase7-runs/7.5-02-before-a against the stored Rust baseline (bench/results/phase7-start, medians of 3 Rust reps) and the F# starting point

| Workload | c | F# req/s | F# start | vs start | Rust stored | **F# / Rust (vs stored Rust baseline)** | F# CPU us/req | start | Rust stored |
|---|---|---|---|---|---|---|---|---|---|
| search | 16 | 33,951 | 20,026 | 1.695x | 26,606 | **1.276x** | 107.8 | 183.8 | 124.2 |
| search | 1 | 5,885 | 3,990 | 1.475x | 5,638 | **1.044x** | 324.9 | 467.5 | 157.9 |
| post a message | 16 | 9,074 | 6,046 | 1.501x | 7,140 | **1.271x** | 346.8 | 548.9 | 389.4 |
| post a message | 1 | 3,063 | 1,958 | 1.564x | 2,814 | **1.089x** | 635.0 | 997.8 | 399.2 |

Checks:
  responses: every one 2xx with 0 connection errors in 4 runs
  decoded bytes equal Rust's on every probed route; post rows written equal the 2xx answers in every phase
