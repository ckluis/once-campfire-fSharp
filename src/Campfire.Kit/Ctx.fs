// Port of rust/crates/kit/src/ctx.rs
//
// `Ctx`: everything a controller action touches, in place of a Rails controller instance.
namespace Campfire.Kit

open System
open System.Buffers
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Microsoft.Extensions.Logging
open Campfire.RailsCompat
open Campfire.RailsCompat.Clock
open Campfire.Kit

/// Options for `redirect_to`.
type Redirect =
    {
        /// Defaults to 302 Found, like Rails.
        Status: int voption
        Notice: string | null
        Alert: string | null
        AllowOtherHost: bool
    }

module Redirect =
    let Default: Redirect =
        { Status = ValueNone
          Notice = null
          Alert = null
          AllowOtherHost = false }

/// Validators for `fresh_when` / `stale?`.
type Freshness =
    {
        /// The weak ETag validator, already expanded to a cache key (e.g. `record.cache_key_with_version`).
        Etag: string | null
        StrongEtag: string | null
        LastModified: Timestamp voption
        Public: bool
        /// The template digest `ETagWithTemplateDigest` would add.
        Template: string | null
    }

module Freshness =
    let Default: Freshness =
        { Etag = null
          StrongEtag = null
          LastModified = ValueNone
          Public = false
          Template = null }

    /// A weak validator, as `fresh_when(record)` makes.
    let OfEtag (validator: string) : Freshness = { Default with Etag = validator }

