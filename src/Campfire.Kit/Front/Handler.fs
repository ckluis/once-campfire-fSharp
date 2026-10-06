// Port of rust/crates/kit/src/front/handler.rs
//
// Thruster's handler chain (`internal/handler.go`), outermost first: request logging, the request
// size limit, compression, `X-Request-Start`, the response cache, and the proxy's `X-Forwarded-*`
// headers in front of the app.
//
// Rust wraps the app's `Response<Body>` in bodies that record it for the cache, compress it and count
// its bytes for the log. Here the app writes into the `HttpContext`'s response, so `FrontResponse` is
// the `IHttpResponseBodyFeature` it writes to: it decides, from the headers the app has set by the
// time it first writes, whether to record the body (for the cache) and whether to hold the start of it
// to compress it, and otherwise passes everything straight on. What changes the response's headers
// happens in an `OnStarting` callback registered before the app's own, so it runs after them.
namespace Campfire.Kit

open System
open System.Buffers
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.IO.Pipelines
open System.Net
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Primitives

/// The settings of a front handler that don't change, and the cache it fills.
[<Sealed>]
type FrontServices(config: FrontConfig, logger: ILogger, lines: RequestLines | null) =
    let acceptEncodings = ConcurrentDictionary<string, Encoding>(StringComparer.Ordinal)

    member val Cache = MemoryCache(config.CacheSize, config.MaxCacheItemSize)
    member _.Config = config
    member _.Logger = logger
    member val MaxCacheableBody = int (min (max config.MaxCacheItemSize 0L) (int64 Int32.MaxValue))
    member val MaxRequestBody = uint64 (max config.MaxRequestBody 0L)
    member val CompressionEnabled = config.GzipCompressionEnabled

    /// `strings.Repeat("Padding-", ...)`, or empty without jitter.
    member val Padding = FrontCompression.jitterPadding config.GzipCompressionJitter
    member val LogRequests = config.LogRequests && logger.IsEnabled LogLevel.Information

    /// Where request lines go as bytes when the host asked for it (`RequestLog.stdout`); null leaves them to `Logger`.
    member _.Lines = lines

    new(config: FrontConfig, logger: ILogger) = FrontServices(config, logger, null)

    /// What `gzhttp` would pick for a request with this method and `Accept-Encoding`: clients send a
    /// handful of distinct values, so each is worked out once (up to a bound, so that a client
    /// inventing values can't grow it).
    member _.SelectEncoding(meth: string, acceptEncoding: string | null) : Encoding =
        match acceptEncoding with
        | null
        | "" -> Encoding.NoEncoding
        | _ when String.Equals(meth, "HEAD", StringComparison.Ordinal) -> Encoding.NoEncoding
        | accept ->
            match acceptEncodings.TryGetValue accept with
            | true, encoding -> encoding
            | _ ->
                let encoding = FrontCompression.selectEncoding meth accept
                if acceptEncodings.Count < 512 then acceptEncodings.TryAdd(accept, encoding) |> ignore
                encoding

module internal FrontRequest =
    /// The request target as the client sent it.
    let rawTarget (ctx: HttpContext) : string | null =
        match ctx.Features.Get<IHttpRequestFeature>() with
        | null -> null
        | feature -> feature.RawTarget

module internal FrontHeaders =
    /// The first value of a header, or "".
    let first (headers: IHeaderDictionary) (name: string) : string =
        let values = headers[name]
        if values.Count = 0 then "" else (match values[0] with null -> "" | v -> v)

    /// What `Go`'s `http.Server` drops when it writes the headers of a status that can't carry them
    /// (`suppressedHeaders`): `Content-Type`, `Content-Length` and `Transfer-Encoding` on a 304
    /// (whether Rails or the cache produced it), the length on 1xx and 204.
    let suppressBodilessHeaders (status: int) (headers: IHeaderDictionary) : unit =
        if status = 304 then
            headers.Remove "Content-Type" |> ignore
            headers.Remove "Content-Length" |> ignore
            headers.Remove "Transfer-Encoding" |> ignore
        elif (status >= 100 && status <= 199) || status = 204 then
            headers.Remove "Content-Length" |> ignore
            headers.Remove "Transfer-Encoding" |> ignore

    /// A copy of the headers, a line to each value.
    let snapshot (headers: IHeaderDictionary) : struct (string * string)[] =
        let lines = ResizeArray<struct (string * string)>(headers.Count + 2)
        for header in headers do
            if not (header.Key.Equals("x-cache", StringComparison.OrdinalIgnoreCase)) then
                for value in header.Value do
                    match value with
                    | null -> ()
                    | value -> lines.Add(struct (header.Key, value))
        lines.ToArray()

    /// `gzhttp` adds `Vary: Accept-Encoding` to every response before the handler runs.
    let addVary (headers: IHeaderDictionary) (merge: HeaderMerge) : unit =
        let existing = headers.Vary
        if merge = HeaderMerge.Replace && existing.Count > 0 then
            ()
        else
            let values = Array.zeroCreate<string | null> (existing.Count + 1)
            values[0] <- "Accept-Encoding"
            for i in 0 .. existing.Count - 1 do
                values[i + 1] <- existing[i]
            headers.Vary <- StringValues values

