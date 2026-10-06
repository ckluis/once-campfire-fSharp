F# in bench/results/phase7-unit-7.1 against the stored Rust baseline (bench/results/phase7-start, medians of 3 Rust reps) and the F# starting point

| Workload | c | F# req/s | F# start | vs start | Rust stored | **F# / Rust (vs stored Rust baseline)** | F# CPU us/req | start | Rust stored |
|---|---|---|---|---|---|---|---|---|---|
| room page | 16 | 28,328 [27,778-28,640] | 21,892 | 1.294x | 28,720 | **0.986x** | 135.8 [135-138] | 173.0 | 121.6 |
| room page | 1 | 5,589 [5,503-5,707] | 3,885 | 1.439x | 6,500 | **0.860x** | 258.1 [256-266] | 453.2 | 141.6 |
| messages page | 16 | 31,066 [30,858-31,476] | 23,346 | 1.331x | 30,230 | **1.028x** | 124.0 [123-126] | 160.8 | 113.7 |
| messages page | 1 | 6,096 [5,889-6,101] | 4,227 | 1.442x | 6,981 | **0.873x** | 243.1 [242-252] | 449.3 | 135.7 |
| sidebar | 16 | 27,356 [26,739-27,691] | 18,888 | 1.448x | 28,378 | **0.964x** | 142.1 [140-144] | 199.5 | 122.8 |
| sidebar | 1 | 5,177 [5,096-5,258] | 3,837 | 1.349x | 6,366 | **0.813x** | 277.6 [274-278] | 442.7 | 145.8 |
| search | 16 | 27,801 [27,110-27,933] | 20,026 | 1.388x | 26,606 | **1.045x** | 134.2 [134-138] | 183.8 | 124.2 |
| search | 1 | 4,855 [4,551-4,955] | 3,990 | 1.217x | 5,638 | **0.861x** | 363.6 [360-376] | 467.5 | 157.9 |
| post a message | 16 | 7,004 [5,496-7,290] | 6,046 | 1.158x | 7,140 | **0.981x** | 452.1 [441-462] | 548.9 | 389.4 |
| post a message | 1 | 2,328 [2,154-2,370] | 1,958 | 1.189x | 2,814 | **0.827x** | 783.3 [783-829] | 997.8 | 399.2 |

Checks:
  note messages_page c=16: warm-up did not settle (reached the maximum)
  note post_message c=1: warm-up did not settle (reached the maximum)
  responses: every one 2xx with 0 connection errors in 30 runs
  decoded bytes equal Rust's on every probed route; post rows written equal the 2xx answers in every phase
