### CPU per request (cgroup cpu.stat of the container, unprofiled; median [min–max] over reps)

| Route | c | Rust CPU µs/req | F# CPU µs/req | F# / Rust | Rust user+sys | F# user+sys | Rust req/s | F# req/s | Cores busy: Rust / F# server, load generator |
|---|---|---|---|---|---|---|---|---|---|
| room_show | 16 | 122.2 [122.2–122.3] | 173.8 [171.8–177.0] | 1.42× | 95+27 | 145+29 | 28,622 | 21,503 | 3.50 / 3.73, 0.89 |
| room_show | 1 | 141.5 [141.3–142.1] | 538.4 [534.7–559.5] | 3.80× | 99+43 | 410+128 | 6,483 | 3,323 | 0.92 / 1.80, 0.20 |
| messages_page | 16 | 116.6 [116.1–116.9] | 166.2 [165.4–166.9] | 1.43× | 89+28 | 139+28 | 29,608 | 22,278 | 3.45 / 3.68, 0.85 |
| messages_page | 1 | 138.6 [137.7–138.7] | 463.5 [454.7–473.8] | 3.34× | 92+47 | 338+126 | 6,758 | 4,095 | 0.94 / 1.90, 0.20 |
| sidebar | 16 | 123.8 [122.7–124.7] | 199.2 [196.0–202.3] | 1.61× | 99+25 | 165+35 | 28,199 | 18,653 | 3.49 / 3.71, 0.79 |
| sidebar | 1 | 148.9 [147.0–149.2] | 465.3 [455.5–474.9] | 3.12× | 110+39 | 345+120 | 6,139 | 3,684 | 0.91 / 1.71, 0.18 |
| search | 16 | 125.4 [124.9–126.2] | 188.5 [186.4–189.2] | 1.50× | 90+35 | 143+46 | 26,304 | 19,702 | 3.30 / 3.68, 0.74 |
| search | 1 | 158.3 [158.3–158.7] | 488.2 [468.0–493.4] | 3.08× | 107+51 | 353+135 | 5,623 | 3,811 | 0.89 / 1.86, 0.17 |
| post_message | 16 | 381.4 [378.8–405.0] | 545.9 [540.1–554.2] | 1.43× | 252+129 | 381+166 | 6,770 | 6,255 | 2.58 / 3.41, 0.32 |
| post_message | 1 | 394.7 [393.4–397.7] | 1,024.3 [1,007.9–1,054.3] | 2.60× | 263+133 | 720+304 | 2,830 | 1,916 | 1.12 / 1.97, 0.08 |
| up | 16 | 23.0 [22.8–23.0] | 28.2 [27.5–29.7] | 1.23× | 16+7 | 18+10 | 115,497 | 78,258 | 2.65 / 2.21, 1.67 |
| up | 1 | 43.2 [42.1–44.0] | 152.6 [148.5–157.0] | 3.53× | 25+18 | 95+58 | 15,631 | 10,711 | 0.66 / 1.64, 0.41 |
| static_css | 16 | 15.1 [15.0–15.1] | 20.8 [20.1–21.5] | 1.38× | 8+7 | 11+10 | 148,447 | 128,579 | 2.24 / 2.67, 1.70 |
| static_css | 1 | 31.6 [31.5–31.7] | 124.2 [120.5–126.6] | 3.93× | 14+17 | 73+52 | 21,478 | 14,268 | 0.68 / 1.75, 0.34 |
| avatar | 16 | 16.5 [16.2–16.5] | 22.1 [21.5–24.3] | 1.34× | 9+8 | 12+10 | 137,613 | 119,862 | 2.26 / 2.65, 1.80 |
| avatar | 1 | 32.1 [32.0–32.1] | 127.2 [124.4–130.4] | 3.96× | 14+18 | 75+52 | 20,870 | 13,839 | 0.67 / 1.75, 0.35 |

#### F# time per layer: share of the sampled thread time that is work or spinning (waits for GC and locks left out; %)

