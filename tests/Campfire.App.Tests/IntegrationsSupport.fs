// Port of rust/crates/campfire/src/integrations/test_support.rs
//
// Fakes for the integration tests: DNS answers, a dialer that sends fake public addresses to a local server, and that
// server (plain or TLS), which replays canned responses and records what it was asked.
module Campfire.App.Tests.IntegrationsSupport

open System
open System.Collections.Generic
open System.IO
open System.IO.Compression
open System.Net
open System.Net.Security
open System.Net.Sockets
open System.Security.Cryptography.X509Certificates
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging.Abstractions
open Campfire.App
open Campfire.App.Integrations
open Campfire.Db
open Campfire.RailsCompat
open Campfire.RailsCompat.Clock

/// Fixed answers per host. A host with several answer lists gives the next one on each lookup and then keeps giving the
/// last (like the Ruby tests' `Resolv.stubs(...).returns(a, b)`).
type FakeResolver() =
    let answers = Dictionary<string, IPAddress list list>()
    let lookups = List<string>()

    new(hosts: (string * string list) list) as this =
        FakeResolver()
        then
            for (host, addresses) in hosts do
                this.Set(host, [ addresses |> List.map IPAddress.Parse ])

    member _.Set(host: string, hostAnswers: IPAddress list list) : unit = lock lookups (fun () -> answers[host] <- hostAnswers)

    member _.Lookups: string list = lock lookups (fun () -> List.ofSeq lookups)

    interface IResolver with
        member _.Lookup(host: string) : Task<Result<IPAddress list, exn>> =
            lock lookups (fun () ->
                lookups.Add host
                match answers.TryGetValue host with
                | true, first :: rest ->
                    if not rest.IsEmpty then answers[host] <- rest
                    Task.FromResult(Ok first)
                | _ -> Task.FromResult(Error(exn $"no address for {host}")))

/// Connects the given addresses (any port) to `target`; anything else is dialed for real.
type MappingDialer(publicAddresses: HashSet<IPAddress>, target: IPEndPoint) =
    let dialed = List<IPEndPoint>()

    member _.Dialed: IPEndPoint list = lock dialed (fun () -> List.ofSeq dialed)

    interface IDialer with
        member _.Connect(address: IPEndPoint, cancellation: CancellationToken) : Task<Socket> =
            lock dialed (fun () -> dialed.Add address)
            let destination = if publicAddresses.Contains address.Address then target else address
            (TcpDialer() :> IDialer).Connect(destination, cancellation)

let private testdata (name: string) : string = Path.Combine(AppContext.BaseDirectory, "testdata", name)

let testTlsRoots () : X509Certificate2Collection =
    let roots = X509Certificate2Collection()
    roots.Add(X509Certificate2.CreateFromPem(File.ReadAllText(testdata "tls/ca.pem"))) |> ignore
    roots

let network (resolver: IResolver) (dialer: IDialer) : Network =
    { Resolver = resolver
      Dialer = dialer
      Tls = CustomRoots(testTlsRoots ()) }

type Route =
    { Method: string
      Host: string
      Path: string
      Status: int
      Reason: string
      Headers: (string * string) list
      Body: byte[]
      Chunked: bool
      Gzip: bool
      /// Wait this long before answering.
      Delay: TimeSpan }

module Route =
    let create (meth: string) (host: string) (path: string) (status: int) : Route =
        { Method = meth
          Host = host
          Path = path
          Status = status
          Reason = "Status"
          Headers = []
          Body = [||]
          Chunked = false
          Gzip = false
          Delay = TimeSpan.Zero }

    let header (name: string) (value: string) (route: Route) : Route = { route with Headers = route.Headers @ [ name, value ] }

    let body (body: byte[]) (route: Route) : Route = { route with Body = body }

    let text (body: string) (route: Route) : Route = { route with Body = Encoding.UTF8.GetBytes body }

/// A request as the server saw it.
type Received =
    {
        Method: string
        Target: string
        /// Header lines as sent (name case preserved), in order.
        Headers: (string * string) list
        Body: byte[]
    }

    member this.Header(name: string) : string option =
        this.Headers |> List.tryFind (fun (n, _) -> n.Equals(name, StringComparison.OrdinalIgnoreCase)) |> Option.map snd

