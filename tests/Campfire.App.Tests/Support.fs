/// What the app's tests share: a real Kestrel server on a loopback port running a pipeline, a raw
/// HTTP/1.1 client for it (`Uri` rewrites a bare `%` in a query to `%25`, and these tests need the bytes
/// they wrote on the wire), and a booted app over a copy of a reference-built parity seed
/// (`parity/bin/seed build`, which needs Docker), skipped with a message when it hasn't been built
/// (`CAMPFIRE_REQUIRE_SEED=1` fails the test instead).
module Campfire.App.Tests.Support

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open Campfire.App
open Campfire.Kit
open Campfire.Tests

/// The pinned values of the parity environment (`parity/.env.reference`).
let parityEnv (name: string) : string option =
    let path = Repo.path "parity/.env.reference"
    if File.Exists path then
        File.ReadAllLines path
        |> Array.tryPick (fun line -> if line.StartsWith(name + "=", StringComparison.Ordinal) then Some(line.Substring(name.Length + 1)) else None)
    else
        None

let str (element: JsonElement) : string =
    match element.GetString() with
    | null -> failwith "not a string"
    | text -> text

// --- Requests and replies --------------------------------------------------------------------------------

/// A request as it goes on the wire.
type Req =
    { Method: string
      Target: string
      Headers: (string * string) list
      Body: byte[] option }

    /// Set a header, replacing any the request already has under that name.
    member this.With(name: string, value: string) : Req =
        let others = this.Headers |> List.filter (fun (k, _) -> not (k.Equals(name, StringComparison.OrdinalIgnoreCase)))
        { this with Headers = others @ [ name, value ] }

    member this.WithBody(body: string) : Req = { this with Body = Some(Encoding.UTF8.GetBytes body) }

    /// A form post's content type and body.
    member this.Form(body: string) : Req =
        { this.With("content-type", "application/x-www-form-urlencoded") with Body = Some(Encoding.UTF8.GetBytes body) }

let request (meth: string) (target: string) : Req =
    { Method = meth
      Target = target
      Headers = [ "host", "campfire.test" ]
      Body = None }

let get (target: string) : Req = request "GET" target

let getWithCookie (target: string) (cookie: string) : Req = (get target).With("cookie", cookie)

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

    member this.Text: string = Encoding.UTF8.GetString this.Body

/// Everything after the headers, with chunked framing removed.
let private decodeChunked (body: byte[]) : byte[] =
    use output = new MemoryStream()
    let mutable i = 0
    let mutable go = true
    while go && i < body.Length do
        let eol = Array.IndexOf(body, byte '\r', i)
        let line = Encoding.ASCII.GetString(body, i, eol - i)
        let size = Convert.ToInt32(((line.Split ';')[0]).Trim(), 16)
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

/// A Kestrel server on a free loopback port running a pipeline.
type Host(host: WebApplication, port: int) =
    member _.Port = port

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
            | Some body -> head.Append($"content-length: {body.Length}\r\n") |> ignore
            | None -> ()
            head.Append("connection: close\r\n\r\n") |> ignore
            do! stream.WriteAsync(Encoding.UTF8.GetBytes(head.ToString()))
            match req.Body with
            | Some body -> do! stream.WriteAsync(body)
            | None -> ()
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

/// Serve `pipeline` on a free loopback port.
let startPipeline (pipeline: IApplicationBuilder -> unit) : Task<Host> =
    task {
        let builder = WebApplication.CreateSlimBuilder()
        builder.WebHost.ConfigureKestrel(fun (options: KestrelServerOptions) ->
            Adapter.configureKestrel options
            options.Listen(IPAddress.Loopback, 0))
        |> ignore
        builder.Logging.ClearProviders() |> ignore
        let app = builder.Build()
        pipeline app
        do! app.StartAsync()
        let addresses = (nonNull (app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>())).Addresses
        return new Host(app, Uri(Seq.head addresses).Port)
    }

