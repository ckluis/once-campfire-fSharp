// Port of rust/crates/kit/tests/front.rs (the ACME test is in FrontAcmeTests)
//
// The front server (Thruster's job) end to end, over real sockets.
module Campfire.Kit.Tests.FrontTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Net.WebSockets
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Xunit
open Campfire.Kit
open Campfire.Kit.Tests.FrontHarness

type Counter() =
    [<DefaultValue>]
    val mutable Value: int

let private write (ctx: HttpContext) (text: string) : Task = ctx.Response.WriteAsync text

let private header (ctx: HttpContext) (name: string) : string =
    match ctx.Request.Headers[name].ToString() with
    | "" -> "-"
    | v -> v

/// An app with the kinds of responses Campfire sends.
let testApp (renders: Counter) (app: IApplicationBuilder) : unit =
    app.UseWebSockets() |> ignore
    app.Run(
        RequestDelegate(fun ctx ->
            let response = ctx.Response
            match ctx.Request.Path.Value with
            | "/public" ->
                let n = Interlocked.Increment(&renders.Value) - 1
                response.Headers["Cache-Control"] <- "public, max-age=60"
                response.ContentType <- "text/css"
                response.Headers["ETag"] <- "\"v1\""
                response.Headers.Append("Set-Cookie", "tracked=1; path=/")
                response.Headers["Vary"] <- "Accept-Encoding"
                write ctx ("render " + string n)
            | "/streamed" ->
                // `send_file`: a streamed body whose length is declared up front.
                response.Headers["Cache-Control"] <- "public, max-age=60"
                response.ContentLength <- 8L
                response.Headers["Vary"] <- "Accept-Encoding"
                task {
                    do! response.Body.WriteAsync(ReadOnlyMemory<byte>(Encoding.ASCII.GetBytes "stream"))
                    do! response.Body.WriteAsync(ReadOnlyMemory<byte>(Encoding.ASCII.GetBytes "ed"))
                }
                :> Task
            | "/private" ->
                response.Headers["Cache-Control"] <- "max-age=0, private, must-revalidate"
                response.Headers["Vary"] <- "Accept-Encoding"
                response.Headers.Append("Set-Cookie", "session=1; path=/")
                write ctx "private"
            | "/page" ->
                response.ContentType <- "text/html; charset=utf-8"
                write ctx (String.replicate 200 "<p>campfire</p>")
            | "/headers" ->
                let h = header ctx
                if ctx.Request.QueryString.Value = "?h2" then
                    let uri = ctx.Request.Path.Value + ctx.Request.QueryString.Value
                    write ctx ("host=" + h "Host" + " cookie=" + h "Cookie" + " version=" + ctx.Request.Protocol + " uri=" + uri)
                else
                    let peer = match ctx.Connection.RemoteIpAddress with null -> "-" | ip -> (if ip.IsIPv4MappedToIPv6 then ip.MapToIPv4() else ip).ToString()
                    let start = (h "X-Request-Start").StartsWith "t="
                    write
                        ctx
                        ("for=" + h "X-Forwarded-For" + " host=" + h "X-Forwarded-Host" + " proto=" + h "X-Forwarded-Proto" + " forwarded=" + h "Forwarded" + " start=" + string start + " peer=" + peer)
            | "/upload" ->
                task {
                    use body = new MemoryStream()
                    do! ctx.Request.Body.CopyToAsync body
                    do! write ctx $"{body.Length} bytes"
                }
                :> Task
            | "/slow" ->
                task {
                    do! Task.Delay(TimeSpan.FromSeconds 3.0)
                    do! write ctx "late"
                }
                :> Task
            | "/cable" when ctx.WebSockets.IsWebSocketRequest ->
                task {
                    use! socket = ctx.WebSockets.AcceptWebSocketAsync()
                    let buffer = Array.zeroCreate<byte> 1024
                    let mutable open' = true
                    while open' do
                        let! received = socket.ReceiveAsync(ArraySegment buffer, CancellationToken.None)
                        if received.MessageType = WebSocketMessageType.Close then
                            open' <- false
                        else
                            let text = Encoding.UTF8.GetString(buffer, 0, received.Count)
                            do! socket.SendAsync(ArraySegment(Encoding.UTF8.GetBytes $"echo {text}"), WebSocketMessageType.Text, true, CancellationToken.None)
                }
                :> Task
            | _ ->
                response.StatusCode <- 404
                Task.CompletedTask)
    )

