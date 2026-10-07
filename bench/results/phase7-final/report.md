```
date: 2026-10-06T21:58:23-04:00
host: 6.8.0-117-generic, , 8 threads, 5GB
server cpus: 0-3 (nproc 4); loadgen cpus: 4-7; network: host
env: WEB_CONCURRENCY=3 JOB_CONCURRENCY=3 RAILS_MAX_THREADS=5 
rust extra env: 
fsharp extra env: 
user agent: (none)
reference image: campfire-reference:app sha256:d2ca3b5d7a7f79e78164fb73024811dfaae06d416458c10379c1e9fff12fd00d 2026-10-05T12:04:17.590477719-04:00 built from commit: 
rust image: campfire-rust:app sha256:d582a01d3a4ae9c5fd4bc9cd7b0262e1a5d4440cc0058d200b50ad9b27e1365c 2026-10-05T23:48:02.375329666-04:00 built from commit: 
fsharp image: campfire-fsharp:app sha256:82ebcbc734ec1a793019ffc1800d303826456a79b016a45a791b3b96ebe4b0aa 2026-10-06T21:57:12.047578487-04:00 built from commit: c3e0ecab5a831df782788b7589f5c36b3cff6ce4
repo HEAD: c3e0eca (dirty: 0 files under src/ and rust/)
work dir (seed copies): /var/tmp/campfire-bench-work (ext4)
loadgen: /Users/clank/Desktop/projects/once-campfire-fsharp/target/rust/aarch64-unknown-linux-gnu/release/loadgen (2026-10-06 03:04:07), built with rust:1.98.1-trixie
schedule: apps reference,rust,fsharp, reps 3; suites: http; http secs 8, concs 1 16 64, routes room_show messages_page sidebar search avatar static_css up post_message (in parts, one container per part; cable and upload in the last part), warm-up 30s at each route and concentration, idle 10s
log retention: 2 x 10m (json-file)
```

Reps: reference 3, rust 3, fsharp 3. Cells: median [min–max].

### Startup and memory

| Metric | Rails | Rust | F# | Rust adv. | F# adv. |
|---|---|---|---|---|---|
| cold start: docker run → /up 200 (ms) | 2,127 [2,125–2,230] | 123 [118–191] | 253 [233–259] | 17.3× | 0.49× |
| idle memory.current (MB) | 303 [300–316] | 16.0 [16.0–24.0] | 28.0 [27.0–28.0] | 18.9× | 0.57× |
| idle anon (MB) | 283 [279–296] | 15.0 [14.0–15.0] | 15.0 [15.0–15.0] | 18.9× | 1.00× |
| peak memory.current under load (MB) | 1,475 [1,383–1,476] | 654 [545–657] | 811 [728–1,020] | 2.3× | 0.81× |
| peak anon under load (MB) | 1,435 [1,344–1,437] | 269 [261–276] | 717 [633–925] | 5.3× | 0.38× |

### HTTP (signed in as david; keep-alive; c = concurrent connections)

`CPU µs/req` is the container's CPU (cgroup cpu.stat, user + system, unprofiled) over the run divided by its requests; for CPU a ratio below 1 means the first app spends more per request.