/// Log lines written to a factory, formatted as the console would (without its timestamp).
type CapturingLoggers() =
    let lines = List<string>()

    member _.Text: string = lock lines (fun () -> String.Join('\n', lines))

    member this.Logger(category: string) : ILogger = (this :> ILoggerFactory).CreateLogger category

    interface ILoggerFactory with
        member _.AddProvider(_: ILoggerProvider) = ()

        member _.CreateLogger(category: string) : ILogger =
            { new ILogger with
                member _.BeginScope<'S when 'S: not null>(_: 'S) : IDisposable | null = null
                member _.IsEnabled(_: LogLevel) = true

                member _.Log<'S>(level: LogLevel, _: EventId, state: 'S, ex: exn | null, formatter: Func<'S, exn | null, string>) =
                    lock lines (fun () -> lines.Add $"{level} {category}: {formatter.Invoke(state, ex)}") }

        member _.Dispose() = ()

// --- A booted app ------------------------------------------------------------------------------------------

/// A booted app over its own copy of a seed (or an empty storage directory), and a server for its pipeline.
type Test(booted: Booted, dir: string, host: Host, logs: CapturingLoggers) =
    member _.Booted = booted

    /// What the app logged.
    member _.Logs: string = logs.Text
    member _.App = booted.App
    member _.Dir = dir
    member _.Host = host

    /// The request to the booted app's whole pipeline.
    member _.Send(req: Req) : Task<Reply> = host.Send req

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(
                task {
                    do! (host :> IAsyncDisposable).DisposeAsync()
                    do! booted.Jobs.Shutdown(TimeSpan.FromSeconds 5.0)
                    (booted.App.Db :> IDisposable).Dispose()
                    try
                        Directory.Delete(dir, true)
                    with _ ->
                        ()
                }
            )

let private copyDir (from: string) (target: string) : unit =
    let rec copy (from: string) (target: string) =
        Directory.CreateDirectory target |> ignore
        for file in Directory.GetFiles from do
            File.Copy(file, Path.Combine(target, nonNull (Path.GetFileName file)))
        for sub in Directory.GetDirectories from do
            copy sub (Path.Combine(target, nonNull (Path.GetFileName sub)))
    copy from target

/// The app's configuration over `root` as its storage, with the parity secret.
let configFor (root: string) (extra: (string * string) list) : AppConfig =
    let secret = defaultArg (parityEnv "SECRET_KEY_BASE") "test-secret-key-base"
    let vars =
        dict (
            [ "SECRET_KEY_BASE", secret
              "DISABLE_SSL", "true"
              "APP_VERSION", "parity"
              "GIT_REVISION", "parity"
              "CAMPFIRE_STORAGE_PATH", root ]
            @ extra
        )
    match AppConfig.fromLookup (fun name -> match vars.TryGetValue name with | true, value -> value | _ -> null) with
    | Ok config -> config
    | Error message -> failwith message

let private start (config: AppConfig) (dir: string) : Task<Test> =
    task {
        let logs = new CapturingLoggers()
        let! booted = Boot.boot config logs
        let! host = startPipeline booted.Pipeline
        return new Test(booted, dir, host, logs)
    }

/// An app booted over a private copy of seed `name`, or `None` (a skip) when the seed isn't built.
let bootSeeded (name: string) : Task<Test> =
    task {
        match Repo.seed name with
        | None ->
            Assert.Skip $"parity seed {name} is not built (parity/bin/seed build {name}); CAMPFIRE_REQUIRE_SEED=1 fails it instead"
            return Unchecked.defaultof<Test>
        | Some seed ->
            let dir = Directory.CreateTempSubdirectory("campfire-app-test").FullName
            Directory.CreateDirectory(Path.Combine(dir, "db")) |> ignore
            for file in [ "production.sqlite3"; "production.sqlite3-wal"; "production.sqlite3-shm" ] do
                let from = Path.Combine(seed, "db", file)
                if File.Exists from then File.Copy(from, Path.Combine(dir, "db", file))
            let storage = Path.Combine(seed, "storage")
            if Directory.Exists storage then copyDir storage (Path.Combine(dir, "files"))
            return! start (configFor dir []) dir
    }

/// An app booted over an empty storage directory (a fresh install's database).
let bootEmpty () : Task<Test> =
    task {
        let dir = Directory.CreateTempSubdirectory("campfire-app-test").FullName
        let config =
            match AppConfig.fromLookup (fun name -> match name with "SECRET_KEY_BASE_DUMMY" -> "1" | "CAMPFIRE_STORAGE_PATH" -> dir | _ -> null) with
            | Ok config -> config
            | Error message -> failwith message
        return! start config dir
    }

/// The Rails-issued session cookies and blob paths of `vectors/campfire_sessions.json`.
type SessionVector = { UserName: string; CookieHeader: string }

let sessionVectors () : SessionVector list * string list * string =
    let root = (Repo.vector "campfire_sessions").RootElement
    let sessions =
        [ for s in root.GetProperty("sessions").EnumerateArray() ->
              { UserName = str (s.GetProperty "user_name")
                CookieHeader = str (s.GetProperty "cookie_header") } ]
    let blobs = [ for b in root.GetProperty("blobs").EnumerateArray() -> str (b.GetProperty "redirect_path") ]
    sessions, blobs, str (root.GetProperty("forged").GetProperty "cookie_header")

