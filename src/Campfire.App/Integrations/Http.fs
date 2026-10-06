// Port of rust/crates/campfire/src/integrations/net/http.rs
//
// One HTTP/1.1 request over a fresh connection, the way `Net::HTTP` makes it: the connection goes to a pinned
// address when the caller has one (`http.ipaddr = ip`), TLS verifies the peer against the host name, `open_timeout`
// covers the connect and TLS handshake, `read_timeout` covers the response head as a whole and then each read of
// the body, and a body the client asked to be compressed
// (`Accept-Encoding: gzip;q=1.0,deflate;q=0.6,identity;q=0.3`) is inflated as it's read.
//
// Rust writes this on hyper's client connection; .NET's HttpClient can't be told the order or spelling of the
// headers it writes, which the tests check against what `Net::HTTP` sent, so this is the exchange itself over a
// stream: the request line and headers as given, the head and body framing (length, chunked, until close) read back.
namespace Campfire.App.Integrations

open System
open System.Collections.Generic
open System.IO
open System.IO.Compression
open System.Net
open System.Net.Security
open System.Net.Sockets
open System.Security.Authentication
open System.Security.Cryptography.X509Certificates
open System.Text
open System.Threading
open System.Threading.Tasks
open Campfire.RichText
open Campfire.Ruby

type HttpError =
    /// `Net::OpenTimeout`
    | OpenTimeout
    /// `Net::ReadTimeout`
    | ReadTimeout
    /// The host has no addresses (`SocketError`).
    | Unresolvable of string
    /// `OpenSSL::SSL::SSLError`: the TLS handshake or a TLS read failed.
    | Tls of string
    | Io of exn
    | Http of string
    /// `Zlib::Error` while inflating a compressed body.
    | Inflate of string

module HttpError =
    let message (error: HttpError) : string =
        match error with
        | OpenTimeout -> "execution expired"
        | ReadTimeout -> "Net::ReadTimeout"
        | Unresolvable message -> $"getaddrinfo: {message}"
        | Tls message -> $"SSL error: {message}"
        | Io e -> e.Message
        | Http message -> message
        | Inflate message -> message

    /// `Errno::ECONNREFUSED`
    let isConnectionRefused (error: HttpError) : bool =
        match error with
        | Io(:? SocketException as e) -> e.SocketErrorCode = SocketError.ConnectionRefused
        | _ -> false

/// How a failure leaves the middle of an exchange; `exchange` and `ReadBody` turn it back into a `Result`.
exception internal HttpFailure of HttpError

/// Where the request goes: `Net::HTTP.new(host, port)` plus `use_ssl`.
type Endpoint =
    { Https: bool
      /// The host as `URI#host` gives it (IPv6 literals in brackets): the TLS server name.
      Host: string
      Port: int
      /// `http.ipaddr`: connect here instead of resolving `host`.
      PinnedIp: IPAddress option }

module Endpoint =
    let bareHost (endpoint: Endpoint) : string =
        if endpoint.Host.StartsWith '[' && endpoint.Host.EndsWith ']' then endpoint.Host.Substring(1, endpoint.Host.Length - 2) else endpoint.Host

    /// `Net::HTTP#addr_port`: the `Host` header when the request doesn't carry one.
    let hostHeader (endpoint: Endpoint) : string =
        let host =
            if endpoint.Host.Contains ':' && not (endpoint.Host.StartsWith '[') then $"[{endpoint.Host}]" else endpoint.Host
        let defaultPort = if endpoint.Https then 443 else 80
        if endpoint.Port = defaultPort then host else $"{host}:{endpoint.Port}"

type Timeouts = { Open: TimeSpan; Read: TimeSpan }

module Timeouts =
    /// `Net::HTTP`'s default open and read timeouts.
    let NetHttpDefault: TimeSpan = TimeSpan.FromSeconds 60.0

    let defaults: Timeouts = { Open = NetHttpDefault; Read = NetHttpDefault }

type Request =
    { Method: string
      /// `request_uri`: path and query.
      Target: string
      /// In the order `Net::HTTP` writes them, `Host` included.
      Headers: (string * string) list
      Body: byte[]
      /// We sent `NetHttpAcceptEncoding`, so a gzip or deflate body is inflated on read.
      DecodeContent: bool }

