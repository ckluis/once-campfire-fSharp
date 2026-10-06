F# in bench/results/phase7-unit-7.4/t2-gzip against the stored Rust baseline (bench/results/phase7-start, medians of 3 Rust reps) and the F# starting point

| Workload | c | F# req/s | F# start | vs start | Rust stored | **F# / Rust (vs stored Rust baseline)** | F# CPU us/req | start | Rust stored |
|---|---|---|---|---|---|---|---|---|---|
| post a message | 16 | 8,852 | 6,046 | 1.464x | 7,140 | **1.240x** | 356.3 | 548.9 | 389.4 |
| post a message | 1 | 2,985 | 1,958 | 1.524x | 2,814 | **1.061x** | 644.4 | 997.8 | 399.2 |

Checks:
  responses: every one 2xx with 0 connection errors in 2 runs
  decoded bytes equal Rust's on every probed route; post rows written equal the 2xx answers in every phase