| Layer | room_show | messages_page | sidebar | search | post_message | up | static_css | avatar |
|---|---|---|---|---|---|---|---|---|
| SQLite reads | 70.1 | 73.0 | 70.1 | 74.2 | 47.3 | 0.0 | 0.0 | 0.0 |
| Kestrel and ASP.NET Core HTTP | 7.7 | 7.7 | 5.3 | 7.8 | 4.6 | 43.7 | 62.5 | 64.1 |
| request log line (console logger) | 4.1 | 3.8 | 2.9 | 3.7 | 1.4 | 27.9 | 33.7 | 32.1 |
| cookies, session, crypto | 8.8 | 7.8 | 10.6 | 6.6 | 1.3 | 0.7 | 0.0 | 0.0 |
| kit adapter, Ctx, params, response | 1.7 | 2.6 | 5.7 | 1.7 | 1.5 | 18.9 | 1.3 | 1.6 |
| SQLite writes | 0.0 | 0.0 | 0.0 | 0.0 | 26.0 | 0.0 | 0.0 | 0.0 |
| controllers and presenters | 3.9 | 2.3 | 4.0 | 3.5 | 1.5 | 3.8 | 0.0 | 0.0 |
| thread-pool spin and hand-off | 0.7 | 0.4 | 0.3 | 0.4 | 0.7 | 4.1 | 1.9 | 1.3 |
| gzip and the page splice | 1.5 | 0.5 | 0.0 | 1.1 | 5.6 | 0.1 | 0.0 | 0.0 |
| runtime, tasks and other | 0.1 | 0.1 | 0.0 | 0.0 | 8.6 | 0.0 | 0.0 | 0.0 |
| templates and fragment cache | 1.1 | 1.6 | 1.0 | 0.7 | 1.2 | 0.1 | 0.0 | 0.0 |
| front server (cache, headers, timeouts) | 0.2 | 0.2 | 0.1 | 0.2 | 0.0 | 0.5 | 0.6 | 1.0 |
| rich text | 0.0 | 0.0 | 0.0 | 0.0 | 0.3 | 0.0 | 0.0 | 0.0 |
| route table | 0.1 | 0.1 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| Falco and routing | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| *GC suspension, % of all busy samples* | 16.2 | 11.9 | 12.1 | 14.0 | 10.3 | 37.2 | 28.9 | 27.2 |
| *lock contention, % of all busy samples* | 22.7 | 20.8 | 13.1 | 20.8 | 14.5 | 17.6 | 25.1 | 27.5 |

#### F# runtime counters while profiled (c=16, tracing on, so throughput is lower than unprofiled)

| Route | req/s | allocated KB/req | gen0 GCs/s | gen1 | gen2 | GC pause (% of wall) | lock contentions/req | thread-pool work items/req | process CPU cores |
|---|---|---|---|---|---|---|---|---|---|
| room_show | 17,974 | 160.79 | 96 | 48 | 0 | 4.8 | 0.26 | 7.36 | 3.52 |
| messages_page | 19,656 | 136.37 | 214 | 0 | 0 | 6.4 | 0.22 | 8.45 | 3.46 |
| sidebar | 17,917 | 145.37 | 92 | 48 | 0 | 4.1 | 0.30 | 6.12 | 3.53 |
| search | 18,544 | 100.25 | 152 | 0 | 0 | 4.4 | 0.49 | 7.28 | 3.50 |
| post_message | 5,746 | 236.98 | 12 | 3 | 3 | 1.7 | 1.23 | 18.27 | 3.27 |
| up | 74,444 | 12.55 | 173 | 0 | 0 | 3.6 | 0.04 | 1.76 | 2.34 |
| static_css | 123,746 | 4.81 | 96 | 0 | 0 | 1.9 | 0.04 | 2.03 | 2.55 |
| avatar | 116,102 | 6.22 | 89 | 0 | 0 | 2.5 | 0.09 | 2.29 | 3.01 |

#### Heaviest F# frames per route (exclusive, thread-ms; the leaf-most managed frame of work samples)

