#!/usr/bin/env python3
"""Tables from a bench/breakdown results directory.

  breakdown_report.py DIR [--nolog DIR2] [--perf DIR3] [--identity DIR4]

DIR holds `<app>-<rep>.json` from `bench/breakdown cpu` (CPU per request, Rust and F#) and
`profile-<route>.json` from `bench/breakdown profile` (F# time per layer, runtime counters). --nolog
names a cpu directory made with LOG_REQUESTS=false for both apps, for the cost of the request log line. --perf
names a `bench/breakdown perf` directory (`perf-<app>-<route>.json`, CPU per layer from perf for F# and Rust).
--identity names a cpu directory made with --identity (Accept-Encoding: identity).
"""
import glob, json, os, re, statistics, sys

ROUTES = ["room_show", "messages_page", "sidebar", "search", "post_message", "up", "static_css", "avatar"]
NAME = {"rust": "Rust", "fsharp": "F#"}


def load_cpu(d):
    runs = {}
    for f in sorted(glob.glob(os.path.join(d, "*-[0-9].json"))):
        r = json.load(open(f))
        for run in r["runs"]:
            runs.setdefault((r["app"], run["route"], run["conc"]), []).append((r["rep"], run))
    return runs


def med(vals):
    vals = [v for v in vals if v is not None]
    return (statistics.median(vals), min(vals), max(vals)) if vals else (None, None, None)


def fmt(m, digits=1):
    if m[0] is None:
        return "–"
    s = f"{m[0]:,.{digits}f}"
    return s + (f" [{m[1]:,.{digits}f}–{m[2]:,.{digits}f}]" if m[1] != m[2] else "")


def cpu_table(runs):
    print("| Route | c | Rust CPU µs/req | F# CPU µs/req | F# / Rust | Rust user+sys | F# user+sys | Rust req/s | F# req/s | Cores busy: Rust / F# server, load generator |")
    print("|---|---|---|---|---|---|---|---|---|---|")
    for route in ROUTES:
        for c in (16, 1):
            row = {}
            for app in ("rust", "fsharp"):
                rs = [r for _, r in runs.get((app, route, c), [])]
                if not rs:
                    continue
                row[app] = {
                    "cpu": med([r["cost"]["server_cpu_us_per_req"] for r in rs]),
                    "user": med([r["cost"]["server_user_us_per_req"] for r in rs]),
                    "sys": med([r["cost"]["server_system_us_per_req"] for r in rs]),
                    "rps": med([r["rps"] for r in rs]),
                    "cores": med([r["cost"]["server_cores_busy"] for r in rs]),
                    "lg": med([r["cost"]["loadgen_cores_busy"] for r in rs]),
                    "bad": sum(r["errors"] + sum(v for k, v in r["statuses"].items() if int(k) >= 400) for r in rs),
                    "rows": [r.get("rows_written") for r in rs] if route == "post_message" else None,
                    "ok": [r["ok"] for r in rs],
                }
            if "rust" not in row or "fsharp" not in row:
                continue
            ratio = row["fsharp"]["cpu"][0] / row["rust"]["cpu"][0]
            lg = max(row["rust"]["lg"][0], row["fsharp"]["lg"][0])
            flag = " (load generator near its 4 cores: not a server limit)" if lg >= 3.0 else ""
            print(f"| {route} | {c} | {fmt(row['rust']['cpu'])} | {fmt(row['fsharp']['cpu'])} | {ratio:.2f}× | "
                  f"{row['rust']['user'][0]:.0f}+{row['rust']['sys'][0]:.0f} | {row['fsharp']['user'][0]:.0f}+{row['fsharp']['sys'][0]:.0f} | "
                  f"{row['rust']['rps'][0]:,.0f} | {row['fsharp']['rps'][0]:,.0f} | "
                  f"{row['rust']['cores'][0]:.2f} / {row['fsharp']['cores'][0]:.2f}, {lg:.2f}{flag} |")
            for app in ("rust", "fsharp"):
                if row[app]["bad"]:
                    print(f"| **{NAME[app]} {route} c={c}: {row[app]['bad']} non-2xx/3xx answers or errors** | | | | | | | | | |")
                if row[app]["rows"] is not None and row[app]["rows"] != row[app]["ok"]:
                    print(f"| **{NAME[app]} post_message c={c}: rows written {row[app]['rows']} != answers {row[app]['ok']}** | | | | | | | | | |")


