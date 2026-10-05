// Port of rust/crates/kit/src/response.rs
//
// Responses as controllers build them, before the kit finishes them (cookies, default headers,
// ETags, conditional GET, HEAD) and hands them to the host.
namespace Campfire.Kit

open System
open System.Buffers
open System.IO
open System.Text
open Campfire.RailsCompat

/// HTTP status codes (`http::StatusCode`) as plain integers, with the reason phrases the `http`
/// crate gives them.
module Status =
    [<Literal>]
    let Ok = 200

    [<Literal>]
    let Created = 201

    [<Literal>]
    let NoContent = 204

    [<Literal>]
    let MovedPermanently = 301

    [<Literal>]
    let Found = 302

    [<Literal>]
    let SeeOther = 303

    [<Literal>]
    let NotModified = 304

    [<Literal>]
    let PermanentRedirect = 308

    [<Literal>]
    let BadRequest = 400

    [<Literal>]
    let Forbidden = 403

    [<Literal>]
    let NotFound = 404

    [<Literal>]
    let MethodNotAllowed = 405

    [<Literal>]
    let NotAcceptable = 406

    [<Literal>]
    let PayloadTooLarge = 413

    [<Literal>]
    let UnprocessableEntity = 422

    [<Literal>]
    let InternalServerError = 500

    /// `StatusCode::canonical_reason`.
    let canonicalReason (code: int) : string | null =
        match code with
        | 100 -> "Continue"
        | 101 -> "Switching Protocols"
        | 102 -> "Processing"
        | 200 -> "OK"
        | 201 -> "Created"
        | 202 -> "Accepted"
        | 203 -> "Non-Authoritative Information"
        | 204 -> "No Content"
        | 205 -> "Reset Content"
        | 206 -> "Partial Content"
        | 207 -> "Multi-Status"
        | 208 -> "Already Reported"
        | 226 -> "IM Used"
        | 300 -> "Multiple Choices"
        | 301 -> "Moved Permanently"
        | 302 -> "Found"
        | 303 -> "See Other"
        | 304 -> "Not Modified"
        | 305 -> "Use Proxy"
        | 307 -> "Temporary Redirect"
        | 308 -> "Permanent Redirect"
        | 400 -> "Bad Request"
        | 401 -> "Unauthorized"
        | 402 -> "Payment Required"
        | 403 -> "Forbidden"
        | 404 -> "Not Found"
        | 405 -> "Method Not Allowed"
        | 406 -> "Not Acceptable"
        | 407 -> "Proxy Authentication Required"
        | 408 -> "Request Timeout"
        | 409 -> "Conflict"
        | 410 -> "Gone"
        | 411 -> "Length Required"
        | 412 -> "Precondition Failed"
        | 413 -> "Payload Too Large"
        | 414 -> "URI Too Long"
        | 415 -> "Unsupported Media Type"
        | 416 -> "Range Not Satisfiable"
        | 417 -> "Expectation Failed"
        | 418 -> "I'm a teapot"
        | 421 -> "Misdirected Request"
        | 422 -> "Unprocessable Entity"
        | 423 -> "Locked"
        | 424 -> "Failed Dependency"
        | 426 -> "Upgrade Required"
        | 428 -> "Precondition Required"
        | 429 -> "Too Many Requests"
        | 431 -> "Request Header Fields Too Large"
        | 451 -> "Unavailable For Legal Reasons"
        | 500 -> "Internal Server Error"
        | 501 -> "Not Implemented"
        | 502 -> "Bad Gateway"
        | 503 -> "Service Unavailable"
        | 504 -> "Gateway Timeout"
        | 505 -> "HTTP Version Not Supported"
        | 506 -> "Variant Also Negotiates"
        | 507 -> "Insufficient Storage"
        | 508 -> "Loop Detected"
        | 510 -> "Not Extended"
        | 511 -> "Network Authentication Required"
        | _ -> null

    /// A status that carries no body (`1xx`, `204`, `304`).
    let hasNoBody (code: int) : bool = (code >= 100 && code <= 199) || code = 204 || code = 304

    /// `100..=199 | 204 | 205 | 304`: a bare `head` leaves the content type off these.
    let hasNoContent (code: int) : bool = hasNoBody code || code = 205

