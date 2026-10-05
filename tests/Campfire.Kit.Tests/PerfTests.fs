// How much the kit costs per request, measured in process: bytes allocated and time for the whole
// pipeline (the pre-routing middleware, an action through the adapter, the writer) on a
// DefaultHttpContext, with the response going to a null stream. The thresholds are generous
// ceilings, so that a change that makes a request several times more expensive fails here; the
// numbers themselves are in the test output.
module Campfire.Kit.Tests.PerfTests

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Xunit
open Campfire.RailsCompat
open Campfire.Kit
open Campfire.Kit.Tests.Helpers
open Campfire.Kit.Tests.Harness

let private page = Encoding.UTF8.GetBytes(String.replicate 1200 "<div class=\"message\"><p>Hello there, see you at 10:30</p></div>\n")

let private signedCookie =
    lazy (CookieJar.escape (Cookies.sign secrets.Value "session_token" "Kjcb4WUEktkJJaYJrSbT8wcb" None))

let private hello (c: Ctx) = act { return c.Html "hello" }

let private pageAction (c: Ctx) = act { return c.Html(ReadOnlyMemory<byte> page) }

let private withSession (c: Ctx) =
    act {
        match c.Cookies.Signed "session_token" with
        | null -> return! c.RedirectTo "/session/new"
        | token -> return c.Html token
    }

let private post (c: Ctx) =
    act {
        do! c.VerifyAuthenticityToken()
        let! message = c.Params.Require "message"
        let length = match message.Get "body" with ValueSome b -> (nonNull (b.ToS())).Length | ValueNone -> 0
        return! c.RedirectTo("/rooms/1?m=" + string length)
    }

let private kit = lazy (kitWith KitConfig.Default (obj ()))

let private context (meth: string) (target: string) (headers: (string * string) list) (body: byte[]) : HttpContext =
    let http = DefaultHttpContext()
    http.Request.Method <- meth
    http.Request.Scheme <- "http"
    http.Request.Path <- PathString(target.Split('?')[0])
    (match target.IndexOf '?' with
     | -1 -> ()
     | q -> http.Request.QueryString <- QueryString(target.Substring q))
    (nonNull (http.Features.Get<IHttpRequestFeature>())).RawTarget <- target
    http.Request.Headers["host"] <- "chat.example.com"
    for (k, v) in headers do
        http.Request.Headers[k] <- v
    http.Request.Body <- new MemoryStream(body)
    http.Request.ContentLength <- if body.Length > 0 then Nullable(int64 body.Length) else Nullable()
    http.Response.Body <- Stream.Null
    http.Connection.RemoteIpAddress <- IPAddress.Loopback
    http

/// Allocated bytes and microseconds per request over `n` requests after a warm-up.
let private measure (name: string) (n: int) (make: unit -> HttpContext) (run: HttpContext -> Task) : struct (int64 * float) =
    let kit = kit.Value
    let step = Adapter.railsMiddleware kit
    for _ in 1..2000 do
        let http = make ()
        (step.Invoke(http, RequestDelegate run)).GetAwaiter().GetResult()
    let mutable bytes = 0L
    let mutable ticks = 0L
    for _ in 1..n do
        let http = make ()
        let delegateToRun = RequestDelegate run
        let before = GC.GetAllocatedBytesForCurrentThread()
        let started = Stopwatch.GetTimestamp()
        (step.Invoke(http, delegateToRun)).GetAwaiter().GetResult()
        ticks <- ticks + (Stopwatch.GetTimestamp() - started)
        bytes <- bytes + (GC.GetAllocatedBytesForCurrentThread() - before)
        Assert.True(http.Response.StatusCode < 500, $"{name}: {http.Response.StatusCode}")
    let perRequest = bytes / int64 n
    let micros = (Stopwatch.GetElapsedTime(0L, ticks)).TotalMilliseconds * 1000.0 / float n
    Console.Error.WriteLine $"kit perf: %-26s{name} {perRequest,7} B/request {micros,8:F2} us/request"
    struct (perRequest, micros)

[<Fact>]
let ``a small page costs little to serve`` () =
    let route = Adapter.dispatch kit.Value hello
    let struct (bytes, _) = measure "GET hello" 20000 (fun () -> context "GET" "/rooms/5" [] [||]) (fun http -> route http)
    Assert.True(bytes < 8_000L, $"{bytes} bytes per request")

