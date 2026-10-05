// Port of rust/crates/assets/src/serve.rs
/// ActionDispatch::Static (actionpack/lib/action_dispatch/middleware/static.rb) over Rack::Files,
/// serving the embedded reference/public plus the precompiled public/assets, with the headers
/// from reference/config/environments/production.rb (`public_file_server.headers`).
module Campfire.Assets.Serve

open System
open System.IO
open System.Text
open System.Text.Unicode
open Campfire.Ruby

/// The last `config.public_file_server.headers` assignment in production.rb wins.
[<Literal>]
let private CacheControl = "public, max-age=2592000"

/// Rack::Files::MULTIPART_BOUNDARY
[<Literal>]
let private MultipartBoundary = "AaB03x"

/// The parts of a request ActionDispatch::Static looks at. `Path` is the raw (still
/// percent-encoded) request path, without the query string.
type StaticRequest =
    { Method: string
      Path: string
      AcceptEncoding: string option
      Range: string option
      IfModifiedSince: string option }

module StaticRequest =
    /// A request with only a method and a path, as Rust's `..Default::default()`.
    let create (method: string) (path: string) : StaticRequest =
        { Method = method; Path = path; AcceptEncoding = None; Range = None; IfModifiedSince = None }

/// `Headers` are in the order Rack builds them. Names are as Rack spells them ("Cache-Control"
/// comes from the app's config); HTTP/1.1 and HTTP/2 treat them case-insensitively. `Body` is
/// empty for HEAD requests, and views into the embedded bytes are never copied.
type StaticResponse =
    { Status: int
      Headers: (string * string) list
      Body: ReadOnlyMemory<byte> }

module StaticResponse =
    let header (name: string) (response: StaticResponse) : string option =
        response.Headers
        |> List.tryFind (fun (n, _) -> String.Equals(n, name, StringComparison.OrdinalIgnoreCase))
        |> Option.map snd

type private ContentHeaders = (string * string) list

/// FileHandler's compressible_content_types: /\A(?:text\/|application\/javascript|image\/svg\+xml)/
let private compressible (contentType: string) : bool =
    contentType.StartsWith("text/", StringComparison.Ordinal)
    || contentType.StartsWith("application/javascript", StringComparison.Ordinal)
    || contentType.StartsWith("image/svg+xml", StringComparison.Ordinal)

let private isWordByte (b: byte) : bool =
    (b >= byte 'a' && b <= byte 'z') || (b >= byte 'A' && b <= byte 'Z') || (b >= byte '0' && b <= byte '9') || b = byte '_'

/// `accept_encoding.any? { |enc, _| /\b#{encoding}\b/i.match?(enc) }` over Rack's parsed header.
let private accepts (acceptEncoding: string) (encoding: string) : bool =
    acceptEncoding.Split ','
    |> Array.exists (fun part ->
        let semicolon = part.IndexOf ';'
        let value = (if semicolon < 0 then part else part.Substring(0, semicolon)).Trim().ToLowerInvariant()
        let bytes = Encoding.UTF8.GetBytes value
        let mutable from = 0
        let mutable found = false
        while not found && from <= value.Length - encoding.Length do
            match value.IndexOf(encoding, from, StringComparison.Ordinal) with
            | -1 -> from <- value.Length
            | i ->
                let before = i = 0 || not (isWordByte bytes[i - 1])
                let after = i + encoding.Length >= bytes.Length || not (isWordByte bytes[i + encoding.Length])
                if before && after then found <- true else from <- i + 1
        found)

/// File.extname on bytes.
let private fileExtname (path: byte[]) : byte[] =
    let slash = Array.FindLastIndex(path, fun b -> b = byte '/')
    let b = if slash < 0 then path else path[slash + 1 ..]
    let start = b |> Array.tryFindIndex (fun c -> c <> byte '.') |> Option.defaultValue b.Length
    let trimmed = b[start..]
    match Array.FindLastIndex(trimmed, fun c -> c = byte '.') with
    | -1 -> [||]
    | dot -> trimmed[dot..]