let private startApp (vars: (string * string) list) : Task<FrontServer * Counter> =
    task {
        let renders = Counter()
        let! server = startWith vars (testApp renders)
        return server, renders
    }

let private some (s: string) = Some s

[<Fact>]
let ``caches public responses`` () =
    task {
        let! server, renders = startApp []
        let identity = "Accept-Encoding: identity\r\n"
        let! first = exchange server.Http (getRequest "/public" identity)
        Assert.Equal(200, first.Status)
        Assert.Equal(some "miss", first.Get "x-cache")
        Assert.Equal(None, first.Get "set-cookie") // cacheable responses lose their cookies
        Assert.Equal<string list>([ "Accept-Encoding" ], first.All "vary")
        Assert.True((first.Get "date").IsSome)
        Assert.Equal("render 0", first.Text)

        let! second = exchange server.Http (getRequest "/public?" identity)
        Assert.Equal(some "hit", second.Get "x-cache")
        Assert.Equal("render 0", second.Text)
        Assert.Equal(some "\"v1\"", second.Get "etag")

        // Another Accept-Encoding is another variant.
        let! other = exchange server.Http (getRequest "/public" "Accept-Encoding: br\r\n")
        Assert.Equal((some "miss", "render 1"), (other.Get "x-cache", other.Text))
        let! again = exchange server.Http (getRequest "/public" "Accept-Encoding: br\r\n")
        Assert.Equal((some "hit", "render 1"), (again.Get "x-cache", again.Text))

        let! revalidated = exchange server.Http (getRequest "/public" (identity + "If-None-Match: \"v1\"\r\n"))
        Assert.Equal((304, some "hit"), (revalidated.Status, revalidated.Get "x-cache"))
        Assert.Empty revalidated.Body

        let! ranged = exchange server.Http (getRequest "/public" (identity + "Range: bytes=0-1\r\n"))
        Assert.Equal(some "bypass", ranged.Get "x-cache") // ranges go to the app
        Assert.Equal(3, renders.Value)

        // Long URIs aren't cached.
        let long = "/public?pad=" + String('x', 4096)
        for _ in 0..1 do
            let! reply = exchange server.Http (getRequest long identity)
            Assert.Equal(some "bypass", reply.Get "x-cache")
        Assert.Equal(5, renders.Value)

        for _ in 0..1 do
            let! priv = exchange server.Http (getRequest "/private" "")
            Assert.Equal(some "miss", priv.Get "x-cache")
            Assert.Equal(some "session=1; path=/", priv.Get "set-cookie")
            Assert.Equal<string list>([ "Accept-Encoding" ], priv.All "vary")
        do! server.Stop()
    }

[<Fact>]
let ``caches streamed responses of declared length`` () =
    task {
        let! server, _ = startApp []
        for (encoding, expected) in [ "identity", "miss"; "identity", "hit"; "gzip", "miss"; "gzip", "hit" ] do
            let! reply = exchange server.Http (getRequest "/streamed" $"Accept-Encoding: {encoding}\r\n")
            Assert.Equal((some expected, "streamed"), (reply.Get "x-cache", reply.Text))
        do! server.Stop()
    }

