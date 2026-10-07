F# in bench/results/phase7-unit-7.5/t2-control-c16 against the stored Rust baseline (bench/results/phase7-start, medians of 3 Rust reps) and the F# starting point

| Workload | c | F# req/s | F# start | vs start | Rust stored | **F# / Rust (vs stored Rust baseline)** | F# CPU us/req | start | Rust stored |
|---|---|---|---|---|---|---|---|---|---|
| room page | 16 | 39,848 | 21,892 | 1.820x | 28,720 | **1.387x** | 95.1 | 173.0 | 121.6 |
| search | 16 | 33,282 | 20,026 | 1.662x | 26,606 | **1.251x** | 114.1 | 183.8 | 124.2 |

Checks:
  responses: every one 2xx with 0 connection errors in 2 runs
  decoded bytes equal Rust's on every probed route; post rows written equal the 2xx answers in every phase
