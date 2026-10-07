F# in bench/results/phase7-runs/7.1-04-no-yield against the stored Rust baseline (bench/results/phase7-start, medians of 3 Rust reps) and the F# starting point

| Workload | c | F# req/s | F# start | vs start | Rust stored | **F# / Rust (vs stored Rust baseline)** | F# CPU us/req | start | Rust stored |
|---|---|---|---|---|---|---|---|---|---|
| room page | 16 | 27,240 | 21,892 | 1.244x | 28,720 | **0.948x** | 140.6 | 173.0 | 121.6 |
| room page | 1 | 5,405 | 3,885 | 1.391x | 6,500 | **0.832x** | 263.8 | 453.2 | 141.6 |
| sidebar | 16 | 26,888 | 18,888 | 1.424x | 28,378 | **0.948x** | 143.2 | 199.5 | 122.8 |
| sidebar | 1 | 5,175 | 3,837 | 1.349x | 6,366 | **0.813x** | 275.4 | 442.7 | 145.8 |

Checks:
  responses: every one 2xx with 0 connection errors in 4 runs
  decoded bytes equal Rust's on every probed route; post rows written equal the 2xx answers in every phase
