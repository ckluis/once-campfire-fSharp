F# in bench/results/phase7-unit-7.5/t2-spin10 against the stored Rust baseline (bench/results/phase7-start, medians of 3 Rust reps) and the F# starting point

| Workload | c | F# req/s | F# start | vs start | Rust stored | **F# / Rust (vs stored Rust baseline)** | F# CPU us/req | start | Rust stored |
|---|---|---|---|---|---|---|---|---|---|
| messages page | 16 | 40,273 | 23,346 | 1.725x | 30,230 | **1.332x** | 85.7 | 160.8 | 113.7 |
| messages page | 1 | 7,581 | 4,227 | 1.793x | 6,981 | **1.086x** | 125.1 | 449.3 | 135.7 |
| search | 16 | 32,826 | 20,026 | 1.639x | 26,606 | **1.234x** | 102.2 | 183.8 | 124.2 |
| search | 1 | 5,880 | 3,990 | 1.474x | 5,638 | **1.043x** | 175.0 | 467.5 | 157.9 |

Checks:
  responses: every one 2xx with 0 connection errors in 4 runs
  decoded bytes equal Rust's on every probed route; post rows written equal the 2xx answers in every phase