/// Header names the kit reads and writes, in lowercase as HTTP/2 sends them.
module Hdr =
    [<Literal>]
    let Accept = "accept"

    [<Literal>]
    let AcceptEncoding = "accept-encoding"

    [<Literal>]
    let CacheControl = "cache-control"

    [<Literal>]
    let ClientIp = "client-ip"

    [<Literal>]
    let ContentDisposition = "content-disposition"

    [<Literal>]
    let ContentEncoding = "content-encoding"

    [<Literal>]
    let ContentLength = "content-length"

    [<Literal>]
    let ContentType = "content-type"

    [<Literal>]
    let Cookie = "cookie"

    [<Literal>]
    let Date = "date"

    [<Literal>]
    let ETag = "etag"

    [<Literal>]
    let Forwarded = "forwarded"

    [<Literal>]
    let Host = "host"

    [<Literal>]
    let IfModifiedSince = "if-modified-since"

    [<Literal>]
    let IfNoneMatch = "if-none-match"

    [<Literal>]
    let LastModified = "last-modified"

    [<Literal>]
    let Location = "location"

    [<Literal>]
    let Origin = "origin"

    [<Literal>]
    let Referer = "referer"

    [<Literal>]
    let SecFetchSite = "sec-fetch-site"

    [<Literal>]
    let SetCookie = "set-cookie"

    [<Literal>]
    let StrictTransportSecurity = "strict-transport-security"

    [<Literal>]
    let TurboFrame = "turbo-frame"

    [<Literal>]
    let UserAgent = "user-agent"

    [<Literal>]
    let Vary = "vary"

    [<Literal>]
    let XForwardedFor = "x-forwarded-for"

    [<Literal>]
    let XForwardedHost = "x-forwarded-host"

    [<Literal>]
    let XForwardedProto = "x-forwarded-proto"

    [<Literal>]
    let XForwardedScheme = "x-forwarded-scheme"

    [<Literal>]
    let XForwardedSsl = "x-forwarded-ssl"

    [<Literal>]
    let XHttpMethodOverride = "x-http-method-override"

    [<Literal>]
    let XRequestId = "x-request-id"

    [<Literal>]
    let XRequestedWith = "x-requested-with"

    [<Literal>]
    let XRuntime = "x-runtime"

/// A response's headers (`http::HeaderMap`): case-insensitive names, any number of values for a
/// name. A response has about ten, so this is arrays scanned in order, not a dictionary.
[<Sealed; AllowNullLiteral>]
type HeaderMap() =
    let mutable names: string[] = Array.zeroCreate 8
    let mutable values: string[] = Array.zeroCreate 8
    let mutable count = 0

    static let sameName (a: string) (b: string) = String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

    member _.Count = count

    member _.NameAt(i: int) : string = names[i]
    member _.ValueAt(i: int) : string = values[i]

    member _.IndexOf(name: string) : int =
        let mutable found = -1
        let mutable i = 0
        while found < 0 && i < count do
            if sameName names[i] name then found <- i
            i <- i + 1
        found

    member this.Contains(name: string) : bool = this.IndexOf name >= 0

    /// The first value for `name`, null when there is none.
    member this.Get(name: string) : string | null =
        match this.IndexOf name with
        | -1 -> null
        | i -> values[i]

    /// Add a value after those `name` already has.
    member _.Append(name: string, value: string) : unit =
        if count = names.Length then
            Array.Resize(&names, count * 2)
            Array.Resize(&values, count * 2)
        names[count] <- name
        values[count] <- value
        count <- count + 1

    member this.Remove(name: string) : unit =
        let mutable kept = 0
        for i in 0 .. count - 1 do
            if not (sameName names[i] name) then
                names[kept] <- names[i]
                values[kept] <- values[i]
                kept <- kept + 1
        for i in kept .. count - 1 do
            names[i] <- Unchecked.defaultof<string>
            values[i] <- Unchecked.defaultof<string>
        count <- kept

    /// Replace every value `name` has with `value` (`HeaderMap::insert`).
    member this.Insert(name: string, value: string) : unit =
        match this.IndexOf name with
        | -1 -> this.Append(name, value)
        | first ->
            names[first] <- name
            values[first] <- value
            let mutable kept = first + 1
            for i in first + 1 .. count - 1 do
                if not (sameName names[i] name) then
                    names[kept] <- names[i]
                    values[kept] <- values[i]
                    kept <- kept + 1
            for i in kept .. count - 1 do
                names[i] <- Unchecked.defaultof<string>
                values[i] <- Unchecked.defaultof<string>
            count <- kept

    /// Every value for `name`, in order.
    member this.GetAll(name: string) : string list =
        [ for i in 0 .. count - 1 do
              if sameName names[i] name then
                  values[i] ]

    /// `HeaderValue::from_str`: no control characters except tab, and no DEL.
    static member IsValidValue(value: string) : bool =
        let mutable ok = true
        let mutable i = 0
        while ok && i < value.Length do
            let c = value[i]
            if (c < ' ' && c <> '\t') || c = '\127' then ok <- false
            i <- i + 1
        ok

    /// Replaces `name` with `value` the way Puma writes a Rack header (puma 7.2.1,
    /// `Puma::Request#str_headers`). A value with a line break goes out one line per header line
    /// (`split("\n")`, which drops trailing empty lines), and a line holding any other control
    /// character (`ILLEGAL_HEADER_VALUE_REGEX`) is left out. Values come from the request at times,
    /// like `?disposition=` on a proxied blob. Puma writes a DEL, which Thruster then refuses with a
    /// 502; that line is left out too.
    member this.SetPumaValue(name: string, value: string) : unit =
        if HeaderMap.IsValidValue value then
            this.Insert(name, value)
        else
            this.Remove name
            let lines = value.Split '\n'
            let mutable last = lines.Length
            while last > 0 && lines[last - 1] = "" do
                last <- last - 1
            for i in 0 .. last - 1 do
                if HeaderMap.IsValidValue lines[i] then this.Append(name, lines[i])