| Metric | Rails | Rust | F# | Rust adv. | F# adv. |
|---|---|---|---|---|---|
| room_show c=1 req/s | 107 [103–111] | 6,481 [6,438–6,500] | 7,617 [7,583–7,662] | 60.5× | 1.18× |
| room_show c=1 CPU µs/req | 9,514 [9,133–9,832] | 142 [141–143] | 209 [206–209] | 67.0× | 0.68× |
| room_show c=1 p50 ms | 8.90 [8.82–9.10] | 0.15 [0.15–0.15] | 0.13 [0.13–0.13] | 58.6× | 1.20× |
| room_show c=1 p99 ms | 14.3 [10.4–30.3] | 0.18 [0.18–0.18] | 0.17 [0.17–0.17] | 77.6× | 1.06× |
| room_show c=16 req/s | 239 [238–241] | 28,623 [28,510–28,701] | 40,422 [38,482–40,429] | 119.6× | 1.41× |
| room_show c=16 CPU µs/req | 12,236 [12,113–12,267] | 122 [122–123] | 93.6 [93.4–99.3] | 100.0× | 1.31× |
| room_show c=16 p50 ms | 65.6 [63.7–68.5] | 0.54 [0.53–0.54] | 0.31 [0.31–0.34] | 122.6× | 1.71× |
| room_show c=16 p99 ms | 131 [117–143] | 1.07 [1.07–1.07] | 1.64 [1.64–1.66] | 122.2× | 0.65× |
| room_show c=64 req/s | 222 [214–226] | 29,072 [28,652–29,223] | 36,654 [36,543–37,859] | 130.7× | 1.26× |
| room_show c=64 CPU µs/req | 13,022 [12,841–13,269] | 120 [120–122] | 97.0 [93.9–97.1] | 108.1× | 1.24× |
| room_show c=64 p50 ms | 277 [277–300] | 2.13 [2.13–2.17] | 1.51 [1.51–1.52] | 129.9× | 1.41× |
| room_show c=64 p99 ms | 363 [338–364] | 3.99 [3.97–4.09] | 5.01 [4.61–5.09] | 91.1× | 0.80× |
| messages_page c=1 req/s | 218 [208–218] | 6,822 [6,768–6,827] | 8,312 [8,301–8,348] | 31.3× | 1.22× |
| messages_page c=1 CPU µs/req | 4,715 [4,706–4,948] | 139 [139–140] | 199 [197–199] | 33.9× | 0.70× |
| messages_page c=1 p50 ms | 4.51 [4.48–4.73] | 0.14 [0.14–0.15] | 0.12 [0.12–0.12] | 31.1× | 1.25× |
| messages_page c=1 p99 ms | 5.88 [5.82–6.09] | 0.18 [0.18–0.18] | 0.17 [0.17–0.17] | 33.4× | 1.05× |
| messages_page c=16 req/s | 381 [346–391] | 30,063 [30,028–30,274] | 37,836 [37,727–38,181] | 79.0× | 1.26× |
| messages_page c=16 CPU µs/req | 7,359 [7,132–7,944] | 114 [114–115] | 102 [101–102] | 64.4× | 1.12× |
| messages_page c=16 p50 ms | 42.0 [41.0–42.1] | 0.51 [0.51–0.51] | 0.35 [0.35–0.35] | 82.3× | 1.46× |
| messages_page c=16 p99 ms | 78.6 [73.3–149.5] | 0.99 [0.98–0.99] | 3.19 [2.93–3.25] | 79.6× | 0.31× |
| messages_page c=64 req/s | 369 [364–376] | 30,849 [30,818–30,968] | 40,382 [39,703–40,513] | 83.5× | 1.31× |
| messages_page c=64 CPU µs/req | 7,475 [7,296–7,501] | 112 [112–112] | 91.1 [90.8–93.1] | 66.9× | 1.23× |
| messages_page c=64 p50 ms | 172 [170–173] | 2.02 [2.02–2.02] | 1.44 [1.43–1.45] | 85.1× | 1.41× |
| messages_page c=64 p99 ms | 213 [212–214] | 3.62 [3.60–3.62] | 4.66 [4.65–4.81] | 58.9× | 0.78× |
| sidebar c=1 req/s | 287 [270–290] | 6,196 [6,185–6,242] | 8,000 [7,971–8,005] | 21.6× | 1.29× |
| sidebar c=1 CPU µs/req | 3,585 [3,540–3,796] | 149 [148–150] | 181 [181–182] | 24.1× | 0.82× |
| sidebar c=1 p50 ms | 3.47 [3.41–3.51] | 0.16 [0.16–0.16] | 0.12 [0.12–0.12] | 21.8× | 1.34× |
| sidebar c=1 p99 ms | 4.47 [4.43–6.70] | 0.19 [0.19–0.19] | 0.18 [0.18–0.18] | 23.7× | 1.06× |
| sidebar c=16 req/s | 599 [594–606] | 28,238 [28,177–28,250] | 37,555 [36,864–37,754] | 47.2× | 1.33× |
| sidebar c=16 CPU µs/req | 4,901 [4,860–4,927] | 123 [123–123] | 102 [101–104] | 39.8× | 1.21× |
| sidebar c=16 p50 ms | 26.1 [25.3–26.4] | 0.54 [0.54–0.54] | 0.34 [0.34–0.36] | 48.3× | 1.57× |
| sidebar c=16 p99 ms | 47.7 [47.5–47.9] | 1.11 [1.11–1.11] | 1.75 [1.71–1.75] | 42.8× | 0.64× |
| sidebar c=64 req/s | 524 [500–526] | 28,856 [28,786–28,866] | 36,455 [35,969–36,530] | 55.0× | 1.26× |
| sidebar c=64 CPU µs/req | 5,622 [5,603–5,890] | 121 [121–121] | 97.6 [97.6–98.9] | 46.5× | 1.24× |
| sidebar c=64 p50 ms | 120 [119–131] | 2.15 [2.15–2.15] | 1.54 [1.54–1.55] | 56.0× | 1.39× |
| sidebar c=64 p99 ms | 174 [163–205] | 4.13 [4.13–4.17] | 4.83 [4.66–5.19] | 42.0× | 0.86× |
| search c=1 req/s | 193 [173–199] | 5,646 [5,581–5,647] | 6,055 [5,914–6,092] | 29.3× | 1.07× |
| search c=1 CPU µs/req | 5,228 [5,072–5,795] | 158 [158–160] | 315 [314–324] | 33.0× | 0.50× |
| search c=1 p50 ms | 5.06 [4.97–5.30] | 0.18 [0.17–0.18] | 0.17 [0.16–0.17] | 28.8× | 1.06× |
| search c=1 p99 ms | 6.65 [6.39–17.52] | 0.21 [0.20–0.21] | 0.21 [0.21–0.21] | 32.1× | 1.00× |
| search c=16 req/s | 408 [372–411] | 26,606 [26,559–26,607] | 34,070 [33,951–34,097] | 65.2× | 1.28× |
| search c=16 CPU µs/req | 7,029 [6,984–7,549] | 124 [124–124] | 107 [107–108] | 56.7× | 1.15× |
| search c=16 p50 ms | 38.8 [37.8–40.5] | 0.57 [0.57–0.58] | 0.44 [0.44–0.44] | 67.5× | 1.32× |
| search c=16 p99 ms | 74.6 [65.4–85.2] | 1.11 [1.11–1.12] | 1.36 [1.35–1.37] | 66.9× | 0.82× |
| search c=64 req/s | 367 [361–373] | 32,782 [32,336–33,049] | 42,413 [42,111–42,624] | 89.3× | 1.29× |
| search c=64 CPU µs/req | 7,771 [7,672–7,823] | 109 [109–110] | 84.3 [83.8–84.6] | 71.1× | 1.30× |
| search c=64 p50 ms | 179 [167–183] | 1.88 [1.87–1.90] | 1.35 [1.34–1.35] | 95.0× | 1.40× |
| search c=64 p99 ms | 257 [223–259] | 3.33 [3.30–3.56] | 3.81 [3.56–3.85] | 77.0× | 0.88× |
| static_css c=1 req/s | 15,167 [15,140–15,268] | 21,715 [21,594–21,840] | 26,867 [26,407–26,941] | 1.4× | 1.24× |
| static_css c=1 CPU µs/req | 56.6 [56.2–57.0] | 31.7 [31.5–31.8] | 81.2 [81.1–82.1] | 1.8× | 0.39× |
| static_css c=1 p50 ms | 0.06 [0.06–0.06] | 0.04 [0.04–0.04] | 0.04 [0.03–0.04] | 1.4× | 1.29× |
| static_css c=1 p99 ms | 0.10 [0.10–0.10] | 0.07 [0.07–0.07] | 0.07 [0.07–0.07] | 1.6× | 0.92× |
| static_css c=16 req/s | 98,776 [97,868–102,758] | 149,556 [148,260–151,847] | 190,580 [189,467–197,125] | 1.5× | 1.27× |
| static_css c=16 CPU µs/req | 28.8 [28.1–29.3] | 14.8 [14.7–14.8] | 19.0 [18.2–19.6] | 1.9× | 0.78× |
| static_css c=16 p50 ms | 0.11 [0.11–0.11] | 0.09 [0.09–0.09] | 0.07 [0.07–0.08] | 1.2× | 1.25× |
| static_css c=16 p99 ms | 0.80 [0.74–0.84] | 0.27 [0.27–0.28] | 0.27 [0.25–0.28] | 2.9× | 1.01× |
| static_css c=64 req/s | 87,196 [84,014–90,433] | 178,094 [177,229–179,062] | 234,334 [233,719–235,086] | 2.0× | 1.32× |
| static_css c=64 CPU µs/req | 33.4 [32.1–33.4] | 12.8 [12.8–12.9] | 13.9 [13.9–14.2] | 2.6× | 0.92× |
| static_css c=64 p50 ms | 0.31 [0.30–0.31] | 0.32 [0.32–0.32] | 0.25 [0.25–0.26] | 1.0× | 1.27× |
| static_css c=64 p99 ms | 3.76 [3.59–4.19] | 0.97 [0.96–0.99] | 0.63 [0.62–0.64] | 3.9× | 1.54× |
| up c=1 req/s | 2,101 [2,094–2,154] | 15,129 [15,069–15,137] | 14,909 [14,516–14,996] | 7.2× | 0.99× |
| up c=1 CPU µs/req | 529 [515–529] | 44.0 [43.9–44.1] | 126 [126–130] | 12.0× | 0.35× |
| up c=1 p50 ms | 0.45 [0.44–0.45] | 0.06 [0.06–0.06] | 0.07 [0.06–0.07] | 7.0× | 0.98× |
| up c=1 p99 ms | 0.90 [0.87–0.94] | 0.09 [0.09–0.09] | 0.10 [0.10–0.10] | 10.3× | 0.85× |
| up c=16 req/s | 4,229 [4,178–4,281] | 117,356 [117,313–117,512] | 147,639 [146,825–152,951] | 27.8× | 1.26× |
| up c=16 CPU µs/req | 641 [631–648] | 22.4 [22.3–22.4] | 20.5 [20.3–20.8] | 28.6× | 1.09× |
| up c=16 p50 ms | 3.71 [3.66–3.75] | 0.12 [0.12–0.12] | 0.09 [0.09–0.09] | 30.7× | 1.29× |
| up c=16 p99 ms | 6.89 [6.75–7.06] | 0.36 [0.36–0.36] | 0.25 [0.25–0.26] | 18.9× | 1.43× |
| up c=64 req/s | 4,196 [4,085–4,239] | 135,012 [134,279–135,413] | 183,684 [182,828–184,244] | 32.2× | 1.36× |
| up c=64 CPU µs/req | 652 [646–666] | 19.9 [19.9–20.0] | 15.7 [15.2–15.9] | 32.7× | 1.27× |
| up c=64 p50 ms | 15.1 [14.8–15.4] | 0.43 [0.43–0.43] | 0.31 [0.31–0.31] | 34.7× | 1.40× |
| up c=64 p99 ms | 25.4 [21.6–26.4] | 1.20 [1.18–1.22] | 1.26 [1.24–1.28] | 21.2× | 0.95× |
| post_message c=1 req/s | 167 [156–171] | 2,779 [2,773–2,814] | 3,052 [3,032–3,072] | 16.7× | 1.10× |
| post_message c=1 CPU µs/req | 8,157 [8,024–8,577] | 404 [398–404] | 629 [625–634] | 20.2× | 0.64× |
| post_message c=1 p50 ms | 5.52 [5.52–5.58] | 0.34 [0.34–0.35] | 0.30 [0.30–0.31] | 16.1× | 1.14× |
| post_message c=1 p99 ms | 14.9 [9.3–20.1] | 0.53 [0.50–0.54] | 0.56 [0.55–0.57] | 28.1× | 0.94× |
| post_message c=16 req/s | 330 [322–332] | 7,174 [6,268–7,229] | 8,905 [8,769–9,036] | 21.7× | 1.24× |
| post_message c=16 CPU µs/req | 10,044 [10,025–10,301] | 386 [384–388] | 353 [352–353] | 26.0× | 1.09× |
| post_message c=16 p50 ms | 40.3 [32.3–49.8] | 2.10 [2.08–2.10] | 1.55 [1.54–1.55] | 19.2× | 1.35× |
| post_message c=16 p99 ms | 135 [120–160] | 6.04 [5.88–6.05] | 5.99 [5.94–6.05] | 22.4× | 1.01× |
| post_message c=64 req/s | 332 [300–338] | 4,809 [4,550–4,920] | 5,888 [5,426–6,112] | 14.5× | 1.22× |
| post_message c=64 CPU µs/req | 10,220 [10,064–11,190] | 389 [385–389] | 341 [340–343] | 26.3× | 1.14× |
| post_message c=64 p50 ms | 188 [188–205] | 8.52 [8.50–8.54] | 6.43 [6.39–6.46] | 22.1× | 1.32× |
| post_message c=64 p99 ms | 286 [270–341] | 18.0 [17.8–19.9] | 15.4 [15.2–19.5] | 15.9× | 1.17× |
| avatar c=1 req/s | 12,838 [12,669–12,896] | 20,966 [20,954–21,039] | 23,919 [23,771–24,856] | 1.6× | 1.14× |
| avatar c=1 CPU µs/req | 64.4 [64.1–65.1] | 32.3 [32.2–32.4] | 89.7 [86.4–89.7] | 2.0× | 0.36× |
| avatar c=1 p50 ms | 0.07 [0.07–0.07] | 0.05 [0.05–0.05] | 0.04 [0.04–0.04] | 1.6× | 1.24× |
| avatar c=1 p99 ms | 0.14 [0.14–0.15] | 0.07 [0.07–0.07] | 0.08 [0.08–0.08] | 2.1× | 0.86× |
| avatar c=16 req/s | 69,481 [67,231–71,849] | 140,426 [138,594–140,961] | 174,192 [173,862–174,479] | 2.0× | 1.24× |
| avatar c=16 CPU µs/req | 38.9 [38.4–40.5] | 15.7 [15.5–15.8] | 20.4 [20.1–20.5] | 2.5× | 0.77× |
| avatar c=16 p50 ms | 0.15 [0.15–0.16] | 0.10 [0.10–0.10] | 0.08 [0.08–0.08] | 1.5× | 1.20× |
| avatar c=16 p99 ms | 1.01 [0.97–1.07] | 0.28 [0.28–0.28] | 0.23 [0.23–0.24] | 3.6× | 1.21× |
| avatar c=64 req/s | 57,064 [54,584–58,347] | 167,507 [163,075–169,824] | 201,516 [199,699–206,348] | 2.9× | 1.20× |
| avatar c=64 CPU µs/req | 47.4 [46.1–49.3] | 13.7 [13.6–13.7] | 17.0 [15.6–17.3] | 3.5× | 0.81× |
| avatar c=64 p50 ms | 0.41 [0.39–0.43] | 0.35 [0.34–0.35] | 0.30 [0.29–0.30] | 1.2× | 1.17× |
| avatar c=64 p99 ms | 5.62 [5.54–5.93] | 1.01 [0.98–1.05] | 0.72 [0.71–0.77] | 5.5× | 1.40× |