def profile_tables(d):
    profiles = {}
    for r in ROUTES:
        f = os.path.join(d, f"profile-{r}.json")
        if os.path.exists(f):
            profiles[r] = json.load(open(f))
    if not profiles:
        return
    layers = []
    for p in profiles.values():
        for k in p["cpu_layers_pct"]:
            if k not in layers:
                layers.append(k)
    totals = {k: sum(p["cpu_layers_pct"].get(k, 0) for p in profiles.values()) for k in layers}
    layers.sort(key=lambda k: -totals[k])
    print("\n#### F# time per layer: share of the sampled thread time that is work or spinning (waits for GC and locks left out; %)\n")
    print("| Layer | " + " | ".join(profiles) + " |")
    print("|---|" + "---|" * len(profiles))
    for k in layers:
        print(f"| {k} | " + " | ".join(f"{p['cpu_layers_pct'].get(k, 0):.1f}" for p in profiles.values()) + " |")
    print("| *GC suspension, % of all busy samples* | " + " | ".join(f"{p['waits_pct_of_busy'].get('GC suspension', 0):.1f}" for p in profiles.values()) + " |")
    print("| *lock contention, % of all busy samples* | " + " | ".join(f"{p['waits_pct_of_busy'].get('lock contention', 0):.1f}" for p in profiles.values()) + " |")

    print("\n#### F# runtime counters while profiled (c=16, tracing on, so throughput is lower than unprofiled)\n")
    print("| Route | req/s | allocated KB/req | gen0 GCs/s | gen1 | gen2 | GC pause (% of wall) | lock contentions/req | thread-pool work items/req | process CPU cores |")
    print("|---|---|---|---|---|---|---|---|---|---|")
    for route, p in profiles.items():
        c, lg = p.get("counters", {}), p.get("loadgen", {})
        rps = lg.get("rps")

        def mean(prefix):
            for k, v in c.items():
                if k.startswith(prefix):
                    return v["mean"]
            return None
        alloc = mean("dotnet.gc.heap.total_allocated")
        gen = lambda g: mean(f"dotnet.gc.collections ({{collection}} / 1 sec)[gc.heap.generation={g}]")
        pause = mean("dotnet.gc.pause.time")
        lock = mean("dotnet.monitor.lock_contentions")
        work = mean("dotnet.thread_pool.work_item.count")
        cpu = (mean("dotnet.process.cpu.time (s / 1 sec)[cpu.mode=user]") or 0) + (mean("dotnet.process.cpu.time (s / 1 sec)[cpu.mode=system]") or 0)
        per = lambda x: f"{x / rps:,.2f}" if x is not None and rps else "–"
        print(f"| {route} | {rps:,.0f} | {per(alloc / 1024 if alloc else None)} | {gen('gen0') or 0:.0f} | {gen('gen1') or 0:.0f} | {gen('gen2') or 0:.0f} | "
              f"{100 * pause:.1f} | {per(lock)} | {per(work)} | {cpu:.2f} |")

    print("\n#### Heaviest F# frames per route (exclusive, thread-ms; the leaf-most managed frame of work samples)\n")
    for route, p in profiles.items():
        print(f"- **{route}**: " + "; ".join(f"`{k.split('!')[-1][:70]}` {v:,}" for k, v in p["top_exclusive"][:6] if "CPU_TIME" not in k))


def nolog_table(base, nolog):
    a, b = load_cpu(base), load_cpu(nolog)
    print("\n| Route | c | Rust µs/req | Rust, log off | change | F# µs/req | F# , log off | change |")
    print("|---|---|---|---|---|---|---|---|")
    for route in ROUTES:
        c = 16
        cells = []
        for app in ("rust", "fsharp"):
            on = [r["cost"]["server_cpu_us_per_req"] for _, r in a.get((app, route, c), [])]
            off = [r["cost"]["server_cpu_us_per_req"] for _, r in b.get((app, route, c), [])]
            if not on or not off:
                cells += ["–", "–", "–"]
                continue
            m_on, m_off = statistics.median(on), statistics.median(off)
            cells += [f"{m_on:.1f}", f"{m_off:.1f}", f"{100 * (m_off / m_on - 1):+.0f}%"]
        print(f"| {route} | {c} | " + " | ".join(cells) + " |")


