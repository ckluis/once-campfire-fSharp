// Port of rust/crates/kit/src/adapter.rs
//
// The only place ASP.NET Core shows through: route handlers that build a `Ctx`, run a plain
// `Ctx -> Task<Result<Response, Error>>` action, and turn the result into an HTTP response; plus
// the outer middleware that must run before routing (`Rack::MethodOverride`, `ActionDispatch::SSL`,
// `ActionDispatch::RequestId`). Controllers never touch `HttpContext`; routing is Falco's.
//
// Where Axum has response extensions (`StaticFile`, `AppContentLength`, the body digest), a
// `Response` carries the same facts as fields, and where it has layers over responses
// (`Rack::Deflater`, the request id and `X-Runtime` headers), `write` and an `OnStarting` hook
// apply them to whatever the host is about to send.
namespace Campfire.Kit

open System
open System.Buffers
open System.Diagnostics
open System.IO
open System.Net
open System.Text
open System.Threading.Tasks
open Falco
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Primitives
open Campfire.Kit

/// What the pre-routing middleware learned about a request, handed on to the action's adapter and
/// the response writer.
[<Sealed>]
type RequestState(kit: Kit, http: HttpContext) =
    member _.Kit = kit
    member _.Http = http

    /// `ActionDispatch::RequestId`, for logging and the `X-Request-Id` header.
    member val RequestId = "" with get, set

    member val Started = Stopwatch.GetTimestamp() with get, set

    /// Whether the request is HTTPS, as the proxy headers and the connection say.
    member val Ssl = false with get, set

    /// The method on the wire when `_method` overrode it (`rack.methodoverride.original_method`).
    member val OriginalMethod: string | null = null with get, set

    /// The body, when the pre-routing middleware had to read it to find `_method`.
    member val Parsed: ParsedBody voption = ValueNone with get, set

    /// Whether the app wraps its responses in `Rack::Deflater` (see `Adapter.useDeflater`).
    member val Deflate = false with get, set

    /// The response comes from outside the middleware the kit installs below `ActionDispatch::Static`
    /// (`Rack::Runtime`, `ActionDispatch::RequestId`): public files, and the deflater's own 406.
    member val SkipRequestHeaders = false with get, set

    /// The response is the deflater's 406, which replaces everything below it, `ActionDispatch::SSL` too:
    /// no HSTS, no `secure` on cookies.
    member val Replaced = false with get, set

