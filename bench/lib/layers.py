#!/usr/bin/env python3
"""Per-layer time of the F# app from a dotnet-trace sampled-thread-time profile (bench/breakdown profile).

  layers.py TRACE.speedscope.json [COUNTERS.csv [LOADGEN.json [ROUTE]]]    # prints one JSON object
  layers.py --top N TRACE.speedscope.json                                  # the N heaviest frames, to refine RULES

The trace is every managed thread's stack at a fixed interval, as dotnet-trace converts it to speedscope's
"evented" format; a sample's leaf frame says what the thread was doing: `CPU_TIME` (running managed code,
which includes the SQLite and zlib calls that make no GC transition, and spinning) or
`UNMANAGED_CODE_TIME` (in a native call: waiting in epoll or on a lock, but also native work that
did make the transition). Idle time is left out: an `UNMANAGED_CODE_TIME` sample whose caller is a wait
(thread pool parking, a Monitor.Wait, the reader threads' queue, the logger's dequeue, the file
watcher's read, epoll). What is left is busy time, in thread-milliseconds, and each sample is given to the
first layer in RULES that owns a frame of its stack, searching from the leaf towards the root: a sample inside
Kestrel's parser is Kestrel's even though the kit's adapter called it, one inside System.Text.Json called
by a controller is the controller's.

It measures where threads are, not CPU exactly: a thread that spins, waits for a core or waits for a GC
to suspend it shows as busy. Those are layers of their own (`GC suspension`, `lock contention`,
`thread-pool spin`) so the rest isn't inflated by them. Sampling slows the app by about a quarter, so the
shares are the result, not the milliseconds. bench/breakdown cpu has the CPU per request, unsampled.
"""
import csv, json, re, sys
from collections import Counter

# Frame name is "assembly!Namespace.Type.Method(args)". Rules are tried leaf to root, first layer wins;
# within a layer any pattern matches.
RULES = [
    # runtime-state layers, tested on the few frames next to the leaf only (see classify)
    # layers by owner
    ("Kestrel and ASP.NET Core HTTP", [r"^Microsoft\.AspNetCore\.Server\.Kestrel", r"^System\.IO\.Pipelines", r"^System\.Net\.Sockets",
                                       r"^System\.Net\.Security", r"^System\.Net\.Primitives", r"^System\.Net\.Http", r"^System\.IO\.FileSystem\.Watcher",
                                       r"^Microsoft\.AspNetCore\.(Http|Hosting|Http\.Features|Http\.Abstractions|Connections|WebUtilities)"]),
    ("Falco and routing", [r"^Falco", r"^Microsoft\.AspNetCore\.Routing"]),
    ("SQLite reads", [r"^Campfire\.Db!", r"^Microsoft\.Data\.Sqlite", r"^SQLitePCL"]),
    ("gzip and the page splice", [r"^System\.IO\.Compression", r"Campfire\.Kit!Campfire\.Kit\.(Deflater|Splice|PageParts|Crc|Fragment|Generations)",
                                  r"Campfire\.Kit!Campfire\.Kit\.Front\.Compression"]),
    ("rich text", [r"^Campfire\.RichText"]),
    ("templates and fragment cache", [r"^Campfire\.Views", r"Campfire\.App!Campfire\.App\.FragmentGlue", r"FragmentCache"]),
    ("cookies, session, crypto", [r"Campfire\.Kit!Campfire\.Kit\.(CookieJar|Cookies|Session|Secrets)", r"^Campfire\.RailsCompat",
                                  r"^System\.Security\.Cryptography", r"^BCrypt"]),
    ("authentication", [r"Campfire\.App!Campfire\.App\.Concerns", r"Campfire\.App!Campfire\.App\.Current"]),
    ("request log line (console logger)", [r"^Microsoft\.Extensions\.Logging", r"System\.Console!"]),
    ("front server (cache, headers, timeouts)", [r"Campfire\.Kit!Campfire\.Kit\.Front"]),
    ("route table", [r"Campfire\.App!Campfire\.App\.(RouteTable|Controllers\b|ControllersModule)", r"Campfire\.Routes"]),
    ("kit adapter, Ctx, params, response", [r"^Campfire\.Kit"]),
    ("controllers and presenters", [r"^Campfire\.App", r"^Campfire\.Storage", r"^Campfire\.Assets", r"^Campfire\.Cable", r"^Campfire\.Ruby", r"^campfire!"]),
]
COMPILED = [(name, [re.compile(p) for p in pats]) for name, pats in RULES]

