#!/usr/bin/env python3
"""Where the F# app's CPU goes, from `perf record -g` of its process (bench/breakdown perf).

  perf_layers.py [--rust] [--unprofiled CPURUN.json] PERF.script LOADGEN.json ROUTE SECS      # prints one JSON object

PERF.script is `perf script -F comm,tid,ip,sym,dso` of a task-clock profile (999 Hz per thread, so a
sample is one millisecond of CPU) taken while the load generator held c=16: every sample's call chain, kernel
frames first, leaf first. The chains survive the P/Invoke into SQLite, the kernel and the CLR's own frames
(all have frame pointers), and managed methods are named through perf-<pid>.map (DOTNET_PerfMapEnabled=1;
W^X off so the code is not double-mapped). Methods precompiled into the images that the JIT has not
replaced show as their assembly's file.

Two views of the same samples:

- by layer: a sample belongs to the first layer in RULES that owns a frame of its chain, searching from the leaf
  towards the root. A `memcpy` or a `futex` wake-up under Kestrel's send is Kestrel's; a `malloc` under a
  template is the template's. Threads with no managed frame at all are the runtime's own: the server GC's
  threads (`.NET Server GC`), the JIT and the rest.
- by resource: the leaf's own object (kernel, SQLite's engine, OpenSSL, the CLR, libc, managed code),
  whoever called it.

Microseconds per request, raw: the CPU the samples add up to divided by the requests the load generator completed in the
same seconds (its rate times SECS). perf's own cost is in those numbers (3-14%, more on Rust). With --unprofiled (the
cpurun.py result of an unprofiled window of the same process, route and concurrency: the container's cgroup CPU per
request) every share is also given as `*_us_scaled` = share x the unprofiled CPU per request, which is the figure to
quote: the raw `layers_us_per_request` is kept for comparison with older data only.

Allocation is attributed the same way for both apps, ahead of the layer rules (so it is its own bucket, not the caller's):
  runtime allocator   F#: the CLR's allocation helpers (RhpNew*, JIT_New*, GCHeap::Alloc, gc_heap::allocate_*, adjust_limit_clr
                      and the memset under it, anywhere on the chain); Rust: jemalloc and the __rust_alloc shims
  reclamation         F#: the GC (collections on the server-GC threads, suspension, the join spins); Rust has none: free is
                      in the allocator
  libc malloc/free    both: malloc, free, realloc and glibc's internals (SQLite's, OpenSSL's and the runtime's allocations)
It needs libcoreclr's and libc's symbols (bench/lib/clr-symbols); without them the F# buckets are mostly empty and the CLR's time
stays "[unknown]". Rust's call chains stop at the first Rust frame, so the memcpy a realloc does is in whatever libc names it.
"""
import json, re, sys
from collections import Counter