module Request =
    /// The `Accept-Encoding` `Net::HTTP` adds to requests whose responses have bodies, and then decodes itself.
    [<Literal>]
    let NetHttpAcceptEncoding = "gzip;q=1.0,deflate;q=0.6,identity;q=0.3"

    let private has (name: string) (request: Request) : bool =
        request.Headers |> List.exists (fun (n, _) -> n.Equals(name, StringComparison.OrdinalIgnoreCase))

    let private withDefault (name: string) (value: string) (request: Request) : Request =
        if has name request then request else { request with Headers = request.Headers @ [ name, value ] }

    /// `Net::HTTP::Get.new(uri)` / `Head.new(uri)` / `Post.new(uri_or_path, headers)`: the caller's headers, then
    /// `Accept-Encoding` (decoded when the response has a body), `Accept`, `User-Agent`, and `Host` when built from
    /// a URI (`uri_host`).
    let netHttp (meth: string) (target: string) (uriHost: string option) (headers: (string * string) list) : Request =
        let responseHasBody = meth <> "HEAD"
        let request =
            { Method = meth
              Target = target
              Headers = headers
              Body = [||]
              DecodeContent = false }
        let request =
            if not (has "accept-encoding" request) && not (has "range" request) then
                { request with
                    DecodeContent = responseHasBody
                    Headers = request.Headers @ [ "Accept-Encoding", NetHttpAcceptEncoding ] }
            else
                request
        let request = request |> withDefault "Accept" "*/*" |> withDefault "User-Agent" "Ruby"
        match uriHost with
        | Some host -> withDefault "Host" host request
        | None -> request

    /// `Net::HTTP#request` on a connection that wasn't started (`Connection: close`), then `begin_transport`
    /// (`Host` from `addr_port` unless the request has one).
    let transport (unstarted: bool) (endpoint: Endpoint) (request: Request) : Request =
        let request = if unstarted then withDefault "Connection" "close" request else request
        withDefault "Host" (Endpoint.hostHeader endpoint) request

/// What reading a body with a size limit produced.
type Body =
    | Complete of byte[]
    /// It ran past the limit; reading stopped there.
    | TooLarge

/// The connection's input: what the head read past its end, then the stream, each read within `read`.
[<Sealed>]
type private Reader(stream: Stream, read: TimeSpan) =
    let buffer = Array.zeroCreate<byte> 16384
    let mutable start = 0
    let mutable finish = 0

    static member Failure(e: exn) : exn =
        match e with
        | HttpFailure _ -> e
        | :? TimeoutException -> HttpFailure ReadTimeout
        | :? AuthenticationException as e -> HttpFailure(Tls e.Message)
        | :? IOException as e when (e.InnerException :? AuthenticationException) -> HttpFailure(Tls (nonNull e.InnerException).Message)
        | e -> HttpFailure(Io e)

    member _.Available = finish - start

    /// Refills the buffer: false at the end of the stream.
    member _.Fill() : Task<bool> =
        task {
            if start < finish then
                return true
            else
                try
                    let! n = stream.ReadAsync(buffer, 0, buffer.Length).WaitAsync(read)
                    start <- 0
                    finish <- n
                    return n > 0
                with e ->
                    return raise (Reader.Failure e)
        }

    /// Up to `count` bytes (at least one unless the stream has ended).
    member this.ReadSome(target: byte[], offset: int, count: int) : Task<int> =
        task {
            let! more = this.Fill()
            if not more then
                return 0
            else
                let n = min count (finish - start)
                Buffer.BlockCopy(buffer, start, target, offset, n)
                start <- start + n
                return n
        }

    /// A line without its CRLF (or bare LF); `None` at the end of the stream.
    member this.ReadLine(limit: int) : Task<string option> =
        task {
            let line = List<byte>()
            let mutable result: string option option = None
            while result.IsNone do
                let! more = this.Fill()
                if not more then
                    result <- Some(if line.Count = 0 then None else Some(Encoding.Latin1.GetString(line.ToArray())))
                else
                    let mutable i = start
                    while i < finish && buffer[i] <> byte '\n' do
                        i <- i + 1
                    line.AddRange(ArraySegment<byte>(buffer, start, i - start))
                    if line.Count > limit then raise (HttpFailure(Http "header line too long"))
                    if i < finish then
                        start <- i + 1
                        let text = Encoding.Latin1.GetString(line.ToArray())
                        result <- Some(Some(text.TrimEnd '\r'))
                    else
                        start <- finish
            return result.Value
        }

type private Framing =
    | NoBody
    | Length of int64
    | Chunked
    | UntilClose

