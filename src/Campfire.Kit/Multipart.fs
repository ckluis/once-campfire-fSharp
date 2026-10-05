// A port of the `multer` crate (3.1.0, what rust/crates/kit/src/body.rs reads multipart with), with the
// `mime` (0.3.17) and `httparse` (1.10.1) parsing it relies on, so that what is accepted, what is refused
// and where each part ends are what the Rust port decides, byte for byte, on well-formed and malformed
// bodies alike. The stages of `Multipart::poll_next_field` and `StreamBuffer::read_field_data` are the
// ones in multer's source.
namespace Campfire.Kit

open System
open System.Buffers
open System.IO
open System.Text
open System.Threading.Tasks

/// Why a multipart body could not be read (`multer::Error`, in the groups the kit tells apart).
type MultipartError =
    /// More was promised than arrived (a missing boundary or part header).
    | IncompleteStream
    | IncompleteFieldData
    /// `httparse` or `http` refused a part's headers.
    | ReadHeaderFailed
    | StreamSizeExceeded
    /// The request body could not be read.
    | StreamReadFailed

module Multipart =
    // --- `mime`: the boundary of a content type ----------------------------------------------------

    /// `mime`'s `TOKEN_MAP`: the characters of an HTTP token.
    let private isToken (c: char) =
        c < '\128'
        && (Char.IsAsciiLetterOrDigit c || "!#$%&'*+-.^_`|~".IndexOf c >= 0)

    /// `mime`'s `is_restricted_quoted_char`.
    let private isRestrictedQuotedChar (c: char) = c > '\031' && c <> '\127'

    /// What `multer::parse_boundary` returns for a content type: its `boundary` parameter, provided
    /// the type parses as a media type (`mime::Mime::from_str`) and is `multipart/form-data`.
    let parseBoundary (contentType: string) : string voption =
        let s = contentType
        // The `mime` parser: top-level type, then `/`, then the subtype, then `;` parameters.
        let mutable i = 0
        let mutable slash = -1
        let mutable ok = true
        while ok && slash < 0 do
            if i >= s.Length then
                ok <- false
            elif isToken s[i] then
                i <- i + 1
            elif s[i] = '/' && i > 0 then
                slash <- i
                i <- i + 1
            else
                ok <- false
        if not ok then
            ValueNone
        else
            let subStart = i
            let mutable plus = -1
            let mutable semicolon = -1
            let mutable finished = false
            while ok && not finished do
                if i >= s.Length then
                    finished <- true
                elif s[i] = '+' && i > subStart then
                    plus <- i
                    i <- i + 1
                elif s[i] = ';' && i > subStart then
                    semicolon <- i
                    finished <- true
                elif isToken s[i] then
                    i <- i + 1
                else
                    ok <- false
            if not ok then
                ValueNone
            else
                let parameters = ResizeArray<struct (string * string)>()
                if semicolon >= 0 then
                    // `params_from_str`: after the `;`, any number of `name=value` separated by `;`, with
                    // leading spaces skipped before a name.
                    i <- semicolon + 1
                    let mutable start = i
                    while ok && start < s.Length do
                        // name
                        let mutable nameEnd = -1
                        let mutable restart = false
                        while ok && nameEnd < 0 && not restart do
                            if i >= s.Length then
                                ok <- false
                            elif s[i] = ' ' && i = start then
                                start <- i + 1
                                i <- i + 1
                                restart <- true
                            elif isToken s[i] then
                                i <- i + 1
                            elif s[i] = '=' && i > start then
                                nameEnd <- i
                                i <- i + 1
                            else
                                ok <- false
                        if ok && not restart then
                            let name = s.Substring(start, nameEnd - start)
                            start <- i
                            // value: a token, or a quoted string
                            let mutable quoted = false
                            let mutable valueEnd = -1
                            while ok && valueEnd < 0 do
                                if quoted then
                                    if i >= s.Length then ok <- false
                                    elif s[i] = '"' && i > start then
                                        valueEnd <- i
                                        i <- i + 1
                                    elif isRestrictedQuotedChar s[i] then i <- i + 1
                                    else ok <- false
                                else if i >= s.Length then
                                    valueEnd <- s.Length
                                elif s[i] = '"' && i = start then
                                    quoted <- true
                                    start <- i + 1
                                    i <- i + 1
                                elif isToken s[i] then
                                    i <- i + 1
                                elif s[i] = ';' && i > start then
                                    valueEnd <- i
                                    i <- i + 1
                                else
                                    ok <- false
                            if ok then
                                let value = s.Substring(start, valueEnd - start)
                                parameters.Add(struct (name, value))
                                if quoted then
                                    // after the closing quote: spaces, then `;` or the end
                                    let mutable go = true
                                    while ok && go do
                                        if i >= s.Length then
                                            go <- false
                                        elif s[i] = ';' then
                                            i <- i + 1
                                            go <- false
                                        elif s[i] = ' ' then
                                            i <- i + 1
                                        else
                                            ok <- false
                                start <- i
                if not ok then
                    ValueNone
                else
                    let endOfSubtype = if plus >= 0 then plus elif semicolon >= 0 then semicolon else s.Length
                    let typeName = s.Substring(0, slash).ToLowerInvariant()
                    let subtype = s.Substring(slash + 1, endOfSubtype - slash - 1).ToLowerInvariant()
                    if typeName <> "multipart" || subtype <> "form-data" then
                        ValueNone
                    else
                        let mutable found = ValueNone
                        for struct (name, value) in parameters do
                            if found.IsNone && String.Equals(name, "boundary", StringComparison.OrdinalIgnoreCase) then
                                found <- ValueSome value
                        found

    // --- `httparse`: a part's headers --------------------------------------------------------------

    /// `httparse::is_header_value_token`: tab, printable ASCII (not DEL) and everything from 0x80.
    let private isHeaderValueByte (b: byte) = b = 9uy || (b >= 32uy && b <= 126uy) || b >= 128uy

    let private isHeaderNameByte (b: byte) = isToken (char b) && b < 128uy

    /// `max headers` of multer (`constants::MAX_HEADERS`).
    [<Literal>]
    let private MaxHeaders = 32

    /// `httparse::parse_headers` over a part's header block, then multer's conversion into a `HeaderMap`
    /// (`insert`: a name repeated keeps its first place and its last value, lowercased). `Error` is any
    /// failure of either.
    let parseHeaders (block: ReadOnlySpan<byte>) : Result<(string * byte[]) list, unit> =
        let headers = ResizeArray<struct (string * byte[])>()
        let mutable i = 0
        let mutable failed = false
        let mutable complete = false
        let mutable count = 0
        while not failed && not complete do
            if i >= block.Length then
                // httparse: Partial, which multer treats as `IncompleteHeaders`.
                failed <- true
            else
                let b = block[i]
                if b = byte '\r' then
                    if i + 1 < block.Length && block[i + 1] = byte '\n' then complete <- true else failed <- true
                elif b = byte '\n' then
                    complete <- true
                elif not (isHeaderNameByte b) then
                    failed <- true
                else
                    // the name, up to the colon
                    let nameStart = i
                    while i < block.Length && isHeaderNameByte block[i] do
                        i <- i + 1
                    if i >= block.Length || block[i] <> byte ':' then
                        failed <- true
                    else
                        let name = Encoding.ASCII.GetString(block.Slice(nameStart, i - nameStart)).ToLowerInvariant()
                        i <- i + 1
                        // whitespace between the colon and the value
                        while i < block.Length && (block[i] = byte ' ' || block[i] = byte '\t') do
                            i <- i + 1
                        if i >= block.Length then
                            failed <- true
                        else
                            let valueStart = i
                            let mutable valueEnd = i
                            if isHeaderValueByte block[i] then
                                while i < block.Length && isHeaderValueByte block[i] do
                                    i <- i + 1
                                valueEnd <- i
                            // the end of the line
                            if i >= block.Length then
                                failed <- true
                            elif block[i] = byte '\r' then
                                if i + 1 < block.Length && block[i + 1] = byte '\n' then i <- i + 2 else failed <- true
                            elif block[i] = byte '\n' then
                                i <- i + 1
                            else
                                failed <- true
                            if not failed then
                                // trailing whitespace is not part of the value
                                let mutable trimmed = valueEnd
                                while trimmed > valueStart && (block[trimmed - 1] = byte ' ' || block[trimmed - 1] = byte '\t') do
                                    trimmed <- trimmed - 1
                                count <- count + 1
                                if count > MaxHeaders then
                                    failed <- true
                                else
                                    let value = block.Slice(valueStart, trimmed - valueStart).ToArray()
                                    match headers.FindIndex(fun (struct (n, _)) -> n = name) with
                                    | -1 -> headers.Add(struct (name, value))
                                    | at -> headers[at] <- struct (name, value)
        if failed then Error() else Ok [ for struct (n, v) in headers -> n, v ]

    // --- `multer`: the streaming parser ---------------------------------------------------------------

    type private Stage =
        | FindingFirstBoundary
        | ReadingBoundary
        | DeterminingBoundaryType
        | ReadingTransportPadding
        | ReadingFieldHeaders
        | ReadingFieldData
        | Eof

    let private crlf = "\r\n"B
    let private crlfCrlf = "\r\n\r\n"B
    let private boundaryExt = "--"B

    /// `multer::Multipart` over a request body.
    [<Sealed>]
    type Reader(stream: Stream, boundary: string, wholeStreamLimit: int64) =
        let mutable buffer: byte[] = ArrayPool<byte>.Shared.Rent 65536
        let mutable start = 0
        let mutable stop = 0
        let mutable eof = false
        let mutable total = 0L
        let mutable stage = FindingFirstBoundary
        let mutable fieldDone = true
        let mutable fieldHeaders: (string * byte[]) list = []
        let boundaryBytes = Encoding.UTF8.GetBytes boundary
        // `--boundary`, and `\r\n--boundary`
        let boundaryDeriv = Array.append boundaryExt boundaryBytes
        let fieldEnd = Array.concat [ crlf; boundaryExt; boundaryBytes ]

        member private _.Available = stop - start

        member private _.Span = ReadOnlySpan<byte>(buffer, start, stop - start)

        /// How many spaces and tabs the buffer starts with.
        member private this.PaddingLength() : int =
            let span = this.Span
            let mutable skip = 0
            while skip < span.Length && (span[skip] = byte ' ' || span[skip] = byte '\t') do
                skip <- skip + 1
            skip

        member private _.Consume(n: int) =
            start <- start + n
            if start = stop then
                start <- 0
                stop <- 0

        /// Make room for another read, keeping what is buffered.
        member private _.MakeRoom() =
            if start > 0 then
                Buffer.BlockCopy(buffer, start, buffer, 0, stop - start)
                stop <- stop - start
                start <- 0
            if buffer.Length - stop < 16384 then
                let bigger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2)
                Buffer.BlockCopy(buffer, 0, bigger, 0, stop)
                ArrayPool<byte>.Shared.Return buffer
                buffer <- bigger

        /// Read more of the body into the buffer (`StreamBuffer::poll_stream`, one read's worth).
        member private this.Fill() : Task<Result<unit, MultipartError>> =
            task {
                if eof then
                    return Ok()
                else
                    this.MakeRoom()
                    try
                        let! n = stream.ReadAsync(Memory<byte>(buffer, stop, buffer.Length - stop))
                        if n = 0 then
                            eof <- true
                            return Ok()
                        else
                            total <- total + int64 n
                            if total > wholeStreamLimit then
                                return Error StreamSizeExceeded
                            else
                                stop <- stop + n
                                return Ok()
                    with
                    | :? InvalidOperationException
                    | :? IOException
                    | :? OperationCanceledException
                    | :? ArgumentException -> return Error StreamReadFailed
            }

        /// The headers of the field `NextField` last returned (lowercased names, raw values).
        member _.Headers = fieldHeaders

        /// Return the buffer to the pool.
        member _.Release() =
            if not (obj.ReferenceEquals(buffer, null)) then
                ArrayPool<byte>.Shared.Return buffer
                buffer <- Unchecked.defaultof<byte[]>

        /// Yields the next field's headers (`Multipart::next_field`): `Ok true` when there is one, `Ok false`
        /// at the closing boundary.
        member this.NextField() : Task<Result<bool, MultipartError>> =
            task {
                let mutable result: Result<bool, MultipartError> voption = ValueNone
                // Run the stages in order, each waiting for more of the body where multer returns Pending.
                while result.IsNone do
                    match stage with
                    | Eof -> result <- ValueSome(Ok false)
                    | FindingFirstBoundary ->
                        match this.Span.IndexOf(ReadOnlySpan<byte>(boundaryDeriv)) with
                        | -1 ->
                            if eof then
                                result <- ValueSome(Error IncompleteStream)
                            else
                                // What came before the boundary is discarded; keep only a tail the boundary could straddle.
                                let keep = boundaryDeriv.Length - 1
                                if this.Available > keep then this.Consume(this.Available - keep)
                                match! this.Fill() with
                                | Error e -> result <- ValueSome(Error e)
                                | Ok() -> ()
                        | at ->
                            this.Consume at
                            stage <- ReadingBoundary
                    | ReadingFieldData ->
                        // The previous field was not read to its end: read it out.
                        match! this.NextChunk() with
                        | Error e -> result <- ValueSome(Error e)
                        | Ok _ -> ()
                    | ReadingBoundary ->
                        if this.Available < boundaryDeriv.Length then
                            if eof then
                                result <- ValueSome(Error IncompleteStream)
                            else
                                match! this.Fill() with
                                | Error e -> result <- ValueSome(Error e)
                                | Ok() -> ()
                        elif this.Span.Slice(0, boundaryDeriv.Length).SequenceEqual(ReadOnlySpan<byte>(boundaryDeriv)) then
                            this.Consume boundaryDeriv.Length
                            stage <- DeterminingBoundaryType
                        else
                            result <- ValueSome(Error IncompleteStream)
                    | DeterminingBoundaryType ->
                        if this.Available < 2 then
                            if eof then
                                result <- ValueSome(Error IncompleteStream)
                            else
                                match! this.Fill() with
                                | Error e -> result <- ValueSome(Error e)
                                | Ok() -> ()
                        elif this.Span.Slice(0, 2).SequenceEqual(ReadOnlySpan<byte>(boundaryExt)) then
                            stage <- Eof
                            result <- ValueSome(Ok false)
                        else
                            stage <- ReadingTransportPadding
                    | ReadingTransportPadding ->
                        // Spaces and tabs after the boundary, then CRLF.
                        let skip = this.PaddingLength()
                        if skip = this.Available then
                            this.Consume skip
                            if eof then
                                result <- ValueSome(Error IncompleteStream)
                            else
                                match! this.Fill() with
                                | Error e -> result <- ValueSome(Error e)
                                | Ok() -> ()
                        else
                            this.Consume skip
                            if this.Available < 2 then
                                if eof then
                                    result <- ValueSome(Error IncompleteStream)
                                else
                                    match! this.Fill() with
                                    | Error e -> result <- ValueSome(Error e)
                                    | Ok() -> ()
                            elif this.Span.Slice(0, 2).SequenceEqual(ReadOnlySpan<byte>(crlf)) then
                                this.Consume 2
                                stage <- ReadingFieldHeaders
                            else
                                result <- ValueSome(Error IncompleteStream)
                    | ReadingFieldHeaders ->
                        match this.Span.IndexOf(ReadOnlySpan<byte>(crlfCrlf)) with
                        | -1 ->
                            if eof then
                                result <- ValueSome(Error IncompleteStream)
                            else
                                match! this.Fill() with
                                | Error e -> result <- ValueSome(Error e)
                                | Ok() -> ()
                        | at ->
                            let parsed = parseHeaders (this.Span.Slice(0, at + crlfCrlf.Length))
                            this.Consume(at + crlfCrlf.Length)
                            match parsed with
                            | Error() -> result <- ValueSome(Error ReadHeaderFailed)
                            | Ok headers ->
                                fieldHeaders <- headers
                                fieldDone <- false
                                stage <- ReadingFieldData
                                result <- ValueSome(Ok true)
                return result.Value
            }

        /// The next piece of the current field's data (`Field`'s stream, `StreamBuffer::read_field_data`):
        /// `ValueSome` bytes, valid until the next call, then `ValueNone` once the field has ended.
        member this.NextChunk() : Task<Result<ReadOnlyMemory<byte> voption, MultipartError>> =
            task {
                if fieldDone then
                    return Ok ValueNone
                else
                    let mutable result: Result<ReadOnlyMemory<byte> voption, MultipartError> voption = ValueNone
                    while result.IsNone do
                        if this.Available = 0 && eof then
                            result <- ValueSome(Error IncompleteFieldData)
                        elif this.Available = 0 then
                            match! this.Fill() with
                            | Error e -> result <- ValueSome(Error e)
                            | Ok() -> ()
                        else
                            match this.Span.IndexOf(ReadOnlySpan<byte>(fieldEnd)) with
                            | -1 when eof -> result <- ValueSome(Error IncompleteFieldData)
                            | -1 ->
                                // No whole `\r\n--boundary` yet: hold back a tail that might begin one.
                                let length = this.Available
                                let heldMax = fieldEnd.Length - 1
                                let heldFrom = if length >= heldMax then length - heldMax else 0
                                let lastCr = this.Span.Slice(heldFrom).LastIndexOf(byte '\r')
                                if lastCr < 0 then
                                    let chunk = ReadOnlyMemory<byte>(buffer, start, length)
                                    start <- 0
                                    stop <- 0
                                    // `chunk` still points at the bytes just consumed, which stay put until the next fill.
                                    result <- ValueSome(Ok(ValueSome chunk))
                                else
                                    let at = heldFrom + lastCr
                                    let tail = this.Span.Slice at
                                    if ReadOnlySpan<byte>(fieldEnd).IndexOf tail >= 0 then
                                        // The tail may be the start of a boundary: emit what is before it.
                                        if at = 0 then
                                            match! this.Fill() with
                                            | Error e -> result <- ValueSome(Error e)
                                            | Ok() -> ()
                                        else
                                            let chunk = ReadOnlyMemory<byte>(buffer, start, at)
                                            start <- start + at
                                            result <- ValueSome(Ok(ValueSome chunk))
                                    else
                                        let chunk = ReadOnlyMemory<byte>(buffer, start, length)
                                        start <- 0
                                        stop <- 0
                                        result <- ValueSome(Ok(ValueSome chunk))
                            | at ->
                                let chunk = ReadOnlyMemory<byte>(buffer, start, at)
                                // The bytes before the boundary, and the CRLF that belongs to it.
                                start <- start + at + crlf.Length
                                stage <- ReadingBoundary
                                fieldDone <- true
                                result <- ValueSome(Ok(ValueSome chunk))
                    return result.Value
            }