# (layer, patterns on the frame text). Frame text: "<symbol> (<dso>)", managed symbols "ret [Assembly] Type::Method(args)".
RULES = [
    ("Kestrel and ASP.NET Core HTTP", [r"\[Microsoft\.AspNetCore\.Server\.Kestrel", r"\[System\.IO\.Pipelines\]", r"\[System\.Net\.Sockets\]", r"\[System\.Net\.Security\]",
                                       r"\[Microsoft\.AspNetCore\.(Http|Hosting|Http\.Abstractions|Http\.Features|Connections)", r"/(Microsoft\.AspNetCore\.Server\.Kestrel[^/]*|System\.Net\.Sockets|System\.IO\.Pipelines)\.dll\)"]),
    ("Falco and routing", [r"\[Falco\]", r"\[Microsoft\.AspNetCore\.Routing\]", r"/(Falco|Microsoft\.AspNetCore\.Routing)\.dll\)"]),
    ("SQLite engine (libe_sqlite3)", [r"\(/opt/campfire/libe_sqlite3\.so\)"]),
    ("Microsoft.Data.Sqlite and P/Invoke stubs", [r"\[Microsoft\.Data\.Sqlite\]", r"\[SQLitePCLRaw", r"SQLitePCL\.", r"/(Microsoft\.Data\.Sqlite|SQLitePCLRaw[^/]*)\.dll\)"]),
    ("Campfire.Db (queries, row mapping, connection pool)", [r"\[Campfire\.Db\]", r"/Campfire\.Db\.dll\)"]),
    ("gzip and the page splice", [r"\[System\.IO\.Compression\]", r"Campfire\.Kit\.(Deflater|Splice|PageParts|Crc|Generations)", r"/System\.IO\.Compression[^/]*\.dll\)", r"libz\.so", r"System\.IO\.Compression\.Native"]),
    ("rich text", [r"\[Campfire\.RichText\]", r"/Campfire\.RichText\.dll\)"]),
    ("templates, escaping and fragment cache", [r"\[Campfire\.Views\]", r"\[Campfire\.Ruby\]", r"FragmentGlue", r"/Campfire\.(Views|Ruby)\.dll\)"]),
    ("cookies, session, HMAC and key derivation", [r"\[Campfire\.RailsCompat\]", r"\[System\.Security\.Cryptography\]", r"Campfire\.Kit\.(CookieJar|Cookies|Session|Secrets)", r"libcrypto", r"Cryptography\.Native",
                                                    r"/(Campfire\.RailsCompat|System\.Security\.Cryptography)\.dll\)"]),
    ("authentication and current-user concerns", [r"Campfire\.App\.Concerns", r"Campfire\.App\.Current"]),
    ("request log line (console logger)", [r"\[Microsoft\.Extensions\.Logging", r"\[System\.Console\]", r"/Microsoft\.Extensions\.Logging[^/]*\.dll\)", r"/System\.Console\.dll\)"]),
    ("front server (cache, headers, timeouts)", [r"Campfire\.Kit\.Front"]),
    ("route table", [r"Campfire\.App\.(RouteTable|Controllers\b)", r"\[Campfire\.Routes\]"]),
    ("kit adapter, Ctx, params, response", [r"\[Campfire\.Kit\]", r"/Campfire\.Kit\.dll\)"]),
    ("controllers and presenters", [r"\[campfire\]", r"\[Campfire\.(Storage|Assets|Cable)\]", r"/(campfire|Campfire\.Storage|Campfire\.Assets|Campfire\.Cable)\.dll\)"]),
]
COMPILED = [(n, [re.compile(p) for p in ps]) for n, ps in RULES]
UNOWNED = "runtime and BCL with no Campfire caller on the stack"

# The Rust app's symbols are not stripped but its frames have no frame pointers, so chains stop at the first Rust frame:
# a sample is classified by the first frame of its chain that names a crate or a C library (the leaf's own symbol
# in nearly every case). rustc's v0 mangling carries each crate as <len><name>, e.g. `4sha2`, `11ruby_compat`.
RUST_RULES = [
    ("SQLite engine (libsqlite3, static)", [r"^(sqlite3|columnMem|columnMallocFailure|vdbe|btree|pcache|pager|wal|memjrnl|lookaside|whereLoop|sqlite|cursor|moveToRoot|balance|insertCell|allocateBtreeSpace|pthreadMutex)", r"libsqlite3_sys"]),
    ("Db crate (queries, rows, pool)", [r"\d+campfire_db", r"\d+rusqlite"]),
    ("gzip and the page splice", [r"zlib_rs", r"flate2", r"crc32fast", r"\d+deflater", r"\d+splice"]),
    ("cookies, session, HMAC and SHA", [r"\d+sha2", r"\d+sha1", r"\d+hmac", r"\d+pbkdf2", r"\d+aes", r"\d+ghash", r"\d+cipher", r"\d+rails_compat", r"\d+base64", r"\d+bcrypt"]),
    ("templates, escaping and fragment cache", [r"\d+askama", r"\d+ruby_compat", r"\d+campfire_views", r"\d+views\d"]),
    ("rich text", [r"\d+campfire_richtext", r"\d+richtext", r"html5ever", r"markup5ever"]),
    ("hyper, tokio, axum (HTTP and runtime)", [r"\d+hyper", r"\d+tokio", r"\d+axum", r"\d+tower", r"\d+http\d", r"\d+h2", r"\d+mio", r"\d+bytes"]),
    ("kit (request, response, front server)", [r"\d+campfire_kit", r"\d+kit\d"]),
    ("controllers and presenters", [r"\d+campfire\d", r"_8campfire", r"\d+campfire_cable", r"\d+campfire_storage", r"\d+campfire_assets"]),
]
RUST_COMPILED = [(n, [re.compile(p) for p in ps]) for n, ps in RUST_RULES]
RUST_UNOWNED = "libc, kernel and unnamed code with no Rust frame on the chain"
WRITE_FRAMES = ("DbRun::runWrite", "Checkpoint::run", "Database+thread@423")

