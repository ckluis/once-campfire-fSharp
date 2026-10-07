F# in bench/results/phase7-runs/7.1-02-direct-natives against the stored Rust baseline (bench/results/phase7-start, medians of 3 Rust reps) and the F# starting point

| Workload | c | F# req/s | F# start | vs start | Rust stored | **F# / Rust (vs stored Rust baseline)** | F# CPU us/req | start | Rust stored |
|---|---|---|---|---|---|---|---|---|---|
| room page | 16 | 25,851 | 21,892 | 1.181x | 28,720 | **0.900x** | 140.9 | 173.0 | 121.6 |
| messages page | 16 | 28,704 | 23,346 | 1.230x | 30,230 | **0.950x** | 125.8 | 160.8 | 113.7 |
| sidebar | 16 | 24,993 | 18,888 | 1.323x | 28,378 | **0.881x** | 150.6 | 199.5 | 122.8 |
| search | 16 | 26,034 | 20,026 | 1.300x | 26,606 | **0.979x** | 145.5 | 183.8 | 124.2 |

Checks:
  responses: every one 2xx with 0 connection errors in 4 runs
  decoded bytes equal Rust's on every probed route; post rows written equal the 2xx answers in every phase
