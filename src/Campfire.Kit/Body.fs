// Port of rust/crates/kit/src/body.rs
//
// Reading request bodies into params the way `Rack::Request#form_pairs` and
// `ActionDispatch::Request#POST` do: JSON by content type, urlencoded forms (also for a POST
// with no content type), and multipart with file parts spooled to temp files.
//
// Where Rust reads multipart with the `multer` crate, `Multipart.fs` is a port of it (and of the `mime`
// and `httparse` code it leans on), behind the same limits.
namespace Campfire.Kit

open System
open System.Buffers
open System.Collections.Generic
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Campfire.Kit

/// The body as read: raw bytes (empty for multipart) and the params parsed from it.
type ParsedBody =
    { Raw: ReadOnlyMemory<byte>
      Params: Result<ParamMap, ParamError>
      /// The files a multipart body spooled to disk, deleted once the request is done
      /// (`Rack::TempfileReaper`).
      Files: UploadedFile[] }

module ParsedBody =
    let empty: ParsedBody =
        { Raw = ReadOnlyMemory.Empty
          Params = Ok(ParamMap())
          Files = Array.empty }

type BodyError =
    | TooLarge
    | Read of string

module BodyError =
    let status (error: BodyError) : int =
        match error with
        | TooLarge -> Status.PayloadTooLarge
        | Read _ -> Status.BadRequest

    let display (error: BodyError) : string =
        match error with
        | TooLarge -> "request body too large"
        | Read message -> $"error reading request body: {message}"

/// What a multipart part's `Content-Disposition` and `Content-Type` say, parsed like
/// `Rack::Multipart::Parser#consume_boundary`.
type internal Part =
    { Name: string | null
      Filename: string | null
      ContentType: string | null
      Head: string }