/// Rack::Mime.mime_type(ext, nil) for the extensions a Campfire deploy serves (and the other
/// common web ones); lookups are case-insensitive.
let private mimeType (extname: byte[]) : string option =
    let lower = String(Encoding.UTF8.GetString(extname) |> Seq.map (fun c -> if c >= 'A' && c <= 'Z' then char (int c + 32) else c) |> Seq.toArray)
    match lower with
    | ".avif" -> Some "image/avif"
    | ".css" -> Some "text/css"
    | ".csv" -> Some "text/csv"
    | ".gif" -> Some "image/gif"
    | ".gz" -> Some "application/x-gzip"
    | ".htm"
    | ".html" -> Some "text/html"
    | ".ico" -> Some "image/vnd.microsoft.icon"
    | ".jpeg"
    | ".jpg" -> Some "image/jpeg"
    | ".js"
    | ".mjs" -> Some "text/javascript"
    | ".json" -> Some "application/json"
    | ".m4a" -> Some "audio/mp4a-latm"
    | ".mp3" -> Some "audio/mpeg"
    | ".mp4" -> Some "video/mp4"
    | ".ogg" -> Some "application/ogg"
    | ".otf" -> Some "font/otf"
    | ".pdf" -> Some "application/pdf"
    | ".png" -> Some "image/png"
    | ".svg" -> Some "image/svg+xml"
    | ".ttf" -> Some "font/ttf"
    | ".txt" -> Some "text/plain"
    | ".wav" -> Some "audio/x-wav"
    | ".webm" -> Some "video/webm"
    | ".webp" -> Some "image/webp"
    | ".woff" -> Some "font/woff"
    | ".woff2" -> Some "font/woff2"
    | ".xml" -> Some "application/xml"
    | ".zip" -> Some "application/zip"
    | _ -> None

let private hexValue (b: byte) : int =
    match char b with
    | c when c >= '0' && c <= '9' -> int c - int '0'
    | c when c >= 'a' && c <= 'f' -> int c - int 'a' + 10
    | c when c >= 'A' && c <= 'F' -> int c - int 'A' + 10
    | _ -> -1

/// URI::RFC2396_Parser#unescape: %XX sequences become bytes, anything else is kept.
let private unescapePath (path: string) : byte[] =
    let bytes = Encoding.UTF8.GetBytes path
    let out = ResizeArray<byte>(bytes.Length)
    let mutable i = 0
    while i < bytes.Length do
        if bytes[i] = byte '%' && i + 2 < bytes.Length && hexValue bytes[i + 1] >= 0 && hexValue bytes[i + 2] >= 0 then
            out.Add(byte (hexValue bytes[i + 1] * 16 + hexValue bytes[i + 2]))
            i <- i + 3
        else
            out.Add bytes[i]
            i <- i + 1
    out.ToArray()

/// FileHandler#clean_path: chomp("/"), percent-decode, reject NUL, then
/// Rack::Utils.clean_path_info. Works on bytes, as Rack does with a binary PATH_INFO.
let private cleanPath (pathInfo: string) : byte[] option =
    let path = unescapePath (if pathInfo.EndsWith '/' then pathInfo.Substring(0, pathInfo.Length - 1) else pathInfo)
    if Array.contains 0uy path then
        None
    else
        let parts = ResizeArray<byte[]>()
        let mutable start = 0
        for i in 0 .. path.Length do
            if i = path.Length || path[i] = byte '/' then
                parts.Add path[start .. i - 1]
                start <- i + 1
        let clean = ResizeArray<byte[]>()
        for part in parts do
            match Encoding.Latin1.GetString part with
            | ""
            | "." -> ()
            | ".." -> if clean.Count > 0 then clean.RemoveAt(clean.Count - 1)
            | _ -> clean.Add part
        let cleaned =
            clean
            |> Seq.mapi (fun i part -> if i = 0 then part else Array.append [| byte '/' |] part)
            |> Array.concat
        // The leading "/" is added back when the path began with an empty part.
        Some(if parts[0].Length = 0 then Array.append [| byte '/' |] cleaned else cleaned)

let private file (path: byte[]) : ReadOnlyMemory<byte> voption =
    if Utf8.IsValid path then Embedded.file (Encoding.UTF8.GetString path) else ValueNone

let private tryFiles (path: byte[]) (contentType: string) (acceptEncoding: string) : (ReadOnlyMemory<byte> * ContentHeaders) option =
    let headers = [ "content-type", contentType ]

    if not (compressible contentType) then
        match file path with
        | ValueSome body -> Some(body, headers)
        | ValueNone -> None
    else
        let rec encodings (headers: ContentHeaders) (candidates: (string * string) list) =
            match candidates with
            | [] ->
                match file path with
                | ValueSome body -> Some(body, headers)
                | ValueNone -> None
            | (encoding, extension) :: rest ->
                match file (Array.append path (Encoding.ASCII.GetBytes extension)) with
                | ValueSome body ->
                    let headers = headers @ [ "vary", "accept-encoding" ]
                    if accepts acceptEncoding encoding then Some(body, headers @ [ "content-encoding", encoding ])
                    else encodings headers rest
                | ValueNone -> encodings headers rest
        encodings headers [ "br", ".br"; "gzip", ".gz" ]

