F# in bench/results/phase7-unit-7.4/tier3 against the stored Rust baseline (bench/results/phase7-start, medians of 3 Rust reps) and the F# starting point

| Workload | c | F# req/s | F# start | vs start | Rust stored | **F# / Rust (vs stored Rust baseline)** | F# CPU us/req | start | Rust stored |
|---|---|---|---|---|---|---|---|---|---|
| room page | 16 | 37,726 [37,233-39,136] | 21,892 | 1.723x | 28,720 | **1.314x** | 101.4 [98-103] | 173.0 | 121.6 |
| room page | 1 | 7,462 [7,419-7,608] | 3,885 | 1.921x | 6,500 | **1.148x** | 210.6 [209-211] | 453.2 | 141.6 |
| messages page | 16 | 38,174 [37,556-38,237] | 23,346 | 1.635x | 30,230 | **1.263x** | 100.5 [100-103] | 160.8 | 113.7 |
| messages page | 1 | 8,238 [8,222-8,269] | 4,227 | 1.949x | 6,981 | **1.180x** | 199.3 [199-201] | 449.3 | 135.7 |
| sidebar | 16 | 37,900 [37,791-38,648] | 18,888 | 2.007x | 28,378 | **1.336x** | 100.8 [99-101] | 199.5 | 122.8 |
| sidebar | 1 | 7,822 [7,595-7,837] | 3,837 | 2.039x | 6,366 | **1.229x** | 185.8 [185-187] | 442.7 | 145.8 |
| search | 16 | 33,565 [33,406-33,685] | 20,026 | 1.676x | 26,606 | **1.262x** | 108.6 [108-109] | 183.8 | 124.2 |
| search | 1 | 5,814 [5,745-6,042] | 3,990 | 1.457x | 5,638 | **1.031x** | 324.9 [316-327] | 467.5 | 157.9 |
| post a message | 16 | 8,892 [8,751-8,997] | 6,046 | 1.471x | 7,140 | **1.245x** | 352.9 [350-353] | 548.9 | 389.4 |
| post a message | 1 | 3,060 [2,994-3,082] | 1,958 | 1.563x | 2,814 | **1.087x** | 631.0 [628-635] | 997.8 | 399.2 |

Checks:
  note messages_page c=16: warm-up did not settle (reached the maximum)
  note messages_page c=16: warm-up did not settle (reached the maximum)
  note messages_page c=16: warm-up did not settle (reached the maximum)
  note room_show c=16: warm-up did not settle (reached the maximum)
  note room_show c=16: warm-up did not settle (reached the maximum)
  note room_show c=16: warm-up did not settle (reached the maximum)
  note search c=1: warm-up did not settle (reached the maximum)
  note search c=1: warm-up did not settle (reached the maximum)
  note sidebar c=16: warm-up did not settle (reached the maximum)
  note sidebar c=16: warm-up did not settle (reached the maximum)
  responses: every one 2xx with 0 connection errors in 30 runs
  decoded bytes equal Rust's on every probed route; post rows written equal the 2xx answers in every phase