/// The SHA-256 `Rack::ETag` took of a response's whole body: the body's identity, so the gzip of a
/// body that repeats (the sidebar) comes from the gzip cache instead of being deflated again.
[<Struct>]
type BodyDigest =
    { A: uint64
      B: uint64
      C: uint64
      D: uint64 }

/// A byte buffer rented from the array pool, returned by `Dispose`. A response that carries one
/// returns it once the host has written the body.
[<Sealed; AllowNullLiteral>]
type PooledBytes(buffer: byte[], length: int) =
    let mutable buffer: byte[] | null = buffer

    /// Rent room for `length` bytes.
    static member Rent(length: int) : PooledBytes = new PooledBytes(ArrayPool<byte>.Shared.Rent length, length)

    /// The rented array, for filling.
    member _.Array: byte[] =
        match buffer with
        | null -> Array.empty
        | buffer -> buffer

    member _.Length = length

    member _.Memory: ReadOnlyMemory<byte> =
        match buffer with
        | null -> ReadOnlyMemory.Empty
        | buffer -> ReadOnlyMemory<byte>(buffer, 0, length)

    /// Return the array to the pool; a second call does nothing.
    member _.Release() : unit =
        match buffer with
        | null -> ()
        | rented ->
            buffer <- null
            ArrayPool<byte>.Shared.Return rented

    interface IDisposable with
        member this.Dispose() = this.Release()

/// An `IBufferWriter<byte>` over pooled arrays: render into it (a template, `Utf8JsonWriter`), then
/// hand the bytes to a response with `ToPooledBytes`, which passes the array on instead of copying.
[<Sealed>]
type PooledBufferWriter(initialCapacity: int) =
    let mutable buffer: byte[] | null = ArrayPool<byte>.Shared.Rent(max initialCapacity 256)
    let mutable written = 0

    new() = new PooledBufferWriter(4096)

    member private _.Buffer: byte[] =
        match buffer with
        | null -> invalidOp "the buffer was handed to a response"
        | b -> b

    member _.Length = written

    member private this.Grow(hint: int) =
        let current = this.Buffer
        let needed = written + max hint 1
        if needed > current.Length then
            let bigger = ArrayPool<byte>.Shared.Rent(max needed (current.Length * 2))
            Buffer.BlockCopy(current, 0, bigger, 0, written)
            ArrayPool<byte>.Shared.Return current
            buffer <- bigger

    interface IBufferWriter<byte> with
        member _.Advance(count: int) = written <- written + count

        member this.GetMemory(sizeHint: int) : Memory<byte> =
            this.Grow sizeHint
            Memory<byte>(this.Buffer, written, this.Buffer.Length - written)

        member this.GetSpan(sizeHint: int) : Span<byte> =
            this.Grow sizeHint
            Span<byte>(this.Buffer, written, this.Buffer.Length - written)

    /// The bytes written so far, in a `PooledBytes` that now owns the array.
    member this.ToPooledBytes() : PooledBytes =
        let owned = this.Buffer
        buffer <- null
        new PooledBytes(owned, written)

    interface IDisposable with
        member _.Dispose() =
            match buffer with
            | null -> ()
            | rented ->
                buffer <- null
                ArrayPool<byte>.Shared.Return rented