ALLOC_LAYER = {"alloc": "allocation: runtime allocator (F#: CLR allocation helpers; Rust: jemalloc)",
               "gc": "allocation: reclamation (F#: GC collections and threads; Rust: none)",
               "libc": "allocation: libc malloc and free"}
CLR_ALLOC = re.compile(r"^(RhpNew\w*|RhNew\w*|RhpGcAlloc\w*|JIT_New\w*|JIT_Box\w*|JIT_Alloc\w*|AllocateObject\w*|AllocateSzArray|AllocateArray|AllocateString|"
                       r"Alloc\(ee_alloc_context|(SVR|WKS)::GCHeap::Alloc|(SVR|WKS)::gc_heap::(allocate_\w+|try_allocate_more_space|a_fit_\w+|soh_try_fit|uoh_try_fit|"
                       r"adjust_limit\w*|balance_heaps|make_unused_array|trigger_gc_for_alloc))")
CLR_GC = re.compile(r"(garbage_collect|gc_heap::gc1|gc_thread_function|GcScanRoots|SuspendEE|RestartEE|GCHeap::GarbageCollect|t_join::join)")
LIBC_MALLOC = re.compile(r"^(malloc|free|calloc|realloc|memalign|posix_memalign|aligned_alloc|cfree|_int_malloc|_int_free\w*|_int_realloc|_int_memalign|_mid_memalign|malloc_consolidate|"
                         r"sysmalloc|tcache_\w+|__libc_malloc\w*|__libc_free|__libc_calloc|__libc_realloc|unlink_chunk\w*|arena_get2|alloc_perturb|malloc_trim)\b.*libc\.so")
RUST_ALLOC = re.compile(r"^(_rjem_\w+|je_\w+|__rust_(alloc|dealloc|realloc|alloc_zeroed)\b|_RNv\w*__rust_(alloc|dealloc|realloc|alloc_zeroed))")


def alloc_bucket(comm, frames, rust):
    """'gc', 'alloc' or 'libc' for a sample that is the allocator's or the collector's work, else None (precedence in this order)."""
    if not rust:
        if "GC" in comm or any(CLR_GC.search(f) for f in frames):
            return "gc"
        if any(CLR_ALLOC.search(f) for f in frames):
            return "alloc"
    elif any(RUST_ALLOC.search(f) for f in frames):
        return "alloc"
    if any(LIBC_MALLOC.search(f) for f in frames):
        return "libc"
    return None


# What the CLR's own code (libcoreclr, libclrjit) is doing, by the leaf symbol and the chain: the finer split of "CLR runtime".
CLR_CLASSES = [
    ("JIT, tiering and PGO instrumentation", re.compile(r"TieredCompilation|JitCompile|PrepareILBasedCode|UnsafeJitFunction|JIT_CountProfile|JIT_ClassProfile|\(.*libclrjit")),
    ("P/Invoke transitions", re.compile(r"^(JIT_InitPInvokeFrame|JIT_PInvoke\w*|InlinedCallFrame|JIT_PopInlinedCallFrame|JIT_RareDisableHelper|Thread::RareDisablePreemptiveGC)")),
    ("monitors, spinning and waits", re.compile(r"^(ThreadNative_SpinWait|ObjHeader::\w*Monitor\w*|ObjectNative::Monitor\w*|AwareLock::\w+|JIT_MonEnter\w*|JIT_MonExit\w*|SyncBlock::\w+|Lse_Interlocked\w+|COMInterlocked::\w+|YieldProcessor\w*)")),
    ("type checks, statics and write barriers", re.compile(r"^(JIT_ChkCast\w*|JIT_IsInstanceOf\w*|JIT_GetSharedNonGCThreadStaticBase\w*|JIT_GetNonGCThreadStaticBase\w*|JIT_GetGCThreadStaticBase\w*|JIT_GetDynamic\w*|ErectWriteBarrier|JIT_WriteBarrier\w*|JIT_CheckedWriteBarrier|JIT_ByRefWriteBarrier|InlinedMemmoveGCRefsHelper|JIT_MemSet|JIT_MemCpy|GetDynamicGCThreadStaticBase|ThreadStatics::\w+)")),
    ("stack walks and exception unwinding", re.compile(r"(StackWalk|RtlpUnwind|EECodeManager|GcInfoDecoder|GcSlotDecoder|EnumGcRefs|UnwindManagedFrame|ExceptionTracker)")),
]


