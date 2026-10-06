F# in bench/results/phase7-runs/7.1-00-mounted-harness against the stored Rust baseline (bench/results/phase7-start, medians of 3 Rust reps) and the F# starting point

| Workload | c | F# req/s | F# start | vs start | Rust stored | **F# / Rust (vs stored Rust baseline)** | F# CPU us/req | start | Rust stored |
|---|---|---|---|---|---|---|---|---|---|
| room page | 16 | 18,437 | 21,892 | 0.842x | 28,720 | **0.642x** | 201.0 | 173.0 | 121.6 |
| messages page | 16 | 21,483 | 23,346 | 0.920x | 30,230 | **0.711x** | 172.4 | 160.8 | 113.7 |
| sidebar | 16 | 17,611 | 18,888 | 0.932x | 28,378 | **0.621x** | 213.0 | 199.5 | 122.8 |
| search | 16 | 18,407 | 20,026 | 0.919x | 26,606 | **0.692x** | 197.9 | 183.8 | 124.2 |
| post a message | 16 | 3,568 | 6,046 | 0.590x | 7,140 | **0.500x** | 578.7 | 548.9 | 389.4 |

Checks:
  note post_message c=16: warm-up did not settle (reached the maximum)
  responses: every one 2xx with 0 connection errors in 5 runs
  decoded bytes equal Rust's on every probed route; post rows written equal the 2xx answers in every phase
