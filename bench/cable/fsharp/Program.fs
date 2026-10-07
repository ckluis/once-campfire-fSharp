// The Cable server bench/cable measures, on Campfire.Cable and Kestrel. bench/cable/rust/src/main.rs is the
// same server on campfire_cable and axum.
//
//   /cable                         the Action Cable endpoint; any connection is accepted, and `BenchChannel`
//                                  streams "bench" (a Turbo Stream broadcasts' stream)
//   POST /broadcast?seq=N&bytes=B&count=C
//                                  broadcasts C Turbo Stream appends of about B bytes of HTML, numbered from N
//                                  as data-seq, to "bench"; answers with how many subscribers got the last one
//   GET /gc                        bytes allocated and collections so far (F# only)
//
//   CableBench PORT
module CableBench

open System
open System.Net
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Campfire.Cable

let filler = "<p>Hello there, see you at 10:30 &amp; bring the <b>slides</b></p>"

let html (seq: int) (bytes: int) : string =
    let head = $"<div class=\"message\" data-seq=\"{seq}\">"
    let body = StringBuilder(head)
    while body.Length < bytes do
        body.Append filler |> ignore
    body.Append("</div>").ToString()

[<EntryPoint>]
let main argv =
    let port = int argv[0]
    let config = { Config.defaults with DisableRequestForgeryProtection = true }
    let server =
        Server.builder config (fun _ -> Task.FromResult(Some "bench")) id
        |> ServerBuilder.channel "BenchChannel" (fun () ->
            { Channel.empty with
                Subscribed =
                    fun sub ->
                        sub.StreamFrom "bench"
                        Channel.ok })
        |> ServerBuilder.build
    let builder = WebApplication.CreateSlimBuilder()
    builder.Logging.ClearProviders() |> ignore
    builder.WebHost.ConfigureKestrel(fun options -> options.Listen(IPAddress.Loopback, port)) |> ignore
    if Environment.GetEnvironmentVariable "CABLE_INLINE" = "1" then
        builder.WebHost.UseSockets(fun sockets -> sockets.UnsafePreferInlineScheduling <- true) |> ignore
    let app = builder.Build()
    Endpoint.map Protocol.DefaultMountPath server app
    app.MapPost(
        "/broadcast",
        RequestDelegate(fun ctx ->
            let query (name: string) (fallback: int) =
                match Int32.TryParse(ctx.Request.Query[name].ToString()) with
                | true, n -> n
                | _ -> fallback
            let seq, bytes, count = query "seq" 0, query "bytes" 600, query "count" 1
            let mutable received = 0
            for i in 0 .. count - 1 do
                received <- Turbo.broadcastAppendTo server [ "bench" ] "messages" (html (seq + i) bytes)
            ctx.Response.WriteAsync(string received))
    )
    |> ignore
    app.MapGet(
        "/gc",
        RequestDelegate(fun ctx ->
            ctx.Response.WriteAsync($"{GC.GetTotalAllocatedBytes(false)} {GC.CollectionCount 0} {GC.CollectionCount 1} {GC.CollectionCount 2}"))
    )
    |> ignore
    app.Run()
    0