module RequestBody =
    /// `Rack::Utils.multipart_total_part_limit` / `multipart_file_limit`.
    [<Literal>]
    let MultipartPartLimit = 4096

    /// The most of a non-multipart body read into memory (forms are limited to 4 MB after that, as in
    /// Rack); more is a 413. Rails reads any size.
    [<Literal>]
    let MaxBufferedBody = 16777216

    [<Literal>]
    let MultipartFileLimit = 128

    /// The most a multipart body's text fields may hold together, since they're kept in memory
    /// (Rack's `BUFFERED_UPLOAD_BYTESIZE_LIMIT`). More is a 413. Files aren't counted.
    [<Literal>]
    let MultipartTextLimit = 16777216

    /// The largest multipart body, files included, when no smaller limit is configured (Rack's
    /// `PARSER_BYTESIZE_LIMIT`).
    [<Literal>]
    let MultipartBytesizeLimit = 10737418240L

    /// `Rack::Multipart::Parser#normalize_filename`: unescape when every `%` is a valid escape, then
    /// keep only the basename (browsers on Windows send full paths).
    let internal normalizeFilename (filename: string) : string =
        let bytes = Encoding.UTF8.GetBytes filename
        let isHex (b: byte) = (b >= byte '0' && b <= byte '9') || (b >= byte 'a' && b <= byte 'f') || (b >= byte 'A' && b <= byte 'F')
        let mutable allValid = true
        for i in 0 .. bytes.Length - 1 do
            if bytes[i] = byte '%' && not (i + 2 < bytes.Length && isHex bytes[i + 1] && isHex bytes[i + 2]) then
                allValid <- false
        let unescaped =
            if not allValid then
                filename
            else
                let decoded = ResizeArray<byte>(bytes.Length)
                let hexValue (b: byte) =
                    if b <= byte '9' then int b - int '0'
                    elif b >= byte 'a' then int b - int 'a' + 10
                    else int b - int 'A' + 10
                let mutable i = 0
                while i < bytes.Length do
                    if bytes[i] = byte '%' then
                        decoded.Add(byte (hexValue bytes[i + 1] * 16 + hexValue bytes[i + 2]))
                        i <- i + 3
                    else
                        decoded.Add bytes[i]
                        i <- i + 1
                Encoding.UTF8.GetString(decoded.ToArray())
        if unescaped.Length = 0 then
            unescaped
        else
            unescaped.Substring(unescaped.LastIndexOfAny [| '/'; '\\' |] + 1)

    let internal parseDisposition (value: string) : Part =
        let mutable name: string | null = null
        let mutable filename: string | null = null
        let mutable filenameStar: string | null = null
        let afterSemicolon (s: string) =
            match s.IndexOf ';' with
            | -1 -> ""
            | i -> s.Substring(i + 1)
        let mutable rest = afterSemicolon value
        let mutable go = true
        while go do
            match rest.IndexOf '=' with
            | -1 -> go <- false
            | eq ->
                let param = rest.Substring(0, eq).Trim().ToLowerInvariant()
                rest <- rest.Substring(eq + 1)
                let paramValue =
                    if rest.StartsWith '"' then
                        let quoted = rest.Substring 1
                        let out = StringBuilder()
                        let mutable ``end`` = quoted.Length
                        let mutable i = 0
                        let mutable closed = false
                        while not closed && i < quoted.Length do
                            match quoted[i] with
                            | '"' ->
                                ``end`` <- i + 1
                                closed <- true
                            | '\\' ->
                                i <- i + 1
                                if i < quoted.Length then
                                    match quoted[i] with
                                    | '"' -> out.Append '"' |> ignore
                                    // IE sends unescaped Windows paths in filenames; keep the backslash.
                                    | escaped when param = "filename" -> out.Append('\\').Append escaped |> ignore
                                    | escaped -> out.Append escaped |> ignore
                            | c -> out.Append c |> ignore
                            i <- i + 1
                        rest <- afterSemicolon (quoted.Substring(min ``end`` quoted.Length))
                        out.ToString()
                    else
                        let v, r =
                            match rest.IndexOf ';' with
                            | -1 -> rest, ""
                            | semi -> rest.Substring(0, semi), rest.Substring(semi + 1)
                        rest <- r
                        v.Trim()
                match param with
                | "name" -> name <- paramValue
                | "filename" -> filename <- paramValue
                | "filename*" -> filenameStar <- paramValue
                | _ -> ()
        match filenameStar with
        | null ->
            match filename with
            | null -> ()
            | f -> filename <- normalizeFilename f
        | star ->
            let parts = star.Split([| '\'' |], 3)
            let encoded = if parts.Length > 2 then parts[2] else ""
            filename <- normalizeFilename encoded
        { Name = name
          Filename = filename
          ContentType = null
          Head = "" }

    /// A part's `Content-Disposition` and `Content-Type`, with the headers as `Part::from_headers` records
    /// them: names lowercased, values decoded leniently, in the order the part gave them.
    let private partFromHeaders (headers: (string * byte[]) list) : Part =
        let text (bytes: byte[]) = Encoding.UTF8.GetString bytes
        let value (name: string) : string | null =
            match headers |> List.tryFind (fun (n, _) -> n = name) with
            | Some(_, bytes) -> text bytes
            | None -> null
        let head = StringBuilder()
        for (name, bytes) in headers do
            head.Append(name).Append(": ").Append(text bytes).Append("\r\n") |> ignore
        let part =
            match value "content-disposition" with
            | null ->
                { Name = value "content-id"
                  Filename = null
                  ContentType = null
                  Head = "" }
            | disposition -> parseDisposition disposition
        { part with
            ContentType = value "content-type"
            Head = head.ToString() }

    /// Rack names nameless parts after the file, or `"<content type>[]"`.
    let private partName (part: Part) : string =
        match part.Name with
        | null
        | "" ->
            match part.Filename with
            | null ->
                let contentType = match part.ContentType with null -> "text/plain" | ct -> ct
                contentType + "[]"
            | filename -> filename
        | name -> name

    /// Why reading a multipart body stopped early.
    type private Stop =
        | StopTooLarge
        | StopParams of ParamError

    let private stopOf (error: MultipartError) : Stop =
        match error with
        | StreamSizeExceeded -> StopTooLarge
        | _ -> StopParams ParamError.Parse

    let private tryDelete (path: string) : unit =
        try
            File.Delete path
        with _ ->
            ()

    /// Writes a file part to a temp file (`RackMultipart...`, as Rack names them), and returns its size and path.
    let private spool (reader: Multipart.Reader) : Task<Result<struct (int64 * string), Stop>> =
        task {
            let path = UploadedFile.NewTempPath()
            let mutable failure: Stop voption = ValueNone
            let mutable size = 0L
            try
                use file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous)
                let mutable reading = true
                while reading && failure.IsNone do
                    match! reader.NextChunk() with
                    | Error e -> failure <- ValueSome(stopOf e)
                    | Ok ValueNone -> reading <- false
                    | Ok(ValueSome chunk) ->
                        size <- size + int64 chunk.Length
                        do! file.WriteAsync chunk
                do! file.FlushAsync()
            with :? IOException as e ->
                failure <- ValueSome(StopParams(ParamError.Invalid e.Message))
            match failure with
            | ValueSome stop ->
                tryDelete path
                return Error stop
            | ValueNone -> return Ok(struct (size, path))
        }

    let private parseMultipart (body: Stream) (boundary: string) (limit: int voption) : Task<Result<ParsedBody, BodyError>> =
        task {
            let limit =
                match limit with
                | ValueSome limit -> min (int64 limit) MultipartBytesizeLimit
                | ValueNone -> MultipartBytesizeLimit
            let reader = Multipart.Reader(body, boundary, limit)
            let pairs = ResizeArray<RawPair>()
            let mutable parts = 0
            let mutable files = 0
            let mutable text = 0
            let mutable stop: Stop voption = ValueNone
            try
                let mutable go = true
                while go && stop.IsNone do
                    match! reader.NextField() with
                    | Error e -> stop <- ValueSome(stopOf e)
                    | Ok false -> go <- false
                    | Ok true ->
                        parts <- parts + 1
                        if parts > MultipartPartLimit then
                            stop <- ValueSome(StopParams(ParamError.Limit "too many multipart parts"))
                        else
                            let part = partFromHeaders reader.Headers
                            match part.Filename with
                            // A blank filename means no file was selected: Rack drops the part.
                            | "" ->
                                let mutable draining = true
                                while draining && stop.IsNone do
                                    match! reader.NextChunk() with
                                    | Error e -> stop <- ValueSome(stopOf e)
                                    | Ok ValueNone -> draining <- false
                                    | Ok(ValueSome _) -> ()
                            | null ->
                                use value = new PooledBufferWriter(256)
                                let writer = value :> System.Buffers.IBufferWriter<byte>
                                let mutable reading = true
                                while reading && stop.IsNone do
                                    match! reader.NextChunk() with
                                    | Error e -> stop <- ValueSome(stopOf e)
                                    | Ok ValueNone -> reading <- false
                                    | Ok(ValueSome chunk) ->
                                        text <- text + chunk.Length
                                        if text > MultipartTextLimit then
                                            stop <- ValueSome StopTooLarge
                                        else
                                            writer.Write chunk.Span
                                if stop.IsNone then
                                    let bytes = value.WrittenMemory.Span
                                    let decoded =
                                        if System.Text.Unicode.Utf8.IsValid bytes then
                                            { Text = Encoding.UTF8.GetString bytes
                                              Invalid = null }
                                        else
                                            { Text = null
                                              Invalid = bytes.ToArray() }
                                    pairs.Add
                                        { Key = { Text = partName part; Invalid = null }
                                          Value = decoded
                                          HasValue = true
                                          File = null }
                            | filename ->
                                files <- files + 1
                                if files > MultipartFileLimit then
                                    stop <- ValueSome(StopParams(ParamError.Limit "too many files"))
                                else
                                    match! spool reader with
                                    | Error s -> stop <- ValueSome s
                                    | Ok(struct (size, path)) ->
                                        let upload = new UploadedFile(filename, part.ContentType, part.Head, size, path)
                                        pairs.Add(Params.filePair (partName part) upload)
            finally
                reader.Release()
            let discard () =
                for pair in pairs do
                    match pair.File with
                    | null -> ()
                    | file -> file.Delete()
            match stop with
            | ValueSome StopTooLarge ->
                discard ()
                return Error TooLarge
            | ValueSome(StopParams error) ->
                discard ()
                return Ok { ParsedBody.empty with Params = Error error }
            | ValueNone ->
                let parsed = Params.fromPairs pairs
                if parsed.IsError then
                    discard ()
                    return Ok { ParsedBody.empty with Params = parsed }
                else
                    let files = [| for pair in pairs do match pair.File with null -> () | file -> file |]
                    return Ok { ParsedBody.empty with Params = parsed; Files = files }
        }

    /// Read `body` into memory, at most `limit` bytes: more is `TooLarge`.
    let private readBuffered (body: Stream) (limit: int) (contentLength: int64 voption) : Task<Result<ReadOnlyMemory<byte>, BodyError>> =
        task {
            match contentLength with
            | ValueSome length when length > int64 limit -> return Error TooLarge
            | ValueSome 0L -> return Ok ReadOnlyMemory.Empty
            | _ ->
                try
                    match contentLength with
                    | ValueSome length ->
                        // The length is known, so one buffer of exactly that size, filled once.
                        let buffer = Array.zeroCreate<byte> (int length)
                        let mutable read = 0
                        let mutable eof = false
                        while not eof && read < buffer.Length do
                            let! n = body.ReadAsync(Memory<byte>(buffer, read, buffer.Length - read))
                            if n = 0 then eof <- true else read <- read + n
                        // A client that sent less than it announced leaves the rest of the buffer unused.
                        return Ok(ReadOnlyMemory<byte>(buffer, 0, read))
                    | ValueNone ->
                        use collected = new MemoryStream()
                        let buffer = ArrayPool<byte>.Shared.Rent 16384
                        let mutable tooLarge = false
                        try
                            let mutable reading = true
                            while reading do
                                let! n = body.ReadAsync(Memory<byte>(buffer))
                                if n = 0 then
                                    reading <- false
                                elif collected.Length + int64 n > int64 limit then
                                    tooLarge <- true
                                    reading <- false
                                else
                                    collected.Write(buffer, 0, n)
                        finally
                            ArrayPool<byte>.Shared.Return buffer
                        if tooLarge then
                            return Error TooLarge
                        else
                            return Ok(ReadOnlyMemory<byte>(collected.ToArray()))
                with
                | :? BadHttpRequestException as e -> return Error(Read e.Message)
                | :? IOException -> return Error(Read "unreadable body")
                | :? OperationCanceledException -> return Error(Read "unreadable body")
        }

    /// Read and parse `body`. `originalMethod` is the method on the wire (Rack's `form_data?`
    /// treats a content-type-less POST as a form).
    let parse
        (originalMethod: string)
        (headers: IHeaderDictionary)
        (contentLength: int64 voption)
        (body: Stream)
        (limit: int voption)
        : Task<Result<ParsedBody, BodyError>> =
        task {
            let contentType =
                match RequestHeaders.get headers Hdr.ContentType with
                | "" -> null
                | ct -> ct
            let media = RequestHeaders.mediaType contentType
            // `matches!(media, form-data | related | mixed) && multer::parse_boundary(ct).ok()`: only
            // `multipart/form-data` parses, so the others are read as plain bodies.
            let boundary =
                match media, contentType with
                | ("multipart/form-data" | "multipart/related" | "multipart/mixed"), ct when not (isNull ct) ->
                    Multipart.parseBoundary (nonNull ct)
                | _ -> ValueNone
            match boundary with
            | ValueSome boundary -> return! parseMultipart body boundary limit
            | ValueNone ->
                // Everything but multipart (whose files spool to disk) is read into memory, so it's bounded
                // while it's read, whatever the configured limit.
                let max =
                    match limit with
                    | ValueSome limit -> min limit MaxBufferedBody
                    | ValueNone -> MaxBufferedBody
                match! readBuffered body max contentLength with
                | Error e -> return Error e
                | Ok raw ->
                    let isJson =
                        match Format.contentMimeType contentType with
                        | Ok(ValueSome f) -> f = Format.Json
                        | _ -> false
                    let isForm =
                        media = "application/x-www-form-urlencoded"
                        || (isNull contentType && originalMethod = "POST")
                        || media = "multipart/form-data"
                        || media = "multipart/related"
                        || media = "multipart/mixed"
                    let parsed =
                        if isJson && raw.Length > 0 then
                            Params.fromJsonBody raw
                        elif isForm then
                            match Params.formPairs raw.Span with
                            | Ok pairs -> Params.fromPairs pairs
                            | Error e -> Error e
                        else
                            Ok(ParamMap())
                    return Ok { Raw = raw; Params = parsed; Files = Array.empty }
        }