### Action Cable fan-out (one room; chatter.js subscriptions per client)

| Metric | Rails | Rust | F# | Rust adv. | F# adv. |
|---|---|---|---|---|---|
| 100 clients: subscribed | 100 [100–100] | 100 [100–100] | 100 [100–100] | 1.0× | 1.00× |
| 100 clients: connect+subscribe all (s) | 0.44 [0.42–0.44] | 0.06 [0.06–0.06] | 0.06 [0.06–0.07] | 7.3× | 1.00× |
| 100 clients: paced post→one client p50 ms | 27.0 [25.1–27.7] | 2.99 [2.91–3.10] | 4.33 [4.24–4.70] | 9.0× | 0.69× |
| 100 clients: paced post→all clients p50 ms | 32.9 [31.6–33.3] | 3.48 [3.46–3.56] | 4.97 [4.90–5.23] | 9.4× | 0.70× |
| 100 clients: paced post→all clients p99 ms | 101 [89–174] | 4.72 [4.42–4.75] | 20.1 [12.1–21.1] | 21.4× | 0.23× |
| 100 clients: max sustained msgs/s (delivered to all) | 99.0 [96.7–99.6] | 3,247 [3,218–3,266] | 2,611 [2,564–2,620] | 32.8× | 0.80× |
| 100 clients: deliveries/s (client×message) | 9,901 [9,671–9,955] | 324,669 [321,752–326,574] | 261,112 [256,442–261,991] | 32.8× | 0.80× |
| 100 clients: saturated post→all p50 ms | 55.0 [53.3–55.8] | 1.46 [1.45–1.46] | 1.63 [1.63–1.66] | 37.7× | 0.90× |
| 100 clients: saturated POST p50 ms | 37.2 [35.4–40.2] | 1.17 [1.16–1.17] | 1.45 [1.44–1.47] | 31.9× | 0.81× |
| 500 clients: subscribed | 500 [500–500] | 500 [500–500] | 500 [500–500] | 1.0× | 1.00× |
| 500 clients: connect+subscribe all (s) | 0.75 [0.75–0.76] | 0.14 [0.13–1.08] | 0.13 [0.13–0.13] | 5.4× | 1.08× |
| 500 clients: paced post→one client p50 ms | 33.1 [32.9–34.0] | 4.96 [4.81–5.17] | 6.29 [6.01–7.06] | 6.7× | 0.79× |
| 500 clients: paced post→all clients p50 ms | 55.1 [54.4–55.2] | 8.16 [7.23–8.23] | 9.71 [9.40–10.11] | 6.8× | 0.84× |
| 500 clients: paced post→all clients p99 ms | 83.6 [64.5–95.3] | 10.6 [9.5–12.4] | 13.9 [13.4–39.8] | 7.9× | 0.76× |
| 500 clients: max sustained msgs/s (delivered to all) | 30.0 [29.8–31.0] | 1,044 [1,035–1,047] | 695 [643–717] | 34.8× | 0.67× |
| 500 clients: deliveries/s (client×message) | 14,993 [14,884–15,502] | 521,954 [517,482–523,343] | 347,578 [321,348–358,465] | 34.8× | 0.67× |
| 500 clients: saturated post→all p50 ms | 107 [103–120] | 8.98 [8.83–9.11] | 9.86 [9.26–9.95] | 11.9× | 0.91× |
| 500 clients: saturated POST p50 ms | 120 [111–121] | 3.60 [3.57–3.61] | 5.51 [5.32–6.05] | 33.3× | 0.65× |
| 1000 clients: subscribed | 1,000 [1,000–1,000] | 1,000 [1,000–1,000] | 1,000 [1,000–1,000] | 1.0× | 1.00× |
| 1000 clients: connect+subscribe all (s) | 1.23 [1.22–1.27] | 0.19 [0.14–0.21] | 0.20 [0.17–0.22] | 6.5× | 0.95× |
| 1000 clients: paced post→one client p50 ms | 40.9 [39.6–41.3] | 7.92 [7.30–8.15] | 10.2 [9.9–10.3] | 5.2× | 0.78× |
| 1000 clients: paced post→all clients p50 ms | 76.9 [74.7–77.2] | 13.1 [13.0–13.1] | 16.7 [16.6–17.8] | 5.9× | 0.78× |
| 1000 clients: paced post→all clients p99 ms | 113 [108–124] | 18.2 [17.3–18.4] | 20.9 [20.8–23.0] | 6.2× | 0.87× |
| 1000 clients: max sustained msgs/s (delivered to all) | 15.5 [15.4–16.0] | 578 [578–579] | 349 [332–354] | 37.3× | 0.60× |
| 1000 clients: deliveries/s (client×message) | 15,486 [15,406–16,030] | 578,445 [578,010–579,026] | 348,927 [332,129–353,954] | 37.4× | 0.60× |
| 1000 clients: saturated post→all p50 ms | 214 [187–641] | 20.5 [20.5–20.6] | 25.2 [21.9–25.6] | 10.4× | 0.82× |
| 1000 clients: saturated POST p50 ms | 173 [109–185] | 6.24 [6.17–6.29] | 10.7 [10.7–11.7] | 27.7× | 0.58× |

