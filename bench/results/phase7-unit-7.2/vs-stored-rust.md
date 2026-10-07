F# in bench/results/phase7-unit-7.2 against the stored Rust baseline (bench/results/phase7-start, medians of 3 Rust reps) and the F# starting point

| Workload | c | F# req/s | F# start | vs start | Rust stored | **F# / Rust (vs stored Rust baseline)** | F# CPU us/req | start | Rust stored |
|---|---|---|---|---|---|---|---|---|---|
| room page | 16 | 30,672 [30,638-31,019] | 21,892 | 1.401x | 28,720 | **1.068x** | 123.7 [121-124] | 173.0 | 121.6 |
| room page | 1 | 5,654 [5,521-5,698] | 3,885 | 1.455x | 6,500 | **0.870x** | 252.4 [252-258] | 453.2 | 141.6 |
| messages page | 16 | 33,229 [33,172-33,248] | 23,346 | 1.423x | 30,230 | **1.099x** | 115.8 [116-116] | 160.8 | 113.7 |
| messages page | 1 | 6,293 [6,232-6,368] | 4,227 | 1.489x | 6,981 | **0.901x** | 229.4 [226-232] | 449.3 | 135.7 |
| sidebar | 16 | 28,192 [27,914-29,208] | 18,888 | 1.493x | 28,378 | **0.993x** | 135.4 [130-136] | 199.5 | 122.8 |
| sidebar | 1 | 5,336 [5,214-5,397] | 3,837 | 1.391x | 6,366 | **0.838x** | 265.7 [262-270] | 442.7 | 145.8 |
| search | 16 | 28,713 [28,032-29,098] | 20,026 | 1.434x | 26,606 | **1.079x** | 128.5 [126-129] | 183.8 | 124.2 |
| search | 1 | 4,695 [4,397-4,829] | 3,990 | 1.177x | 5,638 | **0.833x** | 374.7 [371-384] | 467.5 | 157.9 |
| post a message | 16 | 7,186 [7,154-7,508] | 6,046 | 1.189x | 7,140 | **1.006x** | 441.1 [430-446] | 548.9 | 389.4 |
| post a message | 1 | 2,275 [2,202-2,401] | 1,958 | 1.162x | 2,814 | **0.808x** | 809.0 [782-812] | 997.8 | 399.2 |

Checks:
  note messages_page c=16: warm-up did not settle (reached the maximum)
  note messages_page c=16: warm-up did not settle (reached the maximum)
  note messages_page c=16: warm-up did not settle (reached the maximum)
  note post_message c=1: warm-up did not settle (reached the maximum)
  note room_show c=16: warm-up did not settle (reached the maximum)
  note sidebar c=16: warm-up did not settle (reached the maximum)
  responses: every one 2xx with 0 connection errors in 30 runs
  decoded bytes equal Rust's on every probed route; post rows written equal the 2xx answers in every phase
