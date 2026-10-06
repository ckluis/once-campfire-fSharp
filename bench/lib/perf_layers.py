#!/usr/bin/env python3
"""Where the F# app's CPU goes, from `perf record -g` of its process (bench/breakdown perf).

  perf_layers.py [--rust] PERF.script LOADGEN.json ROUTE SECS      # prints one JSON object

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

Microseconds per request divide the CPU the samples add up to by the requests the load generator
completed in the same seconds (its rate times SECS). perf's own cost is in the numbers; bench/breakdown cpu
has the unprofiled CPU per request.
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
    ("allocator (jemalloc, libc malloc)", [r"^_rjem", r"^(malloc|free|cfree|realloc|calloc|do_rallocx|do_sdallocx|je_)"]),
]
RUST_COMPILED = [(n, [re.compile(p) for p in ps]) for n, ps in RUST_RULES]
RUST_UNOWNED = "libc, kernel and unnamed code with no Rust frame on the chain"
WRITE_FRAMES = ("DbRun::runWrite", "Checkpoint::run", "Database+thread@423")

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
    for f in frames:
        for layer, pats in COMPILED:
            if any(p.search(f) for p in pats):
                if layer in ("SQLite engine (libe_sqlite3)", "Microsoft.Data.Sqlite and P/Invoke stubs", "Campfire.Db (queries, row mapping, connection pool)") \
                        and any(w in g for g in frames for w in WRITE_FRAMES):
                    return layer + ", writer thread"
                return layer
    if "GC" in comm:
        return "GC (server GC threads)"
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
    for f in frames:
        for layer, pats in RUST_COMPILED:
            if any(p.search(f) for p in pats):
                return layer
    return RUST_UNOWNED


RUST_RESOURCES = [
    ("kernel", re.compile(r"\[kernel\.kallsyms\]")),
    ("SQLite engine", re.compile(r"^(sqlite3|columnMem|columnMallocFailure|vdbe|btree|pcache|pager|wal|memjrnl|lookaside|whereLoop|cursor|moveToRoot|balance|insertCell|allocateBtreeSpace|pthreadMutex)")),
    ("SHA/HMAC/AES (Rust crates)", re.compile(r"\d+sha2|\d+sha1|\d+hmac|\d+aes|\d+ghash|\d+pbkdf2")),
    ("allocator (jemalloc, libc malloc)", re.compile(r"^_rjem|^(malloc|free|cfree|realloc|calloc|do_rallocx|do_sdallocx)\b")),
    ("libc (mutexes, memcpy, other)", re.compile(r"libc\.so|libm\.so")),
]


def rust_resource(frames):
    leaf = frames[0] if frames else ""
    for name, rx in RUST_RESOURCES:
        if rx.search(leaf):
            return name
    return "Rust code (app, hyper, tokio, std)"


def main():
    rust = "--rust" in sys.argv
    argv = [a for a in sys.argv[1:] if a != "--rust"]
    script, lg, route, secs = argv[0], argv[1], argv[2], float(argv[3])
    owner_of, resource_of = (rust_owner, rust_resource) if rust else (owner, resource)
    layers, res, leaves, comms = Counter(), Counter(), Counter(), Counter()
    callers = {"CLR runtime (GC, JIT, type system)": Counter(), "kernel": Counter(), "libc (malloc, mutexes, memcpy)": Counter()}
    total = 0
    for comm, frames in parse(script):
        if not frames:
            continue
        total += 1
        layers[owner_of(comm, frames)] += 1
        res[resource_of(frames)] += 1
        if not rust:
            r = resource(frames)
            if r in callers:
                callers[r][managed_caller(frames) or "(no managed frame on the chain: " + comm + ")"] += 1
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
    print(json.dumps(out))


main()
