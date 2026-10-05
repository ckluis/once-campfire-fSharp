/// A real Kestrel server on a loopback port running the kit's pipeline, and a raw HTTP/1.1 client for
/// it (`Uri` rewrites a bare `%` in a query to `%25`, and these tests need the bytes they wrote on the wire).
module Campfire.Kit.Tests.Harness

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading.Tasks
open Falco
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Campfire.RailsCompat
open Campfire.RailsCompat.Clock
open Campfire.Kit
open Campfire.Kit.Tests.Helpers

/// A request as it goes on the wire.
type Req =
    { Method: string
      Target: string
      Headers: (string * string) list
      Body: byte[] voption }

    /// Set a header, replacing any the request already has under that name.
    member this.With(name: string, value: string) : Req =
        let others = this.Headers |> List.filter (fun (k, _) -> not (k.Equals(name, StringComparison.OrdinalIgnoreCase)))
        { this with Headers = others @ [ name, value ] }

    member this.WithBody(body: string) : Req = { this with Body = ValueSome(Encoding.UTF8.GetBytes body) }

    member this.WithBytes(body: byte[]) : Req = { this with Body = ValueSome body }

    /// A form post's content type and body.
    member this.Form(body: string) : Req =
        { this.With("content-type", "application/x-www-form-urlencoded") with Body = ValueSome(Encoding.UTF8.GetBytes body) }

    member this.AsMethod(meth: string) : Req = { this with Method = meth }

let request (meth: string) (target: string) : Req =
    { Method = meth
      Target = target
      Headers = [ "host", "chat.example.com" ]
      Body = ValueNone }

let get (target: string) = request "GET" target
let post (target: string) = request "POST" target

let formPost (target: string) (body: string) = (post target).Form body

/// A response, read off the wire.
type Reply =
    { Status: int
      Headers: (string * string) list
      Body: byte[] }

    /// The first header called `name`, if any.
    member this.Header(name: string) : string option =
        this.Headers |> List.tryFind (fun (k, _) -> k.Equals(name, StringComparison.OrdinalIgnoreCase)) |> Option.map snd

    member this.HeaderValues(name: string) : string list =
        this.Headers |> List.filter (fun (k, _) -> k.Equals(name, StringComparison.OrdinalIgnoreCase)) |> List.map snd

    member this.Cookies: string list = this.HeaderValues "set-cookie"

    /// A `Cookie` request header carrying every cookie this response set.
    member this.CookieJar: string = String.Join("; ", this.Cookies |> List.map (fun c -> c.Split(';')[0]))

    member this.Text: string = Encoding.UTF8.GetString this.Body

    member this.Json: Value = json this.Text

/// Everything after the headers, with chunked framing removed.
let private decodeChunked (body: byte[]) : byte[] =
    use output = new MemoryStream()
    let mutable i = 0
    let mutable go = true
    while go && i < body.Length do
        let eol = Array.IndexOf(body, byte '\r', i)
        let line = Encoding.ASCII.GetString(body, i, eol - i)
        let sizeText = (line.Split ';')[0]
        let size = Convert.ToInt32(sizeText.Trim(), 16)
        i <- eol + 2
        if size = 0 then
            go <- false
        else
            output.Write(body, i, size)
            i <- i + size + 2
    output.ToArray()

let private parseReply (bytes: byte[]) : Reply =
    let text = Encoding.Latin1.GetString bytes
    let split = text.IndexOf "\r\n\r\n"
    let head = text.Substring(0, split).Split("\r\n")
    let status = int (head[0].Split(' ')[1])
    let headers =
        [ for line in head[1..] do
              let colon = line.IndexOf ':'
              // Header values are UTF-8 on this server.
              let value = Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(line.Substring(colon + 1).Trim()))
              line.Substring(0, colon), value ]
    let body = bytes[split + 4 ..]
    let chunked = headers |> List.exists (fun (k, v) -> k.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase) && v = "chunked")
    { Status = status
      Headers = headers
      Body = if chunked then decodeChunked body else body }

type TestApp(host: WebApplication, port: int) =
    member _.Port = port
    member _.Host = host

    /// Send `req` on a connection of its own and read the reply to the end.
    member _.Send(req: Req) : Task<Reply> =
        task {
            use client = new TcpClient()
            do! client.ConnectAsync(IPAddress.Loopback, port)
            let stream = client.GetStream()
            let head = StringBuilder()
            head.Append($"{req.Method} {req.Target} HTTP/1.1\r\n") |> ignore
            for (name, value) in req.Headers do
                head.Append($"{name}: {value}\r\n") |> ignore
            match req.Body with
            | ValueSome body -> head.Append($"content-length: {body.Length}\r\n") |> ignore
            | ValueNone -> ()
            head.Append("connection: close\r\n\r\n") |> ignore
            let bytes = Encoding.UTF8.GetBytes(head.ToString())
            do! stream.WriteAsync(bytes)
            match req.Body with
            | ValueSome body -> do! stream.WriteAsync(body)
            | ValueNone -> ()
            use received = new MemoryStream()
            do! stream.CopyToAsync received
            return parseReply (received.ToArray())
        }

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(
                task {
                    do! host.StopAsync()
                    do! host.DisposeAsync()
                }
            )

/// Lets a test say which peer a request came from: `x-test-peer: 198.51.100.4`.
let private peerFromHeader (app: Microsoft.AspNetCore.Builder.IApplicationBuilder) : unit =
    app.Use(
        Func<HttpContext, RequestDelegate, Task>(fun http next ->
            match http.Request.Headers["x-test-peer"].ToString() with
            | "" -> ()
            | peer -> http.Connection.RemoteIpAddress <- IPAddress.Parse peer
            next.Invoke http)
    )
    |> ignore

/// Serve `endpoints` through the kit on a free loopback port.
let startWith (kit: Kit) (endpoints: HttpEndpoint list) (configure: IApplicationBuilder -> unit) : Task<TestApp> =
    task {
        let builder = WebApplication.CreateBuilder()
        builder.WebHost.ConfigureKestrel(fun (options: KestrelServerOptions) ->
            Adapter.configureKestrel options
            options.Listen(IPAddress.Loopback, 0))
        |> ignore
        builder.Logging.ClearProviders() |> ignore
        let app = builder.Build()
        peerFromHeader app
        configure app
        Adapter.app kit endpoints app
        do! app.StartAsync()
        let addresses = (nonNull (app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>())).Addresses
        let port = Uri(Seq.head addresses).Port
        return new TestApp(app, port)
    }

let start (kit: Kit) (endpoints: HttpEndpoint list) : Task<TestApp> = startWith kit endpoints ignore

/// A kit as the tests build it: `test-secret` and a clock frozen at 2024-06-01T12:00:00Z.
let kitWith (config: KitConfig) (state: obj) : Kit =
    let clock: SharedClock = TestClock.FrozenAt(ts "2024-06-01T12:00:00Z")
    Kit(config, secrets.Value, clock, state)
