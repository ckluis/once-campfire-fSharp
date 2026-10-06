// What the routing layer costs a request, in process.
//
// The app's pipeline ends `UseRouting`, `UseFalco [ any "/cable" ... ]`, `UseFalcoNotFound dispatch`
// (Campfire.App/Boot.fs): every request of the five workloads is a path Falco has no endpoint for, so
// it passes the routing middleware, finds nothing, passes the endpoint middleware, and reaches the
// terminal handler Falco installs, which calls the kit's dispatch. This builds that same tail around a
// handler that does nothing, and variants without Falco or without routing, and times a
// `DefaultHttpContext` through each: nanoseconds and bytes allocated per request, no Kestrel, no
// sockets, no load generator. The difference between variants is what the layer costs.
//
//   FalcoCost [--iterations 3000000] [--rounds 7]
module FalcoCost

open System
open System.Diagnostics
open System.Threading.Tasks
open Falco
open Falco.Routing
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging

let services =
    let s = ServiceCollection()
    s.AddLogging() |> ignore
    s.AddRouting() |> ignore
    s.AddSingleton<System.Diagnostics.DiagnosticListener>(new System.Diagnostics.DiagnosticListener "FalcoCost") |> ignore
    s.AddSingleton<System.Diagnostics.DiagnosticSource>(fun sp -> sp.GetRequiredService<System.Diagnostics.DiagnosticListener>() :> System.Diagnostics.DiagnosticSource) |> ignore
    s.BuildServiceProvider()

let terminal: HttpContext -> Task = fun _ -> Task.CompletedTask

let build (configure: IApplicationBuilder -> unit) : RequestDelegate =
    let app = ApplicationBuilder(services) :> IApplicationBuilder
    configure app
    app.Build()

/// Rails' route table as Falco endpoints would be, to see what routing the app avoided by matching its own.
let manyRoutes =
    [ for i in 1..177 -> any (sprintf "/r%d/{id}/x%d" i i) terminal ]

let variants: (string * RequestDelegate) list =
    [ "terminal handler only (app.Run)", build (fun app -> app.Run(RequestDelegate terminal))
      "hand-rolled: path check for /cable, else handler",
      build (fun app ->
          app.Use(Func<HttpContext, RequestDelegate, Task>(fun http next ->
              if http.Request.Path.StartsWithSegments(PathString "/cable") then terminal http else next.Invoke http))
          |> ignore
          app.Run(RequestDelegate terminal))
      "UseFalcoNotFound only",
      build (fun app -> app.UseFalcoNotFound terminal |> ignore)
      "UseRouting + terminal handler",
      build (fun app ->
          app.UseRouting() |> ignore
          app.Run(RequestDelegate terminal))
      "the app's tail: UseRouting, UseFalco [/cable], UseFalcoNotFound",
      build (fun app ->
          app.UseRouting() |> ignore
          app.UseFalco [ any "/cable" terminal ] |> ignore
          app.UseFalcoNotFound terminal |> ignore)
      "the app's tail, request for /cable (a Falco endpoint)",
      build (fun app ->
          app.UseRouting() |> ignore
          app.UseFalco [ any "/cable" terminal ] |> ignore
          app.UseFalcoNotFound terminal |> ignore)
      "every Rails route as a Falco endpoint (177), request matches none",
      build (fun app ->
          app.UseRouting() |> ignore
          app.UseFalco manyRoutes |> ignore
          app.UseFalcoNotFound terminal |> ignore)
      "every Rails route as a Falco endpoint (177), request matches the last",
      build (fun app ->
          app.UseRouting() |> ignore
          app.UseFalco manyRoutes |> ignore
          app.UseFalcoNotFound terminal |> ignore) ]

let pathOf (name: string) =
    if name.Contains "/cable" then "/cable"
    elif name.Contains "matches the last" then "/r177/42/x177"
    else "/rooms/486777696/messages"

let measure (del: RequestDelegate) (path: string) (iterations: int) : struct (float * float) =
    let ctx = DefaultHttpContext()
    ctx.Request.Method <- "GET"
    ctx.Request.Path <- PathString path
    // Warm until the JIT has promoted the hot methods (tiered compilation with PGO, as the app runs):
    // a pause lets the background compiler catch up, a second burst runs the final code.
    for _ in 1..1_000_000 do
        del.Invoke(ctx).GetAwaiter().GetResult()
    Threading.Thread.Sleep 400
    for _ in 1..1_000_000 do
        del.Invoke(ctx).GetAwaiter().GetResult()
    GC.Collect()
    let before = GC.GetAllocatedBytesForCurrentThread()
    let sw = Stopwatch.StartNew()
    for _ in 1..iterations do
        del.Invoke(ctx).GetAwaiter().GetResult()
    sw.Stop()
    let allocated = GC.GetAllocatedBytesForCurrentThread() - before
    struct (float sw.ElapsedTicks * 1e9 / float Stopwatch.Frequency / float iterations, float allocated / float iterations)

[<EntryPoint>]
let main argv =
    let arg name dflt =
        match Array.tryFindIndex ((=) name) argv with
        | Some i -> int argv[i + 1]
        | None -> dflt
    let iterations = arg "--iterations" 3_000_000
    let rounds = arg "--rounds" 7
    let results = variants |> List.map (fun (name, _) -> name, ResizeArray<float>(), ResizeArray<float>())
    for _ in 1..rounds do
        // variants interleaved within a round, so drift hits them alike
        List.iter2
            (fun (name, del) (_, ns: ResizeArray<float>, bytes: ResizeArray<float>) ->
                let struct (n, b) = measure del (pathOf name) iterations
                ns.Add n
                bytes.Add b)
            variants
            results
    let median (xs: ResizeArray<float>) = (Seq.sort xs |> Seq.toArray)[xs.Count / 2]
    let baseline = results |> List.head |> fun (_, ns, _) -> median ns
    printfn "%-72s %10s %10s %12s" "variant" "ns/req" "vs first" "bytes/req"
    for name, ns, bytes in results do
        let m = median ns
        printfn "%-72s %10.1f %+10.1f %12.1f   [%.1f-%.1f]" name m (m - baseline) (median bytes) (Seq.min ns) (Seq.max ns)
    0