[<Fact>]
let ``bypassed requests repeat vary`` () =
    task {
        let! server, _ = startApp []
        let request = "POST /headers HTTP/1.1\r\nHost: chat.test\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"
        let! reply = exchange server.Http request
        Assert.Equal(some "bypass", reply.Get "x-cache")
        Assert.Equal<string list>([ "Accept-Encoding" ], reply.All "vary")

        let withVary (app: IApplicationBuilder) =
            app.Use(
                Func<HttpContext, RequestDelegate, Task>(fun ctx next ->
                    ctx.Response.OnStarting(fun () ->
                        ctx.Response.Headers["Vary"] <- "Accept-Encoding"
                        Task.CompletedTask)
                    next.Invoke ctx)
            )
            |> ignore
            testApp (Counter()) app
        let! server2 = startWith [] withVary
        let! reply = exchange server2.Http request
        Assert.Equal<string list>([ "Accept-Encoding"; "Accept-Encoding" ], reply.All "vary")
        do! server.Stop()
        do! server2.Stop()
    }

[<Fact>]
let ``no vary is added when compression is off`` () =
    task {
        // Rust's `add_vary` is part of `Compression::apply`, which runs only when compression is on.
        let! server, _ = startApp [ "GZIP_COMPRESSION_ENABLED", "false" ]
        let post = "POST /headers HTTP/1.1\r\nHost: chat.test\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"
        let! bypassed = exchange server.Http post
        Assert.Equal(some "bypass", bypassed.Get "x-cache")
        Assert.Equal<string list>([], bypassed.All "vary")
        let! page = exchange server.Http "GET /page HTTP/1.1\r\nHost: chat.test\r\nConnection: close\r\nAccept-Encoding: gzip\r\n\r\n"
        Assert.Equal(200, page.Status)
        Assert.Equal(None, page.Get "content-encoding")
        Assert.Equal<string list>([], page.All "vary")
        // What the app says about Vary is its own, and stays.
        let! cached = exchange server.Http (getRequest "/public" "Accept-Encoding: gzip\r\n")
        Assert.Equal<string list>([ "Accept-Encoding" ], cached.All "vary")
        do! server.Stop()
    }

let private unzstd (bytes: byte[]) : byte[] =
    use input = new MemoryStream(bytes)
    use zstd = new ZstdSharp.DecompressionStream(input)
    use output = new MemoryStream()
    zstd.CopyTo output
    output.ToArray()

[<Fact>]
let ``compresses what the app left unencoded`` () =
    task {
        let! server, _ = startApp []
        let! zstd = exchange server.Http (getRequest "/page" "Accept-Encoding: zstd\r\n")
        Assert.Equal(some "zstd", zstd.Get "content-encoding")
        Assert.Equal<byte[]>(Encoding.UTF8.GetBytes(String.replicate 200 "<p>campfire</p>"), unzstd zstd.Body)
        let! plain = exchange server.Http (getRequest "/page" "")
        Assert.Equal(None, plain.Get "content-encoding")
        let! head = exchange server.Http "HEAD /page HTTP/1.1\r\nHost: chat.test\r\nConnection: close\r\nAccept-Encoding: gzip\r\n\r\n"
        Assert.Equal(None, head.Get "content-encoding")
        do! server.Stop()
    }

[<Fact>]
let ``forwards client addresses`` () =
    task {
        let! server, _ = startApp []
        let! reply =
            exchange server.Http (getRequest "/headers" "X-Forwarded-For: 203.0.113.9\r\nX-Forwarded-Proto: https\r\nForwarded: for=1.2.3.4\r\n")
        Assert.Equal(
            "for=203.0.113.9, 127.0.0.1 host=chat.test proto=https forwarded=- start=True peer=127.0.0.1",
            reply.Text
        )

        let! untrusting = startApp [ "FORWARD_HEADERS", "false" ]
        let untrusting, _ = untrusting
        let! reply = exchange untrusting.Http (getRequest "/headers" "X-Forwarded-For: 203.0.113.9\r\nX-Forwarded-Proto: https\r\n")
        Assert.Equal("for=127.0.0.1 host=chat.test proto=http forwarded=- start=True peer=127.0.0.1", reply.Text)
        do! server.Stop()
        do! untrusting.Stop()
    }

[<Fact>]
let ``the app still listens on the target port`` () =
    task {
        let! server, _ = startApp []
        let! reply = exchange server.Target (getRequest "/public" "")
        Assert.Equal(200, reply.Status)
        Assert.Equal(None, reply.Get "x-cache")
        // Puma sends no `Date`; Kestrel always does, and has no way to leave it out.
        Assert.Equal(some "tracked=1; path=/", reply.Get "set-cookie")
        do! server.Stop()
    }

