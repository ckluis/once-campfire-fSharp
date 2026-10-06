
### Where the CPU goes, from perf (c=16)

Host CPU probe after the unprofiled and the perf window of each route (1.00 = a quiet host, above 1.10 the numbers overlap noise from outside): highest 1.06; none above 1.10.

Every µs figure below is the share perf measured times the unprofiled CPU per request of the same process, route and concentration (cgroup cpu.stat of an unprofiled window just before the perf window), so the columns add up to the unprofiled total. perf's own overhead is in the next table.

| Route | Rust µs/req unprofiled (cores) | F# µs/req unprofiled (cores) | F# / Rust | Rust perf overhead | F# perf overhead | Rust req/s | F# req/s |
|---|---|---|---|---|---|---|---|
| room_show | 122.3 (3.5) | 174.6 (3.77) | 1.43× | +5% | +4% | 28,584 | 21,608 |
| messages_page | 114.9 (3.44) | 165.6 (3.74) | 1.44× | +8% | +17% | 29,981 | 22,608 |
| sidebar | 124.9 (3.48) | 200.3 (3.78) | 1.60× | +7% | +17% | 27,854 | 18,861 |
| search | 126.1 (3.28) | 204.2 (3.66) | 1.62× | -3% | -4% | 26,057 | 17,906 |
| post_message | 399.3 (2.81) | 589.2 (3.39) | 1.48× | -1% | -2% | 7,038 | 5,749 |

#### CPU per request by what the code is, Rust against F# (µs/req; perf of each app's process at c=16)

Each cell is Rust / F#. Samples are given to the layer that owns the nearest frame of the call chain (F#: the chain is complete, native callees included; Rust: frames have no frame pointers, so the leaf's own symbol or the first named crate on the chain decides, and libc, kernel and allocator time under a Rust frame lands in *libc, kernel and unnamed code* or *allocator*).