### Upload + thumbnail (black_hole.jpg, 505 KB)

| Metric | Rails | Rust | F# | Rust adv. | F# adv. |
|---|---|---|---|---|---|
| POST with attachment (ms) | 61.5 [51.1–61.9] | 28.1 [27.8–28.4] | 28.0 [27.9–28.6] | 2.2× | 1.00× |
| then GET thumb → 200 (ms) | 0.30 [0.30–0.40] | 0.20 [0.20–0.30] | 0.30 [0.30–0.30] | 1.5× | 0.67× |
| POST → thumbnail served (ms) | 61.8 [51.5–62.3] | 28.3 [28.1–28.6] | 28.3 [28.2–28.9] | 2.2× | 1.00× |

### Memory during cable fan-out, by process (MB, peak within the phase)

App process: Rails' Puma master and workers (Action Cable runs in them), or the Rust and F# ports' one campfire
process (its front server included). Pss counts pages shared between forked workers once;
RssAnon counts them in every process.

| Metric | Rails | Rust | F# | Rust adv. | F# adv. |
|---|---|---|---|---|---|
| 100 clients, all subscribed, idle: app process Pss | 349 [348–359] | 121 [121–122] | 191 [190–196] | 2.9× | 0.63× |
| 100 clients, all subscribed, idle: app process RssAnon | 532 [527–541] | 102 [101–103] | 107 [106–112] | 5.2× | 0.95× |
| 100 clients, all subscribed, idle: app + Redis + Thruster Pss | 373 [373–383] | 121 [121–122] | 191 [190–196] | 3.1× | 0.63× |
| 100 clients, all subscribed, idle: whole container Pss | 545 [534–565] | 121 [121–122] | 191 [190–196] | 4.5× | 0.63× |
| 100 clients, saturated fan-out: app process Pss | 656 [645–658] | 121 [118–122] | 295 [284–298] | 5.4× | 0.41× |
| 100 clients, saturated fan-out: app process RssAnon | 810 [799–813] | 102 [98–103] | 207 [197–210] | 8.0× | 0.49× |
| 100 clients, saturated fan-out: app + Redis + Thruster Pss | 694 [683–697] | 121 [118–122] | 295 [284–298] | 5.7× | 0.41× |
| 100 clients, saturated fan-out: whole container Pss | 970 [956–982] | 121 [118–122] | 295 [284–298] | 8.0× | 0.41× |
| 500 clients, all subscribed, idle: app process Pss | 650 [641–651] | 120 [119–122] | 293 [285–299] | 5.4× | 0.41× |
| 500 clients, all subscribed, idle: app process RssAnon | 800 [793–806] | 101 [100–102] | 204 [196–210] | 7.9× | 0.50× |
| 500 clients, all subscribed, idle: app + Redis + Thruster Pss | 704 [694–705] | 120 [119–122] | 293 [285–299] | 5.8× | 0.41× |
| 500 clients, all subscribed, idle: whole container Pss | 982 [964–994] | 120 [119–122] | 293 [285–299] | 8.1× | 0.41× |
| 500 clients, saturated fan-out: app process Pss | 765 [742–838] | 122 [122–122] | 441 [433–457] | 6.3× | 0.28× |
| 500 clients, saturated fan-out: app process RssAnon | 915 [897–988] | 102 [102–102] | 352 [344–368] | 8.9× | 0.29× |
| 500 clients, saturated fan-out: app + Redis + Thruster Pss | 832 [809–905] | 122 [122–122] | 441 [433–457] | 6.8× | 0.28× |
| 500 clients, saturated fan-out: whole container Pss | 1,098 [1,090–1,191] | 122 [122–122] | 441 [433–457] | 9.0× | 0.28× |
| 1000 clients, all subscribed, idle: app process Pss | 754 [734–754] | 125 [124–129] | 442 [433–460] | 6.0× | 0.28× |
| 1000 clients, all subscribed, idle: app process RssAnon | 901 [887–902] | 105 [105–110] | 352 [344–371] | 8.6× | 0.30× |
| 1000 clients, all subscribed, idle: app + Redis + Thruster Pss | 860 [841–879] | 125 [124–129] | 442 [433–460] | 6.9× | 0.28× |
| 1000 clients, all subscribed, idle: whole container Pss | 1,149 [1,102–1,165] | 125 [124–129] | 442 [433–460] | 9.2× | 0.28× |
| 1000 clients, saturated fan-out: app process Pss | 892 [868–895] | 125 [124–129] | 670 [559–865] | 7.1× | 0.19× |
| 1000 clients, saturated fan-out: app process RssAnon | 1,042 [1,015–1,045] | 106 [104–110] | 581 [469–776] | 9.9× | 0.18× |
| 1000 clients, saturated fan-out: app + Redis + Thruster Pss | 1,024 [964–1,026] | 125 [124–129] | 670 [559–865] | 8.2× | 0.19× |
| 1000 clients, saturated fan-out: whole container Pss | 1,310 [1,224–1,312] | 125 [124–129] | 670 [559–865] | 10.5× | 0.19× |