/// This machine's address on its default route, if it has one.
let private nonLoopbackAddress () : IPAddress option =
    try
        use socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        socket.Connect(IPAddress.Parse "192.0.2.1", 9)
        match socket.LocalEndPoint with
        | :? IPEndPoint as endpoint when not (IPAddress.IsLoopback endpoint.Address) && not (endpoint.Address.Equals IPAddress.Any) -> Some endpoint.Address
        | _ -> None
    with _ ->
        None

[<Fact>]
let ``the target port is loopback only and limited`` () =
    task {
        let! server, _ = startApp [ "HTTP_READ_TIMEOUT", "1"; "MAX_REQUEST_BODY", "10" ]
        match nonLoopbackAddress () with
        | Some address ->
            let reachable =
                try
                    use client = new TcpClient()
                    client.ConnectAsync(address, server.Target).Wait(TimeSpan.FromSeconds 3.0) && client.Connected
                with _ ->
                    false
            Assert.False(reachable, $"reachable on {address}")
        | None -> ()

        let! large = exchange server.Target "POST /upload HTTP/1.1\r\nHost: x\r\nConnection: close\r\nContent-Length: 11\r\n\r\nhello world"
        Assert.Equal(413, large.Status)

        use client = new TcpClient()
        do! client.ConnectAsync(IPAddress.Loopback, server.Target)
        let stream = client.GetStream()
        do! stream.WriteAsync(Encoding.ASCII.GetBytes "GET / HTTP/1.1\r\nHost: x\r\n")
        let buffer = Array.zeroCreate<byte> 4096
        use cts = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)
        let! n =
            task {
                try
                    return! stream.ReadAsync(Memory<byte> buffer, cts.Token)
                with :? IOException ->
                    return 0
            }
        Assert.True(n = 0 || Encoding.ASCII.GetString(buffer, 0, n).StartsWith "HTTP/1.1 408")
        do! server.Stop()
    }

[<Fact>]
let ``limits request bodies`` () =
    task {
        let! server, _ = startApp [ "MAX_REQUEST_BODY", "10" ]
        let! small = exchange server.Http "POST /upload HTTP/1.1\r\nHost: x\r\nConnection: close\r\nContent-Length: 5\r\n\r\nhello"
        Assert.Equal((200, "5 bytes"), (small.Status, small.Text))
        let! large = exchange server.Http "POST /upload HTTP/1.1\r\nHost: x\r\nConnection: close\r\nContent-Length: 11\r\n\r\nhello world"
        Assert.Equal(413, large.Status)
        let chunked =
            "POST /upload HTTP/1.1\r\nHost: x\r\nConnection: close\r\nTransfer-Encoding: chunked\r\n\r\n6\r\nhello \r\n5\r\nworld\r\n0\r\n\r\n"
        let! reply = exchange server.Http chunked
        Assert.Equal(413, reply.Status)
        let smallChunked =
            "POST /upload HTTP/1.1\r\nHost: x\r\nConnection: close\r\nTransfer-Encoding: chunked\r\n\r\n3\r\nabc\r\n0\r\n\r\n"
        let! reply = exchange server.Http smallChunked
        Assert.Equal((200, "3 bytes"), (reply.Status, reply.Text))
        do! server.Stop()
    }

