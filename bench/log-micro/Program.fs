// Tier 1 micro-benchmark of the request log (Phase 7, unit 7.3).
//
//   bench/micro-linux log-micro [SECONDS]
//
// One request through `FrontHandler` on a DefaultHttpContext (a GET of a page-sized body, a User-Agent, a remote address, as a benchmarked
// request is), with request logging off, through the console logger (to a pipe-like stream), and as batched bytes (to the same). It prints the
// process's CPU time per request (every thread: the console logger's writer and the batch writer are counted) and the bytes allocated
// per request on the calling thread, best of 5 windows. The numbers compare variants only and are never reported as results.
module LogMicro

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Campfire.Kit

/// What a pipe to a log driver is: a write that costs a system call and keeps nothing.
type DevNull() =
    inherit Stream()
    let fd = new FileStream("/dev/null", FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 1, false)
    override _.CanRead = false
    override _.CanSeek = false
    override _.CanWrite = true
    override _.Length = 0L
    override _.Position with get () = 0L and set _ = ()
    override _.Flush() = ()
    override _.Read(_: byte[], _: int, _: int) = 0
    override _.Seek(_: int64, _: SeekOrigin) = 0L
    override _.SetLength(_: int64) = ()
    override _.Write(buffer: byte[], offset: int, count: int) = fd.Write(buffer, offset, count)
    override _.Dispose(disposing: bool) = if disposing then fd.Dispose()

/// A logger that is on and keeps nothing: what the handler asks of it when the lines go elsewhere.
type Enabled() =
    interface ILogger with
        member _.BeginScope<'s when 's: not null>(_: 's) : IDisposable | null = null
        member _.IsEnabled(_: LogLevel) = true
        member _.Log<'s>(_: LogLevel, _: EventId, _: 's, _: exn | null, _: Func<'s, exn | null, string>) : unit = ()

let private body = Array.init 30000 (fun i -> byte (97 + i % 26))

let private app =
    RequestDelegate(fun ctx ->
        ctx.Response.StatusCode <- 200
        ctx.Response.ContentType <- "text/html; charset=utf-8"
        ctx.Response.ContentLength <- int64 body.Length
        ctx.Response.Body.WriteAsync(body, 0, body.Length))

let private config =
    FrontConfig.fromLookup (fun name ->
        match name with
        | "GZIP_COMPRESSION_ENABLED" -> "false"
        | _ -> null)

let private oneRequest (handler: FrontHandler) (sink: Stream) (i: int) : unit =
    let ctx = DefaultHttpContext()
    ctx.Request.Method <- "GET"
    ctx.Request.Path <- PathString "/rooms/1"
    ctx.Request.QueryString <- QueryString "?page=2"
    ctx.Request.Protocol <- "HTTP/1.1"
    ctx.Request.Headers["User-Agent"] <- "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36"
    ctx.Request.Headers["Accept-Encoding"] <- "identity"
    ctx.Connection.RemoteIpAddress <- IPAddress.Parse "::ffff:172.17.0.1"
    ctx.Connection.RemotePort <- 40000 + i % 1000
    ctx.Response.Body <- sink
    (handler.Invoke ctx).GetAwaiter().GetResult()

let private measure (seconds: float) (variant: string) (handler: FrontHandler) (sink: Stream) (settle: unit -> unit) : unit =
    let warm = Stopwatch.StartNew()
    let mutable i = 0
    while warm.Elapsed.TotalSeconds < 3.0 do
        for _ in 1..200 do
            oneRequest handler sink i
            i <- i + 1
    settle ()
    let process' = Process.GetCurrentProcess()
    let mutable bestCpu = Double.MaxValue
    let mutable bestAlloc = 0.0
    for _ in 1..5 do
        process'.Refresh()
        let cpu0 = process'.TotalProcessorTime
        let a0 = GC.GetAllocatedBytesForCurrentThread()
        let sw = Stopwatch.StartNew()
        let mutable n = 0
        while sw.Elapsed.TotalSeconds < seconds / 5.0 do
            for _ in 1..200 do
                oneRequest handler sink i
                i <- i + 1
            n <- n + 200
        settle ()
        process'.Refresh()
        let cpu = (process'.TotalProcessorTime - cpu0).TotalMilliseconds * 1000.0 / float n
        if cpu < bestCpu then
            bestCpu <- cpu
            bestAlloc <- float (GC.GetAllocatedBytesForCurrentThread() - a0) / float n
    eprintfn "%-44s %10.2f us cpu/request %10.0f bytes/request" variant bestCpu bestAlloc

[<EntryPoint>]
let main argv =
    let seconds = if argv.Length > 0 then float argv[0] else 5.0
    let response = new DevNull()
    let sinkOut = new DevNull()
    // 1. logging off
    let off = FrontHandler(FrontServices(config, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance), app)
    measure seconds "request logging off (control)" off response ignore
    // 2. through the console logger
    let real = Console.Out
    Console.SetOut(new StreamWriter(sinkOut, Text.UTF8Encoding(false), 256, AutoFlush = true))
    let factory =
        LoggerFactory.Create(fun builder ->
            builder.AddSimpleConsole(fun options ->
                options.SingleLine <- true
                options.UseUtcTimestamp <- true
                options.TimestampFormat <- "yyyy-MM-ddTHH:mm:ss.ffffffZ ")
            |> ignore)
    let logged = FrontHandler(FrontServices(config, factory.CreateLogger "thruster"), app)
    measure seconds "ILogger through the console logger" logged response (fun () -> Thread.Sleep 300)
    factory.Dispose()
    Console.SetOut real
    // 3. batched bytes
    let lines = RequestLines(sinkOut, TimeSpan.FromMilliseconds 10.0)
    let enabled = Enabled()
    let direct = FrontHandler(FrontServices(config, enabled, lines), app)
    measure seconds "batched bytes (RequestLines)" direct response (fun () -> lines.Flush())
    lines.Stop()
    0
