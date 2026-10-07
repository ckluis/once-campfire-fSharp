F# in bench/results/phase7-unit-7.5/t2-hcdisable against the stored Rust baseline (bench/results/phase7-start, medians of 3 Rust reps) and the F# starting point

| Workload | c | F# req/s | F# start | vs start | Rust stored | **F# / Rust (vs stored Rust baseline)** | F# CPU us/req | start | Rust stored |
|---|---|---|---|---|---|---|---|---|---|
| room page | 16 | 36,102 | 21,892 | 1.649x | 28,720 | **1.257x** | 98.8 | 173.0 | 121.6 |
| room page | 1 | 7,715 | 3,885 | 1.986x | 6,500 | **1.187x** | 208.9 | 453.2 | 141.6 |
| messages page | 16 | 38,621 | 23,346 | 1.654x | 30,230 | **1.278x** | 92.5 | 160.8 | 113.7 |
| messages page | 1 | 8,392 | 4,227 | 1.985x | 6,981 | **1.202x** | 194.4 | 449.3 | 135.7 |
| sidebar | 16 | 36,570 | 18,888 | 1.936x | 28,378 | **1.289x** | 98.7 | 199.5 | 122.8 |
| sidebar | 1 | 7,828 | 3,837 | 2.040x | 6,366 | **1.229x** | 182.0 | 442.7 | 145.8 |
| search | 16 | 31,837 | 20,026 | 1.590x | 26,606 | **1.197x** | 114.7 | 183.8 | 124.2 |
| search | 1 | 6,073 | 3,990 | 1.522x | 5,638 | **1.077x** | 308.8 | 467.5 | 157.9 |
| post a message | 16 | 8,651 | 6,046 | 1.431x | 7,140 | **1.212x** | 361.2 | 548.9 | 389.4 |
| post a message | 1 | 2,907 | 1,958 | 1.484x | 2,814 | **1.033x** | 638.3 | 997.8 | 399.2 |

Checks:
  responses: every one 2xx with 0 connection errors in 10 runs
  decoded bytes equal Rust's on every probed route; post rows written equal the 2xx answers in every phase