- **room_show**: `System.Threading.Monitor.Enter_Slowpath(class System.Object)` 8,491; `Microsoft.Data.Sqlite.SqliteDataReader.NextResult()` 8,378; `System.Threading.Thread.<PollGC>g__PollGCWorker|67_0()` 6,707; `Microsoft.Data.Sqlite.SqliteDataReader.Dispose(bool)` 1,778; `Microsoft.Data.Sqlite.SqliteDataRecord.Read()` 1,457; `System.Net.Sockets.SocketPal.SysSend(class System.Net.Sockets.SafeSock` 1,022
- **messages_page**: `Microsoft.Data.Sqlite.SqliteDataReader.NextResult()` 9,108; `System.Threading.Monitor.Enter_Slowpath(class System.Object)` 7,292; `System.Threading.Thread.<PollGC>g__PollGCWorker|67_0()` 4,621; `Microsoft.Data.Sqlite.SqliteDataReader.Dispose(bool)` 2,182; `Microsoft.Data.Sqlite.SqliteDataRecord.Read()` 1,361; `System.Net.Sockets.SocketPal.SysSend(class System.Net.Sockets.SafeSock` 1,156
- **sidebar**: `Microsoft.Data.Sqlite.SqliteDataReader.NextResult()` 12,560; `System.Threading.Thread.<PollGC>g__PollGCWorker|67_0()` 5,625; `System.Threading.Monitor.Enter_Slowpath(class System.Object)` 5,135; `Microsoft.Data.Sqlite.SqliteDataRecord.Read()` 3,782; `Campfire.RailsCompat.MessageVerifierModule.sign(class Campfire.RailsCo` 2,600; `Microsoft.Data.Sqlite.SqliteDataReader.Dispose(bool)` 1,930
- **search**: `Microsoft.Data.Sqlite.SqliteDataReader.NextResult()` 14,581; `System.Threading.Monitor.Enter_Slowpath(class System.Object)` 7,981; `System.Threading.Thread.<PollGC>g__PollGCWorker|67_0()` 6,651; `Microsoft.Data.Sqlite.SqliteDataReader.Dispose(bool)` 2,092; `Microsoft.Data.Sqlite.SqliteDataRecord.Read()` 1,951; `System.Threading.Monitor.Exit_Slowpath(value class LeaveHelperAction,c` 1,922
- **post_message**: `Microsoft.Data.Sqlite.SqliteDataReader.NextResult()` 18,540; `System.Threading.Monitor.Enter_Slowpath(class System.Object)` 5,381; `System.Threading.Thread.<PollGC>g__PollGCWorker|67_0()` 4,884; `System.RuntimeTypeHandle.GetRuntimeTypeFromHandleSlow(int)` 3,053; `?` 3,017; `Microsoft.Data.Sqlite.SqliteDataReader.Dispose(bool)` 2,524
- **up**: `System.Threading.Thread.<PollGC>g__PollGCWorker|67_0()` 5,262; `?` 2,552; `System.Threading.Monitor.Enter_Slowpath(class System.Object)` 2,334; `System.IO.StreamWriter.Flush(bool,bool)` 986; `Microsoft.Extensions.Logging.Console.ConsoleLoggerProcessor.Enqueue(va` 761; `Campfire.Kit.CtxHelpers.bodyEtag(class Campfire.Kit.Response)` 731
- **static_css**: `System.Threading.Thread.<PollGC>g__PollGCWorker|67_0()` 4,617; `?` 4,403; `System.Threading.Monitor.Enter_Slowpath(class System.Object)` 3,723; `System.IO.StreamWriter.Flush(bool,bool)` 1,338; `Microsoft.Extensions.Logging.Console.ConsoleLoggerProcessor.Enqueue(va` 1,055; `System.Threading.Monitor.Exit_Slowpath(value class LeaveHelperAction,c` 291
- **avatar**: `System.Threading.Thread.<PollGC>g__PollGCWorker|67_0()` 6,284; `?` 6,254; `System.Threading.Monitor.Enter_Slowpath(class System.Object)` 5,596; `System.IO.StreamWriter.Flush(bool,bool)` 1,751; `Microsoft.Extensions.Logging.Console.ConsoleLoggerProcessor.Enqueue(va` 1,447; `System.Threading.Monitor.Exit_Slowpath(value class LeaveHelperAction,c` 775

### Identity bodies (Accept-Encoding: identity), CPU per request