/// A page of cached fragments, in parts: the ETag and gzip reuse the fragments' digests and
/// compressed pieces instead of working through the whole body, which is never joined. The
/// implementation is the response cache's (`rust/crates/kit/src/deflater/splice.rs`).
type IPageParts =
    abstract BodyLength: int
    /// The hex digest `Rack::ETag` would use for the page (at least 32 characters).
    abstract Etag: unit -> string
    /// The page's bytes, part after part.
    abstract WritePlain: System.Buffers.IBufferWriter<byte> -> unit
    /// The page as one gzip member with this modification time.
    abstract Gzip: mtime: uint32 -> ReadOnlyMemory<byte>

/// A file (or a byte range of one) to stream from disk.
type FileBody = { Path: string; Offset: int64; Len: int64 }

type Body =
    | Empty
    | Bytes of ReadOnlyMemory<byte>
    /// Bytes in a pooled buffer, returned to the pool once written.
    | Pooled of PooledBytes
    /// A page with cached fragments, in parts.
    | Parts of IPageParts
    | File of FileBody
    /// A streaming body (e.g. a proxied blob); never ETagged.
    | Stream of Stream

module Body =
    /// The body as one buffer, when it is one.
    let tryMemory (body: Body) : ReadOnlyMemory<byte> voption =
        match body with
        | Bytes bytes -> ValueSome bytes
        | Pooled pooled -> ValueSome pooled.Memory
        | Empty -> ValueNone
        | _ -> ValueNone

/// `Cache-Control` directives set by `expires_in`, `fresh_when(public:)`, `no_store` and friends,
/// normalized like `ActionDispatch::Http::Cache::Response#merge_and_normalize_cache_control!`.
[<CustomEquality; NoComparison>]
type CacheControl =
    { MaxAge: uint64 voption
      Public: bool
      Private: bool
      MustRevalidate: bool
      NoCache: bool
      NoStore: bool
      MustUnderstand: bool
      StaleWhileRevalidate: uint64 voption
      StaleIfError: uint64 voption
      Immutable: bool
      Extras: string list }

    static member Empty: CacheControl =
        { MaxAge = ValueNone
          Public = false
          Private = false
          MustRevalidate = false
          NoCache = false
          NoStore = false
          MustUnderstand = false
          StaleWhileRevalidate = ValueNone
          StaleIfError = ValueNone
          Immutable = false
          Extras = [] }

    override this.Equals(other: obj) =
        match other with
        | :? CacheControl as o ->
            this.MaxAge = o.MaxAge
            && this.Public = o.Public
            && this.Private = o.Private
            && this.MustRevalidate = o.MustRevalidate
            && this.NoCache = o.NoCache
            && this.NoStore = o.NoStore
            && this.MustUnderstand = o.MustUnderstand
            && this.StaleWhileRevalidate = o.StaleWhileRevalidate
            && this.StaleIfError = o.StaleIfError
            && this.Immutable = o.Immutable
            && this.Extras = o.Extras
        | _ -> false

    override this.GetHashCode() = hash (this.MaxAge, this.Public, this.NoStore, this.NoCache)

    member this.IsEmpty: bool = this.Equals CacheControl.Empty

    member this.ToHeader() : string | null =
        if this.IsEmpty then
            null
        else
            let options = ResizeArray<string>()
            if this.NoStore then
                if this.Private then options.Add "private"
                if this.MustUnderstand then options.Add "must-understand"
                options.Add "no-store"
            elif this.NoCache then
                if this.Public then options.Add "public"
                options.Add "no-cache"
                options.AddRange this.Extras
            else
                match this.MaxAge with
                | ValueSome maxAge -> options.Add $"max-age={maxAge}"
                | ValueNone -> ()
                options.Add(if this.Public then "public" else "private")
                if this.MustRevalidate then options.Add "must-revalidate"
                match this.StaleWhileRevalidate with
                | ValueSome swr -> options.Add $"stale-while-revalidate={swr}"
                | ValueNone -> ()
                match this.StaleIfError with
                | ValueSome sie -> options.Add $"stale-if-error={sie}"
                | ValueNone -> ()
                if this.Immutable then options.Add "immutable"
                options.AddRange this.Extras
            String.Join(", ", options)

/// Options for `expires_in`.
type ExpiresIn =
    { Public: bool
      MustRevalidate: bool
      StaleWhileRevalidate: uint64 voption
      StaleIfError: uint64 voption
      Immutable: bool }

    static member Default: ExpiresIn =
        { Public = false
          MustRevalidate = false
          StaleWhileRevalidate = ValueNone
          StaleIfError = ValueNone
          Immutable = false }