module private CtxHelpers =
    /// `request.fresh?(response)` with `strict_freshness` (the 8.0 default), which `fresh_when` and
    /// `Rack::ConditionalGet` both go by here: an `If-None-Match` list naming the ETag (or `*`), or
    /// else an `If-Modified-Since` no earlier than `Last-Modified`. (Rack's own check wants the whole
    /// `If-None-Match` to equal the ETag.)
    let isFresh (request: Request) (etag: string | null) (lastModified: string | null) : bool =
        match request.Header Hdr.IfNoneMatch with
        | null ->
            match request.Header Hdr.IfModifiedSince with
            | null -> false
            | header ->
                match KitClock.parseHttpdate header with
                | None -> false
                | Some since ->
                    match lastModified with
                    | null -> false
                    | lm ->
                        match KitClock.parseHttpdate lm with
                        | Some lastModified -> since >= lastModified
                        | None -> false
        | ifNoneMatch ->
            match etag with
            | null -> false
            | etag ->
                let mutable fresh = false
                for candidate in ifNoneMatch.Split ',' do
                    let v = candidate.Trim()
                    if String.Equals(v, etag, StringComparison.Ordinal) || v = "*" then fresh <- true
                fresh

    let private digestOf (hash: ReadOnlySpan<byte>) : BodyDigest =
        { A = Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(hash.Slice(0, 8))
          B = Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(hash.Slice(8, 8))
          C = Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(hash.Slice(16, 8))
          D = Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(hash.Slice(24, 8)) }

    /// The hex of `Rack::ETag`'s digest of a non-empty body. A page of cached fragments hashes its
    /// parts' digests rather than the whole body; another body keeps its SHA-256 for the gzip cache.
    let bodyEtag (response: Response) : string | null =
        let ofMemory (memory: ReadOnlyMemory<byte>) : string | null =
            if memory.Length = 0 then
                null
            else
                let hash = Array.zeroCreate<byte> 32
                SHA256.HashData(memory.Span, Span<byte>(hash)) |> ignore
                response.BodyDigest <- ValueSome(digestOf (ReadOnlySpan<byte>(hash)))
                Convert.ToHexStringLower(ReadOnlySpan<byte>(hash, 0, 16))
        match response.Body with
        | Body.Parts parts -> parts.Etag()
        | Body.Bytes bytes -> ofMemory bytes
        | Body.Pooled pooled -> ofMemory pooled.Memory
        | _ -> null

    /// `Rack::ETag` (installed as `Rack::ETag, "no-cache"`): weak SHA-256 ETags for 200/201 bodies
    /// without validators, and a default `Cache-Control`. A Live response's body is a
    /// `Live::Buffer`, which doesn't respond to `to_ary`, so it's never digested: whatever such a
    /// controller renders goes out with `no-cache` and no ETag.
    let rackEtag (response: Response) (digestible: bool) : unit =
        let mutable digested = false
        let skip =
            not digestible
            || response.Headers.Contains Hdr.ETag
            || response.Headers.Contains Hdr.LastModified
        if (response.Status = 200 || response.Status = 201) && not skip then
            match bodyEtag response with
            | null -> ()
            | hex ->
                let etag = String.Concat("W/\"", hex.AsSpan(0, 32), "\"")
                response.Headers.Insert(Hdr.ETag, etag)
                digested <- true
        if not (response.Headers.Contains Hdr.CacheControl) then
            response.Headers.Insert(Hdr.CacheControl, (if digested then "max-age=0, private, must-revalidate" else "no-cache"))

    /// The host of an absolute or protocol-relative URL, null for paths.
    let urlHost (url: string) : string | null =
        let rest: string | null =
            if url.StartsWith("//", StringComparison.Ordinal) then
                url.Substring 2
            else
                match url.IndexOf("://", StringComparison.Ordinal) with
                | -1 -> null
                | at ->
                    let scheme = url.Substring(0, at)
                    let isSchemeChar (c: char) = Char.IsAsciiLetterOrDigit c || c = '+' || c = '-' || c = '.'
                    if scheme.Length = 0 || not (Seq.forall isSchemeChar scheme) then null else url.Substring(at + 3)
        match rest with
        | null -> null
        | rest ->
            let cut = rest.IndexOfAny [| '/'; '?'; '#' |]
            let authority = if cut < 0 then rest else rest.Substring(0, cut)
            let hostPort = authority.Substring(authority.LastIndexOf '@' + 1)
            if hostPort.StartsWith '[' then
                let v6 = hostPort.Substring 1
                let close = v6.IndexOf ']'
                "[" + (if close < 0 then v6 else v6.Substring(0, close)) + "]"
            else
                match hostPort.IndexOf ':' with
                | -1 -> hostPort
                | colon -> hostPort.Substring(0, colon)

    /// Rust's `{:?}` of a `&str`.
    let debugString (s: string) : string =
        let out = StringBuilder(s.Length + 2)
        out.Append '"' |> ignore
        for c in s do
            match c with
            | '"' -> out.Append "\\\"" |> ignore
            | '\\' -> out.Append "\\\\" |> ignore
            | '\n' -> out.Append "\\n" |> ignore
            | '\r' -> out.Append "\\r" |> ignore
            | '\t' -> out.Append "\\t" |> ignore
            | '\000' -> out.Append "\\0" |> ignore
            | c when c < ' ' || c = '\127' -> out.Append("\\u{").Append((int c).ToString "x").Append('}') |> ignore
            | c -> out.Append c |> ignore
        out.Append('"').ToString()

    /// Rust's `{:?}` of an `Option<&serde_json::Value>`.
    let rec debugValue (value: Value) : string =
        match value with
        | Value.Null -> "Null"
        | Value.Bool b -> if b then "Bool(true)" else "Bool(false)"
        | Value.Int i -> $"Number({i})"
        | Value.UInt u -> $"Number({u})"
        | Value.Float f -> $"Number({JsonNumber.toString (NFloat f)})"
        | Value.String s -> $"String({debugString s})"
        | Value.Array items -> "Array [" + String.Join(", ", items |> List.map debugValue) + "]"
        | Value.Object entries ->
            "Object {" + String.Join(", ", entries |> List.map (fun (k, v) -> $"{debugString k}: {debugValue v}")) + "}"