| Route | c | Rust CPU µs/req | F# CPU µs/req | F# / Rust | Rust user+sys | F# user+sys | Rust req/s | F# req/s | Cores busy: Rust / F# server, load generator |
|---|---|---|---|---|---|---|---|---|---|
| room_show | 16 | 171.3 [170.2–172.4] | 262.6 [262.0–263.3] | 1.53× | 104+67 | 200+63 | 19,897 | 14,429 | 3.41 / 3.79, 2.73 |
| room_show | 1 | 167.8 [164.9–170.7] | 626.4 [619.1–633.6] | 3.73× | 99+69 | 464+162 | 4,671 | 2,726 | 0.79 / 1.71, 0.43 |
| messages_page | 16 | 158.8 [157.6–160.1] | 244.2 [243.1–245.3] | 1.54× | 94+65 | 187+57 | 21,233 | 15,541 | 3.38 / 3.79, 2.59 |
| messages_page | 1 | 175.2 [175.1–175.3] | 549.5 [548.8–550.3] | 3.14× | 94+81 | 388+161 | 4,854 | 3,338 | 0.85 / 1.83, 0.41 |

### Where the CPU goes, from perf (c=16)


| Route | Rust µs/req (cores) | F# µs/req (cores) | F# / Rust | Rust req/s | F# req/s |
|---|---|---|---|---|---|
| room_show | 139.3 (3.38) | 179.2 (3.66) | 1.29× | 24,276 | 20,412 |
| messages_page | 129.4 (3.3) | 168.9 (3.63) | 1.31× | 25,506 | 21,514 |
| sidebar | 123.6 (3.47) | 209.2 (3.23) | 1.69× | 28,092 | 15,424 |
| search | 122.0 (3.23) | 221.7 (3.61) | 1.82× | 26,448 | 16,302 |
| post_message | 380.6 (2.34) | 535.6 (2.84) | 1.41× | 6,153 | 5,303 |
| up | 25.3 (2.64) | 27.1 (2.01) | 1.07× | 104,264 | 74,074 |
| static_css | 15.1 (2.16) | 24.1 (2.92) | 1.60× | 143,287 | 121,103 |
| avatar | 15.2 (1.79) | 26.6 (2.62) | 1.75× | 117,324 | 98,474 |

#### CPU per request by what the code is, Rust against F# (µs/req; perf of each app's process at c=16)

Each cell is Rust / F#. Samples are given to the layer that owns the nearest frame of the call chain (F#: the chain is complete, native callees included; Rust: frames have no frame pointers, so the leaf's own symbol or the first named crate on the chain decides, and libc, kernel and allocator time under a Rust frame lands in *libc, kernel and unnamed code* or *allocator*).

