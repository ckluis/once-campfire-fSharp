F# in bench/results/phase7-runs/7.1-00-mounted-c1-c16 against the stored Rust baseline (bench/results/phase7-start, medians of 3 Rust reps) and the F# starting point

| Workload | c | F# req/s | F# start | vs start | Rust stored | **F# / Rust (vs stored Rust baseline)** | F# CPU us/req | start | Rust stored |
|---|---|---|---|---|---|---|---|---|---|
| room page | 16 | 20,336 | 21,892 | 0.929x | 28,720 | **0.708x** | 182.0 | 173.0 | 121.6 |
| room page | 1 | 3,740 | 3,885 | 0.963x | 6,500 | **0.575x** | 464.8 | 453.2 | 141.6 |
| messages page | 16 | 22,083 | 23,346 | 0.946x | 30,230 | **0.730x** | 167.7 | 160.8 | 113.7 |
| messages page | 1 | 3,877 | 4,227 | 0.917x | 6,981 | **0.555x** | 477.5 | 449.3 | 135.7 |
| sidebar | 16 | 17,564 | 18,888 | 0.930x | 28,378 | **0.619x** | 212.7 | 199.5 | 122.8 |
| sidebar | 1 | 3,636 | 3,837 | 0.948x | 6,366 | **0.571x** | 462.7 | 442.7 | 145.8 |

Checks:
  responses: every one 2xx with 0 connection errors in 6 runs
  decoded bytes equal Rust's on every probed route; post rows written equal the 2xx answers in every phase
