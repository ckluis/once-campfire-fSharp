F# in bench/results/phase7-runs/7.3-04-both-mounted-c1 against the stored Rust baseline (bench/results/phase7-start, medians of 3 Rust reps) and the F# starting point

| Workload | c | F# req/s | F# start | vs start | Rust stored | **F# / Rust (vs stored Rust baseline)** | F# CPU us/req | start | Rust stored |
|---|---|---|---|---|---|---|---|---|---|
| room page | 1 | 7,405 | 3,885 | 1.906x | 6,500 | **1.139x** | 213.3 | 453.2 | 141.6 |
| sidebar | 1 | 7,612 | 3,837 | 1.984x | 6,366 | **1.196x** | 192.5 | 442.7 | 145.8 |
| search | 1 | 5,820 | 3,990 | 1.459x | 5,638 | **1.032x** | 326.6 | 467.5 | 157.9 |

Checks:
  responses: every one 2xx with 0 connection errors in 3 runs
  decoded bytes equal Rust's on every probed route; post rows written equal the 2xx answers in every phase