| Group | room_show | messages_page | sidebar | search | post_message | up | static_css | avatar |
|---|---|---|---|---|---|---|---|---|
| SQLite engine | 46.3 / 45.6 | 48.6 / 49.2 | 42.6 / 68.6 | 54.7 / 91.8 | 186.5 / 216.5 | 0.0 / 0.0 | 0.0 / 0.0 | 0.0 / 0.0 |
| Database access around the engine (wrapper, rows, pool) | 8.0 / 37.0 | 8.1 / 30.5 | 5.7 / 33.9 | 5.8 / 30.1 | 17.6 / 76.5 | 0.0 / 0.0 | 0.0 / 0.0 | 0.0 / 0.0 |
| cookies, session, HMAC, key derivation | 6.1 / 15.2 | 4.0 / 11.3 | 17.6 / 29.8 | 3.0 / 12.1 | 25.2 / 15.3 | 0.2 / 1.0 | 0.0 / 0.0 | 0.1 / 0.0 |
| templates, escaping, fragment cache | 16.0 / 15.2 | 13.5 / 13.3 | 9.5 / 15.9 | 6.1 / 11.4 | 4.8 / 9.7 | 0.0 / 0.1 | 0.0 / 0.0 | 0.0 / 0.0 |
| gzip and the page splice | 5.4 / 4.4 | 3.0 / 2.2 | 0.5 / 0.2 | 3.0 / 2.6 | 36.8 / 32.8 | 0.4 / 0.1 | 0.0 / 0.0 | 0.0 / 0.0 |
| HTTP server and request plumbing (server, front, kit, routing, log line) | 33.7 / 37.1 | 34.0 / 36.3 | 25.8 / 38.9 | 27.9 / 40.7 | 56.6 / 60.8 | 17.4 / 19.9 | 10.7 / 18.2 | 11.0 / 20.0 |
| controllers, presenters, authentication, rich text | 15.1 / 7.6 | 12.0 / 6.0 | 12.5 / 7.5 | 13.9 / 9.2 | 32.8 / 47.9 | 6.0 / 1.0 | 4.0 / 0.0 | 3.7 / 0.0 |
| memory management and runtime (F#: GC threads, CLR/BCL without a Campfire caller; Rust: allocator) | 8.7 / 17.0 | 6.3 / 20.0 | 9.3 / 14.3 | 7.5 / 23.6 | 20.2 / 75.9 | 1.3 / 5.0 | 0.3 / 5.9 | 0.4 / 6.6 |
| **total** | 139.3 / 179.2 | 129.4 / 168.9 | 123.6 / 209.2 | 122.0 / 221.7 | 380.6 / 535.6 | 25.3 / 27.1 | 15.1 / 24.1 | 15.2 / 26.6 |

#### CPU per request by resource, Rust against F# (µs/req; the leaf's own object, whoever called it)

| Resource | room_show | messages_page | sidebar | search | post_message | up | static_css | avatar |
|---|---|---|---|---|---|---|---|---|
| kernel | 30.5 / 26.2 | 31.8 / 24.3 | 24.5 / 33.7 | 30.5 / 46.8 | 121.4 / 140.3 | 8.0 / 9.0 | 6.7 / 9.4 | 7.1 / 10.6 |
| SQLite engine | 16.7 / 25.1 | 17.5 / 26.3 | 17.9 / 32.0 | 20.0 / 38.8 | 55.2 / 85.2 | 0.0 / 0.0 | 0.0 / 0.0 | 0.0 / 0.0 |
| crypto: OpenSSL (F#) / SHA, HMAC, AES crates (Rust) | 1.7 / 8.1 | 1.0 / 5.9 | 13.1 / 20.1 | 0.7 / 5.8 | 4.2 / 9.1 | 0.1 / 0.7 | 0.0 / 0.0 | 0.0 / 0.0 |
| memory: CLR runtime, GC, JIT (F#) / allocator (Rust) | 7.0 / 29.2 | 5.0 / 31.6 | 7.3 / 26.4 | 5.9 / 32.6 | 12.6 / 71.8 | 1.2 / 5.6 | 0.3 / 6.4 | 0.3 / 6.6 |
| libc (mutexes, memcpy, other) | 21.9 / 19.7 | 18.8 / 20.4 | 17.1 / 28.0 | 23.2 / 37.2 | 50.2 / 58.9 | 2.3 / 2.0 | 1.1 / 1.6 | 1.0 / 1.8 |
| managed code (F#) / Rust code | 61.6 / 71.0 | 55.4 / 60.5 | 43.8 / 69.2 | 41.7 / 60.3 | 137.4 / 170.9 | 13.8 / 9.8 | 7.0 / 6.7 | 6.8 / 7.7 |

#### F# CPU per request by layer (µs/req and share of the app's CPU)

| Layer | room_show | messages_page | sidebar | search | post_message | up | static_css | avatar |
|---|---|---|---|---|---|---|---|---|
| SQLite engine (libe_sqlite3) | 45.6 (25%) | 49.2 (29%) | 68.6 (33%) | 91.8 (41%) | 123.1 (23%) | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) |
| Campfire.Db (queries, row mapping, connection pool) | 20.3 (11%) | 13.9 (8%) | 17.3 (8%) | 17.5 (8%) | 44.5 (8%) | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) |
| Kestrel and ASP.NET Core HTTP | 19.6 (11%) | 18.3 (11%) | 19.0 (9%) | 22.0 (10%) | 26.9 (5%) | 11.1 (41%) | 11.5 (48%) | 12.8 (48%) |
| runtime and BCL with no Campfire caller on the stack | 7.1 (4%) | 8.1 (5%) | 2.2 (1%) | 14.9 (7%) | 65.5 (12%) | 4.2 (16%) | 5.6 (23%) | 6.2 (23%) |
| SQLite engine (libe_sqlite3), writer thread | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) | 93.4 (17%) | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) |
| Microsoft.Data.Sqlite and P/Invoke stubs | 16.7 (9%) | 16.6 (10%) | 16.6 (8%) | 12.6 (6%) | 23.5 (4%) | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) |
| cookies, session, HMAC and key derivation | 15.2 (8%) | 11.3 (7%) | 29.8 (14%) | 12.1 (6%) | 15.3 (3%) | 1.0 (4%) | 0.0 (0%) | 0.0 (0%) |
| templates, escaping and fragment cache | 15.2 (8%) | 13.3 (8%) | 15.9 (8%) | 11.4 (5%) | 9.7 (2%) | 0.1 (0%) | 0.0 (0%) | 0.0 (0%) |
| GC (server GC threads) | 9.9 (6%) | 11.9 (7%) | 12.1 (6%) | 8.7 (4%) | 10.4 (2%) | 0.8 (3%) | 0.3 (1%) | 0.4 (1%) |
| kit adapter, Ctx, params, response | 7.2 (4%) | 9.2 (5%) | 8.2 (4%) | 8.1 (4%) | 18.7 (4%) | 2.7 (10%) | 0.8 (3%) | 0.7 (3%) |
| controllers and presenters | 6.9 (4%) | 5.1 (3%) | 6.5 (3%) | 8.4 (4%) | 23.1 (4%) | 1.0 (4%) | 0.0 (0%) | 0.0 (0%) |
| request log line (console logger) | 7.7 (4%) | 7.0 (4%) | 8.7 (4%) | 8.7 (4%) | 10.7 (2%) | 5.5 (20%) | 5.7 (24%) | 6.3 (24%) |
| gzip and the page splice | 4.4 (2%) | 2.2 (1%) | 0.2 (0%) | 2.6 (1%) | 32.8 (6%) | 0.1 (0%) | 0.0 (0%) | 0.0 (0%) |
| rich text | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) | 23.1 (4%) | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) |
| route table | 1.7 (1%) | 0.9 (0%) | 2.0 (1%) | 1.0 (0%) | 3.3 (1%) | 0.5 (2%) | 0.0 (0%) | 0.0 (0%) |
| authentication and current-user concerns | 0.7 (0%) | 0.9 (1%) | 1.0 (0%) | 0.8 (0%) | 1.7 (0%) | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) |
| Microsoft.Data.Sqlite and P/Invoke stubs, writer thread | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) | 4.9 (1%) | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) |
| front server (cache, headers, timeouts) | 0.9 (0%) | 0.9 (1%) | 1.0 (0%) | 0.9 (0%) | 1.2 (0%) | 0.1 (0%) | 0.2 (1%) | 0.2 (1%) |
| Campfire.Db (queries, row mapping, connection pool), writer thread | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) | 3.6 (1%) | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) |
| Falco and routing | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) |