[<Sealed>]
type Ctx internal (kit: Kit, request: Request, pathParams: ParamMap, queryParams: ParamMap, requestParams: ParamMap, cookies: CookieJar) =
    let parameters =
        let merged = requestParams.Clone()
        merged.Merge queryParams
        merged.Merge pathParams
        merged

    // Most requests never look at the session, so it exists once they do.
    let mutable session: Session | null = null
    let mutable headers: HeaderMap = null
    let mutable flash: Flash | null = null
    let mutable extensions: Dictionary<Type, objnull> | null = null
    let mutable markedForSameOriginVerification = false
    let mutable formats: Result<Format list, Error> voption = ValueNone
    let mutable renderedFormat: Format voption = ValueNone
    let mutable live = false

    /// Response headers set before the response exists (e.g. `X-Version` in a before-action). Merged
    /// into the final response without overriding what it already sets.
    member _.Headers: HeaderMap =
        match headers with
        | null ->
            let created = HeaderMap()
            headers <- created
            created
        | existing -> existing

    /// `response.cache_control`, applied to the final response.
    member val CacheControl: CacheControl = CacheControl.Empty with get, set

    member _.Kit = kit

    member _.Request = request

    /// `params`: body params, then query params, then path params, merged like Rails.
    member _.Params = parameters

    member _.QueryParams = queryParams
    member _.RequestParams = requestParams
    member _.PathParams = pathParams
    member _.Cookies = cookies

    /// The application state given to `Kit`.
    member _.State<'S when 'S: not struct>() : 'S = kit.State<'S>()

    member _.Clock = kit.Clock

    member _.Now() : Timestamp = kit.Clock.Now()

    // --- Current attributes ----------------------------------------------------------------

    /// Store a per-request value such as the current user or session (`Current.user = ...`).
    member _.SetCurrent<'T>(value: 'T) : unit =
        match extensions with
        | null ->
            let created = Dictionary<Type, objnull>()
            extensions <- created
            created[typeof<'T>] <- box value
        | map -> map[typeof<'T>] <- box value

    member _.Current<'T>() : 'T voption =
        match extensions with
        | null -> ValueNone
        | map ->
            match map.TryGetValue typeof<'T> with
            | true, value -> ValueSome(unbox<'T> value)
            | _ -> ValueNone

    member _.TakeCurrent<'T>() : 'T voption =
        match extensions with
        | null -> ValueNone
        | map ->
            match map.TryGetValue typeof<'T> with
            | true, value ->
                map.Remove typeof<'T> |> ignore
                ValueSome(unbox<'T> value)
            | _ -> ValueNone

    // --- Params ------------------------------------------------------------------------------

    member _.Param(key: string) : Param voption = parameters.Get key

    member _.ParamStr(key: string) : string | null = parameters.Str key

    /// `wrap_parameters format: [:json]`: for a JSON request, nest the body params under `key`
    /// unless it's already there. `include` is the model's attribute names when it has a model.
    member _.WrapParameters(key: string, ``include``: string list voption) : unit =
        let isJson =
            match Format.contentMimeType request.ContentType with
            | Ok(ValueSome f) -> f = Format.Json
            | _ -> false
        if isJson && not (parameters.ContainsKey key) then
            let wrapped = ParamMap()
            for struct (k, v) in requestParams.Iter do
                let keep =
                    match ``include`` with
                    | ValueSome names -> List.contains k names
                    | ValueNone -> not (k = "authenticity_token" || k = "_method" || k = "utf8")
                if keep then wrapped.Insert(k, v)
            parameters.Insert(key, Param.Hash(wrapped.Clone()))
            requestParams.Insert(key, Param.Hash wrapped)

    // --- Session and flash ---------------------------------------------------------------------

    member private _.SessionObject: Session =
        match session with
        | null ->
            let created = Session(kit.Config.Session)
            session <- created
            created
        | existing -> existing

    /// `session`, loaded from the cookie on first use.
    member this.Session() : Session = this.SessionObject.Load cookies

    /// `reset_session`: new session id, no data, no flash.
    member this.ResetSession() : unit =
        this.SessionObject.Reset()
        flash <- null

    /// `flash`, loaded from the session on first use.
    member this.Flash() : Flash =
        match flash with
        | null ->
            let stored = this.Session().Get "flash"
            let loaded = Flash.FromSessionValue stored
            flash <- loaded
            loaded
        | f -> f

    // --- Formats -------------------------------------------------------------------------------

    member private _.NegotiationInput() : Format.NegotiationInput =
        { FormatParam = parameters.Str "format"
          Accept = request.Header Hdr.Accept
          ContentType = request.ContentType
          Path = request.Path
          Xhr = request.IsXhr }

    /// `request.formats`; an invalid `Accept` header is a 406 like Rails' `InvalidType`.
    member this.Formats() : Result<Format list, Error> =
        match formats with
        | ValueSome cached -> cached
        | ValueNone ->
            let computed =
                match Format.formats (this.NegotiationInput()) with
                | Ok f -> Ok f
                | Error _ -> Error UnknownFormat
            formats <- ValueSome computed
            computed

    /// `_set_vary_header`, which every `render` runs (not `head` or `redirect_to`): `Vary: Accept`
    /// when the format came from the `Accept` header, unless the response already varies.
    member this.SetVaryHeader(response: Response) : Response =
        if response.Headers.Contains Hdr.Vary || not (Format.shouldApplyVaryHeader (this.NegotiationInput())) then
            response
        else
            response.Header(Hdr.Vary, "Accept")

    /// `request.format`: the first format; ValueNone is Rails' `Mime::NullType`.
    member this.Format() : Result<Format voption, Error> =
        this.Formats() |> Result.map (fun f -> List.tryHead f |> ValueOption.ofOption)

    /// `respond_to do |format| ... end`: pick the first format the client accepts among
    /// `offered` (in the order the block declares them), or 406 via `UnknownFormat`.
    member this.RespondTo(offered: Format list) : Result<Format, Error> =
        match this.Formats() with
        | Error e -> Error e
        | Ok accepted ->
            match Format.negotiate accepted offered with
            | ValueNone -> Error UnknownFormat
            | ValueSome chosen ->
                let chosen =
                    if chosen = Format.All then
                        (match offered with
                         | first :: _ -> first
                         | [] -> Format.Html)
                    else
                        chosen
                renderedFormat <- ValueSome chosen
                Ok chosen

    /// The format a render will use: the `RespondTo` choice, else the first request format.
    member this.RenderedFormat() : Format =
        match renderedFormat with
        | ValueSome format -> format
        | ValueNone ->
            match this.Formats() with
            | Ok(format :: _) when format <> Format.All -> format
            | _ -> Format.Html

    /// `turbo_frame_request?`
    member this.IsTurboFrameRequest: bool =
        match this.TurboFrameRequestId with
        | null -> false
        | id -> not (String.IsNullOrWhiteSpace id)

    member _.TurboFrameRequestId: string | null = request.Header Hdr.TurboFrame

    // --- Rendering -----------------------------------------------------------------------------

    member private this.RenderBody(status: int, contentType: string, body: Body) : Response =
        let response = Response(status).ContentType contentType
        response.Body <- body
        this.SetVaryHeader response

    /// `render html:` / a template, as `text/html; charset=utf-8`.
    member this.RenderHtml(status: int, html: ReadOnlyMemory<byte>) : Response =
        this.RenderBody(status, Response.HtmlUtf8, Body.Bytes html)

    member this.RenderHtml(status: int, html: string) : Response =
        this.RenderHtml(status, ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes html))

    member this.RenderHtml(status: int, html: PooledBytes) : Response =
        this.RenderBody(status, Response.HtmlUtf8, Body.Pooled html)

    /// `render` with an explicit content type (`render json:`, `send_data`, ...).
    member this.RenderAs(status: int, contentType: string, body: ReadOnlyMemory<byte>) : Response =
        this.RenderBody(status, contentType, Body.Bytes body)

    member this.RenderAs(status: int, contentType: string, body: string) : Response =
        this.RenderAs(status, contentType, ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes body))

    member this.RenderAs(status: int, contentType: string, body: PooledBytes) : Response =
        this.RenderBody(status, contentType, Body.Pooled body)

    member this.Html(html: ReadOnlyMemory<byte>) : Response = this.RenderHtml(Status.Ok, html)
    member this.Html(html: string) : Response = this.RenderHtml(Status.Ok, html)
    member this.Html(html: PooledBytes) : Response = this.RenderHtml(Status.Ok, html)

    /// A rendered template, labelled with *the template's* format: Rails sets `rendered_format`
    /// from the template the lookup found, not from the request, so an `.html.erb`-only action
    /// answers `text/html` even when the `Accept` header prefers `text/vnd.turbo-stream.html`
    /// (every Turbo form submission, and the redirect fetch follows). Pick the template with
    /// `RespondTo` (the implicit render's lookup) when an action has several.
    member this.Render(status: int, template: Format, body: ReadOnlyMemory<byte>) : Response =
        renderedFormat <- ValueSome template
        this.RenderAs(status, $"{template.String}; charset=utf-8", body)

    member this.Render(status: int, template: Format, body: string) : Response =
        this.Render(status, template, ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes body))

    member this.Render(status: int, template: Format, body: PooledBytes) : Response =
        renderedFormat <- ValueSome template
        this.RenderAs(status, $"{template.String}; charset=utf-8", body)

    /// `Render` for a page the response cache holds in parts (`IPageParts`): its ETag and gzip reuse
    /// the parts' digests and compressed pieces, and the body is never joined.
    member this.RenderParts(status: int, template: Format, parts: IPageParts) : Response =
        renderedFormat <- ValueSome template
        this.RenderBody(status, $"{template.String}; charset=utf-8", Body.Parts parts)

    /// `render turbo_stream:` (`text/vnd.turbo-stream.html`).
    member this.TurboStream(html: ReadOnlyMemory<byte>) : Response =
        this.RenderAs(Status.Ok, Response.TurboStreamUtf8, html)

    member this.TurboStream(html: string) : Response = this.RenderAs(Status.Ok, Response.TurboStreamUtf8, html)
    member this.TurboStream(html: PooledBytes) : Response = this.RenderAs(Status.Ok, Response.TurboStreamUtf8, html)

    /// `render json:`, from a value.
    member this.Json(status: int, value: Value) : Response =
        this.RenderAs(status, Response.JsonUtf8, Json.generate value)

    /// `render json:`, written straight into a pooled buffer.
    member this.JsonWith(status: int, write: Utf8JsonWriter -> unit) : Response =
        let buffer = new PooledBufferWriter()
        try
            use writer = new Utf8JsonWriter(buffer)
            write writer
            writer.Flush()
            this.RenderAs(status, Response.JsonUtf8, buffer.ToPooledBytes())
        finally
            (buffer :> IDisposable).Dispose()

    /// `head status`: no body; a bare content type (no charset) unless the status has no content.
    member this.Head(status: int) : Response =
        let response = Response(status)
        if not (Status.hasNoContent status) then
            response.ContentType(this.RenderedFormat().String) |> ignore
        response

    /// `head status, location: url`
    member this.HeadWithLocation(status: int, location: string) : Result<Response, Error> =
        match this.ComputeLocation location with
        | Error e -> Error e
        | Ok location -> Ok(this.Head(status).Header(Hdr.Location, location))

    /// `redirect_to location` (302).
    member this.RedirectTo(location: string) : Result<Response, Error> = this.RedirectToWith(location, Redirect.Default)

    /// `redirect_to location, status:, notice:, alert:, allow_other_host:`
    member this.RedirectToWith(location: string, options: Redirect) : Result<Response, Error> =
        match options.Notice with
        | null -> ()
        | notice -> this.Flash().SetNotice notice
        match options.Alert with
        | null -> ()
        | alert -> this.Flash().SetAlert alert
        match this.ComputeLocation location with
        | Error e -> Error e
        | Ok location ->
            if location |> Seq.exists (fun c -> (c >= '\000' && c <= '\008') || (c >= '\010' && c <= '\031')) then
                Error(UnsafeRedirect $"The redirect URL {location} contains illegal characters")
            elif not options.AllowOtherHost && not (this.UrlHostAllowed location) then
                Error(UnsafeRedirect $"Unsafe redirect to {CtxHelpers.debugString location}")
            else
                Ok(
                    Response(match options.Status with ValueSome s -> s | ValueNone -> Status.Found)
                        .ContentType(Response.HtmlUtf8)
                        .Header(Hdr.Location, location)
                )

    /// `redirect_back_or_to fallback`: the referer when it's on this host.
    member this.RedirectBackOrTo(fallback: string) : Result<Response, Error> =
        match request.Referer with
        | null -> this.RedirectTo fallback
        | referer -> if this.UrlHostAllowed referer then this.RedirectTo referer else this.RedirectTo fallback

    /// `_compute_redirect_to_location` for strings: absolute URLs pass through, paths get the
    /// request's protocol and host, path-relative URLs raise (`action_on_path_relative_redirect`).
    member private _.ComputeLocation(location: string) : Result<string, Error> =
        let isAbsolute =
            location.StartsWith("//", StringComparison.Ordinal)
            || (location.Length > 0
                && Char.IsAsciiLetter location[0]
                && (match location.IndexOf ':' with
                    | -1 -> false
                    | colon ->
                        location.Substring(0, colon)
                        |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '-' || c = '+' || c = '.')))
        let url =
            if isAbsolute then
                Ok location
            elif location.Length > 0 && not (location.StartsWith '/') && not (location.StartsWith '?') then
                Error(UnsafeRedirect $"Path relative URL redirect detected: {CtxHelpers.debugString location}")
            else
                Ok(request.Protocol + request.HostWithPort + location)
        url |> Result.map (fun url -> url.Replace("\000", "").Replace("\r", "").Replace("\n", ""))

    /// `_url_host_allowed?`
    member private _.UrlHostAllowed(url: string) : bool =
        match CtxHelpers.urlHost url with
        | null -> url.StartsWith '/' && not (url.StartsWith("//", StringComparison.Ordinal))
        | host -> String.Equals(host, request.Host, StringComparison.OrdinalIgnoreCase)

    /// An absolute URL for `path` on this request's host (what `*_url` helpers produce with
    /// `default_url_options` from `SetCurrentRequest`).
    member _.UrlFor(path: string) : string = request.Protocol + request.HostWithPort + path

    /// `send_file path, type:, disposition:, filename:`
    member _.SendFile(path: string, options: SendOptions) : Result<Response, Error> =
        let info = FileInfo path
        if not info.Exists then
            Error(Internal(Exception $"Cannot read file {path}"))
        else
            let options =
                match options.Filename with
                | null -> { options with Filename = Path.GetFileName path }
                | _ -> options
            Ok(Response.send options (Body.File { Path = path; Offset = 0L; Len = info.Length }))

    /// `send_data data, type:, disposition:, filename:`
    member _.SendData(data: ReadOnlyMemory<byte>, options: SendOptions) : Response =
        Response.send options (Body.Bytes data)

    member this.SendData(data: string, options: SendOptions) : Response =
        this.SendData(ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes data), options)

    // --- Conditional GET -----------------------------------------------------------------------

    member private this.IsFresh() : bool =
        CtxHelpers.isFresh request (this.Headers.Get Hdr.ETag) (this.Headers.Get Hdr.LastModified)

    /// `[validator, *etaggers]` digested like `ActiveSupport::Digest.hexdigest(expand_cache_key(...))`.
    /// The etaggers are turbo-rails' frame etagger, `ETagWithTemplateDigest` and `ETagWithFlash`.
    member private this.CombineEtags(validator: string | null, freshness: Freshness) : string =
        let parts = ResizeArray<string>()
        match validator with
        | null -> ()
        | v -> parts.Add v
        if this.IsTurboFrameRequest then parts.Add "frame"
        match freshness.Template with
        | null -> ()
        | template -> parts.Add template
        let flash = this.Flash()
        if not flash.IsEmpty then
            let flashes =
                flash.Keys
                |> List.map (fun k ->
                    let shown =
                        match flash.Get k with
                        | ValueSome v -> $"Some({CtxHelpers.debugValue v})"
                        | ValueNone -> "None"
                    $"{k}={shown}")
            parts.Add(String.Join("&", flashes))
        Convert.ToHexStringLower(ReadOnlySpan<byte>(SHA256.HashData(Encoding.UTF8.GetBytes(String.Join("/", parts))), 0, 16))

    /// Set a response header ahead of the response (`response.headers[...] = ...`), as Puma would
    /// write the value (see `HeaderMap.SetPumaValue`).
    member this.SetHeader(name: string, value: string) : unit = this.Headers.SetPumaValue(name, value)

    /// `fresh_when`: sets ETag/Last-Modified and returns the 304 when the request is fresh.
    member this.FreshWhen(freshness: Freshness) : Response voption =
        this.CacheControl <- { this.CacheControl with NoStore = false }
        let etagged = not (isNull freshness.StrongEtag) || not (isNull freshness.Etag) || not (isNull freshness.Template)
        if etagged then
            let validator, weak =
                match freshness.StrongEtag with
                | null -> freshness.Etag, true
                | strong -> strong, false
            let etag = this.CombineEtags(validator, freshness)
            this.SetHeader(Hdr.ETag, (if weak then $"W/\"{etag}\"" else $"\"{etag}\""))
        match freshness.LastModified with
        | ValueSome lastModified -> this.SetHeader(Hdr.LastModified, KitClock.httpdate lastModified)
        | ValueNone -> ()
        if freshness.Public then this.CacheControl <- { this.CacheControl with Public = true }
        if this.IsFresh() then ValueSome(this.Head Status.NotModified) else ValueNone

    /// `stale?`: the inverse of freshness, after setting the validators.
    member this.Stale(freshness: Freshness) : bool = (this.FreshWhen freshness).IsNone

    /// `expires_in seconds, public:, stale_while_revalidate:, ...`
    member this.ExpiresIn(seconds: uint64, options: ExpiresIn) : unit =
        this.CacheControl <-
            { this.CacheControl with
                NoStore = false
                MaxAge = ValueSome seconds
                Public = options.Public
                MustRevalidate = options.MustRevalidate
                StaleWhileRevalidate = options.StaleWhileRevalidate
                StaleIfError = options.StaleIfError
                Immutable = options.Immutable }
        if not (this.Headers.Contains Hdr.Date) then
            this.SetHeader(Hdr.Date, KitClock.httpdate (this.Now()))

    /// `expires_now`
    member this.ExpiresNow() : unit = this.CacheControl <- { CacheControl.Empty with NoCache = true }

    /// `no_store`
    member this.NoStore() : unit = this.CacheControl <- { CacheControl.Empty with NoStore = true }

    /// A controller that includes `ActionController::Live` (`ActiveStorage::Streaming` does):
    /// its `make_response!` builds the response with `Live::Response.new`, which skips
    /// `ActionDispatch::Response.create` and so `config.action_dispatch.default_headers`.
    member _.UseLiveResponse() : unit = live <- true

    // --- Forgery protection ---------------------------------------------------------------------

    member private _.ValidRequestOrigin() : Result<bool, Error> =
        if not kit.Config.ForgeryProtectionOriginCheck then
            Ok true
        else
            match request.Origin with
            | "null" -> Error(InvalidAuthenticityToken "The browser returned a 'null' origin")
            | null -> Ok true
            | origin -> Ok(String.Equals(origin, request.BaseUrl, StringComparison.Ordinal))

    /// The `verify_authenticity_token` before-action, by `Sec-Fetch-Site` rather than tokens (Rails
    /// main's `protect_from_forgery using: :header_only`): pages carry no per-request token, so they
    /// render the same until what they show changes. Browsers send the header on every request to a
    /// secure origin; without it (an old browser, or plain HTTP where browsers don't send it) a
    /// write is only allowed when neither the request nor the app uses SSL, where the
    /// `SameSite=Lax` session cookie and the `Origin` check are the protection.
    member this.VerifyAuthenticityToken() : Result<unit, Error> =
        markedForSameOriginVerification <- request.IsGet
        if request.IsGet || request.IsHead then
            Ok()
        else
            match this.ValidRequestOrigin() with
            | Error e -> Error e
            | Ok false ->
                let origin = match request.Origin with null -> "" | o -> o
                Error(
                    InvalidAuthenticityToken
                        $"HTTP Origin header ({origin}) didn't match request.base_url ({request.BaseUrl})"
                )
            | Ok true ->
                match request.Header Hdr.SecFetchSite with
                | "same-origin"
                | "same-site" -> Ok()
                | null when not request.IsSsl && not kit.Config.ForceSsl -> Ok()
                | "cross-site" -> Error(InvalidAuthenticityToken "Sec-Fetch-Site header (cross-site) indicates a cross-site request")
                | other ->
                    let shown =
                        match other with
                        | null -> "None"
                        | o -> $"Some({CtxHelpers.debugString o})"
                    Error(InvalidAuthenticityToken $"Sec-Fetch-Site header is missing or invalid ({shown})")

    // --- Finishing -----------------------------------------------------------------------------

    member private _.DefaultHeaders: (string * string)[] = if live then Array.empty else kit.Config.DefaultHeaders

    /// `verify_same_origin_request`: a GET that renders JavaScript for a non-XHR request is a
    /// cross-origin `<script>` embed.
    member private _.VerifySameOriginRequest(response: Response) : Result<unit, Error> =
        let javascript =
            match response.GetHeader Hdr.ContentType with
            | null -> false
            | ct -> ct.StartsWith("text/javascript", StringComparison.Ordinal) || ct.StartsWith("application/javascript", StringComparison.Ordinal)
        if markedForSameOriginVerification && javascript && not request.IsXhr then
            Error InvalidCrossOriginRequest
        else
            Ok()

    /// `commit_flash`, `commit_session`, then the cookie jar's `write`.
    member private this.Commit(response: Response) : Result<unit, Error> =
        match flash with
        | null -> ()
        | pending ->
            flash <- null
            let hasFlashKey = this.Session().ContainsKey "flash"
            if not pending.IsEmpty || hasFlashKey then
                match pending.ToSessionValue() with
                | ValueSome value -> this.Session().Insert("flash", value)
                | ValueNone -> this.Session().Insert("flash", Value.Null)
        let committed =
            match session with
            | null -> Ok()
            | session ->
                if session.IsLoaded && session.ContainsKey "flash" && (session.Get "flash").IsNone then
                    session.Remove "flash" |> ignore
                session.Commit(cookies, this.Now())
        match committed with
        | Error e -> Error e
        | Ok() ->
            if cookies.HasChanges then
                for cookie in cookies.SetCookieHeaders(request.IsSsl, request.Host) do
                    response.Headers.Append(Hdr.SetCookie, cookie)
            Ok()

    /// `handle_conditional_get!` and `merge_and_normalize_cache_control!`.
    member private this.ApplyCacheHeaders(response: Response) : unit =
        if not (response.Headers.Contains Hdr.CacheControl) then
            let cacheControl =
                if this.CacheControl.IsEmpty
                   && (response.Headers.Contains Hdr.ETag || response.Headers.Contains Hdr.LastModified) then
                    { CacheControl.Empty with
                        MaxAge = ValueSome 0UL
                        MustRevalidate = true }
                else
                    this.CacheControl
            match cacheControl.ToHeader() with
            | null -> ()
            | value -> response.Headers.Insert(Hdr.CacheControl, value)

    /// `Rack::ConditionalGet`
    member private _.ConditionalGet(response: Response) : unit =
        if (request.IsGet || request.IsHead) && response.Status = Status.Ok then
            if CtxHelpers.isFresh request (response.GetHeader Hdr.ETag) (response.GetHeader Hdr.LastModified) then
                response.Status <- Status.NotModified
                response.Headers.Remove Hdr.ContentType
                response.Headers.Remove Hdr.ContentLength
                match response.Body with
                | Body.Pooled pooled -> pooled.Release()
                | _ -> ()
                response.Body <- Body.Empty

    /// What `ShowExceptions` + `PublicExceptions` render for an error raised in the action. The
    /// log line has the error's sources too (`{:#}`: "outer: cause: ...").
    member private this.ErrorResponse(error: Error) : Response =
        let logger = kit.Logger
        let status = Error.status error
        let level = if status >= 500 then LogLevel.Error else LogLevel.Information
        if logger.IsEnabled level then
            if status >= 500 then
                logger.LogError("request failed error={Error} path=\"{Path}\"", Error.display error, request.Path)
            else
                logger.LogInformation("request rejected error={Error} path=\"{Path}\"", Error.display error, request.Path)
        let formats = match this.Formats() with Ok f -> f | Error _ -> []
        Exceptions.render kit.ErrorPages status (List.tryHead formats |> ValueOption.ofOption) request.IsHead

    /// Turn the action's result into the response Rails would send: halts and errors resolved,
    /// flash and session committed into cookies, cache headers, ETag and 304, HEAD bodies dropped.
    member internal this.Finish(result: Result<Response, Error>) : Response =
        let responded =
            match result with
            | Ok response -> ValueSome response
            | Error(Halt response) -> ValueSome response
            | Error _ -> ValueNone
        match responded with
        | ValueNone ->
            match result with
            | Error error -> this.ErrorResponse error
            | Ok _ -> failwith "unreachable"
        | ValueSome response ->
            match headers with
            | null -> ()
            | preset ->
                for i in 0 .. preset.Count - 1 do
                    let name = preset.NameAt i
                    if not (response.Headers.Contains name) then
                        response.Headers.Append(name, preset.ValueAt i)
            match this.VerifySameOriginRequest response with
            | Error error -> this.ErrorResponse error
            | Ok() ->
                match this.Commit response with
                | Error error -> this.ErrorResponse error
                | Ok() ->
                    this.ApplyCacheHeaders response
                    for (name, value) in this.DefaultHeaders do
                        if not (response.Headers.Contains name) then response.Headers.Append(name, value)
                    if not (response.Headers.Contains Hdr.ContentType) && not (Status.hasNoContent response.Status) then
                        response.Headers.Append(Hdr.ContentType, Response.HtmlUtf8)
                    CtxHelpers.rackEtag response (not live)
                    this.ConditionalGet response
                    response
