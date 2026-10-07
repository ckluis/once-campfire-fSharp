// Port of rust/crates/kit/src/front/conn.rs
//
// Accepting and serving connections the way Thruster's `http.Server`s do (`internal/server.go`):
// HTTP/1.1, HTTP/2 over TLS (and cleartext HTTP/2 with H2C_ENABLED), and Go's three timeouts:
//
// - HTTP_READ_TIMEOUT bounds reading a request, headers and body;
// - HTTP_WRITE_TIMEOUT bounds the rest of the exchange, from the request to the response's end;
// - HTTP_IDLE_TIMEOUT closes a keep-alive connection with no request in flight.
//
// Kestrel accepts the connections and speaks the protocols; this file sets its limits to the
// timeouts (`RequestHeadersTimeout` is the read timeout while the headers arrive, `KeepAliveTimeout`
// the idle timeout, which unlike Rust's hyper it also runs for HTTP/1 until the next request's first
// byte, as Go's server did) and adds what it doesn't have: the read deadline on a request's body, the
// write deadline on the whole exchange, and HTTP/2 over cleartext on a port that also speaks HTTP/1.
//
// An upgraded connection (Action Cable's WebSocket) has none: Go clears the deadlines when
// `httputil.ReverseProxy` hijacks it.
namespace Campfire.Kit

open System
open System.Buffers
open System.Diagnostics
open System.IO
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Connections
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Microsoft.AspNetCore.Server.Kestrel.Core

/// The three timeouts (none where zero).
type FrontTimeouts =
    { Idle: TimeSpan
      Read: TimeSpan
      Write: TimeSpan }

/// A request body that fails once its deadline passes (Go's connection read deadline).
[<Sealed>]
type internal DeadlineStream(inner: Stream, deadline: int64) =
    inherit Stream()

    let remaining () =
        let left = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline)
        if left <= TimeSpan.Zero then raise (IOException("read timeout", TimeoutException())) else left

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = raise (NotSupportedException())
        and set _ = raise (NotSupportedException())

    override _.Flush() = ()
    // Synchronous reads are refused, as Kestrel refuses them by default.
    override _.Read(_: byte[], _: int, _: int) : int = raise (InvalidOperationException "Synchronous operations are disallowed. Call ReadAsync instead.")
    override _.Seek(_: int64, _: SeekOrigin) : int64 = raise (NotSupportedException())
    override _.SetLength(_: int64) = raise (NotSupportedException())
    override _.Write(_: byte[], _: int, _: int) = raise (NotSupportedException())

    override _.ReadAsync(buffer: Memory<byte>, ct: CancellationToken) : ValueTask<int> =
        let left = remaining ()
        ValueTask<int>(
            task {
                use linked = CancellationTokenSource.CreateLinkedTokenSource ct
                linked.CancelAfter left
                try
                    return! inner.ReadAsync(buffer, linked.Token)
                with :? OperationCanceledException when not ct.IsCancellationRequested ->
                    return raise (IOException("read timeout", TimeoutException()))
            }
        )

    override this.ReadAsync(buffer: byte[], offset: int, count: int, ct: CancellationToken) : Task<int> =
        this.ReadAsync(Memory<byte>(buffer, offset, count), ct).AsTask()