#### Rust CPU per request by crate or library (µs/req and share)

| Layer | room_show | messages_page | sidebar | search | post_message | up | static_css | avatar |
|---|---|---|---|---|---|---|---|---|
| SQLite engine (libsqlite3, static) | 46.3 (33%) | 48.6 (38%) | 42.6 (34%) | 54.7 (45%) | 186.5 (49%) | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) |
| hyper, tokio, axum (HTTP and runtime) | 26.4 (19%) | 26.7 (21%) | 20.9 (17%) | 22.3 (18%) | 47.9 (13%) | 13.8 (54%) | 9.1 (60%) | 9.4 (62%) |
| templates, escaping and fragment cache | 16.0 (12%) | 13.5 (10%) | 9.5 (8%) | 6.1 (5%) | 4.8 (1%) | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) |
| controllers and presenters | 15.1 (11%) | 12.0 (9%) | 12.5 (10%) | 13.9 (11%) | 21.9 (6%) | 6.0 (24%) | 4.0 (27%) | 3.7 (24%) |
| allocator (jemalloc, libc malloc) | 8.7 (6%) | 6.3 (5%) | 9.3 (8%) | 7.5 (6%) | 20.2 (5%) | 1.3 (5%) | 0.3 (2%) | 0.4 (3%) |
| Db crate (queries, rows, pool) | 8.0 (6%) | 8.1 (6%) | 5.7 (5%) | 5.8 (5%) | 17.6 (5%) | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) |
| kit (request, response, front server) | 7.3 (5%) | 7.3 (6%) | 4.9 (4%) | 5.6 (5%) | 8.7 (2%) | 3.6 (14%) | 1.6 (10%) | 1.6 (11%) |
| cookies, session, HMAC and SHA | 6.1 (4%) | 4.0 (3%) | 17.6 (14%) | 3.0 (2%) | 25.2 (7%) | 0.2 (1%) | 0.0 (0%) | 0.1 (1%) |
| gzip and the page splice | 5.4 (4%) | 3.0 (2%) | 0.5 (0%) | 3.0 (2%) | 36.8 (10%) | 0.4 (2%) | 0.0 (0%) | 0.0 (0%) |
| rich text | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) | 10.9 (3%) | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) |