[<Fact>]
let ``closes idle and slow connections`` () =
    task {
        let! server, _ = startApp [ "HTTP_IDLE_TIMEOUT", "2"; "HTTP_READ_TIMEOUT", "1"; "HTTP_WRITE_TIMEOUT", "2" ]
        let buffer = Array.zeroCreate<byte> 4096

        // An idle keep-alive connection closes after the idle timeout. (Rust's hyper runs its header
        // timer while the connection waits, so there it closes after the shorter of the idle and read
        // timeouts; Go's server, and so Thruster, waited the idle timeout, as Kestrel does.)
        use client = new TcpClient()
        do! client.ConnectAsync(IPAddress.Loopback, server.Http)
        let stream = client.GetStream()
        do! stream.WriteAsync(Encoding.ASCII.GetBytes "GET /private HTTP/1.1\r\nHost: x\r\n\r\n")
        let! n = stream.ReadAsync(Memory<byte> buffer)
        Assert.True(n > 0 && Encoding.ASCII.GetString(buffer, 0, n).StartsWith "HTTP/1.1 200")
        let started = Diagnostics.Stopwatch.StartNew()
        use cts = new CancellationTokenSource(TimeSpan.FromSeconds 6.0)
        let! n =
            task {
                try
                    return! stream.ReadAsync(Memory<byte> buffer, cts.Token)
                with :? IOException ->
                    return 0
            }
        Assert.Equal(0, n) // closed
        let idle = started.Elapsed
        Assert.True(idle >= TimeSpan.FromMilliseconds 1500.0 && idle < TimeSpan.FromMilliseconds 3500.0, $"closed after {idle}")

        // A request that never finishes its headers.
        use slow = new TcpClient()
        do! slow.ConnectAsync(IPAddress.Loopback, server.Http)
        let slowStream = slow.GetStream()
        do! slowStream.WriteAsync(Encoding.ASCII.GetBytes "GET / HTTP/1.1\r\nHost: x\r\n")
        use cts = new CancellationTokenSource(TimeSpan.FromSeconds 6.0)
        let! n =
            task {
                try
                    return! slowStream.ReadAsync(Memory<byte> buffer, cts.Token)
                with :? IOException ->
                    return 0
            }
        Assert.True(n = 0 || Encoding.ASCII.GetString(buffer, 0, n).StartsWith "HTTP/1.1 408", "closed")

        // A response that takes longer than the write timeout: no response at all.
        use late = new TcpClient()
        do! late.ConnectAsync(IPAddress.Loopback, server.Http)
        let lateStream = late.GetStream()
        do! lateStream.WriteAsync(Encoding.ASCII.GetBytes "GET /slow HTTP/1.1\r\nHost: x\r\n\r\n")
        use cts = new CancellationTokenSource(TimeSpan.FromSeconds 6.0)
        let! n =
            task {
                try
                    return! lateStream.ReadAsync(Memory<byte> buffer, cts.Token)
                with :? IOException ->
                    return 0
            }
        Assert.Equal(0, n) // dropped without a response
        do! server.Stop()
    }

/// The bytes of an HTTP/2 request for `path` on a connection with prior knowledge: the preface, empty
/// SETTINGS, and a HEADERS frame (HPACK: `:method GET`, `:scheme http`, `:path`, `:authority`).
let private h2Request (path: string) : byte[] =
    let literal (nameIndex: int) (value: string) =
        Array.concat [ [| byte nameIndex |]; [| byte value.Length |]; Encoding.ASCII.GetBytes value ]
    let block = Array.concat [ [| 0x82uy; 0x86uy |]; literal 4 path; literal 1 "chat.test" ]
    let frame (kind: byte) (flags: byte) (stream: int) (payload: byte[]) =
        Array.concat
            [ [| byte (payload.Length >>> 16); byte (payload.Length >>> 8); byte payload.Length; kind; flags |]
              [| byte (stream >>> 24); byte (stream >>> 16); byte (stream >>> 8); byte stream |]
              payload ]
    Array.concat [ "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"B; frame 4uy 0uy 0 [||]; frame 1uy 5uy 1 block ]