let private findFile (pathInfo: string) (acceptEncoding: string) : (ReadOnlyMemory<byte> * ContentHeaders) option =
    cleanPath pathInfo
    |> Option.bind (fun path ->
        let extname = fileExtname path
        let contentType = mimeType extname

        let candidates =
            [ path, defaultArg contentType "text/plain"
              // Only paths without a resolvable extension also try .html and /index.html.
              if contentType.IsNone && extname <> ".html"B then
                  Array.append path ".html"B, "text/html"
                  Array.append path "/index.html"B, "text/html" ]

        candidates |> List.tryPick (fun (path, contentType) -> tryFiles path contentType acceptEncoding))

/// Sets `name` to `value` in place when present, else appends it.
let private setHeader (headers: ResizeArray<string * string>) (name: string) (value: string) : unit =
    match headers.FindIndex(fun (n, _) -> n = name) with
    | -1 -> headers.Add((name, value))
    | i -> headers[i] <- (name, value)

/// Rack::Files#serving, then FileHandler#serve's `headers.update(content_headers)`.
let private serveFile (request: StaticRequest) (file: ReadOnlyMemory<byte>) (contentHeaders: ContentHeaders) : StaticResponse =
    let lastModified = Embedded.builtAt
    if request.IfModifiedSince = Some lastModified then
        { Status = 304; Headers = []; Body = ReadOnlyMemory.Empty }
    else
        let size = file.Length
        let headers =
            ResizeArray<string * string>(
                [ "last-modified", lastModified
                  "content-type", "" // replaced by the content headers below
                  "Cache-Control", CacheControl ]
            )
        let mutable status = 200
        let mutable body = file

        // Within the file, so each end fits an int.
        let ranges =
            Rack.byteRanges request.Range (uint64 size)
            |> Option.map (List.map (fun (start, end') -> int start, int end'))

        match ranges with
        | None -> ()
        | Some [] ->
            let message = "Byte range unsatisfiable\n"
            headers.Clear()
            headers.AddRange
                [ "content-type", ""
                  "content-length", string message.Length
                  "x-cascade", "pass"
                  "content-range", $"bytes */{size}" ]
            status <- 416
            body <- ReadOnlyMemory(Encoding.UTF8.GetBytes message)
        | Some [ (start, end') ] ->
            headers.Add(("content-range", $"bytes {start}-{end'}/{size}"))
            status <- 206
            body <- file.Slice(start, end' - start + 1)
        | Some ranges ->
            let contentType = snd contentHeaders[0]
            use multipart = new MemoryStream()
            let write (s: string) = multipart.Write(Encoding.UTF8.GetBytes s)
            for start, end' in ranges do
                write $"\r\n--{MultipartBoundary}\r\ncontent-type: {contentType}\r\ncontent-range: bytes {start}-{end'}/{size}\r\n\r\n"
                multipart.Write(file.Span.Slice(start, end' - start + 1))
            write $"\r\n--{MultipartBoundary}--\r\n"
            headers[1] <- ("content-type", $"multipart/byteranges; boundary={MultipartBoundary}")
            status <- 206
            body <- ReadOnlyMemory(multipart.ToArray())

        if status <> 416 then headers.Add(("content-length", string body.Length))

        // A multipart content-type is kept only if Static doesn't override it; it always does.
        for name, value in contentHeaders do
            setHeader headers name value

        if request.Method = "HEAD" then body <- ReadOnlyMemory.Empty

        { Status = status; Headers = List.ofSeq headers; Body = body }

/// FileHandler#attempt: Some response when a public file matches a GET or HEAD request,
/// None to hand the request to the app.
let serve (request: StaticRequest) : StaticResponse option =
    if request.Method <> "GET" && request.Method <> "HEAD" then
        None
    else
        findFile request.Path (defaultArg request.AcceptEncoding "")
        |> Option.map (fun (body, contentHeaders) -> serveFile request body contentHeaders)