GROUPS = [
    ("SQLite engine", lambda a, k: k.startswith("SQLite engine")),
    ("Database access around the engine (wrapper, rows, pool)", lambda a, k: k.startswith(("Microsoft.Data.Sqlite", "Campfire.Db", "Db crate"))),
    ("cookies, session, HMAC, key derivation", lambda a, k: k.startswith("cookies, session")),
    ("templates, escaping, fragment cache", lambda a, k: k.startswith("templates")),
    ("gzip and the page splice", lambda a, k: k.startswith("gzip")),
    ("HTTP server and request plumbing (server, front, kit, routing, log line)",
     lambda a, k: k.startswith(("Kestrel", "Falco", "front server", "request log line", "kit ", "route table", "hyper, tokio")) or k.startswith("kit (")),
    ("controllers, presenters, authentication, rich text", lambda a, k: k.startswith(("controllers", "authentication", "rich text"))),
    ("memory management and runtime (F#: GC threads, CLR/BCL without a Campfire caller; Rust: allocator)",
     lambda a, k: k.startswith(("GC (", "runtime and BCL", "allocator", "libc, kernel and unnamed"))),
]


def perf_low(low):
    if low:
        print("\n#### One connection (c=1): CPU per request by resource, Rust / F# (µs/req)\n")
        print("| Route | req/s Rust / F# | CPU µs/req Rust / F# | cores Rust / F# | kernel | CLR runtime, GC, JIT (F#) / allocator (Rust) | libc | managed (F#) / Rust code |")
        print("|---|---|---|---|---|---|---|---|")
        for route in sorted({r for _, r in low}):
            a, b = low.get(("rust", route)), low.get(("fsharp", route))
            if not a or not b:
                continue
            u = lambda x, *keys: sum(x["resource_pct"].get(k, 0) for k in keys) * x["us_per_request"] / 100
            print(f"| {route} | {a['rps']:,.0f} / {b['rps']:,.0f} | {a['us_per_request']:.0f} / {b['us_per_request']:.0f} | {a['cores']} / {b['cores']} | "
                  f"{u(a, 'kernel'):.0f} / {u(b, 'kernel'):.0f} | {u(a, 'allocator (jemalloc, libc malloc)'):.0f} / {u(b, 'CLR runtime (GC, JIT, type system)'):.0f} | "
                  f"{u(a, 'libc (mutexes, memcpy, other)'):.0f} / {u(b, 'libc (malloc, mutexes, memcpy)', 'System.Native shim'):.0f} | "
                  f"{u(a, 'Rust code (app, hyper, tokio, std)'):.0f} / {u(b, 'managed code (JIT and precompiled)'):.0f} |")
        for route in sorted({r for _, r in low}):
            b = low.get(("fsharp", route))
            if b:
                print(f"\n- F# {route} c=1 threads: " + ", ".join(f"{k} {v}%" for k, v in b["threads_pct"].items()))