/// What a front server logs for each request (`internal/logging_handler.go`), as structured fields.
[<Sealed>]
type RequestLogEntry(fields: KeyValuePair<string, obj | null>[]) =
    interface IReadOnlyList<KeyValuePair<string, obj | null>> with
        member _.Count = fields.Length
        member _.Item with get (index: int) = fields[index]
        member _.GetEnumerator() : IEnumerator<KeyValuePair<string, obj | null>> = (fields :> seq<_>).GetEnumerator()
        member _.GetEnumerator() : Collections.IEnumerator = fields.GetEnumerator()

    override _.ToString() =
        let text = Text.StringBuilder("Request")
        for field in fields do
            text.Append(' ').Append(field.Key).Append('=').Append(field.Value) |> ignore
        text.ToString()

/// What a response being recorded for the cache needs.
[<Sealed>]
type internal RecordState(lifetime: TimeSpan) =
    member _.Lifetime = lifetime
    member val Overflowed = false with get, set

    [<DefaultValue>]
    val mutable Body: ArrayBufferWriter<byte> | null

    [<DefaultValue>]
    val mutable Headers: struct (string * string)[] | null

    member val Vary = "" with get, set

/// What a response that may be compressed needs: the start of the body, held until it's clear whether
/// to, and the encoder once it is.
[<Sealed>]
type internal CompressState() =
    [<DefaultValue>]
    val mutable Held: ArrayBufferWriter<byte> | null

    [<DefaultValue>]
    val mutable Encoder: FrontEncoder | null

    member val Encoding = Encoding.NoEncoding with get, set
    member val FinalLength = -1L with get, set

    [<DefaultValue>]
    val mutable SniffedContentType: string | null