#### F# threads: share of CPU by thread name

- **room_show**: .NET TP Worker 83.8%, campfire-db-rea 7.5%, .NET Server GC 5.5%, Console logger 2.4%, .NET Sockets 0.7%, .NET Tiered Com 0.0%
- **messages_page**: .NET TP Worker 82.9%, .NET Server GC 7.1%, campfire-db-rea 6.9%, Console logger 2.4%, .NET Sockets 0.8%, .NET Tiered Com 0.0%
- **sidebar**: .NET TP Worker 89.3%, .NET Server GC 5.8%, Console logger 2.5%, campfire-db-rea 1.7%, .NET Sockets 0.7%
- **search**: campfire-db-rea 49.7%, .NET TP Worker 43.0%, .NET Server GC 3.9%, Console logger 2.6%, .NET Sockets 0.8%
- **post_message**: .NET TP Worker 40.3%, campfire-db-rea 31.9%, campfire-db-wri 19.2%, .NET File Watch 3.8%, Console logger 1.4%, .NET Server GC 1.3%, campfire-db-che 1.0%, .NET BGC 0.6%
- **up**: .NET TP Worker 82.4%, Console logger 11.4%, .NET Sockets 3.3%, .NET Server GC 2.8%
- **static_css**: .NET TP Worker 83.8%, Console logger 10.7%, .NET Sockets 4.2%, .NET Server GC 1.3%
- **avatar**: .NET TP Worker 83.0%, Console logger 11.0%, .NET Sockets 4.6%, .NET Server GC 1.4%, .NET Tiered Com 0.0%

#### F# heaviest leaf frames per route (% of the app's CPU)

- **room_show**: `libcoreclr.so (no symbols)` 14.07%; `libc.so.6 (no symbols)` 6.52%; `stub InitThreadManagerPerfMapData<JIT_CopiedWriteBarriers>` 2.76%; `pthread_mutex_lock` 2.28%; `ld-linux-aarch64.so.1 (no symbols)` 2.13%; `libcrypto.so.3 (no symbols)` 2.1%; `try_to_wake_up ([kernel.kallsyms])` 1.96%; `sqlite3VdbeExec` 1.85%
- **messages_page**: `libcoreclr.so (no symbols)` 16.62%; `libc.so.6 (no symbols)` 7.44%; `stub InitThreadManagerPerfMapData<JIT_CopiedWriteBarriers>` 2.49%; `pthread_mutex_lock` 2.46%; `try_to_wake_up ([kernel.kallsyms])` 2.35%; `ld-linux-aarch64.so.1 (no symbols)` 2.07%; `sqlite3VdbeExec` 1.82%; `libcrypto.so.3 (no symbols)` 1.61%
- **sidebar**: `libcoreclr.so (no symbols)` 10.66%; `libc.so.6 (no symbols)` 8.37%; `libcrypto.so.3 (no symbols)` 6.7%; `sqlite3VdbeExec` 3.26%; `_raw_spin_unlock_irqrestore ([kernel.kallsyms])` 2.72%; `stub InitThreadManagerPerfMapData<JIT_CopiedWriteBarriers>` 2.41%; `pthread_mutex_lock` 2.25%; `try_to_wake_up ([kernel.kallsyms])` 2.04%
- **search**: `libcoreclr.so (no symbols)` 13.1%; `libc.so.6 (no symbols)` 11.63%; `_raw_spin_unlock_irqrestore ([kernel.kallsyms])` 3.61%; `sqlite3VdbeExec` 2.84%; `pthread_mutex_lock` 2.68%; `el0_svc ([kernel.kallsyms])` 2.5%; `try_to_wake_up ([kernel.kallsyms])` 2.31%; `stub InitThreadManagerPerfMapData<JIT_CopiedWriteBarriers>` 1.66%
- **post_message**: `libcoreclr.so (no symbols)` 11.94%; `libc.so.6 (no symbols)` 7.14%; `libSystem.IO.Compression.Native.so (no symbols)` 5.27%; `try_to_wake_up ([kernel.kallsyms])` 3.19%; `el0_svc ([kernel.kallsyms])` 2.93%; `sqlite3VdbeExec` 1.71%; `stub InitThreadManagerPerfMapData<JIT_CopiedWriteBarriers>` 1.61%; `pthread_mutex_lock` 1.44%
- **up**: `libcoreclr.so (no symbols)` 18.83%; `try_to_wake_up ([kernel.kallsyms])` 4.44%; `libc.so.6 (no symbols)` 4.13%; `__wake_up_sync_key ([kernel.kallsyms])` 4.09%; `stub InitThreadManagerPerfMapData<JIT_CopiedWriteBarriers>` 3.18%; `el0_svc ([kernel.kallsyms])` 2.99%; `ld-linux-aarch64.so.1 (no symbols)` 1.72%; `_raw_spin_unlock_irqrestore ([kernel.kallsyms])` 1.6%
- **static_css**: `libcoreclr.so (no symbols)` 24.77%; `libc.so.6 (no symbols)` 4.61%; `_raw_spin_unlock_irqrestore ([kernel.kallsyms])` 3.32%; `try_to_wake_up ([kernel.kallsyms])` 3.2%; `schedule ([kernel.kallsyms])` 2.82%; `el0_svc ([kernel.kallsyms])` 2.71%; `stub InitThreadManagerPerfMapData<JIT_CopiedWriteBarriers>` 2.63%; `__wake_up_sync_key ([kernel.kallsyms])` 2.46%
- **avatar**: `libcoreclr.so (no symbols)` 23.14%; `libc.so.6 (no symbols)` 4.49%; `_raw_spin_unlock_irqrestore ([kernel.kallsyms])` 4.0%; `try_to_wake_up ([kernel.kallsyms])` 2.98%; `el0_svc ([kernel.kallsyms])` 2.46%; `stub InitThreadManagerPerfMapData<JIT_CopiedWriteBarriers>` 2.33%; `schedule ([kernel.kallsyms])` 2.21%; `__wake_up_sync_key ([kernel.kallsyms])` 2.14%

