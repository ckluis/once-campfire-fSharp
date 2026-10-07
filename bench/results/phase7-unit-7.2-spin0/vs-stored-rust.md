F# in bench/results/phase7-unit-7.2 against the stored Rust baseline (bench/results/phase7-start, medians of 3 Rust reps) and the F# starting point

| Workload | c | F# req/s | F# start | vs start | Rust stored | **F# / Rust (vs stored Rust baseline)** | F# CPU us/req | start | Rust stored |
|---|---|---|---|---|---|---|---|---|---|
| room page | 16 | 28,416 [27,971-28,468] | 21,892 | 1.298x | 28,720 | **0.989x** | 119.7 [119-120] | 173.0 | 121.6 |
| room page | 1 | 5,474 [5,019-5,585] | 3,885 | 1.409x | 6,500 | **0.842x** | 161.8 [161-183] | 453.2 | 141.6 |
| messages page | 16 | 30,606 [28,500-30,789] | 23,346 | 1.311x | 30,230 | **1.012x** | 109.4 [108-116] | 160.8 | 113.7 |
| messages page | 1 | 6,013 [6,012-6,071] | 4,227 | 1.422x | 6,981 | **0.861x** | 148.0 [146-148] | 449.3 | 135.7 |
| sidebar | 16 | 26,808 [26,784-26,980] | 18,888 | 1.419x | 28,378 | **0.945x** | 128.9 [128-129] | 199.5 | 122.8 |
| sidebar | 1 | 5,148 [5,125-5,242] | 3,837 | 1.342x | 6,366 | **0.809x** | 177.1 [174-178] | 442.7 | 145.8 |
| search | 16 | 26,078 [25,881-26,311] | 20,026 | 1.302x | 26,606 | **0.980x** | 126.1 [125-128] | 183.8 | 124.2 |
| search | 1 | 4,721 [4,673-4,785] | 3,990 | 1.183x | 5,638 | **0.837x** | 191.2 [190-194] | 467.5 | 157.9 |
| post a message | 16 | 7,323 [7,249-7,335] | 6,046 | 1.211x | 7,140 | **1.026x** | 411.0 [407-414] | 548.9 | 389.4 |
| post a message | 1 | 2,322 [2,275-2,389] | 1,958 | 1.186x | 2,814 | **0.825x** | 515.1 [510-527] | 997.8 | 399.2 |

Checks:
  note messages_page c=16: warm-up did not settle (reached the maximum)
  note room_show c=16: warm-up did not settle (reached the maximum)
  note room_show c=16: warm-up did not settle (reached the maximum)
  note sidebar c=16: warm-up did not settle (reached the maximum)
  note sidebar c=16: warm-up did not settle (reached the maximum)
  responses: every one 2xx with 0 connection errors in 30 runs
  decoded bytes equal Rust's on every probed route; post rows written equal the 2xx answers in every phase