module FrontConn =
    /// The deadlines of a request: its body must arrive within the read timeout, and the exchange must
    /// end within the write timeout, or the connection is dropped without a response.
    let withDeadlines (timeouts: FrontTimeouts) (next: RequestDelegate) : RequestDelegate =
        RequestDelegate(fun ctx ->
            let started = Stopwatch.GetTimestamp()
            if timeouts.Read > TimeSpan.Zero then
                let mayHaveBody =
                    match ctx.Features.Get<IHttpRequestBodyDetectionFeature>() with
                    | null -> true
                    | feature -> feature.CanHaveBody
                if mayHaveBody then
                    ctx.Request.Body <- new DeadlineStream(ctx.Request.Body, started + int64 (timeouts.Read.TotalSeconds * float Stopwatch.Frequency))
            let work = next.Invoke ctx
            if timeouts.Write = TimeSpan.Zero || work.IsCompleted then
                work
            else
                task {
                    use cts = new CancellationTokenSource()
                    let left = timeouts.Write - Stopwatch.GetElapsedTime started
                    let timer = Task.Delay((if left > TimeSpan.Zero then left else TimeSpan.Zero), cts.Token)
                    let! first = Task.WhenAny(work, timer)
                    if obj.ReferenceEquals(first, work) then
                        cts.Cancel()
                        do! work
                    elif ctx.Response.StatusCode = 101 then
                        // Upgraded: the connection is the socket's now.
                        do! work
                    else
                        ctx.Abort()
                        // The app stops when it notices the connection is gone; its outcome no longer matters.
                        try
                            do! work
                        with _ ->
                            ()
                }
                :> Task)

    /// Kestrel's limits for these timeouts.
    let configureLimits (options: KestrelServerOptions) (timeouts: FrontTimeouts) : unit =
        let orForever (span: TimeSpan) = if span > TimeSpan.Zero then span else TimeSpan.MaxValue
        options.Limits.KeepAliveTimeout <- orForever timeouts.Idle
        options.Limits.RequestHeadersTimeout <- orForever timeouts.Read
        // The deadlines above are what bounds a slow client; Kestrel's rate minimums would be a second, different rule.
        options.Limits.MinRequestBodyDataRate <- null
        options.Limits.MinResponseDataRate <- null

/// HTTP/2 without TLS on a port that also serves HTTP/1.1 (Rust's `Protocol::Auto`). Kestrel serves one
/// or the other on a cleartext endpoint, so each connection's first bytes are read for HTTP/2's
/// preface, and the protocol it will speak is handed to Kestrel's connection middleware the way TLS's
/// ALPN result is, through the (internal) feature it reads. If Kestrel's internals aren't as expected,
/// the port serves HTTP/1.1 only, and `available` says so.
module internal FrontH2c =
    let private preface = "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"B

    /// Hands a connection's protocols to Kestrel: `HttpProtocolsFeature(protocols)` set on the connection's features.
    let private setProtocols: (IFeatureCollection -> HttpProtocols -> unit) voption =
        try
            match typeof<KestrelServerOptions>.Assembly.GetType "Microsoft.AspNetCore.Server.Kestrel.Core.Internal.HttpProtocolsFeature" with
            | null -> ValueNone
            | featureType ->
                let constructor =
                    featureType.GetConstructor(BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic, null, [| typeof<HttpProtocols> |], null)
                let set = typeof<IFeatureCollection>.GetMethod "Set"
                match constructor, set with
                | null, _
                | _, null -> ValueNone
                | constructor, set ->
                    let set = set.MakeGenericMethod featureType
                    ValueSome(fun features protocols -> set.Invoke(features, [| constructor.Invoke [| box protocols |] |]) |> ignore)
        with _ ->
            ValueNone

    let available = setProtocols.IsSome

    /// A connection middleware that sends connections starting with the HTTP/2 preface to Kestrel's HTTP/2.
    let sniff (timeout: TimeSpan) : Func<ConnectionContext, Func<Task>, Task> =
        Func<ConnectionContext, Func<Task>, Task>(fun connection next ->
            task {
                use cts = new CancellationTokenSource()
                if timeout > TimeSpan.Zero then cts.CancelAfter timeout
                let reader = connection.Transport.Input
                let mutable decided = false
                let mutable http2 = false
                let mutable aborted = false
                try
                    while not decided do
                        let! result = reader.ReadAsync cts.Token
                        let buffer = result.Buffer
                        let seen = int (min buffer.Length (int64 preface.Length))
                        let head = buffer.Slice(0L, int64 seen).ToArray()
                        let matches = ReadOnlySpan<byte>(head).SequenceEqual(ReadOnlySpan<byte>(preface, 0, seen))
                        if not matches then
                            decided <- true
                        elif seen = preface.Length then
                            decided <- true
                            http2 <- true
                        elif result.IsCompleted then
                            decided <- true
                        // Consume nothing: Kestrel reads the same bytes again.
                        if decided then reader.AdvanceTo(buffer.Start, buffer.Start) else reader.AdvanceTo(buffer.Start, buffer.End)
                with :? OperationCanceledException ->
                    aborted <- true
                    connection.Abort()
                if not aborted then
                    if http2 then
                        match setProtocols with
                        | ValueSome set -> set connection.Features HttpProtocols.Http2
                        | ValueNone -> ()
                    do! next.Invoke()
            }
            :> Task)
