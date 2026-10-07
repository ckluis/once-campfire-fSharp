F# in bench/results/phase7-runs/7.1-01-raw-sqlitepclraw against the stored Rust baseline (bench/results/phase7-start, medians of 3 Rust reps) and the F# starting point

| Workload | c | F# req/s | F# start | vs start | Rust stored | **F# / Rust (vs stored Rust baseline)** | F# CPU us/req | start | Rust stored |
|---|---|---|---|---|---|---|---|---|---|
| sidebar | 16 | 23,107 | 18,888 | 1.223x | 28,378 | **0.814x** | 162.3 | 199.5 | 122.8 |
| sidebar | 1 | 3,909 | 3,837 | 1.019x | 6,366 | **0.614x** | 448.8 | 442.7 | 145.8 |
| search | 16 | 23,082 | 20,026 | 1.153x | 26,606 | **0.868x** | 159.3 | 183.8 | 124.2 |
| search | 1 | 4,158 | 3,990 | 1.042x | 5,638 | **0.737x** | 453.5 | 467.5 | 157.9 |

Checks:
  responses: every one 2xx with 0 connection errors in 4 runs
  decoded bytes equal Rust's on every probed route; post rows written equal the 2xx answers in every phase