/// Options for `send_file` / `send_data`.
type SendOptions =
    { Filename: string | null
      /// `type:`; defaults to the filename's MIME type, else `application/octet-stream`.
      ContentType: string | null
      /// `disposition:`; null omits the header. Defaults to `attachment`.
      Disposition: string | null
      Status: int }

    static member Default: SendOptions =
        { Filename = null
          ContentType = null
          Disposition = "attachment"
          Status = Status.Ok }

    static member Inline(contentType: string) : SendOptions =
        { SendOptions.Default with
            ContentType = contentType
            Disposition = "inline" }

[<Sealed; AllowNullLiteral>]
type Response(status: int) =
    member val Status: int = status with get, set
    member val Headers: HeaderMap = HeaderMap() with get, set
    member val Body: Body = Body.Empty with get, set

    /// The body's SHA-256, when `Rack::ETag` digested it.
    member val BodyDigest: BodyDigest voption = ValueNone with get, set

    /// Marks a response `ActionDispatch::Static` served (a public file or an asset): the middleware
    /// below `Static` in the reference (`Rack::Runtime`, `ActionDispatch::RequestId`) never saw it.
    member val StaticFile: bool = false with get, set

    static member WithBody(status: int, contentType: string, body: ReadOnlyMemory<byte>) : Response =
        Response(status).ContentType(contentType).SetBody body

    static member WithBody(status: int, contentType: string, body: string) : Response =
        Response(status).ContentType(contentType).SetBody body

    static member WithBody(status: int, contentType: string, body: PooledBytes) : Response =
        Response(status).ContentType(contentType).SetBody body

    member this.SetBody(body: ReadOnlyMemory<byte>) : Response =
        this.Body <- Body.Bytes body
        this

    member this.SetBody(body: byte[]) : Response =
        this.Body <- Body.Bytes(ReadOnlyMemory<byte> body)
        this

    member this.SetBody(body: string) : Response =
        this.Body <- Body.Bytes(ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes body))
        this

    member this.SetBody(body: PooledBytes) : Response =
        this.Body <- Body.Pooled body
        this

    member this.ContentType(contentType: string) : Response = this.Header(Hdr.ContentType, contentType)

    /// Set (replace) a header, as Puma would write the value (see `HeaderMap.SetPumaValue`).
    member this.Header(name: string, value: string) : Response =
        this.Headers.SetPumaValue(name, value)
        this

    member this.GetHeader(name: string) : string | null = this.Headers.Get name

    member this.Location: string | null = this.Headers.Get Hdr.Location

    /// The body bytes, for tests and middleware; `ValueNone` for files, streams and parts.
    member this.BodyBytes: ReadOnlyMemory<byte> voption =
        match this.Body with
        | Body.Empty -> ValueNone
        | body -> Body.tryMemory body

module Response =
    let private fileExtension (name: string) : string voption =
        // `Path::extension`: nothing after the last dot of the file name, unless that name is only a
        // leading dot and an extension.
        let fileName =
            let slash = name.LastIndexOf '/'
            if slash >= 0 then name.Substring(slash + 1) else name
        match fileName.LastIndexOf '.' with
        | -1
        | 0 -> ValueNone
        | dot -> ValueSome(fileName.Substring(dot + 1))

    /// `Content-Disposition` as `ActionDispatch::Http::ContentDisposition#to_s` builds it.
    let internal contentDisposition (disposition: string) (filename: string | null) : string =
        match filename with
        | null -> disposition
        | filename -> ContentDisposition.format disposition filename

    /// `send_file_headers!` then the body: file or bytes, whole, whatever the `Range` header asks for
    /// (as Rails' `send_file` and `send_data` do).
    let internal send (options: SendOptions) (body: Body) : Response =
        let contentType =
            match options.ContentType with
            | null ->
                let fromName =
                    match options.Filename with
                    | null -> ValueNone
                    | filename ->
                        fileExtension filename
                        |> ValueOption.bind (fun ext -> Format.lookupByExtension (ext.ToLowerInvariant()))
                match fromName with
                | ValueSome mime -> mime.String
                | ValueNone -> "application/octet-stream"
            | contentType -> contentType
        let response = Response(options.Status).ContentType contentType
        match options.Disposition with
        | null -> ()
        | disposition -> response.Header(Hdr.ContentDisposition, contentDisposition disposition options.Filename) |> ignore
        response.Header("content-transfer-encoding", "binary") |> ignore
        response.Body <- body
        response

    [<Literal>]
    let HtmlUtf8 = "text/html; charset=utf-8"

    [<Literal>]
    let JsonUtf8 = "application/json; charset=utf-8"

    [<Literal>]
    let TurboStreamUtf8 = "text/vnd.turbo-stream.html; charset=utf-8"