## Validity

### Load average before each run (1, 5, 15 minutes; the run waited for the 1-minute figure to fall below LOAD_MAX)

| App | Rep | Before | After |
|---|---|---|---|
| Rails | 1 | 0.60 1.55 2.06 | 3.46 3.28 2.92 |
| Rails | 2 | 1.45 3.26 3.68 | 3.22 3.16 3.30 |
| Rails | 3 | 1.43 2.69 3.13 | 3.47 3.49 3.37 |
| Rust | 1 | 1.38 2.71 2.75 | 5.00 4.30 3.70 |
| Rust | 2 | 1.25 3.70 4.04 | 5.66 4.32 4.04 |
| Rust | 3 | 1.42 2.91 3.17 | 6.34 4.64 3.95 |
| F# | 1 | 1.39 3.30 3.39 | 8.05 5.76 4.65 |
| F# | 2 | 1.37 4.01 4.13 | 6.68 5.23 4.51 |
| F# | 3 | 1.43 3.44 3.59 | 5.51 5.12 4.66 |

### Host CPU probe (a fixed spin timed on every pinned CPU, against the quietest this VM has shown; bench/lib/hostprobe.py)

The VM's load average cannot see another program on the Mac taking the cores its vCPUs run on; this can. A ratio of 1.00 is a quiet host, above 1.10 the pinned server and load generator were slowed by something outside the run. `After each window` probes the machine right after every measured run of the rep.

