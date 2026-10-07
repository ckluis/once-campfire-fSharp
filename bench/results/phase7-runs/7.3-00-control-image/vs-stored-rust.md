F# in bench/results/phase7-runs/7.3-00-control-image against the stored Rust baseline (bench/results/phase7-start, medians of 3 Rust reps) and the F# starting point

| Workload | c | F# req/s | F# start | vs start | Rust stored | **F# / Rust (vs stored Rust baseline)** | F# CPU us/req | start | Rust stored |
|---|---|---|---|---|---|---|---|---|---|
| room page | 16 | 30,626 | 21,892 | 1.399x | 28,720 | **1.066x** | 122.0 | 173.0 | 121.6 |
| sidebar | 16 | 28,195 | 18,888 | 1.493x | 28,378 | **0.994x** | 134.8 | 199.5 | 122.8 |
| search | 16 | 29,478 | 20,026 | 1.472x | 26,606 | **1.108x** | 130.9 | 183.8 | 124.2 |

Checks:
  responses: every one 2xx with 0 connection errors in 3 runs
  decoded bytes equal Rust's on every probed route; post rows written equal the 2xx answers in every phase