def clr_class(comm, frames):
    """For a sample whose leaf is in the CLR's native code: allocation, reclamation, or what else the runtime does."""
    b = alloc_bucket(comm, frames, False)
    if b == "gc":
        return "reclamation (GC)"
    if b == "alloc":
        return "allocation helpers"
    for name, rx in CLR_CLASSES:
        if rx.search(frames[0]) or (name.startswith("JIT") and any(rx.search(f) for f in frames[:12])):
            return name
    return "other runtime (thread-pool dispatch, stubs, type loads, ...)"


RESOURCES = [
    ("kernel", re.compile(r"\[kernel\.kallsyms\]|\(\[kernel")),
    ("SQLite engine", re.compile(r"libe_sqlite3")),
    ("OpenSSL", re.compile(r"libcrypto|libssl|Cryptography\.Native")),
    ("CLR runtime (GC, JIT, type system)", re.compile(r"libcoreclr|libclrjit|libclrgc|ld-linux|libstdc\+\+")),
    ("libc (malloc, mutexes, memcpy)", re.compile(r"libc\.so|libm\.so|libpthread")),
    ("System.Native shim", re.compile(r"libSystem\.Native")),
]


def parse(path):
    """Yield (comm, [frame text, ...]) per sample, leaf first."""
    comm, frames = None, []
    for line in open(path, errors="replace"):
        line = line.rstrip("\n")
        if not line.strip():
            if comm is not None:
                yield comm, frames
            comm, frames = None, []
        elif not line[0].isspace():
            comm, frames = line.rsplit(None, 1)[0].strip() if line.split() else line, []
        else:
            parts = line.strip().split(None, 1)
            if len(parts) == 2:
                frames.append(parts[1])
    if comm is not None:
        yield comm, frames


def owner(comm, frames):
    b = alloc_bucket(comm, frames, False)
    if b:
        return ALLOC_LAYER[b]
    for f in frames:
        for layer, pats in COMPILED:
            if any(p.search(f) for p in pats):
                if layer in ("SQLite engine (libe_sqlite3)", "Microsoft.Data.Sqlite and P/Invoke stubs", "Campfire.Db (queries, row mapping, connection pool)") \
                        and any(w in g for g in frames for w in WRITE_FRAMES):
                    return layer + ", writer thread"
                return layer
    return UNOWNED


MANAGED = re.compile(r"\[([A-Za-z0-9_.]+)\] (.+?)(?:\(|\[Optimized|\[Tier|$)")


def managed_caller(frames):
    """The nearest managed frame of a chain, as `Type::Method`, for samples whose leaf is native."""
    for f in frames:
        m = MANAGED.search(f)
        if m and m.group(1) not in ("unknown", "kernel.kallsyms", "vdso") and m.group(2).strip() and "stub " not in f.split("[")[0]:
            name = m.group(2).strip()
            name = re.sub(r"<[^>]*>", "", name)
            return name[-110:]
        m = re.search(r"/opt/campfire/([A-Za-z0-9_.]+\.dll)\)", f)
        if m:
            return m.group(1) + " (precompiled, unnamed)"
    return None


def resource(frames):
    leaf = frames[0] if frames else ""
    for name, rx in RESOURCES:
        if rx.search(leaf):
            return name
    return "managed code (JIT and precompiled)"


def rust_owner(comm, frames):
    b = alloc_bucket(comm, frames, True)
    if b:
        return ALLOC_LAYER[b]
    for f in frames:
        for layer, pats in RUST_COMPILED:
            if any(p.search(f) for p in pats):
                return layer
    return RUST_UNOWNED


RUST_RESOURCES = [
    ("kernel", re.compile(r"\[kernel\.kallsyms\]")),
    ("SQLite engine", re.compile(r"^(sqlite3|columnMem|columnMallocFailure|vdbe|btree|pcache|pager|wal|memjrnl|lookaside|whereLoop|cursor|moveToRoot|balance|insertCell|allocateBtreeSpace|pthreadMutex)")),
    ("SHA/HMAC/AES (Rust crates)", re.compile(r"\d+sha2|\d+sha1|\d+hmac|\d+aes|\d+ghash|\d+pbkdf2")),
    ("allocator (jemalloc, libc malloc)", re.compile(r"^_rjem|^(malloc|free|cfree|realloc|calloc|do_rallocx|do_sdallocx)\b|^je_")),
    ("libc (mutexes, memcpy, other)", re.compile(r"libc\.so|libm\.so")),
]