| App | Rep | Before the run | After the run | Windows probed above 1.10 | Highest after a window |
|---|---|---|---|---|---|
| Rails | 1 | 1.01 | 1.02 | 0/24 | 1.07 |
| Rails | 2 | 1.02 | 1.07 | 0/24 | 1.08 |
| Rails | 3 | 1.04 | 1.03 | 0/24 | 1.04 |
| Rust | 1 | 1.03 | 1.03 | 0/24 | 1.06 |
| Rust | 2 | 1.06 | 1.02 | 0/24 | 1.06 |
| Rust | 3 | 1.01 | 1.04 | 0/24 | 1.06 |
| F# | 1 | 1.01 | 1.03 | 0/24 | 1.06 |
| F# | 2 | 1.04 | 1.03 | 0/24 | 1.06 |
| F# | 3 | 1.03 | 1.02 | 0/24 | 1.05 |

### Responses by status class, every HTTP run of every rep

| App | Route | 2xx | 3xx | 4xx | 5xx | Errors (no response) |
|---|---|---|---|---|---|---|
| Rails | room_show | 13,859 | 0 | 0 | 0 | 0 |
| Rails | messages_page | 23,194 | 0 | 0 | 0 | 0 |
| Rails | sidebar | 33,818 | 0 | 0 | 0 | 0 |
| Rails | search | 23,101 | 0 | 0 | 0 | 0 |
| Rails | static_css | 4,853,523 | 0 | 0 | 0 | 0 |
| Rails | up | 252,706 | 0 | 0 | 0 | 0 |
| Rails | post_message | 19,837 | 0 | 0 | 0 | 0 |
| Rails | avatar | 3,336,101 | 0 | 0 | 0 | 0 |
| Rust | room_show | 1,537,850 | 0 | 0 | 0 | 0 |
| Rust | messages_page | 1,627,563 | 0 | 0 | 0 | 0 |
| Rust | sidebar | 1,518,596 | 0 | 0 | 0 | 0 |
| Rust | search | 1,558,743 | 0 | 0 | 0 | 0 |
| Rust | avatar | 7,867,430 | 0 | 0 | 0 | 0 |
| Rust | static_css | 8,394,331 | 0 | 0 | 0 | 0 |
| Rust | up | 6,418,423 | 0 | 0 | 0 | 0 |
| Rust | post_message | 346,947 | 0 | 0 | 0 | 0 |
| F# | room_show | 2,026,327 | 0 | 0 | 0 | 0 |
| F# | messages_page | 2,074,720 | 0 | 0 | 0 | 0 |
| F# | sidebar | 1,961,079 | 0 | 0 | 0 | 0 |
| F# | search | 1,978,991 | 0 | 0 | 0 | 0 |
| F# | post_message | 431,789 | 0 | 0 | 0 | 0 |
| F# | avatar | 9,622,515 | 0 | 0 | 0 | 0 |
| F# | static_css | 10,885,343 | 0 | 0 | 0 | 0 |
| F# | up | 8,341,652 | 0 | 0 | 0 | 0 |

### Warm-up before each measured run (the app runs the route at the measured concurrency until its throughput is flat; bench/lib/warmup.py)

`measured / warm-up tail` is the measured run's requests a second over the mean of the last three 2 s warm-up windows: well above 1 would mean the app was still speeding up when measurement began, well below 1 that it was slowing down.