[<Fact>]
let ``a large page costs no more than its etag and its copy`` () =
    let route = Adapter.dispatch kit.Value pageAction
    let struct (bytes, _) = measure "GET 100 KB page" 5000 (fun () -> context "GET" "/rooms/5" [] [||]) (fun http -> route http)
    Assert.True(bytes < 8_000L, $"{bytes} bytes per request")

[<Fact>]
let ``a request with a signed cookie`` () =
    let route = Adapter.dispatch kit.Value withSession
    let struct (bytes, _) =
        measure "GET with signed cookie" 20000 (fun () -> context "GET" "/rooms/5" [ "cookie", "session_token=" + signedCookie.Value ] [||]) (fun http -> route http)
    Assert.True(bytes < 16_000L, $"{bytes} bytes per request")

[<Fact>]
let ``posting a form`` () =
    let route = Adapter.dispatch kit.Value post
    let body = Encoding.UTF8.GetBytes "message%5Bbody%5D=hello+there&message%5Bclient_message_id%5D=abc"
    let struct (bytes, _) =
        measure
            "POST form"
            20000
            (fun () -> context "POST" "/rooms/1/messages" [ "content-type", "application/x-www-form-urlencoded"; "sec-fetch-site", "same-origin" ] body)
            (fun http -> route http)
    Assert.True(bytes < 16_000L, $"{bytes} bytes per request")

/// Where a request's cost goes, stage by stage. Prints; asserts nothing.
[<Fact>]
let ``breakdown of a small request`` () =
    let kit = kit.Value
    let stage (name: string) (n: int) (f: unit -> objnull) =
        for _ in 1..5000 do f () |> ignore
        let before = GC.GetAllocatedBytesForCurrentThread()
        let started = Stopwatch.GetTimestamp()
        for _ in 1..n do f () |> ignore
        let elapsed = Stopwatch.GetElapsedTime started
        let bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / int64 n
        Console.Error.WriteLine $"kit stage: %-28s{name} {bytes,7} B {elapsed.TotalMilliseconds * 1000.0 / float n,8:F3} us"
    let http = context "GET" "/rooms/5" [] [||]
    stage "DefaultHttpContext" 20000 (fun () -> box (context "GET" "/rooms/5" [] [||]))
    stage "Guid request id" 100000 (fun () -> box (Guid.NewGuid().ToString()))
    stage "headers.TryGetValue x4" 100000 (fun () ->
        box (RequestHeaders.schemeIsHttps http.Request.Headers "http"))
    let headers = http.Request.Headers
    stage "Request.Create" 100000 (fun () ->
        box (Request.Create("GET", "GET", "/rooms/5", null, null, "http", headers, IPAddress.Loopback, ReadOnlyMemory.Empty, kit.Config.Proxy)))
    let request = Request.Create("GET", "GET", "/rooms/5", null, null, "http", headers, IPAddress.Loopback, ReadOnlyMemory.Empty, kit.Config.Proxy)
    stage "Ctx.ctor (+cookie jar, maps)" 100000 (fun () ->
        box (Ctx(kit, request, ParamMap(), ParamMap(), ParamMap(), CookieJar(kit.Secrets, kit.Clock))))
    stage "ctx + html + finish" 50000 (fun () ->
        let c = Ctx(kit, request, ParamMap(), ParamMap(), ParamMap(), CookieJar(kit.Secrets, kit.Clock))
        box (c.Finish(Ok(c.Html "hello"))))
    stage "Response.Header x5" 100000 (fun () ->
        let r = Response(200)
        r.Headers.Append("a", "1") |> ignore
        box r)
    stage "sha256 of 5 bytes + hex" 100000 (fun () ->
        let hash = Security.Cryptography.SHA256.HashData(ReadOnlySpan<byte>(Encoding.UTF8.GetBytes "hello"))
        box (Convert.ToHexStringLower(ReadOnlySpan<byte>(hash, 0, 16))))
    stage "Stopwatch+ToString F6" 100000 (fun () -> box ((Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp())).TotalSeconds.ToString("F6", Globalization.CultureInfo.InvariantCulture)))
    stage "Adapter.write (hello)" 20000 (fun () ->
        let http = context "GET" "/rooms/5" [] [||]
        let response = Response(200).ContentType("text/html").SetBody "hello"
        box ((Adapter.write kit http response false).GetAwaiter().GetResult()))
    stage "dispatch only" 20000 (fun () ->
        let http = context "GET" "/rooms/5" [] [||]
        box ((Adapter.dispatch kit hello http).GetAwaiter().GetResult()))
