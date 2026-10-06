# Phase 7, unit 7.0: how long F# takes to warm up, and what that did to the numbers

The baseline review found the first measured window of an F# run 13-20% slow: `bench/run` warmed each route once, for 2 s at c=4,
then measured c=1, 16 and 64 for 8 s each. This is the measurement behind the warm-up bench/run uses now (`bench/lib/warmup.py`,
`WARMUP_SECS=30`, `WARMUP_MAX_SECS=90`) and what it changed.

## The curves

`bench/warmup` starts a fresh container for each (route, concurrency) and runs the load generator in back-to-back 2 s windows. Raw
data: [`phase7-warmup/`](phase7-warmup/) (`curves/` F#, `rust-curves/`, `fsharp-curves-quiet/`; one JSON per curve with every window's
requests a second). "First" is the first window over the plateau (the median of the last quarter of the windows); "0.9x" and "0.97x"
are the seconds until the throughput is at that fraction of the plateau and stays there for three windows.

| App | Route | c | First window | 0.9x at | 0.97x at |
|---|---|---|---|---|---|
| F# | room page | 16 | 0.44 | 8 s | 8 s |
| F# | messages page | 16 | 0.48 | 6 s | 8 s |
| F# | sidebar | 16 | 0.52 | 6 s | 6 s |
| F# | search | 16 | 0.54 | 6 s | 12 s |
| F# | post a message | 16 | 0.75 | 2 s | 4 s |
| F# | room page | 1 | 0.48 | 8 s | 8 s |
| F# | messages page | 1 | 0.47 | 6 s | 8 s |
| F# | sidebar | 1 | 0.52 | 4 s | 8 s |
| F# | search | 1 | 0.33 | 4 s | 14 s |
| F# | post a message | 1 | 0.54 | 8 s | 14 s |
| F# (quiet host, 25 windows) | messages page | 16 | 0.46 | 8 s | 32 s |
| F# (quiet host, 25 windows) | sidebar | 16 | 0.51 | 6 s | 10 s |
| Rust | room page, sidebar, post | 1 and 16 | 0.96-1.03 | 0 s | 0-2 s (post c=16 has 3 windows at 0.80-0.82 at 8-12 s, then 1.0) |

Three readings. F# is at 0.9x of its plateau after 6-8 s of load and Rust is there from the first window. On a page route at c=16
F# then sits near 0.93-0.95x for 10-25 s and reaches 1.0x only at 25-45 s (messages page: 0.91-0.95 from 10 s to 24 s, 1.00 at
32 s): the tier-1 recompilation runs on a background thread that competes with sixteen busy request threads for four CPUs, so
the last promotions come late. At c=1, on an idle box, it is done in 8-14 s. And two long curves (`*-long-a`, `*-long-b`, 45
windows, the messages page ramping from 0.39x to 0.9x in 40 s) were taken while another program was running its tests on the
Mac, which slows the pinned vCPUs; windows then swing between 0.62x and 1.07x. That is host noise, not the JIT, and why
`bench/lib/hostprobe.py` exists (see `phase7-start.md`).

## The rule

A fixed time cannot be right for both: Rust needs none, F# 8 s on a good run and 40 s when it is slow. `bench/lib/warmup.py` runs
2 s windows at the measured concurrency for at least 30 s (the rule needs six windows to see anything, and 30 s covers the creep
above), then until the mean of the last three windows is within 3% of the mean of the three before and at least 97% of the best
three so far, at most 90 s (the run is then flagged unsettled). The rule is the same for every app. The result of every run
records its warm-up seconds, window rates and verdict, and `bench/report` prints them with the ratio of the measured run to the
last three warm-up windows.

In the `phase7-start` matrix (10 route-concentration cells, 3 reps, so 30 warm-ups per app) every warm-up ended settled; 27 of
30 F# and 28 of 30 Rust warm-ups stopped at the 30 s minimum. The longest were F# 76 s (messages page c=1, rep 3), 38 s (room page
c=16, rep 3) and 32 s; Rust 48 s (post c=16, rep 3) and 44 s (search c=16, rep 3). The measured run over the mean of the last
three warm-up windows is 1.00 in every cell but F# search c=1 (1.02), F# room page and sidebar c=1 (1.01) and Rust post c=16
(0.98): the app was flat when measurement began.

## What it changed

F# with the old warm-up (2 s at c=4, one rep, `phase7-warmup/old-2s/`) against the new rule (median of 3, `phase7-start/`), req/s:

| Route | c=1 old | c=1 new | c=16 old | c=16 new |
|---|---|---|---|---|
| room page | 3,104 | 3,885 | 20,613 | 21,892 |
| messages page | 4,122 | 4,227 | 21,837 | 23,346 |
| sidebar | 3,691 | 3,837 | 18,572 | 18,888 |
| search | 3,861 | 3,990 | 18,885 | 20,026 |
| post a message | 1,867 | 1,958 | 5,669 | 6,046 |

The old method read 2-20% low (the first route measured, the room page at c=1, worst: its window contained the JIT's whole ramp).
Fixed warm-ups of 15 s and 45 s on the same routes (`fixed-15s/`, `fixed-45s/`, one rep each, taken during the noisy
period) agree with the new numbers within the noise of that period and are kept only as raw data.

It also moved Rust, and not in the direction one would guess. Post a message at c=16 is 7,140 req/s [7,050-7,151] with the new
warm-up and was 6,082 [5,969-6,259] in the baseline; an A/B in this unit on one image gives 7,121 with a 30 s warm-up against 6,333
and 6,605 with a 2 s one (two runs of each, alternating; the second 30 s run landed in a noisy period, 4,086 req/s after a 91 s warm-up that never settled, and is not counted). F# posts 6,046 [6,001-6,120] now and 6,186 [6,034-6,264]
in the baseline, within noise, and 5,669 against 6,046 old against new in the single-rep comparison above. So the ratio on
posting went from 1.02x to 0.85x without either app changing: the baseline's Rust figure was a short-warm-up figure. Rust's own
curve shows why (post c=16 above: three windows at 0.80-0.82x around 8-12 s, then 1.0x): the write path has a settling period of
its own, and a 2 s warm-up measured Rust inside it. I did not chase the SQLite mechanism (a fresh WAL file and the first
checkpoints are the candidates); what is established is that the 30 s figure repeats (7,050-7,151 over three reps, 7,121 in the
A/B) and the 2 s one does not (6,333 against 6,605).
