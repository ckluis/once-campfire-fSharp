F# in bench/results/phase7-runs/7.1-03-post against the stored Rust baseline (bench/results/phase7-start, medians of 3 Rust reps) and the F# starting point

| Workload | c | F# req/s | F# start | vs start | Rust stored | **F# / Rust (vs stored Rust baseline)** | F# CPU us/req | start | Rust stored |
|---|---|---|---|---|---|---|---|---|---|
| post a message | 16 | 6,940 | 6,046 | 1.148x | 7,140 | **0.972x** | 467.4 | 548.9 | 389.4 |
| post a message | 1 | 1,933 | 1,958 | 0.987x | 2,814 | **0.687x** | 989.6 | 997.8 | 399.2 |

Checks:
  responses: every one 2xx with 0 connection errors in 2 runs
  decoded bytes equal Rust's on every probed route; post rows written equal the 2xx answers in every phase