/// The body as the connection delivers it, framing removed.
[<Sealed>]
type private BodyStream(reader: Reader, framing: Framing) =
    inherit Stream()
    let mutable remaining = match framing with Length n -> n | _ -> 0L
    let mutable finished = (framing = NoBody || framing = Length 0L)
    let mutable chunkLeft = 0L
    let mutable inChunk = false

    let truncated () = HttpFailure(Http "connection closed before message completed")

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = raise (NotSupportedException())
    override _.Position
        with get () = raise (NotSupportedException())
        and set _ = raise (NotSupportedException())
    override _.Flush() = ()
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength _ = raise (NotSupportedException())
    override _.Write(_, _, _) = raise (NotSupportedException())
    override this.Read(buffer: byte[], offset: int, count: int) : int = this.ReadAsyncCore(buffer, offset, count).GetAwaiter().GetResult()

    override this.ReadAsync(buffer: Memory<byte>, _cancellationToken: CancellationToken) : ValueTask<int> =
        let temp = Array.zeroCreate<byte> (min buffer.Length 65536)
        ValueTask<int>(
            task {
                let! n = this.ReadAsyncCore(temp, 0, temp.Length)
                temp.AsMemory(0, n).CopyTo buffer
                return n
            }
        )

    member private _.ReadChunkHeader() : Task<int64> =
        task {
            let! line = reader.ReadLine 1024
            match line with
            | None -> return raise (truncated ())
            | Some line ->
                let size = (line.Split(';')[0]).Trim()
                match Int64.TryParse(size, Globalization.NumberStyles.AllowHexSpecifier, Globalization.CultureInfo.InvariantCulture) with
                | true, n -> return n
                | _ -> return raise (HttpFailure(Http "invalid chunk size"))
        }

    member private this.ReadAsyncCore(buffer: byte[], offset: int, count: int) : Task<int> =
        task {
            if finished || count = 0 then
                return 0
            else
                match framing with
                | NoBody -> return 0
                | UntilClose ->
                    let! n = reader.ReadSome(buffer, offset, count)
                    if n = 0 then finished <- true
                    return n
                | Length _ ->
                    let! n = reader.ReadSome(buffer, offset, int (min (int64 count) remaining))
                    if n = 0 then raise (truncated ())
                    remaining <- remaining - int64 n
                    if remaining = 0L then finished <- true
                    return n
                | Chunked ->
                    if not inChunk then
                        let! size = this.ReadChunkHeader()
                        if size = 0L then
                            // Trailers, up to the blank line.
                            let mutable go = true
                            while go do
                                let! line = reader.ReadLine 8192
                                match line with
                                | None
                                | Some "" -> go <- false
                                | Some _ -> ()
                            finished <- true
                        else
                            chunkLeft <- size
                            inChunk <- true
                    if finished then
                        return 0
                    else
                        let! n = reader.ReadSome(buffer, offset, int (min (int64 count) chunkLeft))
                        if n = 0 then raise (truncated ())
                        chunkLeft <- chunkLeft - int64 n
                        if chunkLeft = 0L then
                            inChunk <- false
                            // The CRLF after the chunk's data.
                            let! _ = reader.ReadLine 8
                            ()
                        return n
        }

/// Puts bytes already read back in front of a stream.
[<Sealed>]
type private PrefixedStream(prefix: byte[], inner: Stream) =
    inherit Stream()
    let mutable used = 0

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = raise (NotSupportedException())
    override _.Position
        with get () = raise (NotSupportedException())
        and set _ = raise (NotSupportedException())
    override _.Flush() = ()
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength _ = raise (NotSupportedException())
    override _.Write(_, _, _) = raise (NotSupportedException())

    override _.Read(buffer: byte[], offset: int, count: int) : int =
        if used < prefix.Length then
            let n = min count (prefix.Length - used)
            Buffer.BlockCopy(prefix, used, buffer, offset, n)
            used <- used + n
            n
        else
            inner.Read(buffer, offset, count)

    override this.ReadAsync(buffer: Memory<byte>, cancellationToken: CancellationToken) : ValueTask<int> =
        if used < prefix.Length then
            let n = min buffer.Length (prefix.Length - used)
            prefix.AsMemory(used, n).CopyTo buffer
            used <- used + n
            ValueTask<int> n
        else
            inner.ReadAsync(buffer, cancellationToken)

