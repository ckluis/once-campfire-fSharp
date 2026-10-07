F# in bench/results/phase7-unit-7.3 against the stored Rust baseline (bench/results/phase7-start, medians of 3 Rust reps) and the F# starting point

| Workload | c | F# req/s | F# start | vs start | Rust stored | **F# / Rust (vs stored Rust baseline)** | F# CPU us/req | start | Rust stored |
|---|---|---|---|---|---|---|---|---|---|
| room page | 16 | 38,606 [38,027-39,915] | 21,892 | 1.763x | 28,720 | **1.344x** | 97.5 [95-100] | 173.0 | 121.6 |
| room page | 1 | 7,524 [7,473-7,529] | 3,885 | 1.937x | 6,500 | **1.158x** | 209.7 [209-210] | 453.2 | 141.6 |
| messages page | 16 | 37,933 [36,937-38,006] | 23,346 | 1.625x | 30,230 | **1.255x** | 101.5 [101-104] | 160.8 | 113.7 |
| messages page | 1 | 8,112 [8,035-8,154] | 4,227 | 1.919x | 6,981 | **1.162x** | 199.5 [199-200] | 449.3 | 135.7 |
| sidebar | 16 | 36,659 [36,588-37,228] | 18,888 | 1.941x | 28,378 | **1.292x** | 104.2 [103-105] | 199.5 | 122.8 |
| sidebar | 1 | 7,690 [7,613-7,728] | 3,837 | 2.004x | 6,366 | **1.208x** | 187.5 [186-192] | 442.7 | 145.8 |
| search | 16 | 33,798 [33,717-33,950] | 20,026 | 1.688x | 26,606 | **1.270x** | 108.0 [108-109] | 183.8 | 124.2 |
| search | 1 | 5,837 [5,796-5,993] | 3,990 | 1.463x | 5,638 | **1.035x** | 321.5 [319-324] | 467.5 | 157.9 |
| post a message | 16 | 8,108 [7,861-8,124] | 6,046 | 1.341x | 7,140 | **1.135x** | 396.5 [394-399] | 548.9 | 389.4 |
| post a message | 1 | 2,727 [2,420-2,766] | 1,958 | 1.392x | 2,814 | **0.969x** | 697.0 [694-744] | 997.8 | 399.2 |

Checks:
  note messages_page c=16: warm-up did not settle (reached the maximum)
  note messages_page c=16: warm-up did not settle (reached the maximum)
  note messages_page c=16: warm-up did not settle (reached the maximum)
  note post_message c=1: warm-up did not settle (reached the maximum)
  note room_show c=16: warm-up did not settle (reached the maximum)
  note sidebar c=16: warm-up did not settle (reached the maximum)
  note sidebar c=16: warm-up did not settle (reached the maximum)
  note sidebar c=16: warm-up did not settle (reached the maximum)
  responses: every one 2xx with 0 connection errors in 30 runs
  decoded bytes equal Rust's on every probed route; post rows written equal the 2xx answers in every phase