| Group | room_show | messages_page | sidebar | search | post_message |
|---|---|---|---|---|---|
| SQLite engine | 40.1 / 43.6 | 42.1 / 39.6 | 42.2 / 69.0 | 58.1 / 73.4 | 197.3 / 238.4 |
| Database access around the engine (wrapper, rows, pool) | 6.8 / 32.7 | 7.0 / 31.0 | 6.2 / 28.5 | 5.9 / 26.1 | 19.6 / 76.7 |
| cookies, session, HMAC, key derivation | 5.5 / 13.3 | 3.9 / 9.0 | 17.7 / 24.9 | 3.1 / 10.5 | 25.9 / 16.5 |
| templates, escaping, fragment cache | 14.3 / 12.8 | 11.7 / 10.1 | 9.8 / 12.5 | 6.2 / 9.3 | 4.8 / 9.1 |
| gzip and the page splice | 4.7 / 3.4 | 2.8 / 1.9 | 0.5 / 0.2 | 2.9 / 1.9 | 38.1 / 36.1 |
| HTTP server and request plumbing (server, front, kit, routing, log line) | 29.0 / 36.6 | 31.3 / 35.1 | 26.6 / 34.8 | 28.4 / 38.8 | 58.5 / 65.6 |
| controllers, presenters, authentication, rich text | 14.5 / 6.4 | 11.0 / 4.7 | 13.3 / 6.2 | 14.5 / 8.2 | 34.0 / 45.7 |
| allocation and reclamation (F#: CLR allocation helpers + GC; Rust: jemalloc; both: libc malloc/free) | 7.4 / 23.4 | 5.1 / 22.4 | 8.6 / 22.9 | 7.0 / 20.3 | 21.2 / 36.5 |
| runtime, other (F#: CLR/BCL with no Campfire caller; Rust: libc, kernel and unnamed code) | 0.0 / 2.3 | 0.0 / 11.9 | 0.0 / 1.4 | 0.0 / 15.8 | 0.0 / 64.5 |
| **total** | 122.3 / 174.6 | 114.9 / 165.6 | 124.9 / 200.3 | 126.1 / 204.2 | 399.3 / 589.2 |

#### Allocation, attributed the same way for both apps (µs/req, unprofiled-scaled; Rust / F#)

The runtime allocator is F#'s CLR allocation helpers (RhpNew*, JIT_New*, GCHeap::Alloc, gc_heap::allocate_*, the memset that clears an allocation context) and Rust's jemalloc; reclamation is F#'s GC (Rust frees in the allocator, so it has none); libc malloc/free is both apps' native allocations (SQLite, OpenSSL, the runtime). A sample belongs to the first bucket whose frames appear anywhere on its call chain; Rust's chains stop at the first Rust frame.

| Bucket | room_show | messages_page | sidebar | search | post_message |
|---|---|---|---|---|---|
| runtime allocator | 6.9 / 10.0 | 4.6 / 7.5 | 7.2 / 9.3 | 5.2 / 8.3 | 14.9 / 19.8 |
| reclamation (GC) | 0.0 / 11.9 | 0.0 / 13.8 | 0.0 / 10.5 | 0.0 / 9.0 | 0.0 / 10.3 |
| libc malloc/free | 0.5 / 1.6 | 0.5 / 1.2 | 1.4 / 3.1 | 1.9 / 3.0 | 6.2 / 6.3 |
| **total** | 7.4 / 23.4 | 5.1 / 22.4 | 8.6 / 22.9 | 7.0 / 20.3 | 21.2 / 36.5 |
| *share of CPU, total* | 6.1% / 13.4% | 4.4% / 13.6% | 6.9% / 11.4% | 5.6% / 9.9% | 5.3% / 6.2% |

#### F#: what the CLR's own native code does (µs/req, unprofiled-scaled; samples whose leaf is in libcoreclr or libclrjit)

| What | room_show | messages_page | sidebar | search | post_message |
|---|---|---|---|---|---|
| reclamation (GC) | 9.4 | 11.5 | 8.3 | 7.5 | 9.2 |
| allocation helpers | 4.8 | 4.7 | 5.2 | 4.5 | 12.2 |
| monitors, spinning and waits | 3.6 | 11.1 | 1.6 | 13.1 | 31.5 |
| P/Invoke transitions | 2.0 | 2.2 | 1.7 | 1.2 | 2.7 |
| other runtime (thread-pool dispatch, stubs, type loads, ...) | 1.4 | 2.0 | 1.6 | 2.8 | 6.0 |
| type checks, statics and write barriers | 1.1 | 1.0 | 1.1 | 1.1 | 2.2 |
| JIT, tiering and PGO instrumentation | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |
| stack walks and exception unwinding | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 |

#### CPU per request by resource, Rust against F# (µs/req; the leaf's own object, whoever called it)

| Resource | room_show | messages_page | sidebar | search | post_message |
|---|---|---|---|---|---|
| kernel | 27.0 / 24.8 | 26.9 / 24.0 | 24.4 / 33.8 | 32.0 / 41.2 | 130.0 / 161.1 |
| SQLite engine | 14.5 / 24.2 | 15.7 / 22.1 | 17.9 / 30.6 | 20.5 / 34.9 | 56.5 / 90.1 |
| crypto: OpenSSL (F#) / SHA, HMAC, AES crates (Rust) | 1.5 / 7.7 | 1.0 / 5.0 | 12.9 / 18.5 | 0.7 / 5.7 | 4.6 / 10.6 |
| CLR runtime: GC, allocation, JIT, locks, spinning (F#) / jemalloc (Rust) | 6.8 / 26.1 | 4.4 / 36.3 | 7.4 / 22.8 | 6.1 / 32.9 | 14.3 / 71.1 |
| libc (mutexes, memcpy, other) | 18.9 / 19.6 | 16.1 / 18.9 | 17.0 / 28.0 | 24.3 / 31.5 | 53.0 / 67.7 |
| managed code (F#) / Rust code | 53.5 / 72.2 | 50.8 / 59.2 | 45.3 / 66.6 | 42.5 / 58.0 | 140.9 / 188.7 |

#### F# CPU per request by layer (µs/req and share of the app's CPU)

| Layer | room_show | messages_page | sidebar | search | post_message |
|---|---|---|---|---|---|
| SQLite engine (libe_sqlite3) | 43.6 (25%) | 39.6 (24%) | 69.0 (34%) | 73.4 (36%) | 137.5 (23%) |
| Kestrel and ASP.NET Core HTTP | 20.0 (11%) | 19.0 (12%) | 18.0 (9%) | 20.8 (10%) | 29.1 (5%) |
| Campfire.Db (queries, row mapping, connection pool) | 15.8 (9%) | 15.2 (9%) | 13.3 (7%) | 15.4 (8%) | 45.4 (8%) |
| SQLite engine (libe_sqlite3), writer thread | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) | 100.9 (17%) |
| runtime and BCL with no Campfire caller on the stack | 2.3 (1%) | 11.9 (7%) | 1.4 (1%) | 15.8 (8%) | 64.5 (11%) |
| Microsoft.Data.Sqlite and P/Invoke stubs | 16.9 (10%) | 15.7 (10%) | 15.2 (8%) | 10.6 (5%) | 23.1 (4%) |
| cookies, session, HMAC and key derivation | 13.3 (8%) | 9.0 (5%) | 24.9 (12%) | 10.5 (5%) | 16.5 (3%) |
| allocation: reclamation (F#: GC collections and threads; Rust: none) | 11.9 (7%) | 13.8 (8%) | 10.5 (5%) | 9.0 (4%) | 10.3 (2%) |
| allocation: runtime allocator (F#: CLR allocation helpers; Rust: jemalloc) | 10.0 (6%) | 7.5 (4%) | 9.3 (5%) | 8.3 (4%) | 19.8 (3%) |
| templates, escaping and fragment cache | 12.8 (7%) | 10.1 (6%) | 12.5 (6%) | 9.3 (5%) | 9.1 (2%) |
| controllers and presenters | 5.8 (3%) | 3.9 (2%) | 5.7 (3%) | 7.6 (4%) | 24.7 (4%) |
| kit adapter, Ctx, params, response | 6.5 (4%) | 7.7 (5%) | 6.6 (3%) | 7.6 (4%) | 18.9 (3%) |
| gzip and the page splice | 3.4 (2%) | 1.9 (1%) | 0.2 (0%) | 1.9 (1%) | 36.1 (6%) |
| request log line (console logger) | 7.5 (4%) | 6.8 (4%) | 7.7 (4%) | 8.6 (4%) | 12.9 (2%) |
| rich text | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) | 19.1 (3%) |
| allocation: libc malloc and free | 1.6 (1%) | 1.2 (1%) | 3.1 (2%) | 3.0 (2%) | 6.3 (1%) |
| route table | 1.7 (1%) | 0.7 (0%) | 1.4 (1%) | 0.7 (0%) | 3.3 (1%) |
| front server (cache, headers, timeouts) | 1.0 (1%) | 0.8 (0%) | 1.1 (1%) | 1.0 (0%) | 1.4 (0%) |
| Microsoft.Data.Sqlite and P/Invoke stubs, writer thread | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) | 4.7 (1%) |
| authentication and current-user concerns | 0.6 (0%) | 0.8 (0%) | 0.5 (0%) | 0.6 (0%) | 1.9 (0%) |
| Campfire.Db (queries, row mapping, connection pool), writer thread | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) | 3.5 (1%) |
| Falco and routing | 0.0 (0%) | 0.0 (0%) | 0.1 (0%) | 0.0 (0%) | 0.0 (0%) |

#### Rust CPU per request by crate or library (µs/req and share)

| Layer | room_show | messages_page | sidebar | search | post_message |
|---|---|---|---|---|---|
| SQLite engine (libsqlite3, static) | 40.1 (33%) | 42.1 (37%) | 42.2 (34%) | 58.1 (46%) | 197.3 (49%) |
| hyper, tokio, axum (HTTP and runtime) | 22.5 (18%) | 23.8 (21%) | 21.2 (17%) | 22.5 (18%) | 49.3 (12%) |
| controllers and presenters | 14.5 (12%) | 11.0 (10%) | 13.3 (11%) | 14.5 (12%) | 22.7 (6%) |
| templates, escaping and fragment cache | 14.3 (12%) | 11.7 (10%) | 9.8 (8%) | 6.2 (5%) | 4.8 (1%) |
| allocation: runtime allocator (F#: CLR allocation helpers; Rust: jemalloc) | 6.9 (6%) | 4.6 (4%) | 7.2 (6%) | 5.2 (4%) | 14.9 (4%) |
| Db crate (queries, rows, pool) | 6.8 (6%) | 7.0 (6%) | 6.2 (5%) | 5.9 (5%) | 19.6 (5%) |
| kit (request, response, front server) | 6.5 (5%) | 7.5 (6%) | 5.4 (4%) | 5.9 (5%) | 9.1 (2%) |
| cookies, session, HMAC and SHA | 5.5 (4%) | 3.9 (3%) | 17.7 (14%) | 3.1 (2%) | 25.9 (6%) |
| gzip and the page splice | 4.7 (4%) | 2.8 (2%) | 0.5 (0%) | 2.9 (2%) | 38.1 (10%) |
| allocation: libc malloc and free | 0.5 (0%) | 0.5 (0%) | 1.4 (1%) | 1.9 (2%) | 6.2 (2%) |
| rich text | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) | 0.0 (0%) | 11.3 (3%) |

#### F# threads: share of CPU by thread name

- **room_show**: .NET TP Worker 89.3%, .NET Server GC 6.8%, Console logger 2.5%, campfire-db-rea 0.7%, .NET Sockets 0.7%, .NET Tiered Com 0.0%
- **messages_page**: .NET TP Worker 69.0%, campfire-db-rea 19.5%, .NET Server GC 8.3%, Console logger 2.4%, .NET Sockets 0.8%
- **sidebar**: .NET TP Worker 91.8%, .NET Server GC 5.2%, Console logger 2.4%, .NET Sockets 0.7%
- **search**: .NET TP Worker 48.2%, campfire-db-rea 44.0%, .NET Server GC 4.4%, Console logger 2.6%, .NET Sockets 0.8%
- **post_message**: .NET TP Worker 39.8%, campfire-db-rea 32.7%, campfire-db-wri 19.2%, .NET File Watch 3.7%, Console logger 1.4%, .NET Server GC 1.1%, campfire-db-che 1.1%, .NET BGC 0.6%

#### F# heaviest leaf frames per route (% of the app's CPU)

- **room_show**: `stub InitThreadManagerPerfMapData<JIT_CopiedWriteBarriers>` 2.81%; `pthread_mutex_lock@@GLIBC_2.17` 2.47%; `__GI___pthread_mutex_unlock_usercnt` 2.4%; `libcrypto.so.3 (no symbols)` 2.18%; `_dl_tlsdesc_dynamic` 2.1%; `sqlite3VdbeExec` 1.81%; `try_to_wake_up ([kernel.kallsyms])` 1.55%; `SVR::t_join::join(SVR::gc_heap*, int)` 1.54%
- **messages_page**: `ThreadNative_SpinWait` 4.13%; `pthread_mutex_lock@@GLIBC_2.17` 2.71%; `stub InitThreadManagerPerfMapData<JIT_CopiedWriteBarriers>` 2.47%; `_dl_tlsdesc_dynamic` 2.34%; `__GI___pthread_mutex_unlock_usercnt` 2.3%; `SVR::t_join::join(SVR::gc_heap*, int)` 2.2%; `sqlite3VdbeExec` 1.99%; `_raw_spin_unlock_irqrestore ([kernel.kallsyms])` 1.9%
- **sidebar**: `libcrypto.so.3 (no symbols)` 6.28%; `sqlite3VdbeExec` 3.21%; `_raw_spin_unlock_irqrestore ([kernel.kallsyms])` 2.82%; `pthread_mutex_lock@@GLIBC_2.17` 2.41%; `stub InitThreadManagerPerfMapData<JIT_CopiedWriteBarriers>` 2.36%; `try_to_wake_up ([kernel.kallsyms])` 2.21%; `__GI___pthread_mutex_unlock_usercnt` 1.94%; `el0_svc ([kernel.kallsyms])` 1.64%
- **search**: `ThreadNative_SpinWait` 4.34%; `try_to_wake_up ([kernel.kallsyms])` 3.32%; `el0_svc ([kernel.kallsyms])` 2.93%; `sqlite3VdbeExec` 2.75%; `__GI___lll_lock_wake` 2.19%; `pthread_mutex_lock@@GLIBC_2.17` 2.14%; `stub InitThreadManagerPerfMapData<JIT_CopiedWriteBarriers>` 1.86%; `_raw_spin_unlock_irqrestore ([kernel.kallsyms])` 1.83%
- **post_message**: `libSystem.IO.Compression.Native.so (no symbols)` 5.45%; `ThreadNative_SpinWait` 3.88%; `try_to_wake_up ([kernel.kallsyms])` 2.99%; `el0_svc ([kernel.kallsyms])` 2.75%; `_raw_spin_unlock_irqrestore ([kernel.kallsyms])` 1.87%; `stub InitThreadManagerPerfMapData<JIT_CopiedWriteBarriers>` 1.58%; `sqlite3VdbeExec` 1.57%; `__arch_copy_to_user ([kernel.kallsyms])` 1.49%