[<Fact>]
let ``http2 connections close after the idle timeout`` () =
    task {
        let! server, _ = startApp [ "H2C_ENABLED", "true"; "HTTP_IDLE_TIMEOUT", "2"; "HTTP_READ_TIMEOUT", "1" ]
        use client = new TcpClient()
        do! client.ConnectAsync(IPAddress.Loopback, server.Http)
        let stream = client.GetStream()
        do! stream.WriteAsync(h2Request "/private")
        // Read until the response's DATA frame ends its stream (flag 0x1), then wait for the close.
        let received = new MemoryStream()
        let buffer = Array.zeroCreate<byte> 4096
        let mutable answered = false
        use cts = new CancellationTokenSource(TimeSpan.FromSeconds 10.0)
        let mutable closedAt = None
        let mutable answeredAt = Diagnostics.Stopwatch.GetTimestamp()
        while closedAt.IsNone do
            let! n =
                task {
                    try
                        return! stream.ReadAsync(Memory<byte> buffer, cts.Token)
                    with
                    | :? IOException
                    | :? OperationCanceledException -> return 0
                }
            if n = 0 then
                closedAt <- Some(Diagnostics.Stopwatch.GetTimestamp())
            else
                received.Write(buffer, 0, n)
                let bytes = received.ToArray()
                // Walk the frames for the stream's end.
                let mutable at = 0
                while at + 9 <= bytes.Length do
                    let length = (int bytes[at] <<< 16) ||| (int bytes[at + 1] <<< 8) ||| int bytes[at + 2]
                    let kind = bytes[at + 3]
                    let flags = bytes[at + 4]
                    if (kind = 0uy || kind = 1uy) && flags &&& 1uy <> 0uy && not answered then
                        answered <- true
                        answeredAt <- Diagnostics.Stopwatch.GetTimestamp()
                    at <- at + 9 + length
        Assert.True(answered, "the response came")
        let idle = Diagnostics.Stopwatch.GetElapsedTime(answeredAt, closedAt.Value)
        Assert.True(idle >= TimeSpan.FromMilliseconds 1500.0 && idle < TimeSpan.FromMilliseconds 4500.0, $"closed after {idle}")
        do! server.Stop()
    }

[<Fact>]
let ``websockets pass through and outlive the timeouts`` () =
    task {
        let! server, _ = startApp [ "HTTP_IDLE_TIMEOUT", "1"; "HTTP_READ_TIMEOUT", "1"; "HTTP_WRITE_TIMEOUT", "1" ]
        use socket = new ClientWebSocket()
        socket.Options.CollectHttpResponseDetails <- true
        do! socket.ConnectAsync(Uri $"ws://127.0.0.1:{server.Http}/cable", CancellationToken.None)
        Assert.Equal(HttpStatusCode.SwitchingProtocols, socket.HttpStatusCode)
        let headers = nonNull socket.HttpResponseHeaders
        Assert.Equal("bypass", String.Join(",", headers["x-cache"]))
        Assert.Equal("Accept-Encoding", String.Join(",", headers["vary"]))
        do! Task.Delay 2500
        do! socket.SendAsync(ArraySegment(Encoding.UTF8.GetBytes "ping"), WebSocketMessageType.Text, true, CancellationToken.None)
        let buffer = Array.zeroCreate<byte> 64
        use cts = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)
        let! received = socket.ReceiveAsync(ArraySegment buffer, cts.Token)
        Assert.Equal("echo ping", Encoding.UTF8.GetString(buffer, 0, received.Count))
        socket.Dispose()
        do! server.Stop()
    }

let private h2Get (port: int) (path: string) : Task<HttpResponseMessage> =
    task {
        use handler = new SocketsHttpHandler()
        let client = new HttpClient(handler)
        use request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}{path}", Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact)
        request.Headers.Add("cookie", "a=1")
        request.Headers.Add("cookie", "b=2")
        let! response = client.SendAsync(request, HttpCompletionOption.ResponseContentRead)
        return response
    }