/// The response a handler gives the connection: the `IHttpResponseBodyFeature` the app writes to.
[<Sealed>]
type FrontResponse
    (
        services: FrontServices,
        ctx: HttpContext,
        inner: IHttpResponseBodyFeature,
        merge: HeaderMerge,
        negotiation: Negotiation,
        cacheKey: string | null,
        now: int64
    ) =
    /// 0: not decided (the app hasn't written yet), 1: pass on, 2: holding the start of the body to
    /// decide whether to compress, 3: compressing.
    let mutable mode = 0
    let mutable hit = false
    let mutable logged = false
    let mutable bytesSent = 0L
    let mutable record: RecordState | null = null
    let mutable compress: CompressState | null = null
    let mutable scratch: ArrayBufferWriter<byte> | null = null
    let mutable stream: FrontStream | null = null
    let mutable writer: FrontWriter | null = null

    member _.Services = services

    /// The `X-Cache` value: `bypass` for a request the cache can't serve, `miss`, or `hit`.
    member val XCache: string = (if isNull cacheKey then "bypass" else "miss") with get, set

    member _.BytesSent = bytesSent

    /// Called as the headers go out, after the app's own `OnStarting` callbacks.
    member this.Starting() : unit =
        this.Decide()
        let headers = ctx.Response.Headers
        let status = ctx.Response.StatusCode
        // The cache keeps the response as the app made it, before anything below.
        match record with
        | null -> ()
        | state -> state.Headers <- FrontHeaders.snapshot headers
        // Only `Compression::apply` adds it in Rust, and Thruster installs gzhttp only when compression is on.
        if services.CompressionEnabled then FrontHeaders.addVary headers merge
        match compress with
        | null -> ()
        | state ->
            if state.Encoding <> Encoding.NoEncoding then
                headers.ContentEncoding <- (if state.Encoding = Encoding.Gzip then "gzip" else "zstd")
                headers.Remove "Content-Length" |> ignore
                headers.Remove "Accept-Ranges" |> ignore
                if state.FinalLength >= 0L then headers.ContentLength <- state.FinalLength
            match state.SniffedContentType with
            | null -> ()
            | contentType -> if not (headers.ContainsKey "Content-Type") then headers.ContentType <- contentType
        headers["X-Cache"] <- StringValues this.XCache
        FrontHeaders.suppressBodilessHeaders status headers
        // An upgraded connection's request never ends, so it's logged as it starts.
        if status = 101 then this.Log()

    /// What the app has set by now says whether to record the body for the cache and whether to hold
    /// its start to compress it.
    member private this.Decide() : unit =
        if mode = 0 then
            let status = ctx.Response.StatusCode
            let headers = ctx.Response.Headers
            if not (isNull cacheKey) && not hit && status <> 101 then
                let vary = FrontHeaders.first headers "Vary"
                match FrontCache.cacheLifetime status (FrontHeaders.first headers "Cache-Control") vary with
                | ValueSome lifetime ->
                    let state = RecordState(lifetime)
                    state.Vary <- vary
                    record <- state
                    headers.Remove "Set-Cookie" |> ignore
                | ValueNone -> ()
            mode <- 1
            if status >= 200 then
                // `NO_COMPRESSION` is the guard's veto, never sent.
                let vetoed = headers.ContainsKey FrontCompression.NoCompression
                if vetoed then headers.Remove FrontCompression.NoCompression |> ignore
                if services.CompressionEnabled && negotiation.Encoding <> Encoding.NoEncoding then
                    let guarded =
                        negotiation.UserSpecificRequest
                        || (services.Config.GzipCompressionDisableOnAuth && FrontCompression.hasUserSpecificResponseHeaders headers)
                    if not (guarded || vetoed) && FrontCompression.mayCompress headers then
                        compress <- CompressState()
                        mode <- 2

    /// Whether the body can go straight to the connection: nothing needs to see it.
    member private _.Direct = mode = 1 && isNull record

    member private _.Record(data: ReadOnlySpan<byte>) : unit =
        match record with
        | null -> ()
        | state ->
            if not state.Overflowed then
                let buffer =
                    match state.Body with
                    | null ->
                        let created = ArrayBufferWriter<byte>(min services.MaxCacheableBody 4096)
                        state.Body <- created
                        created
                    | buffer -> buffer
                if buffer.WrittenCount + data.Length > services.MaxCacheableBody then
                    state.Overflowed <- true
                    state.Body <- null
                else
                    buffer.Write data

    member private this.PassOn(data: ReadOnlyMemory<byte>) : ValueTask =
        if data.Length = 0 then
            ValueTask.CompletedTask
        else
            bytesSent <- bytesSent + int64 data.Length
            inner.Stream.WriteAsync data

    /// Takes the next piece of the body, as the app wrote it.
    member this.Write(data: ReadOnlyMemory<byte>) : ValueTask =
        if mode = 0 then this.Decide()
        if data.Length > 0 && not (isNull record) then this.Record data.Span
        match mode with
        | 1 -> this.PassOn data
        | 2 ->
            let state = nonNull compress
            let buffer =
                match state.Held with
                | null ->
                    let created = ArrayBufferWriter<byte>(4096)
                    state.Held <- created
                    created
                | buffer -> buffer
            buffer.Write data.Span
            let want = if services.Padding = "" then FrontCompression.MinSize else max FrontCompression.JitterBuffer FrontCompression.MinSize
            if buffer.WrittenCount >= want then this.StartCompressing false else ValueTask.CompletedTask
        | _ ->
            match (nonNull compress).Encoder with
            | null -> this.PassOn data
            | encoder -> this.PassOn(ReadOnlyMemory<byte>(encoder.Write data.Span))

    /// Decides, with the start of the body in hand (all of it if `ended`), whether it's long enough
    /// and of a type worth compressing, and goes on with it either way.
    member private this.StartCompressing(ended: bool) : ValueTask =
        let state = nonNull compress
        let buffered = nonNull state.Held
        let headers = ctx.Response.Headers
        let status = ctx.Response.StatusCode
        let bytes = buffered.WrittenMemory
        let mutable contentType = FrontHeaders.first headers "Content-Type"
        if contentType = "" && status <> 204 && status <> 304 && bytes.Length > 0 then
            contentType <- FrontCompression.detectContentType bytes.Span
            if not (headers.ContainsKey "Content-Type") then state.SniffedContentType <- contentType
        state.Held <- null
        if not (bytes.Length >= FrontCompression.MinSize && FrontCompression.contentTypeFilter contentType) then
            mode <- 1
            this.PassOn bytes
        else
            state.Encoding <- negotiation.Encoding
            let jitter = FrontCompression.jitterFor services.Padding bytes.Span
            let created = new FrontEncoder(state.Encoding, jitter)
            state.Encoder <- created
            mode <- 3
            if ended then
                let head = created.Write bytes.Span
                let tail = created.Finish()
                let whole = Array.append head tail
                state.Encoder <- null
                // Go's server gives a response a length when the handler returns with all of it still
                // in its 2 KB chunking buffer, and sends anything longer chunked.
                if whole.Length <= FrontCompression.GoChunkingBuffer then state.FinalLength <- int64 whole.Length
                this.PassOn(ReadOnlyMemory<byte> whole)
            else
                this.PassOn(ReadOnlyMemory<byte>(created.Write bytes.Span))

    /// Pushes out what a `PipeWriter` the app wrote to hasn't flushed.
    member private this.FlushScratch() : ValueTask =
        match scratch with
        | null -> ValueTask.CompletedTask
        | buffer when buffer.WrittenCount = 0 -> ValueTask.CompletedTask
        | buffer ->
            let pending = this.Write buffer.WrittenMemory
            if pending.IsCompletedSuccessfully then
                buffer.Clear()
                ValueTask.CompletedTask
            else
                ValueTask(
                    task {
                        do! pending
                        buffer.Clear()
                    }
                )

    /// The request is over: logs it (once), as Thruster's logging handler does when the response ends.
    member val LogEntry: (unit -> RequestLogEntry) | null = null with get, set

    /// The same request as `LogEntry`, for a host that writes the lines itself (`FrontServices.Lines`).
    member val LogCapture: RequestLogCapture | null = null with get, set

    member private this.Log() : unit =
        if not logged then
            logged <- true
            match this.LogCapture with
            | null ->
                match this.LogEntry with
                | null -> ()
                | entry -> services.Logger.Log(LogLevel.Information, EventId 0, entry (), null, (fun (state: RequestLogEntry) _ -> state.ToString()))
            | capture ->
                match services.Lines with
                | null -> ()
                | lines ->
                    let headers = ctx.Response.Headers
                    let writer = LineWriter.Current
                    RequestLog.writeLine
                        writer
                        capture
                        ctx.Response.StatusCode
                        (int64 (Stopwatch.GetElapsedTime(capture.Started).TotalMilliseconds))
                        bytesSent
                        (FrontHeaders.first headers "Content-Type")
                        (FrontHeaders.first headers "X-Cache")
                    lines.Append writer.Span

    /// Whether finishing needs to do more than log: nothing was held, compressed or recorded.
    member private this.Settled(completed: bool) : bool =
        mode = 1
        && isNull scratch
        && (isNull record || not completed)

    member private this.FinishSlowly(completed: bool) : Task =
        task {
            do! this.FlushScratch()
            if mode = 2 then
                let state = nonNull compress
                match state.Held with
                | null -> ()
                | buffer ->
                    if completed then
                        do! this.StartCompressing true
                    else
                        // The app failed with the start of the body in hand: it goes out as it is, and the
                        // response is cut short rather than completed.
                        state.Held <- null
                        mode <- 1
                        do! this.PassOn buffer.WrittenMemory
            match compress with
            | null -> ()
            | state ->
                match state.Encoder with
                | null -> ()
                | encoder ->
                    if completed then do! this.PassOn(ReadOnlyMemory<byte>(encoder.Finish()))
                    (encoder :> IDisposable).Dispose()
            match record with
            | null -> ()
            | state ->
                if completed && not state.Overflowed then
                    let head = String.Equals(ctx.Request.Method, "HEAD", StringComparison.Ordinal)
                    let body =
                        match state.Body with
                        | null -> ReadOnlyMemory<byte>.Empty
                        | buffer -> buffer.WrittenMemory
                    let headers = match state.Headers with null -> FrontHeaders.snapshot ctx.Response.Headers | h -> h
                    let variant = Variant.OfRequest(ctx.Request.Method, FrontRequest.rawTarget ctx, ctx.Request.Headers)
                    variant.SetResponseHeaders state.Vary
                    let response =
                        CachedResponse(ctx.Response.StatusCode, headers, (if head then ReadOnlyMemory<byte>.Empty else body), variant.VariantHeaders())
                    services.Cache.Set(nonNull cacheKey, response, now + int64 state.Lifetime.TotalMilliseconds, now)
            this.Log()
        }

    /// The app is done: what's held goes out, a compressed body ends, and a recorded one is stored
    /// (unless the app failed).
    member this.Finish(completed: bool) : Task =
        this.Decide()
        if this.Settled completed then
            this.Log()
            Task.CompletedTask
        else
            this.FinishSlowly completed

    /// Sends a stored response (or the 304 for it) without calling the app. The cache's headers go on
    /// as they were stored, then `Starting` decides on compression as for any other response.
    member this.Hit(cached: CachedResponse, ifNoneMatch: string | null) : Task =
        let notModified = FrontCache.wasNotModified cached ifNoneMatch
        ctx.Response.StatusCode <- (if notModified then 304 else cached.Status)
        let headers = ctx.Response.Headers
        for struct (name, value) in cached.Headers do
            headers.Append(name, value)
        this.XCache <- "hit"
        hit <- true
        let head = String.Equals(ctx.Request.Method, "HEAD", StringComparison.Ordinal)
        if not notModified && not head then
            headers.ContentLength <- int64 cached.Body.Length
            this.Write(cached.Body).AsTask()
        else
            Task.CompletedTask

    // --- the response the app writes to ---------------------------------------------------------

    member internal this.Stream: Stream =
        match stream with
        | null ->
            let created = new FrontStream(this)
            stream <- created
            created :> Stream
        | s -> s :> Stream

    member internal this.Writer: PipeWriter =
        match writer with
        | null ->
            let created = new FrontWriter(this)
            writer <- created
            created :> PipeWriter
        | w -> w :> PipeWriter

    member internal _.Inner = inner

    /// The `PipeWriter`'s memory: the connection's own while the body goes straight to it, a buffer of
    /// ours while something has to see it first.
    member internal this.GetMemory(sizeHint: int) : Memory<byte> =
        if mode = 0 then this.Decide()
        if this.Direct && isNull scratch then
            inner.Writer.GetMemory sizeHint
        else
            let buffer =
                match scratch with
                | null ->
                    let created = ArrayBufferWriter<byte>(4096)
                    scratch <- created
                    created
                | buffer -> buffer
            buffer.GetMemory sizeHint

    member internal this.Advance(count: int) : unit =
        if isNull scratch then
            bytesSent <- bytesSent + int64 count
            inner.Writer.Advance count
        else
            (nonNull scratch).Advance count

    member internal this.FlushWriter(ct: CancellationToken) : ValueTask<FlushResult> =
        if isNull scratch then
            inner.Writer.FlushAsync ct
        else
            let flushed = this.FlushScratch()
            if flushed.IsCompletedSuccessfully then
                ValueTask<FlushResult>(FlushResult(false, false))
            else
                ValueTask<FlushResult>(
                    task {
                        do! flushed
                        return FlushResult(false, false)
                    }
                )

    interface IHttpResponseBodyFeature with
        member this.Stream = this.Stream
        member this.Writer = this.Writer
        member _.DisableBuffering() = inner.DisableBuffering()

        member this.StartAsync(ct: CancellationToken) : Task =
            task {
                this.Decide()
                do! this.FlushScratch()
                // `gzhttp`'s `Flush` is the same: nothing starts until it's clear whether the body will be
                // compressed, which takes the start of the body.
                if mode <> 2 then do! inner.StartAsync ct
            }

        member this.SendFileAsync(path: string, offset: int64, count: Nullable<int64>, ct: CancellationToken) : Task =
            task {
                this.Decide()
                do! this.FlushScratch()
                if this.Direct then
                    bytesSent <- bytesSent + (if count.HasValue then count.Value else 0L)
                    do! inner.SendFileAsync(path, offset, count, ct)
                else
                    // Something has to see the bytes, so they're read here.
                    use file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous ||| FileOptions.SequentialScan)
                    file.Seek(offset, SeekOrigin.Begin) |> ignore
                    let buffer = ArrayPool<byte>.Shared.Rent(64 * 1024)
                    try
                        let mutable remaining = if count.HasValue then count.Value else file.Length - offset
                        while remaining > 0L do
                            let! n = file.ReadAsync(Memory<byte>(buffer, 0, int (min remaining (int64 buffer.Length))), ct)
                            if n = 0 then
                                remaining <- 0L
                            else
                                remaining <- remaining - int64 n
                                do! this.Write(ReadOnlyMemory<byte>(Array.sub buffer 0 n))
                    finally
                        ArrayPool<byte>.Shared.Return buffer
            }

        member this.CompleteAsync() : Task =
            task {
                do! this.Finish true
                do! inner.CompleteAsync()
            }

