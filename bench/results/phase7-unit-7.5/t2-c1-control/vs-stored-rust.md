F# in bench/results/phase7-unit-7.5/t2-c1-control against the stored Rust baseline (bench/results/phase7-start, medians of 3 Rust reps) and the F# starting point

| Workload | c | F# req/s | F# start | vs start | Rust stored | **F# / Rust (vs stored Rust baseline)** | F# CPU us/req | start | Rust stored |
|---|---|---|---|---|---|---|---|---|---|
| room page | 1 | 7,446 | 3,885 | 1.917x | 6,500 | **1.146x** | 207.3 | 453.2 | 141.6 |
| messages page | 1 | 7,952 | 4,227 | 1.881x | 6,981 | **1.139x** | 203.5 | 449.3 | 135.7 |
| sidebar | 1 | 7,662 | 3,837 | 1.997x | 6,366 | **1.204x** | 191.8 | 442.7 | 145.8 |
| search | 1 | 5,800 | 3,990 | 1.454x | 5,638 | **1.029x** | 325.2 | 467.5 | 157.9 |
| post a message | 1 | 2,976 | 1,958 | 1.519x | 2,814 | **1.057x** | 642.6 | 997.8 | 399.2 |

Checks:
  responses: every one 2xx with 0 connection errors in 5 runs
  decoded bytes equal Rust's on every probed route; post rows written equal the 2xx answers in every phase