def rust_resource(frames):
    leaf = frames[0] if frames else ""
    for name, rx in RUST_RESOURCES:
        if rx.search(leaf):
            return name
    return "Rust code (app, hyper, tokio, std)"


CLR_DSO = re.compile(r"libcoreclr|libclrjit|libclrgc")


def main():
    rust = "--rust" in sys.argv
    argv = [a for a in sys.argv[1:] if a != "--rust"]
    unprofiled = None
    if "--unprofiled" in argv:
        i = argv.index("--unprofiled")
        unprofiled = json.load(open(argv[i + 1]))
        argv = argv[:i] + argv[i + 2:]
    script, lg, route, secs = argv[0], argv[1], argv[2], float(argv[3])
    owner_of, resource_of = (rust_owner, rust_resource) if rust else (owner, resource)
    layers, res, leaves, comms, clr = Counter(), Counter(), Counter(), Counter(), Counter()
    buckets = Counter()
    callers = {"CLR runtime (GC, JIT, type system)": Counter(), "kernel": Counter(), "libc (malloc, mutexes, memcpy)": Counter()}
    total = 0
    for comm, frames in parse(script):
        if not frames:
            continue
        total += 1
        layers[owner_of(comm, frames)] += 1
        res[resource_of(frames)] += 1
        buckets[alloc_bucket(comm, frames, rust) or "none"] += 1
        if not rust:
            r = resource(frames)
            if r in callers:
                callers[r][managed_caller(frames) or "(no managed frame on the chain: " + comm + ")"] += 1
            if CLR_DSO.search(frames[0]):
                clr[clr_class(comm, frames)] += 1
        leaf = re.sub(r"^\S+\s+", "", frames[0]) if not frames[0].startswith(("[unknown]",)) else frames[0]
        leaves[frames[0][:140]] += 1
        comms[comm] += 1
    load = json.load(open(lg))
    requests = load["rps"] * secs
    cpu_us = total * 1000.0 / 0.999   # 999 Hz: one sample is 1.001 ms of CPU
    out = {
        "app": "rust" if rust else "fsharp", "route": route, "samples": total, "secs": secs, "cores": round(cpu_us / 1e6 / secs, 2), "rps": load["rps"],
        "us_per_request": round(cpu_us / requests, 1) if requests else None,
        "layers_pct": {k: round(100 * v / total, 1) for k, v in layers.most_common()},
        "layers_us_per_request": {k: round(v * 1000.0 / 0.999 / requests, 1) for k, v in layers.most_common()} if requests else {},
        "resource_pct": {k: round(100 * v / total, 1) for k, v in res.most_common()},
        "threads_pct": {k: round(100 * v / total, 1) for k, v in comms.most_common(8)},
        "native_callers": {k: [[n, round(100 * v / total, 2)] for n, v in c.most_common(10)] for k, c in callers.items()},
        "top_leaves": [[k, round(100 * v / total, 2)] for k, v in leaves.most_common(25)],
        "loadgen": load,
    }
    if unprofiled:
        cost = unprofiled["cost"]
        u = cost["server_cpu_us_per_req"]
        scaled = lambda counter: {k: round(v * u / total, 2) for k, v in counter.most_common()}
        out["unprofiled_us_per_request"] = u
        out["unprofiled_cores"] = cost["server_cores_busy"]
        out["unprofiled_rps"] = unprofiled["rps"]
        out["perf_overhead_pct"] = round(100 * (out["us_per_request"] / u - 1), 1) if out["us_per_request"] else None
        out["layers_us_scaled"] = scaled(layers)
        out["resource_us_scaled"] = scaled(res)
        out["allocation_us_scaled"] = {
            "runtime allocator": round(buckets["alloc"] * u / total, 2),
            "reclamation (GC)": round(buckets["gc"] * u / total, 2),
            "libc malloc/free": round(buckets["libc"] * u / total, 2),
            "total": round((buckets["alloc"] + buckets["gc"] + buckets["libc"]) * u / total, 2),
        }
        if clr:
            out["clr_us_scaled"] = scaled(clr)
    print(json.dumps(out))


main()