def perf_tables(d):
    data = {}
    low = {}
    for f in glob.glob(os.path.join(d, "perf-*-*.json")):
        r = json.load(open(f))
        (data if r["loadgen"].get("conc") == 16 else low)[(r["app"], r["route"])] = r
    if not data:
        return
    routes = [r for r in ROUTES if ("fsharp", r) in data and ("rust", r) in data]
    print("\n| Route | Rust µs/req (cores) | F# µs/req (cores) | F# / Rust | Rust req/s | F# req/s |")
    print("|---|---|---|---|---|---|")
    for r in routes:
        a, b = data[("rust", r)], data[("fsharp", r)]
        print(f"| {r} | {a['us_per_request']:.1f} ({a['cores']}) | {b['us_per_request']:.1f} ({b['cores']}) | {b['us_per_request'] / a['us_per_request']:.2f}× | {a['rps']:,.0f} | {b['rps']:,.0f} |")

    print("\n#### CPU per request by what the code is, Rust against F# (µs/req; perf of each app's process at c=16)\n")
    print("Each cell is Rust / F#. Samples are given to the layer that owns the nearest frame of the call chain (F#: the chain is complete, native callees included; Rust: frames have no frame pointers, so the leaf's own symbol or the first named crate on the chain decides, and libc, kernel and allocator time under a Rust frame lands in *libc, kernel and unnamed code* or *allocator*).\n")
    print("| Group | " + " | ".join(routes) + " |")
    print("|---|" + "---|" * len(routes))
    for name, match in GROUPS:
        cells = []
        for r in routes:
            vals = []
            for app in ("rust", "fsharp"):
                x = data[(app, r)]
                vals.append(sum(v for k, v in x["layers_us_per_request"].items() if match(app, k)))
            cells.append(f"{vals[0]:.1f} / {vals[1]:.1f}")
        print(f"| {name} | " + " | ".join(cells) + " |")
    print("| **total** | " + " | ".join(f"{data[('rust', r)]['us_per_request']:.1f} / {data[('fsharp', r)]['us_per_request']:.1f}" for r in routes) + " |")

    print("\n#### CPU per request by resource, Rust against F# (µs/req; the leaf's own object, whoever called it)\n")
    names = ["kernel", "SQLite engine", "crypto: OpenSSL (F#) / SHA, HMAC, AES crates (Rust)", "memory: CLR runtime, GC, JIT (F#) / allocator (Rust)", "libc (mutexes, memcpy, other)", "managed code (F#) / Rust code"]

    def res(app, x, name):
        t = x["resource_us"]
        if name == "kernel": return t.get("kernel", 0)
        if name == "SQLite engine": return t.get("SQLite engine", 0)
        if name.startswith("crypto"): return t.get("OpenSSL", 0) + t.get("SHA/HMAC/AES (Rust crates)", 0)
        if name.startswith("memory"): return t.get("CLR runtime (GC, JIT, type system)", 0) + t.get("allocator (jemalloc, libc malloc)", 0)
        if name.startswith("libc"): return t.get("libc (malloc, mutexes, memcpy)", 0) + t.get("libc (mutexes, memcpy, other)", 0) + t.get("System.Native shim", 0)
        return t.get("managed code (JIT and precompiled)", 0) + t.get("Rust code (app, hyper, tokio, std)", 0)
    for x in data.values():
        x["resource_us"] = {k: v * x["us_per_request"] / 100 for k, v in x["resource_pct"].items()}
    print("| Resource | " + " | ".join(routes) + " |")
    print("|---|" + "---|" * len(routes))
    for name in names:
        print(f"| {name} | " + " | ".join(f"{res('rust', data[('rust', r)], name):.1f} / {res('fsharp', data[('fsharp', r)], name):.1f}" for r in routes) + " |")

    print("\n#### F# CPU per request by layer (µs/req and share of the app's CPU)\n")
    layers = []
    for r in routes:
        for k in data[("fsharp", r)]["layers_us_per_request"]:
            if k not in layers:
                layers.append(k)
    layers.sort(key=lambda k: -sum(data[("fsharp", r)]["layers_us_per_request"].get(k, 0) for r in routes if r in ("room_show", "messages_page", "sidebar", "search", "post_message")))
    print("| Layer | " + " | ".join(routes) + " |")
    print("|---|" + "---|" * len(routes))
    for k in layers:
        print(f"| {k} | " + " | ".join(f"{data[('fsharp', r)]['layers_us_per_request'].get(k, 0):.1f} ({data[('fsharp', r)]['layers_pct'].get(k, 0):.0f}%)" for r in routes) + " |")

    print("\n#### Rust CPU per request by crate or library (µs/req and share)\n")
    layers = []
    for r in routes:
        for k in data[("rust", r)]["layers_us_per_request"]:
            if k not in layers:
                layers.append(k)
    print("| Layer | " + " | ".join(routes) + " |")
    print("|---|" + "---|" * len(routes))
    for k in layers:
        print(f"| {k} | " + " | ".join(f"{data[('rust', r)]['layers_us_per_request'].get(k, 0):.1f} ({data[('rust', r)]['layers_pct'].get(k, 0):.0f}%)" for r in routes) + " |")

    print("\n#### F# threads: share of CPU by thread name\n")
    for r in routes:
        print(f"- **{r}**: " + ", ".join(f"{k} {v}%" for k, v in data[("fsharp", r)]["threads_pct"].items()))

    print("\n#### F# heaviest leaf frames per route (% of the app's CPU)\n")
    strip = re.compile(r" \(/.*$")
    for r in routes:
        def label(k):
            m = re.match(r"\[unknown\] \((.*/)?([^/)]+)\)", k)
            return f"{m.group(2)} (no symbols)" if m else strip.sub("", k)[-95:]
        leaves = [(label(k), v) for k, v in data[("fsharp", r)]["top_leaves"][:8]]
        print(f"- **{r}**: " + "; ".join(f"`{name}` {v}%" for name, v in leaves))
    perf_low(low)


def main():
    argv = sys.argv[1:]
    d = argv[0]
    print("### CPU per request (cgroup cpu.stat of the container, unprofiled; median [min–max] over reps)\n")
    cpu_table(load_cpu(d))
    profile_tables(d)
    if "--identity" in argv:
        print("\n### Identity bodies (Accept-Encoding: identity), CPU per request\n")
        cpu_table(load_cpu(argv[argv.index("--identity") + 1]))
    if "--perf" in argv:
        print("\n### Where the CPU goes, from perf (c=16)\n")
        perf_tables(argv[argv.index("--perf") + 1])
    if "--nolog" in argv:
        print("\n### The request log line: CPU per request at c=16 with LOG_REQUESTS=false (one rep) against the default runs\n")
        nolog_table(d, argv[argv.index("--nolog") + 1])


main()