# A managed leaf that is a thread parked or waiting for work: the sampler reports such a thread as running
# (CPU_TIME) or in native code, but it is not using a CPU. Matched against the stack's last managed frame.
IDLE_LEAF = ("WorkerThreadStart()", "LowLevelLifoSemaphore.Wait", "LowLevelLifoSemaphore.WaitForSignal", "LowLevelLifoSemaphore.WaitNative",
             "SocketAsyncEngine.EventLoop", "WriteQueue.Take", "ReadQueue.Next", "ManualResetEventSlim.Wait", "Monitor.Wait", "Monitor.ObjWait",
             "WaitHandle.WaitOne", "ConsoleLoggerProcessor.TryDequeue", "FileSystem.Watcher", "Thread.Sleep", "Task.InternalWait",
             "SemaphoreSlim.Wait", "Interop+Sys.Read(", "WaitForSocketEvents", "BlockingCollection", "GC.RunFinalizers",
             "PortableThreadPool+GateThread", "PollingCounter", "CounterGroup", "RuntimeEventSource", "DestroyScout.Finalize")
SYNTHETIC = ("CPU_TIME", "UNMANAGED_CODE_TIME", "?!?")
# Frames that mark a sample as one of the runtime-state layers when they are the leaf's own callers.
GC_WAIT = ("<PollGC>", "PollGCWorker", "RhpWaitForGC", "GCHeapAffinitize")
LOCK = ("Monitor.Enter_Slowpath", "Monitor.EnterSpin", "Monitor.TryEnter_Slowpath", "Monitor.Exit_Slowpath")
POOL_SPIN = ("LowLevelSpinWaiter.Wait", "ThreadPoolWorkQueue", "PortableThreadPool", "ReleaseSemaphore", "ThreadPool.UnsafeQueueUserWorkItem",
             "LowLevelLifoSemaphore.Release")

# Layers that are waiting (or spinning) rather than work: left out of the CPU shares.
WAIT_LAYERS = ("GC suspension", "lock contention")
UNOWNED = "runtime, tasks and other"


def load(path):
    d = json.load(open(path))
    return [f["name"] for f in d["shared"]["frames"]], d["profiles"]


def classify(names):
    """Layer of a busy sample, or None for idle. `names` is the stack root to leaf."""
    k = len(names) - 1
    while k > 0 and names[k] in SYNTHETIC:
        k -= 1
    managed_leaf = names[k]
    if any(w in managed_leaf for w in IDLE_LEAF):
        return None
    near = names[-4:]
    if any(g in n for n in near for g in GC_WAIT):
        return "GC suspension"
    if any(k in n for n in near for k in LOCK):
        return "lock contention"
    for n in reversed(names):
        for layer, pats in COMPILED:
            if any(p.search(n) for p in pats):
                if layer == "SQLite reads" and any("DbRun.runWrite" in m or "Checkpoint.run" in m or "Database+thread@423" in m for m in names):
                    return "SQLite writes"   # the single writer's thread, its transactions and WAL checkpoints
                return layer
    # an owner-less stack: thread-pool dispatch frames in it mean the pool was moving work
    if any(k in n for n in names[-6:] for k in POOL_SPIN):
        return "thread-pool spin and hand-off"
    return UNOWNED


