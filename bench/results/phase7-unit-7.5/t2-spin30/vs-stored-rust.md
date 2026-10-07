F# in bench/results/phase7-unit-7.5/t2-spin30 against the stored Rust baseline (bench/results/phase7-start, medians of 3 Rust reps) and the F# starting point

| Workload | c | F# req/s | F# start | vs start | Rust stored | **F# / Rust (vs stored Rust baseline)** | F# CPU us/req | start | Rust stored |
|---|---|---|---|---|---|---|---|---|---|
| messages page | 16 | 41,287 | 23,346 | 1.769x | 30,230 | **1.366x** | 84.8 | 160.8 | 113.7 |
| messages page | 1 | 7,423 | 4,227 | 1.756x | 6,981 | **1.063x** | 177.9 | 449.3 | 135.7 |
| search | 16 | 33,593 | 20,026 | 1.677x | 26,606 | **1.263x** | 107.5 | 183.8 | 124.2 |
| search | 1 | 5,315 | 3,990 | 1.332x | 5,638 | **0.943x** | 282.5 | 467.5 | 157.9 |
| post a message | 16 | 8,834 | 6,046 | 1.461x | 7,140 | **1.237x** | 348.7 | 548.9 | 389.4 |
| post a message | 1 | 2,897 | 1,958 | 1.479x | 2,814 | **1.029x** | 574.3 | 997.8 | 399.2 |

Checks:
  responses: every one 2xx with 0 connection errors in 6 runs
  decoded bytes equal Rust's on every probed route; post rows written equal the 2xx answers in every phase
