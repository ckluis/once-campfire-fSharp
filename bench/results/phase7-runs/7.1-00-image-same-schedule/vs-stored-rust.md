F# in bench/results/phase7-runs/7.1-00-image-same-schedule against the stored Rust baseline (bench/results/phase7-start, medians of 3 Rust reps) and the F# starting point

| Workload | c | F# req/s | F# start | vs start | Rust stored | **F# / Rust (vs stored Rust baseline)** | F# CPU us/req | start | Rust stored |
|---|---|---|---|---|---|---|---|---|---|
| room page | 16 | 20,737 | 21,892 | 0.947x | 28,720 | **0.722x** | 179.3 | 173.0 | 121.6 |
| messages page | 16 | 18,681 | 23,346 | 0.800x | 30,230 | **0.618x** | 194.1 | 160.8 | 113.7 |
| sidebar | 16 | 17,644 | 18,888 | 0.934x | 28,378 | **0.622x** | 211.9 | 199.5 | 122.8 |
| search | 16 | 18,520 | 20,026 | 0.925x | 26,606 | **0.696x** | 198.0 | 183.8 | 124.2 |
| post a message | 16 | 5,656 | 6,046 | 0.936x | 7,140 | **0.792x** | 579.5 | 548.9 | 389.4 |

Checks:
  note messages_page c=16: warm-up did not settle (reached the maximum)
  responses: every one 2xx with 0 connection errors in 5 runs
  decoded bytes equal Rust's on every probed route; post rows written equal the 2xx answers in every phase
