/// A front server (`Front.serveWith`) on loopback ports in front of a test app, and a raw HTTP/1.1
/// client for it, as rust/crates/kit/tests/front.rs has.
module Campfire.Kit.Tests.FrontHarness

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open Campfire.Kit

let freePort () : int =
    let listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    let port = (listener.LocalEndpoint :?> IPEndPoint).Port
    listener.Stop()
    port

type FrontServer(http: int, target: int, stop: TaskCompletionSource, running: Task) =
    member _.Http = http
    member _.Target = target

    member _.Stop() : Task =
        task {
            stop.TrySetResult() |> ignore
            let! finished = Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds 15.0))
            ignore finished
        }

    interface IAsyncDisposable with
        member this.DisposeAsync() = ValueTask(this.Stop())

/// `None` when the server stops before it's listening (a port was taken).
let tryStart
    (vars: (string * string) list)
    (app: IApplicationBuilder -> unit)
    (acme: AcmeOptions voption)
    (loggerFactory: ILoggerFactory)
    (http: int)
    (https: int)
    : Task<FrontServer option> =
    task {
        let target = freePort ()
        let env = Dictionary<string, string>()
        for (k, v) in vars do
            env[k] <- v
        env.TryAdd("HTTP_PORT", string http) |> ignore
        env.TryAdd("HTTPS_PORT", string https) |> ignore
        env["TARGET_PORT"] <- string target
        env.TryAdd("LOG_REQUESTS", "false") |> ignore
        let config =
            FrontConfig.fromLookup (fun name ->
                match env.TryGetValue name with
                | true, v -> v
                | _ -> null)
        let stop = TaskCompletionSource()
        let running = Front.serveWith config app acme loggerFactory stop.Task
        let mutable listening = false
        let mutable attempts = 0
        while not listening && not running.IsCompleted && attempts < 200 do
            attempts <- attempts + 1
            try
                use client = new TcpClient()
                do! client.ConnectAsync(IPAddress.Loopback, http)
                listening <- true
            with :? SocketException ->
                do! Task.Delay 20
        if running.IsCompleted then
            // Observe the failure; a taken port is the only one expected.
            try
                do! running
            with _ ->
                ()
            return None
        else
            return Some(FrontServer(http, target, stop, running))
    }

/// On fresh ports, picking new ones when something took one between `freePort` and the bind.
let startWith (vars: (string * string) list) (app: IApplicationBuilder -> unit) : Task<FrontServer> =
    task {
        let mutable server: FrontServer option = None
        let mutable attempts = 0
        while server.IsNone && attempts < 5 do
            attempts <- attempts + 1
            let! started = tryStart vars app ValueNone NullLoggerFactory.Instance (freePort ()) (freePort ())
            server <- started
        return (match server with Some s -> s | None -> failwith "front server didn't start")
    }

/// A raw HTTP/1.1 exchange on a fresh connection, read to EOF.
type Reply =
    { Status: int
      Headers: (string * string) list
      Body: byte[] }

    member this.All(name: string) : string list =
        this.Headers |> List.filter (fun (n, _) -> n.Equals(name, StringComparison.OrdinalIgnoreCase)) |> List.map snd

    member this.Get(name: string) : string option = this.All name |> List.tryHead
    member this.Text = Encoding.UTF8.GetString this.Body

let private dechunk (data: byte[]) : byte[] =
    use output = new MemoryStream()
    let mutable at = 0
    let mutable go = true
    while go do
        let lineEnd = Array.IndexOf(data, byte '\r', at)
        let size = Convert.ToInt32(Encoding.ASCII.GetString(data, at, lineEnd - at).Trim(), 16)
        at <- lineEnd + 2
        if size = 0 then
            go <- false
        else
            output.Write(data, at, size)
            at <- at + size + 2
    output.ToArray()

let parseReply (raw: byte[]) : Reply =
    let text = Encoding.Latin1.GetString raw
    let split = text.IndexOf "\r\n\r\n"
    if split < 0 then failwith "a complete head"
    let head = text.Substring(0, split).Split("\r\n")
    let status = int (head[0].Split(' ')[1])
    let headers =
        [ for line in head[1..] do
              let colon = line.IndexOf ": "
              line.Substring(0, colon).ToLowerInvariant(), line.Substring(colon + 2) ]
    let body = raw[split + 4 ..]
    let chunked = headers |> List.exists (fun (n, v) -> n = "transfer-encoding" && v = "chunked")
    { Status = status; Headers = headers; Body = (if chunked then dechunk body else body) }

let exchange (port: int) (request: string) : Task<Reply> =
    task {
        use client = new TcpClient()
        do! client.ConnectAsync(IPAddress.Loopback, port)
        let stream = client.GetStream()
        do! stream.WriteAsync(Encoding.ASCII.GetBytes request)
        use received = new MemoryStream()
        use cts = new CancellationTokenSource(TimeSpan.FromSeconds 10.0)
        do! stream.CopyToAsync(received, cts.Token)
        return parseReply (received.ToArray())
    }

let getRequest (path: string) (extra: string) : string =
    $"GET {path} HTTP/1.1\r\nHost: chat.test\r\nConnection: close\r\n{extra}\r\n"