let private readHead (stream: Stream) : Task<string> =
    task {
        let head = List<byte>()
        let one = Array.zeroCreate<byte> 1
        let mutable fin = false
        while not fin do
            let! n = stream.ReadAsync(one, 0, 1)
            if n = 0 then
                fin <- true
            else
                head.Add one[0]
                let count = head.Count
                fin <- count >= 4 && head[count - 4] = 13uy && head[count - 3] = 10uy && head[count - 2] = 13uy && head[count - 1] = 10uy
        return Encoding.Latin1.GetString(head.ToArray())
    }

let private serve (stream: Stream) (routes: Route list) (log: List<Received>) : Task =
    task {
        let! head = readHead stream
        if head <> "" then
            let lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            let parts = lines[0].Split ' '
            let meth, target = parts[0], (if parts.Length > 1 then parts[1] else "")
            let headers =
                [ for line in lines |> Array.skip 1 do
                      match line.IndexOf ": " with
                      | -1 -> ()
                      | at -> line.Substring(0, at), line.Substring(at + 2) ]
            let find (name: string) =
                headers |> List.tryFind (fun (n, _) -> n.Equals(name, StringComparison.OrdinalIgnoreCase)) |> Option.map snd
            let length =
                match find "content-length" with
                | Some v ->
                    match Int32.TryParse v with
                    | true, n -> n
                    | _ -> 0
                | None -> 0
            let body = Array.zeroCreate<byte> length
            let mutable got = 0
            while got < length do
                let! n = stream.ReadAsync(body, got, length - got)
                if n = 0 then raise (EndOfStreamException())
                got <- got + n
            let host =
                let host = defaultArg (find "host") ""
                match host.LastIndexOf ':' with
                | -1 -> host
                | at when host.Substring(at + 1) |> Seq.forall Char.IsAsciiDigit -> host.Substring(0, at)
                | _ -> host
            lock log (fun () ->
                log.Add
                    { Method = meth
                      Target = target
                      Headers = headers
                      Body = body })

            let notFound = Route.create meth host target 404 |> Route.header "Content-Type" "text/plain" |> Route.text "not found"
            let route =
                routes
                |> List.tryFind (fun r -> r.Method = meth && (r.Host = host || r.Host = "*") && r.Path = target)
                |> Option.defaultValue notFound
            do! Task.Delay route.Delay
            let responseBody =
                if route.Gzip then
                    use output = new MemoryStream()
                    (use encoder = new GZipStream(output, CompressionLevel.Optimal, true)
                     encoder.Write(route.Body, 0, route.Body.Length))
                    output.ToArray()
                else
                    route.Body
            let response = StringBuilder()
            response.Append($"HTTP/1.1 {route.Status} {route.Reason}\r\n") |> ignore
            for (name, value) in route.Headers do
                response.Append($"{name}: {value}\r\n") |> ignore
            if route.Gzip then response.Append "Content-Encoding: gzip\r\n" |> ignore
            if route.Chunked then
                response.Append "Transfer-Encoding: chunked\r\n" |> ignore
            elif not (route.Headers |> List.exists (fun (n, _) -> n.Equals("content-length", StringComparison.OrdinalIgnoreCase))) then
                response.Append($"Content-Length: {responseBody.Length}\r\n") |> ignore
            response.Append "Connection: close\r\n\r\n" |> ignore
            let headBytes = Encoding.UTF8.GetBytes(response.ToString())
            do! stream.WriteAsync(headBytes, 0, headBytes.Length)
            if meth <> "HEAD" then
                if route.Chunked then
                    let mutable at = 0
                    while at < responseBody.Length do
                        let n = min (64 * 1024) (responseBody.Length - at)
                        let size = Encoding.ASCII.GetBytes($"{n:x}\r\n")
                        do! stream.WriteAsync(size, 0, size.Length)
                        do! stream.WriteAsync(responseBody, at, n)
                        do! stream.WriteAsync([| 13uy; 10uy |], 0, 2)
                        at <- at + n
                    let last = Encoding.ASCII.GetBytes "0\r\n\r\n"
                    do! stream.WriteAsync(last, 0, last.Length)
                else
                    do! stream.WriteAsync(responseBody, 0, responseBody.Length)
            do! stream.FlushAsync()
    }

/// A TLS server certificate that the tests' CA signed (for `fcm.googleapis.com`, `www.example.com`, `example.com` and
/// `bots.example`).
let private serverCertificate () : X509Certificate2 =
    use pem = X509Certificate2.CreateFromPemFile(testdata "tls/server.pem", testdata "tls/server.key")
    // Platforms' TLS stacks want a certificate whose private key they can use, which a PFX gives them.
    X509CertificateLoader.LoadPkcs12(pem.Export X509ContentType.Pfx, null)