/// The `Stream` the app writes the body to.
and [<Sealed>] internal FrontStream(response: FrontResponse) =
    inherit Stream()
    override _.CanRead = false
    override _.CanSeek = false
    override _.CanWrite = true
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = raise (NotSupportedException())
        and set _ = raise (NotSupportedException())

    override _.Flush() = ()
    override _.FlushAsync(_: CancellationToken) : Task = Task.CompletedTask
    override _.Read(_: byte[], _: int, _: int) : int = raise (NotSupportedException())
    override _.Seek(_: int64, _: SeekOrigin) : int64 = raise (NotSupportedException())
    override _.SetLength(_: int64) = raise (NotSupportedException())
    // Synchronous writes are refused, as Kestrel refuses them by default.
    override _.Write(_: byte[], _: int, _: int) = raise (InvalidOperationException "Synchronous operations are disallowed. Call WriteAsync instead.")
    override _.Write(_: ReadOnlySpan<byte>) = raise (InvalidOperationException "Synchronous operations are disallowed. Call WriteAsync instead.")
    override _.WriteAsync(buffer: byte[], offset: int, count: int, _: CancellationToken) : Task =
        (response.Write(ReadOnlyMemory<byte>(buffer, offset, count))).AsTask()
    override _.WriteAsync(buffer: ReadOnlyMemory<byte>, _: CancellationToken) : ValueTask = response.Write buffer