/// A response whose head has been read. Dispose it to close the connection.
[<Sealed>]
type HttpResponse
    private
    (
        status: int,
        reason: string,
        headers: (string * string) list,
        stream: Stream,
        reader: Reader,
        framing: Framing,
        decodeContent: bool,
        registration: CancellationTokenRegistration
    ) =
    let mutable body: BodyStream | null = null

    member _.Status = status

    /// The status line's reason phrase (`response.message`).
    member _.Reason = reason

    member _.Headers = headers

    /// `response[name]`: every value of the header, joined with ", ".
    member _.Header(name: string) : string option =
        match headers |> List.filter (fun (n, _) -> n.Equals(name, StringComparison.OrdinalIgnoreCase)) |> List.map snd with
        | [] -> None
        | values -> Some(String.Join(", ", values))

    /// `Net::HTTPHeader#content_type`: the media type before any `;`, main and sub type each stripped (not
    /// downcased), or just the main type when there's no `/`.
    member this.ContentType() : string option =
        this.Header "content-type"
        |> Option.map (fun header ->
            let media = (header.Split ';')[0]
            let parts = media.Split '/'
            let main = Ruby.strip parts[0]
            if parts.Length > 1 then $"{main}/{Ruby.strip parts[1]}" else main)

    /// `Net::HTTPHeader#content_length`: the first run of digits, or `HTTPHeaderSyntaxError`.
    member this.ContentLength() : Result<uint64 option, HttpError> =
        match this.Header "content-length" with
        | None -> Ok None
        | Some header ->
            let digits = header |> Seq.skipWhile (fun c -> not (Char.IsAsciiDigit c)) |> Seq.takeWhile Char.IsAsciiDigit |> Seq.toArray |> String
            if digits = "" then
                Error(Http "wrong Content-Length format")
            else
                match UInt64.TryParse digits with
                | true, n -> Ok(Some n)
                | _ -> Ok(Some UInt64.MaxValue)

    /// `Net::HTTPResponse#inflater`: gzip, x-gzip or deflate, unless it's a range.
    member private this.Inflates: bool =
        decodeContent
        && (this.Header "content-range").IsNone
        && (match this.Header "content-encoding" with
            | Some encoding ->
                match encoding.ToLowerInvariant() with
                | "gzip"
                | "x-gzip"
                | "deflate" -> true
                | _ -> false
            | None -> false)

    /// Reads the body (inflating it when `Net::HTTP` would), stopping once it would exceed `limit` bytes.
    /// `Net::HTTP` reads any size.
    member this.ReadBody(limit: int) : Task<Result<Body, HttpError>> =
        task {
            try
                let raw = new BodyStream(reader, framing)
                body <- raw
                let! source =
                    task {
                        if this.Inflates then
                            // `Zlib::Inflate.new(32 + Zlib::MAX_WBITS)`: gzip or zlib, detected from the header.
                            let head = Array.zeroCreate<byte> 2
                            let mutable got = 0
                            let mutable go = true
                            while go && got < 2 do
                                let! n = raw.ReadAsync(Memory<byte>(head, got, 2 - got), CancellationToken.None)
                                if n = 0 then go <- false else got <- got + n
                            let prefixed = new PrefixedStream(Array.sub head 0 got, raw)
                            if got = 2 && head[0] = 0x1fuy && head[1] = 0x8buy then
                                return new GZipStream(prefixed, CompressionMode.Decompress) :> Stream
                            else
                                return new ZLibStream(prefixed, CompressionMode.Decompress) :> Stream
                        else
                            return raw :> Stream
                    }
                use source = source
                use output = new MemoryStream()
                let buffer = Array.zeroCreate<byte> 65536
                let mutable tooLarge = false
                let mutable go = true
                while go do
                    let! n =
                        task {
                            try
                                return! source.ReadAsync(buffer, 0, buffer.Length)
                            with
                            | HttpFailure _ as e -> return raise e
                            | :? InvalidDataException as e -> return raise (HttpFailure(Inflate e.Message))
                        }
                    if n = 0 then
                        go <- false
                    elif int64 output.Length + int64 n > int64 limit then
                        tooLarge <- true
                        go <- false
                    else
                        output.Write(buffer, 0, n)
                return Ok(if tooLarge then TooLarge else Complete(output.ToArray()))
            with HttpFailure error ->
                return Error error
        }

    interface IDisposable with
        member _.Dispose() =
            registration.Dispose()
            stream.Dispose()

    /// Reads the head: the status line and headers. Cancelling closes the connection, which fails any read
    /// in progress.
    static member internal Read(stream: Stream, timeouts: Timeouts, request: Request, cancellation: CancellationToken) : Task<HttpResponse> =
        task {
            let reader = Reader(stream, timeouts.Read)
            let readHead () : Task<int * string * (string * string) list> =
                task {
                    let mutable result: (int * string * (string * string) list) option = None
                    while result.IsNone do
                        let! statusLine = reader.ReadLine 16384
                        match statusLine with
                        | None -> raise (HttpFailure(Http "connection closed before message completed"))
                        | Some statusLine ->
                            let parts = statusLine.Split(' ', 3)
                            if parts.Length < 2 || not (parts[0].StartsWith "HTTP/") then
                                raise (HttpFailure(Http "invalid HTTP version parsed"))
                            let status =
                                match Int32.TryParse parts[1] with
                                | true, status -> status
                                | _ -> raise (HttpFailure(Http "invalid status code"))
                            let reason = if parts.Length > 2 then parts[2] else ""
                            let headers = List<string * string>()
                            let mutable go = true
                            let mutable total = 0
                            while go do
                                let! line = reader.ReadLine 16384
                                match line with
                                | None -> raise (HttpFailure(Http "connection closed before message completed"))
                                | Some "" -> go <- false
                                | Some line ->
                                    total <- total + line.Length
                                    if total > 65536 || headers.Count >= 100 then raise (HttpFailure(Http "too many headers"))
                                    match line.IndexOf ':' with
                                    | -1 -> ()
                                    | colon -> headers.Add(line.Substring(0, colon), line.Substring(colon + 1).Trim())
                            // An informational response (100 Continue) is followed by the real one.
                            if status >= 100 && status < 200 && status <> 101 then () else result <- Some(status, reason, List.ofSeq headers)
                    return result.Value
                }
            try
                let! status, reason, headers = readHead().WaitAsync(timeouts.Read)
                let header (name: string) =
                    match headers |> List.filter (fun (n, _) -> n.Equals(name, StringComparison.OrdinalIgnoreCase)) |> List.map snd with
                    | [] -> None
                    | values -> Some(String.Join(", ", values))
                let framing =
                    if request.Method = "HEAD" || status = 204 || status = 304 || (status >= 100 && status < 200) then
                        NoBody
                    else
                        match header "transfer-encoding" with
                        | Some encoding when encoding.ToLowerInvariant().Contains "chunked" -> Chunked
                        | _ ->
                            match header "content-length" with
                            | Some length ->
                                match Int64.TryParse(length.Trim()) with
                                | true, n when n >= 0L -> Length n
                                | _ -> raise (HttpFailure(Http "invalid content-length parsed"))
                            | None -> UntilClose
                return new HttpResponse(status, reason, headers, stream, reader, framing, request.DecodeContent, cancellation.Register(fun () -> stream.Dispose()))
            with
            | :? TimeoutException -> return raise (HttpFailure ReadTimeout)
            | e -> return raise (Reader.Failure e)
        }