def analyse(path, top=0):
    F, profiles = load(path)
    layers, excl, incl, lock_owner = Counter(), Counter(), Counter(), Counter()
    by_layer = {}
    total = idle = 0.0
    for p in profiles:
        stack, last = [], p["startValue"]
        for e in p["events"]:
            dt = e["at"] - last
            if dt > 0 and stack:
                names = [F[i] for i in stack]
                total += dt
                layer = classify(names)
                if layer is None:
                    idle += dt
                else:
                    layers[layer] += dt
                    k = len(names) - 1
                    while k > 0 and names[k] in ("CPU_TIME", "UNMANAGED_CODE_TIME"):
                        k -= 1
                    excl[names[k]] += dt
                    by_layer.setdefault(layer, Counter())[names[k]] += dt
                    for n in set(names):
                        incl[n] += dt
                    if layer == "lock contention":
                        owner = next((n for n in reversed(names) if n.startswith(("Campfire", "campfire", "Microsoft.AspNetCore", "Falco"))), "?")
                        lock_owner[owner[:160]] += dt
            last = e["at"]
            if e["type"] == "O":
                stack.append(e["frame"])
            else:
                stack.pop()
    analyse.by_layer = by_layer
    return total, idle, layers, excl, incl, lock_owner


def cpu_shares(layers):
    """Shares of the sampled time that is work or spinning (GC suspension and lock waits left out)."""
    work = sum(v for k, v in layers.items() if k not in WAIT_LAYERS)
    return {k: round(100 * v / work, 1) for k, v in layers.most_common() if k not in WAIT_LAYERS} if work else {}


def counters(path):
    """System.Runtime counters over the window: mean of the gauges, sum of the per-second rates."""
    rows = {}
    try:
        for r in csv.DictReader(open(path)):
            name = r.get("Counter Name", "")
            try:
                v = float(r.get("Mean/Increment", "nan"))
            except ValueError:
                continue
            rows.setdefault(name, []).append(v)
    except OSError:
        return {}
    out = {}
    for name, vals in rows.items():
        unit = re.search(r"\((.*)\)", name)
        unit = unit.group(1) if unit else ""
        out[name] = {"unit": unit, "mean": round(sum(vals) / len(vals), 3), "sum": round(sum(vals), 3), "n": len(vals), "max": round(max(vals), 3),
                     "first": vals[0], "last": vals[-1]}
    return out


def main():
    argv = sys.argv[1:]
    if argv and argv[0] == "--top":
        total, idle, layers, excl, incl, lock_owner = analyse(argv[2])
        print(f"total {total:.0f} ms idle {idle:.0f} busy {total - idle:.0f}")
        for k, v in layers.most_common():
            print(f"{v:9.0f} {100 * v / (total - idle):5.1f}%  {k}")
        print("--- exclusive")
        for k, v in excl.most_common(int(argv[1])):
            print(f"{v:9.0f}  {k[:170]}")
        print("--- inclusive")
        for k, v in incl.most_common(int(argv[1])):
            print(f"{v:9.0f}  {k[:170]}")
        print("--- lock contention by owner")
        for k, v in lock_owner.most_common(10):
            print(f"{v:9.0f}  {k}")
        return
    trace = argv[0]
    total, idle, layers, excl, incl, lock_owner = analyse(trace)
    busy = total - idle
    result = {
        "route": argv[3] if len(argv) > 3 else None,
        "thread_ms_total": round(total), "idle_ms": round(idle), "busy_ms": round(busy),
        "layers_ms": {k: round(v) for k, v in layers.most_common()},
        "layers_pct": {k: round(100 * v / busy, 1) for k, v in layers.most_common()} if busy else {},
        "cpu_layers_pct": cpu_shares(layers),
        "waits_pct_of_busy": {k: round(100 * layers.get(k, 0) / busy, 1) for k in WAIT_LAYERS} if busy else {},
        "top_exclusive": [[k, round(v)] for k, v in excl.most_common(25)],
        "top_inclusive": [[k, round(v)] for k, v in incl.most_common(40)],
        "layer_top_exclusive": {layer: [[k, round(v)] for k, v in c.most_common(6)] for layer, c in analyse.by_layer.items()},
        "lock_contention_by_owner": [[k, round(v)] for k, v in lock_owner.most_common(8)],
    }
    if len(argv) > 1:
        result["counters"] = counters(argv[1])
    if len(argv) > 2:
        try:
            result["loadgen"] = json.load(open(argv[2]))
        except (OSError, ValueError):
            pass
    print(json.dumps(result))


main()