type FakeServer private (listener: TcpListener, received: List<Received>, stop: CancellationTokenSource) =
    member _.Addr: IPEndPoint = listener.LocalEndpoint :?> IPEndPoint

    member _.Received() : Received list = lock received (fun () -> List.ofSeq received)

    static member private StartWith(routes: Route list, tls: X509Certificate2 option) : FakeServer =
        let listener = new TcpListener(IPAddress.Loopback, 0)
        listener.Start 512
        let received = List<Received>()
        let stop = new CancellationTokenSource()
        let accepting =
            task {
                try
                    while not stop.IsCancellationRequested do
                        let! client = listener.AcceptTcpClientAsync stop.Token
                        Task.Run(fun () ->
                            task {
                                use client = client
                                try
                                    use network = client.GetStream()
                                    match tls with
                                    | Some certificate ->
                                        use ssl = new SslStream(network, false)
                                        do! ssl.AuthenticateAsServerAsync(certificate, false, false)
                                        do! serve ssl routes received
                                    | None -> do! serve network routes received
                                with _ ->
                                    ()
                            }
                            :> Task)
                        |> ignore
                with _ ->
                    ()
            }
        accepting |> ignore
        new FakeServer(listener, received, stop)

    static member Start(routes: Route list) : FakeServer = FakeServer.StartWith(routes, None)

    static member StartTls(routes: Route list) : FakeServer = FakeServer.StartWith(routes, Some(serverCertificate ()))

    interface IDisposable with
        member _.Dispose() =
            stop.Cancel()
            listener.Stop()

/// A server that answers every request with `head` and then a byte of body every 50 ms, until the client hangs up.
let trickleServer (head: string) : IDisposable * IPEndPoint =
    let listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    let stop = new CancellationTokenSource()
    let accepting =
        task {
            try
                while not stop.IsCancellationRequested do
                    let! client = listener.AcceptTcpClientAsync stop.Token
                    Task.Run(fun () ->
                        task {
                            use client = client
                            try
                                let stream = client.GetStream()
                                let! _ = stream.ReadAsync(Array.zeroCreate<byte> 4096, 0, 4096)
                                let bytes = Encoding.ASCII.GetBytes head
                                do! stream.WriteAsync(bytes, 0, bytes.Length)
                                while true do
                                    do! stream.WriteAsync([| byte ' ' |], 0, 1)
                                    do! Task.Delay 50
                            with _ ->
                                ()
                        }
                        :> Task)
                    |> ignore
            with _ ->
                ()
        }
    accepting |> ignore
    ({ new IDisposable with
        member _.Dispose() =
            stop.Cancel()
            listener.Stop() }),
    (listener.LocalEndpoint :?> IPEndPoint)

/// A gzip bomb: `megabytes` gzip members of a megabyte of zeros each, about 1 KB apiece.
let gzipBomb (megabytes: int) : byte[] =
    use output = new MemoryStream()
    (use encoder = new GZipStream(output, CompressionLevel.SmallestSize, true)
     encoder.Write(Array.zeroCreate<byte> (1024 * 1024), 0, 1024 * 1024))
    let member' = output.ToArray()
    Array.concat (List.replicate megabytes member')

[<Literal>]
let private SecretKeyBase = "integrations-test-secret-key-base"

/// The reference fixtures in a fresh database (like `fixtures :all`), with the app's rich text.
type TestDb() =
    let dir = Directory.CreateTempSubdirectory("campfire-integrations").FullName
    let clock = TestClock()
    let db =
        let secrets = Secrets.create SecretKeyBase
        let richText = AppRichText(secrets, clock, NullLogger.Instance)
        let env: Env =
            { Clock = clock
              Sink = NullSink()
              RichText = richText
              BcryptCost = 4 }
        let db = Database.Open({ Config.create (Path.Combine(dir, "test.sqlite3")) with Environment = "test" }, env)
        match db.WriteBlocking(fun tx -> Fixtures.load tx.Conn (Fixtures.referenceDir ()) { Now = tx.Now(); BcryptCost = 4 } |> ignore) with
        | Ok() -> ()
        | Error error -> failwith (DbError.display error)
        db

    member _.Db: Database = db

    static member Id(label: string) : int64 = Fixtures.identify label

    interface IDisposable with
        member _.Dispose() =
            (db :> IDisposable).Dispose()
            try
                Directory.Delete(dir, true)
            with _ ->
                ()