| App | Route | c | warm-up seconds | settled | measured / warm-up tail |
|---|---|---|---|---|---|
| Rails | room_show | 1 | 30.1 [30.1–40.1] | 3/3 | 0.95 |
| Rails | room_show | 16 | 30.8 [30.8–32.8] | 3/3 | 1.03 |
| Rails | room_show | 64 | 31.9 [31.8–32.1] | 3/3 | 1.01 |
| Rails | messages_page | 1 | 30.0 [30.0–46.0] | 3/3 | 1.03 |
| Rails | messages_page | 16 | 36.6 [30.5–67.2] | 3/3 | 0.97 |
| Rails | messages_page | 64 | 30.3 [30.3–30.3] | 3/3 | 1.01 |
| Rails | sidebar | 1 | 30.0 [30.0–30.0] | 3/3 | 0.99 |
| Rails | sidebar | 16 | 30.3 [30.3–30.4] | 3/3 | 1.00 |
| Rails | sidebar | 64 | 31.9 [31.8–31.9] | 3/3 | 1.00 |
| Rails | search | 1 | 36.0 [30.0–36.0] | 3/3 | 0.98 |
| Rails | search | 16 | 30.5 [30.5–40.6] | 3/3 | 1.01 |
| Rails | search | 64 | 30.4 [30.4–30.5] | 3/3 | 1.00 |
| Rails | static_css | 1 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| Rails | static_css | 16 | 30.0 [30.0–30.0] | 3/3 | 1.01 |
| Rails | static_css | 64 | 30.0 [30.0–30.0] | 3/3 | 1.01 |
| Rails | up | 1 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| Rails | up | 16 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| Rails | up | 64 | 30.3 [30.3–30.3] | 3/3 | 1.00 |
| Rails | post_message | 1 | 30.0 [30.0–30.0] | 3/3 | 0.98 |
| Rails | post_message | 16 | 30.6 [30.5–30.6] | 3/3 | 0.99 |
| Rails | post_message | 64 | 30.5 [30.4–30.8] | 3/3 | 1.02 |
| Rails | avatar | 1 | 30.0 [30.0–30.0] | 3/3 | 0.99 |
| Rails | avatar | 16 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| Rails | avatar | 64 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| Rust | room_show | 1 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| Rust | room_show | 16 | 30.0 [30.0–32.0] | 3/3 | 1.00 |
| Rust | room_show | 64 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| Rust | messages_page | 1 | 30.0 [30.0–46.0] | 3/3 | 1.00 |
| Rust | messages_page | 16 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| Rust | messages_page | 64 | 30.0 [30.0–30.0] | 3/3 | 1.01 |
| Rust | sidebar | 1 | 30.0 [30.0–30.0] | 3/3 | 0.99 |
| Rust | sidebar | 16 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| Rust | sidebar | 64 | 30.0 [30.0–30.0] | 3/3 | 1.01 |
| Rust | search | 1 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| Rust | search | 16 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| Rust | search | 64 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| Rust | avatar | 1 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| Rust | avatar | 16 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| Rust | avatar | 64 | 30.0 [30.0–30.0] | 3/3 | 1.01 |
| Rust | static_css | 1 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| Rust | static_css | 16 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| Rust | static_css | 64 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| Rust | up | 1 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| Rust | up | 16 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| Rust | up | 64 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| Rust | post_message | 1 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| Rust | post_message | 16 | 32.0 [30.0–36.0] | 3/3 | 0.99 |
| Rust | post_message | 64 | 90.6 [34.3–91.8] | 1/3 **←** | 1.00 |
| F# | room_show | 1 | 30.0 [30.0–30.0] | 3/3 | 1.01 |
| F# | room_show | 16 | 40.0 [30.0–90.0] | 2/3 **←** | 1.00 |
| F# | room_show | 64 | 30.0 [30.0–30.0] | 3/3 | 1.01 |
| F# | messages_page | 1 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| F# | messages_page | 16 | 90.0 [90.0–90.0] | 0/3 **←** | 1.00 |
| F# | messages_page | 64 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| F# | sidebar | 1 | 30.0 [30.0–30.0] | 3/3 | 1.01 |
| F# | sidebar | 16 | 90.0 [90.0–90.0] | 0/3 **←** | 1.00 |
| F# | sidebar | 64 | 30.0 [30.0–30.0] | 3/3 | 1.01 |
| F# | search | 1 | 30.0 [30.0–30.0] | 3/3 | 1.01 |
| F# | search | 16 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| F# | search | 64 | 30.0 [30.0–30.0] | 3/3 | 1.01 |
| F# | post_message | 1 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| F# | post_message | 16 | 30.1 [30.0–30.1] | 3/3 | 1.01 |
| F# | post_message | 64 | 92.1 [90.1–92.4] | 0/3 **←** | 1.04 |
| F# | avatar | 1 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| F# | avatar | 16 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| F# | avatar | 64 | 30.0 [30.0–30.0] | 3/3 | 1.01 |
| F# | static_css | 1 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| F# | static_css | 16 | 30.0 [30.0–30.0] | 3/3 | 1.00 |
| F# | static_css | 64 | 30.0 [30.0–30.0] | 3/3 | 1.01 |
| F# | up | 1 | 76.0 [30.0–90.0] | 2/3 **←** | 1.00 |
| F# | up | 16 | 30.0 [30.0–34.0] | 3/3 | 0.99 |
| F# | up | 64 | 30.0 [30.0–30.0] | 3/3 | 1.00 |

### Rows written by `post_message` (messages of the write room counted in the app's SQLite file around each run)

