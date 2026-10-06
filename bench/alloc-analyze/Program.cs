using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

// usage: AllocAnalyze <trace.nettrace> <requests> [top]
// Prints, per request: bytes by type, bytes by the first Campfire/FSharp.Core/ASP.NET frame that is not a bare runtime frame
// ("allocating method"), and by the nearest Campfire.* frame ("owner"). GCAllocationTick fires about every 100 KB per allocation
// context, and its AllocationAmount64 is the bytes since the last tick, so the sums are unbiased estimates.
class P
{
    static void Main(string[] a)
    {
        var path = a[0]; double reqs = double.Parse(a[1]); int top = a.Length > 2 ? int.Parse(a[2]) : 25;
        var etlx = TraceLog.CreateFromEventPipeDataFile(path);
        using var log = new TraceLog(etlx);
        var byType = new Dictionary<string, long>(); var byMethod = new Dictionary<string, long>(); var byOwner = new Dictionary<string, long>();
        var byTypeMethod = new Dictionary<string, long>();
        long total = 0, ticks = 0, unresolved = 0;
        foreach (var ev in log.Events)
        {
            if (ev is not GCAllocationTickTraceData t) continue;
            long amt = t.AllocationAmount64; total += amt; ticks++;
            string type = string.IsNullOrEmpty(t.TypeName) ? "(unknown)" : t.TypeName;
            Add(byType, type, amt);
            string first = null, owner = null;
            var cs = ev.CallStack();
            for (var f = cs; f != null; f = f.Caller)
            {
                var m = f.CodeAddress.FullMethodName;
                if (string.IsNullOrEmpty(m)) continue;
                if (first == null) first = m;
                if (owner == null && (m.StartsWith("Campfire") || m.Contains("Campfire."))) { owner = m; break; }
            }
            if (first == null) { unresolved += amt; first = "(unresolved)"; }
            Add(byMethod, first, amt); Add(byOwner, owner ?? "(no Campfire frame)", amt);
            Add(byTypeMethod, type + "  <-  " + (owner ?? first), amt);
        }
        Console.WriteLine($"ticks {ticks}, bytes {total}, per request {total / reqs:F0}, unresolved frames {unresolved * 100.0 / Math.Max(1, total):F1}%");
        Print("by type (bytes per request)", byType, reqs, top);
        Print("by allocating method (first frame)", byMethod, reqs, top);
        Print("by nearest Campfire frame", byOwner, reqs, top);
        Print("by type <- nearest Campfire frame", byTypeMethod, reqs, top);
    }
    static void Add(Dictionary<string, long> d, string k, long v) { d.TryGetValue(k, out var x); d[k] = x + v; }
    static void Print(string title, Dictionary<string, long> d, double reqs, int top)
    {
        Console.WriteLine("\n== " + title);
        long sum = d.Values.Sum();
        foreach (var kv in d.OrderByDescending(k => k.Value).Take(top))
            Console.WriteLine($"{kv.Value / reqs,9:F0} B  {kv.Value * 100.0 / sum,5:F1}%  {kv.Key}");
    }
}