#### One connection (c=1): CPU per request by resource, Rust / F# (µs/req)

| Route | req/s Rust / F# | CPU µs/req Rust / F# | cores Rust / F# | kernel | CLR runtime, GC, JIT (F#) / allocator (Rust) | libc | managed (F#) / Rust code |
|---|---|---|---|---|---|---|---|
| room_show | 6,348 / 3,532 | 128 / 478 | 0.82 / 1.69 | 36 / 126 | 5 / 158 | 16 / 44 | 58 / 107 |
| up | 15,022 / 9,862 | 41 / 141 | 0.61 / 1.39 | 17 / 54 | 1 / 49 | 4 / 11 | 19 / 25 |

- F# room_show c=1 threads: .NET TP Worker 93.9%, Console logger 2.5%, .NET Server GC 2.1%, .NET Sockets 1.5%, .NET Tiered Com 0.1%, .NET BGC 0.0%

- F# up c=1 threads: .NET TP Worker 86.1%, Console logger 8.5%, .NET Sockets 4.6%, .NET Server GC 0.8%, .NET Tiered Com 0.0%

### The request log line: CPU per request at c=16 with LOG_REQUESTS=false (one rep) against the default runs


| Route | c | Rust µs/req | Rust, log off | change | F# µs/req | F# , log off | change |
|---|---|---|---|---|---|---|---|
| room_show | 16 | 122.2 | 104.5 | -14% | 173.8 | 156.3 | -10% |
| messages_page | 16 | 116.6 | 99.4 | -15% | 166.2 | 149.7 | -10% |
| sidebar | 16 | 123.8 | 104.1 | -16% | 199.2 | 171.5 | -14% |
| search | 16 | 125.4 | 109.1 | -13% | 188.5 | 162.7 | -14% |
| post_message | 16 | 381.4 | 353.1 | -7% | 545.9 | 504.5 | -8% |
| up | 16 | 23.0 | 18.1 | -21% | 28.2 | 20.0 | -29% |
| static_css | 16 | 15.1 | 10.8 | -28% | 20.8 | 16.7 | -20% |
| avatar | 16 | 16.5 | 12.4 | -25% | 22.1 | 18.2 | -18% |