[<Fact>]
let ``speaks h2c only when enabled`` () =
    task {
        let! server, _ = startApp [ "H2C_ENABLED", "true" ]
        let! reply = h2Get server.Http "/private"
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode)
        Assert.Equal("miss", String.Join(",", reply.Headers.GetValues "x-cache"))
        // The app gets it as Thruster's HTTP/1.1 request: a Host, an origin-form path, one Cookie.
        let! reply = h2Get server.Http "/headers?h2"
        let! body = reply.Content.ReadAsStringAsync()
        Assert.Equal($"host=127.0.0.1:{server.Http} cookie=a=1; b=2 version=HTTP/1.1 uri=/headers?h2", body)
        // HTTP/1.1 still works on the same port.
        let! plain = exchange server.Http (getRequest "/private" "")
        Assert.Equal(200, plain.Status)
        do! server.Stop()

        let! server, _ = startApp []
        let! refused =
            task {
                try
                    let! _ = h2Get server.Http "/private"
                    return false
                with :? HttpRequestException ->
                    return true
            }
        Assert.True(refused, "HTTP/2 without H2C_ENABLED")
        do! server.Stop()
    }

[<Fact>]
let ``logs each request as thruster does`` () =
    task {
        let logs = LoggingTests.CapturingLogger()
        let factory =
            { new Microsoft.Extensions.Logging.ILoggerFactory with
                member _.CreateLogger(_: string) = logs :> Microsoft.Extensions.Logging.ILogger
                member _.AddProvider(_: Microsoft.Extensions.Logging.ILoggerProvider) = ()
                member _.Dispose() = () }
        let renders = Counter()
        let http = freePort ()
        let! started = tryStart [ "LOG_REQUESTS", "true" ] (testApp renders) ValueNone factory http (freePort ())
        let server = started.Value
        let! first = exchange server.Http (getRequest "/public?x=1" "User-Agent: probe/1\r\nAccept-Encoding: identity\r\n")
        let! second = exchange server.Http (getRequest "/public?x=1" "User-Agent: probe/1\r\nAccept-Encoding: identity\r\nX-Forwarded-For: 203.0.113.9\r\n")
        Assert.Equal(some "hit", second.Get "x-cache")
        Assert.Equal(200, first.Status)
        // The log line is written as the response ends, which can be just after the client has read it.
        do! Task.Delay 300
        do! server.Stop()
        let text: string = logs.Text
        let lines = text.Split('\n') |> Array.filter (fun l -> l.Contains "Request path=")
        Assert.Equal(2, lines.Length)
        let miss = lines |> Array.find (fun l -> l.Contains "cache=miss")
        for expected in [ "path=/public"; "status=200"; "method=GET"; "req_content_length=0"; "resp_content_length=8"; "resp_content_type=text/css"; "user_agent=probe/1"; "query=x=1"; "proto=HTTP/1.1"; "remote_addr=127.0.0.1:" ] do
            Assert.Contains(expected, miss)
        // The client's own X-Forwarded-For is what a request is logged under, as Thruster logs it.
        let hit = lines |> Array.find (fun l -> l.Contains "cache=hit")
        Assert.Contains("remote_addr=203.0.113.9", hit)
    }

[<Fact>]
let ``a request body that stalls past the read timeout fails the request`` () =
    task {
        let! server, _ = startApp [ "HTTP_READ_TIMEOUT", "1" ]
        use client = new TcpClient()
        do! client.ConnectAsync(IPAddress.Loopback, server.Http)
        let stream = client.GetStream()
        // Headers and 5 of the 100 bytes the request promises: the rest never comes.
        do! stream.WriteAsync(Encoding.ASCII.GetBytes "POST /upload HTTP/1.1\r\nHost: x\r\nContent-Length: 100\r\n\r\nhello")
        let buffer = Array.zeroCreate<byte> 4096
        use cts = new CancellationTokenSource(TimeSpan.FromSeconds 8.0)
        let started = Diagnostics.Stopwatch.StartNew()
        let! n =
            task {
                try
                    return! stream.ReadAsync(Memory<byte> buffer, cts.Token)
                with
                | :? IOException -> return 0
            }
        // Closed, or answered with an error, once the read timeout is up; never `5 bytes` or `100 bytes`.
        let answer = Encoding.ASCII.GetString(buffer, 0, n)
        Assert.False(answer.StartsWith "HTTP/1.1 200", answer)
        Assert.True(started.Elapsed < TimeSpan.FromSeconds 5.0, $"{started.Elapsed}")
        do! server.Stop()
    }
