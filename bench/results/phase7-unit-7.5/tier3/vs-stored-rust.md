F# in bench/results/phase7-unit-7.5/tier3 against the stored Rust baseline (bench/results/phase7-start, medians of 3 Rust reps) and the F# starting point

| Workload | c | F# req/s | F# start | vs start | Rust stored | **F# / Rust (vs stored Rust baseline)** | F# CPU us/req | start | Rust stored |
|---|---|---|---|---|---|---|---|---|---|
| room page | 16 | 39,144 [39,112-39,172] | 21,892 | 1.788x | 28,720 | **1.363x** | 91.0 [91-91] | 173.0 | 121.6 |
| room page | 1 | 7,391 [7,337-7,518] | 3,885 | 1.903x | 6,500 | **1.137x** | 209.4 [207-211] | 453.2 | 141.6 |
| messages page | 16 | 42,144 [41,883-42,350] | 23,346 | 1.805x | 30,230 | **1.394x** | 84.4 [84-85] | 160.8 | 113.7 |
| messages page | 1 | 8,115 [8,107-8,120] | 4,227 | 1.920x | 6,981 | **1.162x** | 201.7 [201-202] | 449.3 | 135.7 |
| sidebar | 16 | 37,814 [37,803-37,993] | 18,888 | 2.002x | 28,378 | **1.333x** | 95.4 [95-96] | 199.5 | 122.8 |
| sidebar | 1 | 7,752 [7,688-7,836] | 3,837 | 2.021x | 6,366 | **1.218x** | 184.9 [182-188] | 442.7 | 145.8 |
| search | 16 | 34,002 [33,906-34,263] | 20,026 | 1.698x | 26,606 | **1.278x** | 107.5 [107-108] | 183.8 | 124.2 |
| search | 1 | 5,841 [5,739-6,144] | 3,990 | 1.464x | 5,638 | **1.036x** | 326.3 [316-330] | 467.5 | 157.9 |
| post a message | 16 | 8,738 [8,676-8,860] | 6,046 | 1.445x | 7,140 | **1.224x** | 357.5 [356-361] | 548.9 | 389.4 |
| post a message | 1 | 2,976 [2,956-3,066] | 1,958 | 1.520x | 2,814 | **1.057x** | 644.6 [634-649] | 997.8 | 399.2 |

Checks:
  responses: every one 2xx with 0 connection errors in 30 runs
  decoded bytes equal Rust's on every probed route; post rows written equal the 2xx answers in every phase