| App | Rep | Phase | Posts answered below 400 | Rows written | Match |
|---|---|---|---|---|---|
| Rails | 1 | warm-up c=1 | 4,815 | 4,815 | yes |
| Rails | 1 | run c=1 | 1,336 | 1,336 | yes |
| Rails | 1 | warm-up c=16 | 9,965 | 9,965 | yes |
| Rails | 1 | run c=16 | 2,662 | 2,662 | yes |
| Rails | 1 | warm-up c=64 | 10,006 | 10,006 | yes |
| Rails | 1 | run c=64 | 2,722 | 2,722 | yes |
| Rails | 1 | **all** | 31,506 | 31,506 | yes |
| Rails | 2 | warm-up c=1 | 4,876 | 4,876 | yes |
| Rails | 2 | run c=1 | 1,246 | 1,246 | yes |
| Rails | 2 | warm-up c=16 | 10,326 | 10,326 | yes |
| Rails | 2 | run c=16 | 2,666 | 2,666 | yes |
| Rails | 2 | warm-up c=64 | 10,218 | 10,218 | yes |
| Rails | 2 | run c=64 | 2,767 | 2,767 | yes |
| Rails | 2 | **all** | 32,099 | 32,099 | yes |
| Rails | 3 | warm-up c=1 | 4,864 | 4,864 | yes |
| Rails | 3 | run c=1 | 1,369 | 1,369 | yes |
| Rails | 3 | warm-up c=16 | 9,807 | 9,807 | yes |
| Rails | 3 | run c=16 | 2,592 | 2,592 | yes |
| Rails | 3 | warm-up c=64 | 9,096 | 9,096 | yes |
| Rails | 3 | run c=64 | 2,477 | 2,477 | yes |
| Rails | 3 | **all** | 30,205 | 30,205 | yes |
| Rust | 1 | warm-up c=1 | 83,365 | 83,365 | yes |
| Rust | 1 | run c=1 | 22,186 | 22,186 | yes |
| Rust | 1 | warm-up c=16 | 215,410 | 215,410 | yes |
| Rust | 1 | run c=16 | 57,404 | 57,404 | yes |
| Rust | 1 | warm-up c=64 | 457,129 | 457,129 | yes |
| Rust | 1 | run c=64 | 38,491 | 38,491 | yes |
| Rust | 1 | **all** | 873,985 | 873,985 | yes |
| Rust | 2 | warm-up c=1 | 83,884 | 83,884 | yes |
| Rust | 2 | run c=1 | 22,235 | 22,235 | yes |
| Rust | 2 | warm-up c=16 | 229,161 | 229,161 | yes |
| Rust | 2 | run c=16 | 50,426 | 50,426 | yes |
| Rust | 2 | warm-up c=64 | 166,857 | 166,857 | yes |
| Rust | 2 | run c=64 | 36,437 | 36,437 | yes |
| Rust | 2 | **all** | 589,000 | 589,000 | yes |
| Rust | 3 | warm-up c=1 | 84,486 | 84,486 | yes |
| Rust | 3 | run c=1 | 22,510 | 22,510 | yes |
| Rust | 3 | warm-up c=16 | 262,061 | 262,061 | yes |
| Rust | 3 | run c=16 | 57,845 | 57,845 | yes |
| Rust | 3 | warm-up c=64 | 470,563 | 470,563 | yes |
| Rust | 3 | run c=64 | 39,413 | 39,413 | yes |
| Rust | 3 | **all** | 936,878 | 936,878 | yes |
| F# | 1 | warm-up c=1 | 86,487 | 86,487 | yes |
| F# | 1 | run c=1 | 24,413 | 24,413 | yes |
| F# | 1 | warm-up c=16 | 264,398 | 264,398 | yes |
| F# | 1 | run c=16 | 70,167 | 70,167 | yes |
| F# | 1 | warm-up c=64 | 583,574 | 583,574 | yes |
| F# | 1 | run c=64 | 48,958 | 48,958 | yes |
| F# | 1 | **all** | 1,077,997 | 1,077,997 | yes |
| F# | 2 | warm-up c=1 | 84,872 | 84,872 | yes |
| F# | 2 | run c=1 | 24,255 | 24,255 | yes |
| F# | 2 | warm-up c=16 | 263,332 | 263,332 | yes |
| F# | 2 | run c=16 | 72,301 | 72,301 | yes |
| F# | 2 | warm-up c=64 | 597,492 | 597,492 | yes |
| F# | 2 | run c=64 | 47,137 | 47,137 | yes |
| F# | 2 | **all** | 1,089,389 | 1,089,389 | yes |
| F# | 3 | warm-up c=1 | 86,206 | 86,206 | yes |
| F# | 3 | run c=1 | 24,580 | 24,580 | yes |
| F# | 3 | warm-up c=16 | 267,975 | 267,975 | yes |
| F# | 3 | run c=16 | 71,254 | 71,254 | yes |
| F# | 3 | warm-up c=64 | 556,656 | 556,656 | yes |
| F# | 3 | run c=64 | 48,724 | 48,724 | yes |
| F# | 3 | **all** | 1,055,395 | 1,055,395 | yes |

### Response bytes, one request per route (gzip: wire / decoded; identity: bytes) and the average over the c=16 run

The probe is a single request after sign-in, before any load. Bodies carry per-request tokens and the clock, so sizes of two apps agree to within a few bytes when they render the same page. "c=16 avg" is what the load generator received per request on the wire (gzip unless the column says otherwise).

| Route | Rails gzip wire / decoded | Rust gzip wire / decoded | F# gzip wire / decoded | Rails identity | Rust identity | F# identity | Rails c=16 avg | Rust c=16 avg | F# c=16 avg | F# vs Rust |
|---|---|---|---|---|---|---|---|---|---|---|
| room_show | 200 44,385 / 463,708 | 200 24,231 / 416,122 | 200 24,080 / 416,122 | 200 463,708 | 200 416,122 | 200 416,122 | 44,376 | 24,231 | 24,080 | identity +0 B; gzip wire -0.6% |
| messages_page | 200 34,901 / 430,925 | 200 16,158 / 383,844 | 200 15,967 / 383,844 | 200 430,925 | 200 383,844 | 200 383,844 | 34,858 | 16,158 | 15,967 | identity +0 B; gzip wire -1.2% |
| sidebar | 200 6,268 / 31,427 | 200 5,910 / 30,763 | 200 5,910 / 30,763 | 200 31,427 | 200 30,763 | 200 30,763 | 6,267 | 5,910 | 5,910 | identity +0 B; gzip wire +0.0% |
| search | 200 17,532 / 165,519 | 200 9,766 / 149,623 | 200 9,713 / 149,623 | 200 165,519 | 200 149,623 | 200 149,623 | 17,532 | 9,766 | 9,713 | identity +0 B; gzip wire -0.5% |
| static_css | 200 653 / 1,218 | 200 654 / 1,218 | 200 654 / 1,218 | 200 1,218 | 200 1,218 | 200 1,218 | 653 | 654 | 654 | identity +0 B; gzip wire +0.0% |
| up | 200 88 / 73 | 200 90 / 73 | 200 90 / 73 | 200 73 | 200 73 | 200 73 | 88 | 90 | 90 | identity +0 B; gzip wire +0.0% |
| avatar | 200 3,360 / 3,364 | 200 3,368 / 3,364 | 200 3,368 / 3,364 | 200 3,364 | 200 3,364 | 200 3,364 | 3,360 | 3,368 | 3,368 | identity +0 B; gzip wire +0.0% |
| post_message | – | – | – | – | – | – | 2,002 | 1,993 | 1,993 | c=16 avg +0 B |
