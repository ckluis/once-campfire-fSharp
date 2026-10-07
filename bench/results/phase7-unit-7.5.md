# Unit 7.5: the thread pool's hill climbing (2026-10-07)

**Kept: `DOTNET_HillClimbing_Disable=1` in the image (commit 59ee9ae; tier 3 on image `sha256:421f335c3ff8`, label 2e6c08e, `src/` unchanged
since a441ef2).** F# only, 3 reps, `bench/quick` (five workloads, c=1 and c=16), against phase7-final's stored F# and Rust medians. Every response
2xx, 0 connection errors, decoded bytes equal Rust's, rows written equal the 2xx answers. Raw data and validity tables: [`phase7-unit-7.5/tier3/`](phase7-unit-7.5/tier3/);
the table is [`phase7-unit-7.5/tier3/../vs-final.md`](phase7-unit-7.5/vs-final.md) (`vs-final.py`).

| Workload | c | Rust req/s / CPU µs / p99 ms | F# phase7-final | **F# 7.5** | 7.5 / final: req/s, CPU, p99 | F# / Rust req/s |
|---|---|---|---|---|---|---|
| room page | 1 | 6,481 / 142.1 / 0.18 | 7,617 / 208.7 / 0.17 | 7,391 / 209.4 / 0.18 | 0.97, 1.00, 1.02 | 1.14x |
| room page | 16 | 28,623 / 122.4 / 1.07 | 40,422 / 93.6 / 1.64 (steady 38,000 / 100) | 39,144 / 91.0 / **1.08** | 0.97 (1.03 vs steady), 0.97, **0.66** | 1.37x |
| messages page | 1 | 6,822 / 139.2 / 0.18 | 8,312 / 199.3 / 0.17 | 8,115 / 201.7 / 0.17 | 0.98, 1.01, 1.01 | 1.19x |
| messages page | 16 | 30,063 / 114.2 / 0.99 | 37,836 / 102.2 / 3.19 | 42,144 / 84.4 / **1.01** | 1.11, 0.83, **0.32** | 1.40x |
| sidebar | 1 | 6,196 / 149.0 / 0.19 | 8,000 / 181.0 / 0.18 | 7,752 / 184.9 / 0.18 | 0.97, 1.02, 1.02 | 1.25x |
| sidebar | 16 | 28,238 / 123.2 / 1.11 | 37,555 / 101.8 / 1.75 | 37,814 / 95.4 / **1.06** | 1.01, 0.94, **0.61** | 1.34x |
| search | 1 | 5,646 / 158.3 / 0.21 | 6,055 / 315.1 / 0.21 | 5,841 / 326.3 / 0.21 | 0.97, 1.04, 1.03 | 1.03x |
| search | 16 | 26,606 / 123.9 / 1.11 | 34,070 / 107.3 / 1.36 | 34,002 / 107.5 / 1.30 | 1.00, 1.00, 0.96 | 1.28x |
| post a message | 1 | 2,779 / 403.7 / 0.53 | 3,052 / 629.1 / 0.56 | 2,976 / 644.6 / 0.56 | 0.98, 1.02, 0.98 | 1.07x |
| post a message | 16 | 7,174 / 385.6 / 6.04 | 8,905 / 353.0 / 5.99 | 8,738 / 357.5 / 5.81 | 0.98, 1.01, 0.97 | 1.22x |

- **c=16 p99 on the read pages fell to Rust's level**: room 1.64 → 1.08 ms (Rust 1.07), messages 3.19 → 1.01 (Rust 0.99), sidebar 1.75 → 1.06
  (Rust 1.11). Search and post did not move beyond noise. req/s at c=16 is level or better (messages +11% at 17% less CPU per request).
- **The thread count no longer climbs**: the 120 s room page curve (`curve-control.txt` against `curve-hcdisable*.txt`, `curve-env.sh`) holds
  30-31 threads instead of 39 → 44-45. Its req/s is noisy (one run stepped from 40,000 to 36,000 at 70 s with the thread count flat, so not all of
  the 80 s plateau was thread injection); its p99 is 1.0-1.2 ms against 1.6 throughout.
- **c=1 is 2-3.5% below phase7-final on all five, and that is the day, not the change**: a same-day control with hill climbing back on
  (`t2-c1-control`, `DOTNET_HillClimbing_Disable=0`) reads the same (room 7,446, messages 7,952, sidebar 7,662, search 5,800, post 2,976).
  Search c=1 is now 1.03x Rust: still ahead, but inside 10%.

Measured and not kept:
- Spinning at c=1 (target 2): `DOTNET_ThreadPool_UnfairSemaphoreSpinLimit=10` (with hill climbing off) cut c=1 CPU per request 36-43% (messages
  194 → 125 µs, below Rust's 139; search 309 → 175) and no longer cost c=16, but cost c=1 req/s (messages −10%, search −3%); `=30` cut little CPU and
  took search c=1 to 5,315 (0.94x Rust). One rep each (`t2-spin10`, `t2-spin30`). Not kept: it trades the one-connection lead for CPU.
- GC settings for p99 (target 3) were not tried: hill climbing turned out to be most of the c=16 tail, and the time box went to the tier 3.

Not re-run: `bin/verify` and the differentials. The change is one environment variable in the Dockerfile; `src/` is unchanged since a441ef2, and
the tier 3 checked statuses, decoded bytes and rows written on every run.