module Http =
    /// The certificate check for a TLS peer: the name must match, and the chain must lead to a trusted root.
    let private validate (trust: TlsTrust) : RemoteCertificateValidationCallback | null =
        match trust with
        | SystemRoots -> null
        | CustomRoots roots ->
            RemoteCertificateValidationCallback(fun _ certificate _ errors ->
                match certificate with
                | null -> false
                | certificate ->
                    if errors.HasFlag SslPolicyErrors.RemoteCertificateNameMismatch || errors.HasFlag SslPolicyErrors.RemoteCertificateNotAvailable then
                        false
                    else
                        use chain = new X509Chain()
                        chain.ChainPolicy.TrustMode <- X509ChainTrustMode.CustomRootTrust
                        chain.ChainPolicy.CustomTrustStore.AddRange roots
                        chain.ChainPolicy.RevocationMode <- X509RevocationMode.NoCheck
                        use certificate = new X509Certificate2(certificate)
                        chain.Build certificate)

    /// `TCPSocket.open(host, port)`: each resolved address in turn.
    let private connectResolving (net: Network) (endpoint: Endpoint) (cancellation: CancellationToken) : Task<Socket> =
        task {
            let! addresses =
                task {
                    match IPAddress.TryParse(Endpoint.bareHost endpoint) with
                    | true, NonNull ip -> return [ ip ]
                    | _ ->
                        match! net.Resolver.Lookup(Endpoint.bareHost endpoint) with
                        | Ok addresses -> return addresses
                        | Error e -> return raise (HttpFailure(Unresolvable e.Message))
                }
            let mutable connected: Socket option = None
            let mutable lastError: exn option = None
            for ip in addresses do
                if connected.IsNone then
                    try
                        let! socket = net.Dialer.Connect(IPEndPoint(ip, endpoint.Port), cancellation)
                        connected <- Some socket
                    with
                    | :? OperationCanceledException as e -> raise e
                    | e -> lastError <- Some e
            match connected, lastError with
            | Some socket, _ -> return socket
            | None, Some e -> return raise (HttpFailure(Io e))
            | None, None -> return raise (HttpFailure(Unresolvable endpoint.Host))
        }

    let private connect (net: Network) (endpoint: Endpoint) (cancellation: CancellationToken) : Task<Stream> =
        task {
            let! socket =
                match endpoint.PinnedIp with
                | Some ip ->
                    task {
                        try
                            return! net.Dialer.Connect(IPEndPoint(ip, endpoint.Port), cancellation)
                        with
                        | :? OperationCanceledException as e -> return raise e
                        | e -> return raise (HttpFailure(Io e))
                    }
                | None -> connectResolving net endpoint cancellation
            let tcp = new NetworkStream(socket, true)
            if not endpoint.Https then
                return tcp :> Stream
            else
                let ssl = new SslStream(tcp, false)
                try
                    let options = SslClientAuthenticationOptions()
                    options.TargetHost <- Endpoint.bareHost endpoint
                    options.RemoteCertificateValidationCallback <- validate net.Tls
                    options.CertificateRevocationCheckMode <- X509RevocationMode.NoCheck
                    do! ssl.AuthenticateAsClientAsync(options, cancellation)
                    return ssl :> Stream
                with
                | :? OperationCanceledException as e ->
                    ssl.Dispose()
                    return raise e
                | e ->
                    ssl.Dispose()
                    return raise (Reader.Failure e)
        }

    let private writeRequest (stream: Stream) (request: Request) : Task =
        task {
            let head = StringBuilder()
            head.Append(request.Method).Append(' ').Append(request.Target).Append(" HTTP/1.1\r\n") |> ignore
            for (name, value) in request.Headers do
                head.Append(name).Append(": ").Append(value).Append("\r\n") |> ignore
            // A body's length is written after the headers the caller gave, unless one of them is it.
            if request.Body.Length > 0 && not (request.Headers |> List.exists (fun (n, _) -> n.Equals("content-length", StringComparison.OrdinalIgnoreCase))) then
                head.Append("Content-Length: ").Append(request.Body.Length).Append("\r\n") |> ignore
            head.Append "\r\n" |> ignore
            use bytes = new MemoryStream()
            bytes.Write(Encoding.UTF8.GetBytes(head.ToString()))
            bytes.Write(request.Body)
            do! stream.WriteAsync(bytes.GetBuffer().AsMemory(0, int bytes.Length))
            do! stream.FlushAsync()
        }

    let private send (stream: Stream) (request: Request) (timeouts: Timeouts) (cancellation: CancellationToken) : Task<HttpResponse> =
        task {
            try
                do! (writeRequest stream request).WaitAsync(timeouts.Read)
            with
            | :? TimeoutException -> raise (HttpFailure ReadTimeout)
            | HttpFailure _ as e -> raise e
            | e -> raise (Reader.Failure e)
            return! HttpResponse.Read(stream, timeouts, request, cancellation)
        }

    /// Connects (within `open`), sends the request, and returns once the response head arrives (within `read`).
    /// Cancelling abandons the exchange: the connection is closed under whatever read is in progress.
    let exchange
        (net: Network)
        (endpoint: Endpoint)
        (request: Request)
        (timeouts: Timeouts)
        (cancellation: CancellationToken)
        : Task<Result<HttpResponse, HttpError>> =
        task {
            try
                use opening = CancellationTokenSource.CreateLinkedTokenSource cancellation
                opening.CancelAfter timeouts.Open
                let! stream =
                    task {
                        try
                            return! connect net endpoint opening.Token
                        with :? OperationCanceledException when opening.IsCancellationRequested && not cancellation.IsCancellationRequested ->
                            return raise (HttpFailure OpenTimeout)
                    }
                try
                    let! response = send stream request timeouts cancellation
                    return Ok response
                with e ->
                    stream.Dispose()
                    return raise e
            with HttpFailure error ->
                return Error error
        }

    /// `URI::HTTP#request_uri`: path (at least "/") and query.
    let requestUri (url: RubyUri) : string =
        let path = defaultArg url.Path ""
        let target =
            match url.Query with
            | Some query -> $"{path}?{query}"
            | None -> path
        if target.StartsWith '/' then target else "/" + target