/// The `PipeWriter` the app writes the body to.
and [<Sealed>] internal FrontWriter(response: FrontResponse) =
    inherit PipeWriter()
    override _.Advance(count: int) = response.Advance count
    override _.GetMemory(sizeHint: int) = response.GetMemory sizeHint
    override this.GetSpan(sizeHint: int) = (response.GetMemory sizeHint).Span
    override _.FlushAsync(ct: CancellationToken) = response.FlushWriter ct
    override _.Complete(_: exn | null) = ()
    override _.CancelPendingFlush() = response.Inner.Writer.CancelPendingFlush()

/// `FrontHandler`'s pieces that other front code (the upstream listener) uses too.
module internal FrontProxy =
    let private bodyDetection (ctx: HttpContext) : bool =
        match ctx.Features.Get<IHttpRequestBodyDetectionFeature>() with
        | null ->
            let announced = ctx.Request.ContentLength
            (announced.HasValue && announced.Value > 0L) || ctx.Request.Headers.ContainsKey "transfer-encoding"
        | feature -> feature.CanHaveBody

    /// The empty 413 for a body over MAX_REQUEST_BODY.
    let tooLarge (ctx: HttpContext) : unit = ctx.Response.StatusCode <- 413

    /// `http.MaxBytesHandler`: a body over the limit fails the proxied request with 413 before the app
    /// sees it (Puma reads the whole body before calling Rails). A body that announces its length is
    /// refused or let through by it; one that doesn't (chunked, HTTP/2) is read up to the limit first.
    /// `true` when the request may go on.
    let withinLimit (ctx: HttpContext) (limit: uint64) : Task<bool> =
        if limit = 0UL then
            Task.FromResult true
        else
            let declared = ctx.Request.ContentLength
            if declared.HasValue && uint64 declared.Value > limit then
                Task.FromResult false
            elif declared.HasValue || not (bodyDetection ctx) then
                Task.FromResult true
            else
                task {
                    let buffered = new MemoryStream()
                    let chunk = ArrayPool<byte>.Shared.Rent 16384
                    let mutable ok = true
                    let mutable reading = true
                    try
                        while reading do
                            let! n =
                                task {
                                    try
                                        let! n = ctx.Request.Body.ReadAsync(Memory<byte> chunk)
                                        return ValueSome n
                                    with
                                    | :? IOException
                                    | :? BadHttpRequestException -> return ValueNone
                                }
                            match n with
                            | ValueNone ->
                                ok <- false
                                reading <- false
                            | ValueSome 0 -> reading <- false
                            | ValueSome n ->
                                buffered.Write(chunk, 0, n)
                                if uint64 buffered.Length > limit then
                                    ok <- false
                                    reading <- false
                    finally
                        ArrayPool<byte>.Shared.Return chunk
                    if ok then
                        buffered.Position <- 0L
                        ctx.Request.Body <- buffered
                    return ok
                }

    /// The app saw every request as Thruster's HTTP/1.1 request to Puma: an HTTP/2 request's `:authority`
    /// became its `Host` (Kestrel does that itself), its path was in origin form and its cookie fields
    /// were one `Cookie` header (Go's HTTP/2 server joins them with "; ", as Kestrel does).
    let asProxiedHttp1 (ctx: HttpContext) : unit =
        if ctx.Request.Protocol = "HTTP/2" then ctx.Request.Protocol <- "HTTP/1.1"

    /// `setXForwarded` over `ProxyRequest.SetXForwarded`: the client's address is appended to
    /// `X-Forwarded-For`, and `X-Forwarded-Host`/`-Proto` name this request's host and scheme. With
    /// FORWARD_HEADERS the client's own `X-Forwarded-*` are kept (it's trusted to be a proxy); without
    /// it they're replaced. `Forwarded` never reaches the app (`Rewrite` drops it).
    let setForwardedHeaders (ctx: HttpContext) (forwardHeaders: bool) : unit =
        let headers = ctx.Request.Headers
        let priorFor =
            if forwardHeaders then
                let values = headers["X-Forwarded-For"]
                if values.Count = 0 then "" elif values.Count = 1 then (match values[0] with null -> "" | v -> v) else String.Join(", ", values.ToArray())
            else
                ""
        let incomingHost = FrontHeaders.first headers "X-Forwarded-Host"
        let incomingProto = FrontHeaders.first headers "X-Forwarded-Proto"
        let host =
            match headers.Host.ToString() with
            | "" ->
                let struct (_, _, authority) = FrontCache.splitTarget (match ctx.Features.Get<IHttpRequestFeature>() with null -> null | f -> f.RawTarget)
                (match authority with null -> "" | a -> a)
            | host -> host
        let client =
            match ctx.Connection.RemoteIpAddress with
            | null -> ""
            | ip -> (if ip.IsIPv4MappedToIPv6 then ip.MapToIPv4() else ip).ToString()
        headers.Remove "Forwarded" |> ignore
        headers["X-Forwarded-For"] <- StringValues(if priorFor = "" then client else priorFor + ", " + client)
        headers["X-Forwarded-Host"] <- StringValues(if forwardHeaders && incomingHost <> "" then incomingHost else host)
        headers["X-Forwarded-Proto"] <- StringValues(if forwardHeaders && incomingProto <> "" then incomingProto elif ctx.Request.IsHttps then "https" else "http")

    /// The `X-Request-Start` value for the last millisecond asked about: most requests at load share one.
    [<Sealed; AllowNullLiteral>]
    type private RequestStart(millis: int64) =
        member _.Millis = millis
        member val Value = "t=" + string millis

    let mutable private lastRequestStart: RequestStart = null

    /// `NewRequestStartHandler`
    let setRequestStart (headers: IHeaderDictionary) : unit =
        let existing = headers["X-Request-Start"]
        if existing.Count = 0 || existing.ToString() = "" then
            let millis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            let last = lastRequestStart
            let current =
                if not (isNull last) && last.Millis = millis then
                    last
                else
                    let fresh = RequestStart millis
                    lastRequestStart <- fresh
                    fresh
            headers["X-Request-Start"] <- StringValues current.Value