module Adapter =
    let private stateKey = "campfire.kit.state"

    /// `Rack::MethodOverride::HTTP_METHODS`
    let private overridableMethods = [| "GET"; "HEAD"; "PUT"; "POST"; "DELETE"; "OPTIONS"; "PATCH"; "LINK"; "UNLINK" |]

    /// hyper's default `max_buf_size` (8192 + 4096 * 100): the most bytes of request line and headers the
    /// Rust port reads before it answers 431.
    let private maxRequestHead = 417_792
    /// hyper's `MAX_URI_LEN` (414 past it), plus room for the method and ` HTTP/1.1`, since Kestrel limits
    /// the whole request line where hyper limits the target alone.
    let private maxRequestLine = 65_534 + "OPTIONS ".Length + " HTTP/1.1".Length

    /// Settings Campfire's Kestrel needs, whatever else the host configures: no `Server` header,
    /// the kit's own body limits (413) instead of Kestrel's 30 MB, and header values written as UTF-8
    /// the way Puma wrote them.
    let configureKestrel (options: KestrelServerOptions) : unit =
        options.AddServerHeader <- false
        options.Limits.MaxRequestBodySize <- Nullable()
        // Kestrel's 32 KB of headers and 8 KB request line refuse requests Puma (112 KB of headers, 12 KB
        // of URI) and hyper (a 417,792-byte read buffer) serve: a browser with many cookies on a shared
        // domain would be locked out of every page. The ceiling is hyper's, the larger of the two
        // (`MaxRequestBufferSize` stays at its 1 MB, above both).
        options.Limits.MaxRequestHeadersTotalSize <- maxRequestHead
        options.Limits.MaxRequestLineSize <- maxRequestLine
        options.ResponseHeaderEncodingSelector <- (fun _ -> Encoding.UTF8)

    /// The state for this request, made on first use.
    let private stateOf (kit: Kit) (http: HttpContext) : RequestState =
        match http.Items.TryGetValue stateKey with
        | true, (:? RequestState as state) -> state
        | _ ->
            let state = RequestState(kit, http)
            state.Ssl <- kit.Config.Proxy.AssumeSsl || RequestHeaders.schemeIsHttps http.Request.Headers http.Request.Scheme
            http.Items[stateKey] <- state
            state

    /// The peer, with an IPv4 address a dual-stack socket reports in its IPv6 form mapped back.
    let private peerOf (http: HttpContext) : IPAddress | null =
        match http.Connection.RemoteIpAddress with
        | null -> null
        | ip -> if ip.IsIPv4MappedToIPv6 then ip.MapToIPv4() else ip

    /// The target as the client sent it, split at `?`: `(path, query, authority)`. A path is as it came
    /// on the wire, still %-encoded (`PATH_INFO`).
    let private splitTarget (http: HttpContext) : struct (string * string | null * string | null) =
        let target =
            match http.Features.Get<IHttpRequestFeature>() with
            | null -> http.Request.Path.Value + http.Request.QueryString.Value
            | feature -> feature.RawTarget
        let target =
            match target with
            | null
            | "" -> "/"
            | target -> target
        let mutable authority: string | null = null
        let mutable rest = target
        if rest[0] <> '/' && rest <> "*" then
            // Absolute form: `http://host/path?query`.
            match rest.IndexOf("://", StringComparison.Ordinal) with
            | -1 -> ()
            | at ->
                let afterScheme = rest.Substring(at + 3)
                match afterScheme.IndexOf '/' with
                | -1 ->
                    authority <- afterScheme
                    rest <- "/"
                | slash ->
                    authority <- afterScheme.Substring(0, slash)
                    rest <- afterScheme.Substring slash
        match rest.IndexOf '?' with
        | -1 -> struct (rest, null, authority)
        | q -> struct (rest.Substring(0, q), rest.Substring(q + 1), authority)

    let private contentLength (http: HttpContext) : int64 voption =
        let length = http.Request.ContentLength
        if length.HasValue then ValueSome length.Value else ValueNone

    /// Whether bytes may follow the headers: HTTP/1 announces a body with `Content-Length` or
    /// `Transfer-Encoding`, but HTTP/2 can send one with neither, so the host says.
    let private canHaveBody (http: HttpContext) : bool =
        match http.Features.Get<IHttpRequestBodyDetectionFeature>() with
        | null ->
            let announced = http.Request.ContentLength
            (announced.HasValue && announced.Value > 0L) || http.Request.Headers.ContainsKey "transfer-encoding"
        | feature -> feature.CanHaveBody

    /// Read and parse the request's body. One with nothing to read is not read (an empty multipart
    /// body is still a malformed one).
    let private parseBody (kit: Kit) (http: HttpContext) (originalMethod: string) : Task<Result<ParsedBody, BodyError>> =
        let multipart =
            match RequestHeaders.get http.Request.Headers Hdr.ContentType with
            | null -> false
            | ct -> ct.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase)
        if canHaveBody http || multipart then
            RequestBody.parse originalMethod http.Request.Headers (contentLength http) http.Request.Body kit.Config.MaxBodyBytes
        else
            Task.FromResult(
                Ok
                    { Raw = ReadOnlyMemory.Empty
                      Params = Ok(ParamMap())
                      Files = Array.empty }
            )

    // --- writing responses ------------------------------------------------------------------------

    /// Delete the files a request's multipart body spooled.
    let private cleanUp (parsed: ParsedBody voption) : unit =
        match parsed with
        | ValueSome parsed ->
            for file in parsed.Files do
                file.Delete()
        | ValueNone -> ()

    let private copyHeaders (http: HttpContext) (response: Response) : unit =
        http.Response.StatusCode <- response.Status
        let headers = http.Response.Headers
        let map = response.Headers
        let mutable empty = false
        for i in 0 .. map.Count - 1 do
            if map.ValueAt i = "" then empty <- true
        if not empty then
            for i in 0 .. map.Count - 1 do
                headers.Append(map.NameAt i, StringValues(map.ValueAt i))
        else
            // `Append` drops a lone empty value, but a header line with nothing after its colon is one Puma
            // writes (the first line of a `Content-Disposition` that begins with a line break), so the
            // values of a name that has one go out together, as a list.
            let written = Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            for i in 0 .. map.Count - 1 do
                let name = map.NameAt i
                if written.Add name then headers.Append(name, StringValues(Array.ofList (map.GetAll name)))

    /// Send `file` (or the range of it), through `gzip` when there is one.
    let private writeFile (http: HttpContext) (file: FileBody) (gzip: Deflater.GzipWriter | null) : Task =
        task {
            match gzip with
            | null -> do! http.Response.SendFileAsync(file.Path, file.Offset, Nullable file.Len)
            | gzip ->
                use stream =
                    new FileStream(
                        file.Path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        64 * 1024,
                        FileOptions.Asynchronous ||| FileOptions.SequentialScan
                    )
                stream.Seek(file.Offset, SeekOrigin.Begin) |> ignore
                let buffer = ArrayPool<byte>.Shared.Rent(64 * 1024)
                try
                    let mutable remaining = file.Len
                    while remaining > 0L do
                        let! n = stream.ReadAsync(Memory<byte>(buffer, 0, int (min remaining (int64 buffer.Length))))
                        if n = 0 then
                            remaining <- 0L
                        else
                            remaining <- remaining - int64 n
                            do! gzip.Write(ReadOnlyMemory<byte>(buffer, 0, n))
                finally
                    ArrayPool<byte>.Shared.Return buffer
        }

    /// A single-buffer body as one gzip member: from the cache when it repeats (it was digested for
    /// its ETag), compressed afresh when it has no digest.
    let private gzipSingle (body: ReadOnlyMemory<byte>) (digest: BodyDigest voption) (mtime: uint32) : ReadOnlyMemory<byte> =
        match digest with
        | ValueSome digest -> Deflater.gzipDigested body digest mtime
        | ValueNone -> ReadOnlyMemory<byte>(Deflater.gzipMember body.Span mtime)

    let private writeGzipped (http: HttpContext) (response: Response) (mtime: uint32) : Task =
        task {
            let output = http.Response.Body
            match response.Body with
            | Body.Empty ->
                let writer = new Deflater.GzipWriter(output, mtime)
                do! writer.Finish()
            | Body.Parts parts ->
                parts.WriteGzip(http.Response.BodyWriter, mtime)
                let! _ = http.Response.BodyWriter.FlushAsync()
                ()
            | Body.Bytes bytes -> do! output.WriteAsync(gzipSingle bytes response.BodyDigest mtime)
            | Body.Pooled pooled -> do! output.WriteAsync(gzipSingle pooled.Memory response.BodyDigest mtime)
            | Body.File file ->
                let writer = new Deflater.GzipWriter(output, mtime)
                do! writeFile http file writer
                do! writer.Finish()
            | Body.Stream stream ->
                use stream = stream
                let writer = new Deflater.GzipWriter(output, mtime)
                let buffer = ArrayPool<byte>.Shared.Rent(64 * 1024)
                try
                    let mutable reading = true
                    while reading do
                        let! n = stream.ReadAsync(Memory<byte>(buffer))
                        if n = 0 then reading <- false else do! writer.Write(ReadOnlyMemory<byte>(buffer, 0, n))
                finally
                    ArrayPool<byte>.Shared.Return buffer
                do! writer.Finish()
        }

    /// A buffer as the body: written through the stream when it is small, and through the pipe with a size hint (`BufferWrites`)
    /// when it is large enough for the pipe's block size to matter.
    let private writeBuffer (http: HttpContext) (bytes: ReadOnlyMemory<byte>) : Task =
        if bytes.Length <= BufferWrites.Segment then
            http.Response.Body.WriteAsync(bytes).AsTask()
        else
            task {
                BufferWrites.write http.Response.BodyWriter bytes.Span
                let! _ = http.Response.BodyWriter.FlushAsync().AsTask()
                ()
            }

    let private writePlain (http: HttpContext) (response: Response) : Task =
        task {
            match response.Body with
            | Body.Empty -> ()
            | Body.Bytes bytes -> do! writeBuffer http bytes
            | Body.Pooled pooled -> do! writeBuffer http pooled.Memory
            | Body.Parts parts ->
                parts.WritePlain http.Response.BodyWriter
                let! _ = http.Response.BodyWriter.FlushAsync().AsTask()
                ()
            | Body.File file -> do! writeFile http file null
            | Body.Stream stream ->
                use stream = stream
                do! stream.CopyToAsync http.Response.Body
        }

    /// Release what a response holds once it has been written (or dropped).
    let private releaseBody (body: Body) : unit =
        match body with
        | Body.Pooled pooled -> pooled.Release()
        | Body.Stream stream -> stream.Dispose()
        | _ -> ()

    /// Hand a finished response to the host, streaming files and dropping HEAD bodies (`Rack::Head`)
    /// while keeping their `Content-Length`; with `Rack::Deflater` around the app (see `useDeflater`),
    /// gzip it for a client that accepts that.
    let write (kit: Kit) (http: HttpContext) (response: Response) (head: bool) : Task =
        task {
            let state = stateOf kit http
            if response.StaticFile then state.SkipRequestHeaders <- true
            let appSetLength = response.Headers.Contains Hdr.ContentLength
            let mutable response = response
            // A file that can't be opened is a bare 500, before any header goes out.
            let unreadable =
                match response.Body with
                | Body.File file -> not head && not (File.Exists file.Path)
                | _ -> false
            if unreadable then
                match response.Body with
                | Body.File file -> kit.Logger.LogError("send_file failed path={Path}", file.Path)
                | _ -> ()
                http.Response.StatusCode <- Status.InternalServerError
            else
                // The length the host will send: known for a buffer, parts and a file.
                match response.Body with
                | Body.Bytes bytes -> response.Headers.Insert(Hdr.ContentLength, string bytes.Length)
                | Body.Pooled pooled -> response.Headers.Insert(Hdr.ContentLength, string pooled.Length)
                | Body.Parts parts -> response.Headers.Insert(Hdr.ContentLength, string parts.BodyLength)
                | Body.File file -> response.Headers.Insert(Hdr.ContentLength, string file.Len)
                | Body.Empty
                | Body.Stream _ -> ()
                let mutable gzipMtime: uint32 voption = ValueNone
                if state.Deflate then
                    let accept = RequestHeaders.get http.Request.Headers Hdr.AcceptEncoding
                    match Deflater.apply accept response appSetLength with
                    | Unchanged -> ()
                    | Gzip mtime -> gzipMtime <- ValueSome mtime
                    | NotAcceptable ->
                        // The deflater answers outside the layers below it.
                        state.SkipRequestHeaders <- true
                        state.Replaced <- true
                        releaseBody response.Body
                        let struct (path, query, _) = splitTarget http
                        response <- Deflater.notAcceptable (match query with null -> path | q -> path + "?" + q)
                // A POST that `_method` turned into a HEAD is still a POST on the wire, whose body the host
                // expects to be as long as the response says: with no body written, it says nothing.
                if head && not (isNull state.OriginalMethod) then response.Headers.Remove Hdr.ContentLength
                copyHeaders http response
                try
                    try
                        if head then
                            ()
                        else
                            match gzipMtime with
                            | ValueNone -> do! writePlain http response
                            | ValueSome mtime -> do! writeGzipped http response mtime
                    with
                    // The client went away; the host has nothing more to say about it.
                    | :? OperationCanceledException
                    | :? IOException -> ()
                finally
                    releaseBody response.Body
        }

    // --- the middleware that runs before routing ---------------------------------------------------

    /// `ActionDispatch::SSL#flag_cookies_as_secure!`
    let private flagCookiesAsSecure (headers: IHeaderDictionary) : unit =
        let cookies = headers.SetCookie
        if cookies.Count > 0 then
            let flagged =
                [| for cookie in cookies do
                       match cookie with
                       | null -> ()
                       | cookie ->
                           let attributes = cookie.Split ';'
                           let secure =
                               attributes |> Array.skip 1 |> Array.exists (fun a -> a.Trim().Equals("secure", StringComparison.OrdinalIgnoreCase))
                           if secure then cookie else cookie + "; secure" |]
            headers.SetCookie <- StringValues flagged

    /// Headers that go on every response, as the host starts sending it: `X-Request-Id`, `X-Runtime`,
    /// HSTS and secure cookies.
    let private onStarting: Func<obj, Task> =
        Func<obj, Task>(fun o ->
            match o with
            | :? RequestState as state ->
                let headers = state.Http.Response.Headers
                if not state.SkipRequestHeaders then
                    if state.RequestId <> "" then headers[Hdr.XRequestId] <- state.RequestId
                    if not (headers.ContainsKey Hdr.XRuntime) then
                        let seconds = Stopwatch.GetElapsedTime(state.Started).TotalSeconds
                        headers[Hdr.XRuntime] <- seconds.ToString("F6", Globalization.CultureInfo.InvariantCulture)
                let config = state.Kit.Config
                if config.ForceSsl && state.Ssl && not state.Replaced then
                    headers[Hdr.StrictTransportSecurity] <- config.Hsts
                    flagCookiesAsSecure headers
            | _ -> ()
            Task.CompletedTask)

    /// `ActionDispatch::RequestId#make_request_id`
    let private makeRequestId (incoming: string | null) : string =
        if String.IsNullOrWhiteSpace incoming then
            Guid.NewGuid().ToString()
        else
            let id = StringBuilder()
            let incoming = nonNull incoming
            let mutable i = 0
            while i < incoming.Length && id.Length < 255 do
                let c = incoming[i]
                if Char.IsLetter c || Char.IsNumber c || c = '_' || c = '-' || c = '@' then id.Append c |> ignore
                i <- i + 1
            id.ToString()

    /// `ActionDispatch::SSL#redirect_to_https`: 301 for GET/HEAD, 308 otherwise.
    let private redirectToHttps (http: HttpContext) : Response =
        let headers = http.Request.Headers
        let host =
            match RequestHeaders.get headers Hdr.XForwardedHost with
            | null ->
                match RequestHeaders.get headers Hdr.Host with
                | null -> "localhost"
                | h -> h.Substring(h.LastIndexOf ',' + 1).Trim()
            | h -> h.Substring(h.LastIndexOf ',' + 1).Trim()
        let host =
            match host.LastIndexOf ':' with
            | -1 -> host
            | colon -> if Seq.forall Char.IsAsciiDigit (host.Substring(colon + 1)) then host.Substring(0, colon) else host
        let struct (path, query, _) = splitTarget http
        let target = match query with null -> path | q -> path + "?" + q
        let status =
            if http.Request.Method = "GET" || http.Request.Method = "HEAD" then Status.MovedPermanently else Status.PermanentRedirect
        Response(status).ContentType("text/html").Header(Hdr.Location, $"https://{host}{target}")

    /// `Rack::MethodOverride`, which needs the parsed form body, so POST bodies that can carry a
    /// form are parsed here and handed on. Answers the status itself when the body is refused.
    let private methodOverride (kit: Kit) (state: RequestState) (http: HttpContext) : Task<Response voption> =
        task {
            if http.Request.Method <> "POST" then
                return ValueNone
            else
                let media = RequestHeaders.mediaType (RequestHeaders.get http.Request.Headers Hdr.ContentType)
                let formData =
                    match media with
                    | null -> true
                    | media ->
                        media = "application/x-www-form-urlencoded"
                        || media = "multipart/form-data"
                        || media = "multipart/related"
                        || media = "multipart/mixed"
                // Only form data can carry `_method`, so other bodies (JSON, a raw upload) are left for the
                // action to read, rather than parsed here for every POST, before routing.
                let mutable refused = ValueNone
                let mutable fromParam: string | null = null
                if formData then
                    match! parseBody kit http "POST" with
                    | Error error -> refused <- ValueSome(Response(BodyError.status error))
                    | Ok parsed ->
                        state.Parsed <- ValueSome parsed
                        match parsed.Params with
                        | Ok pars -> fromParam <- pars.Str "_method"
                        | Error _ -> ()
                match refused with
                | ValueSome response -> return ValueSome response
                | ValueNone ->
                    let candidate =
                        match fromParam with
                        | null -> RequestHeaders.get http.Request.Headers Hdr.XHttpMethodOverride
                        | m -> m
                    match candidate with
                    | null -> ()
                    | candidate ->
                        let overridden = candidate.ToUpperInvariant()
                        if Array.contains overridden overridableMethods then
                            state.OriginalMethod <- "POST"
                            http.Request.Method <- overridden
                    return ValueNone
        }

    /// Middleware that runs before routing: request id, forced SSL, and `_method` override (which
    /// needs the parsed form body, so POST bodies are parsed here and handed on).
    let railsMiddleware (kit: Kit) : Func<HttpContext, RequestDelegate, Task> =
        Func<HttpContext, RequestDelegate, Task>(fun http next ->
            task {
                let state = stateOf kit http
                state.Started <- Stopwatch.GetTimestamp()
                state.RequestId <- makeRequestId (RequestHeaders.get http.Request.Headers Hdr.XRequestId)
                let config = kit.Config
                // The kit has its own limits, answered with a 413 of its own.
                match http.Features.Get<IHttpMaxRequestBodySizeFeature>() with
                | null -> ()
                | feature -> if not feature.IsReadOnly then feature.MaxRequestBodySize <- Nullable()
                http.Response.OnStarting(onStarting, (state :> obj))
                if config.ForceSsl && not state.Ssl then
                    let response = redirectToHttps http
                    do! write kit http response (http.Request.Method = "HEAD")
                else
                    // Only a POST can carry `_method`, and most requests are not.
                    let! refused =
                        if http.Request.Method = "POST" then methodOverride kit state http else Task.FromResult ValueNone
                    match refused with
                    | ValueSome response -> do! write kit http response (http.Request.Method = "HEAD")
                    | ValueNone -> do! next.Invoke http
            }
            :> Task)

    /// Wrap every response the kit writes in `Rack::Deflater` (`config.ru`'s `use Rack::Deflater`).
    let useDeflater (kit: Kit) (app: IApplicationBuilder) : IApplicationBuilder =
        app.Use(
            Func<HttpContext, RequestDelegate, Task>(fun http next ->
                (stateOf kit http).Deflate <- true
                next.Invoke http)
        )

    // --- running actions -----------------------------------------------------------------------------

    let private actionPanicked (e: exn) : Error =
        Internal(Exception($"action panicked: {e.Message}", e))

    /// Run one action for one request, as an `HttpContext -> Task` Falco and ASP.NET Core can route to.
    let dispatch (kit: Kit) (action: ActionFn) (http: HttpContext) : Task =
        task {
            let state = stateOf kit http
            let meth = http.Request.Method
            let originalMethod = match state.OriginalMethod with null -> meth | m -> m
            let head = String.Equals(meth, "HEAD", StringComparison.Ordinal)
            let pathParams = ParamMap()
            let routeValues = http.Request.RouteValues
            if routeValues.Count > 0 then
                for kv in routeValues do
                    match kv.Value with
                    | null -> ()
                    | value ->
                        match Convert.ToString(value, Globalization.CultureInfo.InvariantCulture) with
                        | null -> ()
                        | text -> pathParams.Insert(kv.Key, Param.Str text)
            let mutable parsed: Result<ParsedBody, BodyError> = Unchecked.defaultof<_>
            match state.Parsed with
            | ValueSome already -> parsed <- Ok already
            | ValueNone ->
                let! read = parseBody kit http originalMethod
                parsed <- read
            let struct (raw, bodyParams, bodyError) =
                match parsed with
                | Ok body ->
                    match body.Params with
                    | Ok pars -> struct (body.Raw, pars, ValueNone)
                    | Error e -> struct (body.Raw, ParamMap(), ValueSome(Error.ofParamError e))
                | Error error -> struct (ReadOnlyMemory<byte>.Empty, ParamMap(), ValueSome(Status(BodyError.status error)))
            let struct (path, query, authority) = splitTarget http
            let request =
                Request(meth, originalMethod, path, query, authority, state.Ssl, http.Request.Headers, peerOf http, raw, kit.Config.Proxy)
            request.RequestId <- state.RequestId
            let struct (queryParams, queryError) =
                if request.QueryString.Length = 0 then
                    struct (ParamMap(), ValueNone)
                else
                    match Params.fromQueryString request.QueryString with
                    | Ok pars -> struct (pars, ValueNone)
                    | Error e -> struct (ParamMap(), ValueSome(Error.ofParamError e))
            let cookies = CookieJar(kit.Secrets, kit.Clock)
            for header in http.Request.Headers.Cookie do
                match header with
                | null -> ()
                | header -> cookies.AddHeader header
            let ctx = Ctx(kit, request, pathParams, queryParams, bodyParams, cookies)
            let failure = if queryError.IsSome then queryError else bodyError
            let mutable result: Result<Response, Error> = Unchecked.defaultof<_>
            match failure with
            | ValueSome error -> result <- Error error
            | ValueNone ->
                // A throwing action is an exception like any other: Rails' `ShowExceptions` answers
                // 500 with `public/500.html`, where an unwinding handler would drop the connection.
                try
                    let! produced = action ctx
                    result <- produced
                with e ->
                    result <- Error(actionPanicked e)
            let response =
                try
                    ctx.Finish result
                with e ->
                    ctx.Finish(Error(actionPanicked e))
            try
                do! write kit http response head
            finally
                match parsed with
                | Ok body when body.Files.Length > 0 -> cleanUp (ValueSome body)
                | _ -> ()
        }
        :> Task

    /// A Falco handler running one action.
    let action (kit: Kit) (action: ActionFn) : HttpHandler = dispatch kit action

    // --- routing ---------------------------------------------------------------------------------

    /// Rails' answer for an unmatched route (`ActionController::RoutingError`): the public 404 page.
    let notFound (kit: Kit) : HttpHandler =
        fun http ->
            task {
                let headers = http.Request.Headers
                let input: Format.NegotiationInput =
                    { FormatParam = null
                      Accept = RequestHeaders.get headers Hdr.Accept
                      ContentType = RequestHeaders.get headers Hdr.ContentType
                      Path = (let struct (path, _, _) = splitTarget http in path)
                      Xhr =
                        (match RequestHeaders.get headers Hdr.XRequestedWith with
                         | null -> false
                         | v -> v.Equals("xmlhttprequest", StringComparison.OrdinalIgnoreCase)) }
                let format =
                    match Format.formats input with
                    | Ok(first :: _) -> ValueSome first
                    | _ -> ValueNone
                let head = http.Request.Method = "HEAD"
                do! write kit http (Exceptions.render kit.ErrorPages Status.NotFound format head) head
            }
            :> Task

    /// A route for `pattern` (`/rooms/{id}`) running a different action per method, as
    /// `kit::get(show).patch(action(update))` does. Any other method is a Rails 404 (axum would say
    /// 405), and HEAD runs the GET action, whose body the writer drops.
    let route (kit: Kit) (pattern: string) (actions: (string * ActionFn) list) : HttpEndpoint =
        let table = actions |> List.map (fun (m, a) -> m.ToUpperInvariant(), dispatch kit a) |> dict
        let handler: HttpHandler =
            fun http ->
                match table.TryGetValue http.Request.Method with
                | true, handler -> handler http
                | _ ->
                    match (if http.Request.Method = "HEAD" then table.TryGetValue "GET" else (false, Unchecked.defaultof<_>)) with
                    | true, handler -> handler http
                    | _ -> notFound kit http
        Routing.any pattern handler

    /// A route that runs `action` for every method.
    let routeAny (kit: Kit) (pattern: string) (action: ActionFn) : HttpEndpoint = Routing.any pattern (dispatch kit action)

    /// Finish an app: Rails-style 404s for unknown paths *and* unknown methods (`route` sends those
    /// to `notFound`), the pre-routing middleware, then the routes.
    let app (kit: Kit) (endpoints: HttpEndpoint list) (builder: IApplicationBuilder) : unit =
        builder.Use(railsMiddleware kit) |> ignore
        builder.UseRouting() |> ignore
        builder.UseFalco endpoints |> ignore
        builder.UseFalcoNotFound(notFound kit) |> ignore