/// The handler chain of the front listeners: request logging, the request size limit, compression,
/// `X-Request-Start`, the response cache and the proxy headers, in front of the app.
[<Sealed>]
type FrontHandler(services: FrontServices, app: RequestDelegate) =
    static let starting =
        Func<obj, Task>(fun o ->
            match o with
            | :? FrontResponse as response -> response.Starting()
            | _ -> ()
            Task.CompletedTask)

    let config = services.Config

    /// What the log line needs of the request as it starts: the `X-Forwarded-*` headers the proxy sets next change what it can read.
    let captureRequest (ctx: HttpContext) (started: int64) (path: string) (query: string | null) : RequestLogCapture =
        let request = ctx.Request
        let headers = request.Headers
        let proto =
            match request.Protocol with
            | "HTTP/2" -> "HTTP/2.0"
            | "HTTP/1.0" -> "HTTP/1.0"
            | _ -> "HTTP/1.1"
        let contentLength =
            match Int64.TryParse(FrontHeaders.first headers "Content-Length") with
            | true, n -> n
            | _ -> if request.ContentLength.HasValue || not (headers.ContainsKey "Transfer-Encoding") && request.Protocol <> "HTTP/2" then 0L else -1L
        RequestLogCapture(
            started,
            path,
            query,
            request.Method,
            proto,
            contentLength,
            FrontHeaders.first headers "Content-Type",
            FrontHeaders.first headers "X-Forwarded-For",
            ctx.Connection.RemoteIpAddress,
            ctx.Connection.RemotePort,
            FrontHeaders.first headers "User-Agent"
        )

    /// The structured entry the `ILogger` is given (tests and tools; the server writes bytes, see `RequestLog`).
    let logEntry (ctx: HttpContext) (capture: RequestLogCapture) (cache: FrontResponse) : unit -> RequestLogEntry =
        let remote =
            if capture.ForwardedFor <> "" then capture.ForwardedFor
            else
                match capture.RemoteIp with
                | null -> ""
                | ip -> $"{(if ip.IsIPv4MappedToIPv6 then ip.MapToIPv4() else ip)}:{capture.RemotePort}"
        fun () ->
            let response = ctx.Response
            let status = response.StatusCode
            let field (name: string) (value: obj | null) = KeyValuePair<string, obj | null>(name, value)
            RequestLogEntry(
                [| field "path" capture.Path
                   field "status" (box status)
                   field "dur" (box (int64 (Stopwatch.GetElapsedTime(capture.Started).TotalMilliseconds)))
                   field "method" capture.Method
                   field "req_content_length" (box capture.ContentLength)
                   field "req_content_type" capture.ContentType
                   field "resp_content_length" (box cache.BytesSent)
                   field "resp_content_type" (FrontHeaders.first response.Headers "Content-Type")
                   field "remote_addr" remote
                   field "user_agent" capture.UserAgent
                   field "cache" (FrontHeaders.first response.Headers "X-Cache")
                   field "query" capture.Query
                   field "proto" capture.Proto |]
            )

    member _.Services = services

    /// The app, for a request that got past the size limit: the `X-Forwarded-*` headers
    /// `httputil.ReverseProxy` sets, then the app itself.
    member private _.CallApp(ctx: HttpContext) : Task =
        FrontProxy.asProxiedHttp1 ctx
        FrontProxy.setForwardedHeaders ctx config.ForwardHeaders
        try
            app.Invoke ctx
        with e ->
            Task.FromException e

    /// The proxy (`internal/proxy_handler.go`): the request size limit Thruster's `http.MaxBytesHandler`
    /// enforces (an oversized body never reaches the app and gets an empty 413), then the app.
    member private this.Proxy(ctx: HttpContext) : Task =
        if services.MaxRequestBody = 0UL then
            this.CallApp ctx
        else
            task {
                let! within = FrontProxy.withinLimit ctx services.MaxRequestBody
                if not within then FrontProxy.tooLarge ctx else do! this.CallApp ctx
            }

    /// Finishes the response once `work` (the app, or a stored response being sent) is done, whether
    /// it succeeded or not; a failure goes on to the host afterwards.
    member private _.Complete(response: FrontResponse, work: Task) : Task =
        if work.IsCompletedSuccessfully then
            response.Finish true
        else
            task {
                let mutable failure: exn | null = null
                try
                    do! work
                with e ->
                    failure <- e
                do! response.Finish(isNull failure)
                match failure with
                | null -> ()
                | e -> Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e).Throw()
            }

    member this.Invoke(ctx: HttpContext) : Task =
        let started = Stopwatch.GetTimestamp()
        let request = ctx.Request
        let headers = request.Headers
        let meth = request.Method
        let negotiation: Negotiation =
            if services.CompressionEnabled then
                { Encoding = services.SelectEncoding(meth, headers.AcceptEncoding.ToString())
                  UserSpecificRequest = config.GzipCompressionDisableOnAuth && FrontCompression.hasUserSpecificRequestHeaders headers }
            else
                { Encoding = Encoding.NoEncoding; UserSpecificRequest = false }
        let rawTarget = FrontRequest.rawTarget ctx
        // Once, for the cache key and the log line.
        let struct (path, query, authority) = FrontCache.splitTarget rawTarget
        FrontProxy.setRequestStart headers
        let eligible =
            FrontCache.shouldCacheRequest
                meth
                (FrontHeaders.first headers "Connection")
                (FrontHeaders.first headers "Upgrade")
                (FrontHeaders.first headers "Range")
                (match rawTarget with null -> 0 | t -> t.Length)
        let now = Environment.TickCount64
        let mutable key: string | null = null
        let mutable found: CachedResponse = null
        if eligible then
            let baseKey = Variant.BaseKeyOf(meth, path, query, authority, headers)
            key <- baseKey
            found <- services.Cache.Get(baseKey, now)
            match found with
            | null -> ()
            | cached ->
                let variant = Variant.OfRequest(meth, rawTarget, headers)
                variant.SetResponseHeaders cached.Vary
                if not (variant.Matches cached.Variant) then
                    key <- variant.CacheKey()
                    found <- services.Cache.Get(nonNull key, now)
        let response =
            FrontResponse(
                services,
                ctx,
                ctx.Features.GetRequiredFeature<IHttpResponseBodyFeature>(),
                (if eligible then HeaderMerge.Replace else HeaderMerge.Append),
                negotiation,
                key,
                now
            )
        if services.LogRequests then
            let capture = captureRequest ctx started path query
            match services.Lines with
            | null -> response.LogEntry <- logEntry ctx capture response
            | _ -> response.LogCapture <- capture
        ctx.Features.Set<IHttpResponseBodyFeature>(response)
        ctx.Response.OnStarting(starting, (response :> obj))
        match found with
        | null -> this.Complete(response, this.Proxy ctx)
        | cached ->
            let ifNoneMatch = FrontHeaders.first headers "If-None-Match"
            this.Complete(response, response.Hit(cached, (if ifNoneMatch = "" then null else ifNoneMatch)))
