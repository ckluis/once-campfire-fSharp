// Port of rust/crates/kit/src/front/compression.rs
//
// Thruster's compression (`internal/compression_handler.go`, `compression_guard_handler.go`), which
// is klauspost/compress v1.18.6's `gzhttp` with a 1 KB minimum size, gzip level 6, zstd (preferred
// over gzip at equal quality) and 32 bytes of BREACH jitter.
//
// The app's own `Rack::Deflater` (`Deflater`) already gzips nearly every response for clients that
// accept gzip, and `gzhttp` leaves encoded responses alone. What it adds is `Vary: Accept-Encoding`
// on every response, and compression of what the app sent unencoded: zstd for clients that accept
// zstd but not gzip, and bodies `Rack::Deflater` skipped.
//
// What Rust's `Compression::apply` does to a response is split here: the choices that depend on the
// headers and the start of the body are `FrontResponse`'s (Handler.fs), which owns the response
// the app writes to; this file has what they use.
namespace Campfire.Kit

open System
open System.Buffers.Binary
open System.IO
open System.IO.Compression
open System.Text
open Microsoft.AspNetCore.Http
open ZstdSharp

type Encoding =
    | NoEncoding = 0
    | Gzip = 1
    | Zstd = 2

/// How the response's own headers were combined with the `Vary: Accept-Encoding` gzhttp set before
/// calling on: Thruster's cache copies stored or recorded headers over it (a `Vary` of the
/// response's replaces it), while bypassed requests add theirs to it (so `Vary` repeats).
type HeaderMerge =
    | Replace = 0
    | Append = 1

/// What `gzhttp` decided about a request before calling on.
[<Struct>]
type Negotiation =
    { Encoding: Encoding
      UserSpecificRequest: bool }

module FrontCompression =
    /// `gzhttp.HeaderNoCompression`: set by the guard to veto compression, never sent.
    [<Literal>]
    let NoCompression = "no-gzip-compression"

    [<Literal>]
    let MinSize = 1024

    /// `RandomJitter(n, 0, false)`: jitter is derived from (at most) the first 64 KB of the body.
    [<Literal>]
    let JitterBuffer = 65536

    /// net/http's `bufferBeforeChunkingSize`.
    [<Literal>]
    let GoChunkingBuffer = 2048

    /// `zstd.SpeedFastest`
    [<Literal>]
    let private ZstdLevel = 1

    /// `strings.Repeat("Padding-", 1+(n/8))[:n+1]`, or empty without jitter.
    let jitterPadding (jitter: int64) : string =
        if jitter > 0L then
            let n = int jitter
            (String.replicate (1 + n / 8) "Padding-").Substring(0, n + 1)
        else
            ""

    /// `parseEncodingQValue`: the first listed coding's quality (1 without a `q`), else 0.
    let private quality (header: string) (encoding: string) : float =
        let mutable result = 0.0
        let mutable found = false
        for part in header.Trim().Split ',' do
            if not found then
                let pieces = part.Split ';'
                let coding = pieces[0].Trim().ToLowerInvariant()
                if coding = encoding then
                    found <- true
                    let mutable q = 1.0
                    for piece in pieces[1..] do
                        let piece = piece.Trim()
                        if piece.StartsWith("q=", StringComparison.Ordinal) then
                            let value = piece.Substring 2
                            // Rust's `f64::from_str`: "inf" and "nan" too, which .NET spells differently.
                            let parsed =
                                match value.ToLowerInvariant() with
                                | "inf"
                                | "+inf"
                                | "infinity"
                                | "+infinity" -> Double.PositiveInfinity
                                | "-inf"
                                | "-infinity" -> Double.NegativeInfinity
                                | "nan"
                                | "+nan"
                                | "-nan" -> Double.NaN
                                | _ ->
                                    match
                                        Double.TryParse(
                                            value,
                                            Globalization.NumberStyles.AllowLeadingSign
                                            ||| Globalization.NumberStyles.AllowDecimalPoint
                                            ||| Globalization.NumberStyles.AllowExponent,
                                            Globalization.CultureInfo.InvariantCulture
                                        )
                                    with
                                    | true, v -> v
                                    | _ -> 0.0
                            q <- if Double.IsNaN parsed then parsed else Math.Clamp(parsed, 0.0, 1.0)
                    result <- q
        result

    /// `selectEncoding`: never for HEAD; zstd when its quality is at least gzip's.
    let selectEncoding (meth: string) (acceptEncoding: string | null) : Encoding =
        if String.Equals(meth, "HEAD", StringComparison.Ordinal) then
            Encoding.NoEncoding
        else
            match acceptEncoding with
            | null
            | "" -> Encoding.NoEncoding
            | acceptEncoding ->
                let gzip = quality acceptEncoding "gzip"
                let zstd = quality acceptEncoding "zstd"
                match gzip > 0.0, zstd > 0.0 with
                | false, false -> Encoding.NoEncoding
                | true, false -> Encoding.Gzip
                | false, true -> Encoding.Zstd
                | true, true -> if gzip > zstd then Encoding.Gzip else Encoding.Zstd

    /// Thruster's `contentTypeFilter`: gzhttp's default filter, minus already-compressed images.
    let contentTypeFilter (contentType: string) : bool =
        let contentType = contentType.Trim().ToLowerInvariant()
        if contentType = "" then
            true
        else
            let excludeContains = [| "compress"; "zip"; "snappy"; "lzma"; "xz"; "zstd"; "brotli"; "stuffit" |]
            let excludePrefix =
                [| "video/"; "audio/"; "image/jp"
                   "image/jpeg"; "image/jpg"; "image/png"; "image/apng"; "image/webp"; "image/gif"
                   "image/avif"; "image/heic"; "image/heif"; "image/jxl" |]
            not (
                excludeContains |> Array.exists (fun s -> contentType.Contains(s, StringComparison.Ordinal))
                || excludePrefix |> Array.exists (fun p -> contentType.StartsWith(p, StringComparison.Ordinal))
            )

    /// `hasUserSpecificRequestHeaders` (GZIP_COMPRESSION_DISABLE_ON_AUTH)
    let hasUserSpecificRequestHeaders (headers: IHeaderDictionary) : bool =
        let present (name: string) =
            let v = headers[name]
            v.Count > 0 && v.ToString() <> ""
        present "cookie" || present "authorization" || present "x-csrf-token"

    /// `hasUserSpecificResponseHeaders`
    let hasUserSpecificResponseHeaders (headers: IHeaderDictionary) : bool =
        let first (name: string) =
            let v = headers[name]
            if v.Count = 0 then "" else (match v[0] with null -> "" | s -> s)
        if first "set-cookie" <> "" then
            true
        else
            let cacheControl = (first "cache-control").ToLowerInvariant()
            let directive (d: string) = (let d = d.Trim() in match d.IndexOf '=' with -1 -> d | i -> d.Substring(0, i))
            if cacheControl.Split ',' |> Array.exists (fun d -> directive d = "private" || directive d = "no-store") then
                true
            else
                (first "vary").Split ',' |> Array.exists (fun t -> t.Trim().Equals("cookie", StringComparison.OrdinalIgnoreCase))

    /// The checks `gzhttp` can make from the headers alone: not already encoded, not a range, and
    /// (when the length and type are known) at least 1 KB of a compressible type.
    let mayCompress (headers: IHeaderDictionary) : bool =
        let get (name: string) =
            let v = headers[name]
            if v.Count = 0 then "" else (match v[0] with null -> "" | s -> s)
        if get "content-encoding" <> "" || get "content-range" <> "" then
            false
        else
            let contentLength =
                match Int64.TryParse(get "content-length", Globalization.NumberStyles.AllowLeadingSign, Globalization.CultureInfo.InvariantCulture) with
                | true, n when n >= 0L -> n
                | _ -> 0L
            let contentType = get "content-type"
            contentLength = 0L || (contentLength >= int64 MinSize && (contentType = "" || contentTypeFilter contentType))

    /// A subset of Go's `http.DetectContentType`, for bodies sent without a `Content-Type` (the app
    /// always sends one, so this is only a fallback).
    let detectContentType (input: ReadOnlySpan<byte>) : string =
        let data = input.Slice(0, min input.Length 512).ToArray()
        let start =
            let mutable i = 0
            while i < data.Length && (data[i] = 9uy || data[i] = 10uy || data[i] = 0x0cuy || data[i] = 13uy || data[i] = 32uy) do
                i <- i + 1
            i
        let trimmed = data[start..]
        let lower (c: byte) = if c >= byte 'A' && c <= byte 'Z' then c + 32uy else c
        let startsWith (bytes: byte[]) (prefix: byte[]) = bytes.Length >= prefix.Length && bytes[0 .. prefix.Length - 1] = prefix
        let htmlSignatures =
            [| "<!DOCTYPE HTML"; "<HTML"; "<HEAD"; "<SCRIPT"; "<IFRAME"; "<H1"; "<DIV"; "<FONT"; "<TABLE"; "<A"; "<STYLE"; "<TITLE"
               "<B"; "<BODY"; "<BR"; "<P"; "<!--" |]
        let isHtml =
            htmlSignatures
            |> Array.exists (fun signature ->
                let signature = Encoding.ASCII.GetBytes signature
                trimmed.Length > signature.Length
                && Array.forall2 (fun a b -> lower a = lower b) trimmed[0 .. signature.Length - 1] signature
                && (trimmed[signature.Length] = byte ' ' || trimmed[signature.Length] = byte '>'))
        if isHtml then
            "text/html; charset=utf-8"
        elif startsWith trimmed "<?xml"B then
            "text/xml; charset=utf-8"
        elif startsWith data "%PDF-"B then
            "application/pdf"
        elif startsWith data "%!PS-Adobe-"B then
            "application/postscript"
        elif startsWith data [| 0xFEuy; 0xFFuy |] then
            "text/plain; charset=utf-16be"
        elif startsWith data [| 0xFFuy; 0xFEuy |] then
            "text/plain; charset=utf-16le"
        elif startsWith data [| 0xEFuy; 0xBBuy; 0xBFuy |] then
            "text/plain; charset=utf-8"
        elif startsWith data "GIF87a"B || startsWith data "GIF89a"B then
            "image/gif"
        elif startsWith data [| 0x89uy; byte 'P'; byte 'N'; byte 'G'; 0x0Duy; 0x0Auy; 0x1Auy; 0x0Auy |] then
            "image/png"
        elif startsWith data [| 0xFFuy; 0xD8uy; 0xFFuy |] then
            "image/jpeg"
        elif data.Length >= 14 && data[0..3] = "RIFF"B && data[8..13] = "WEBPVP"B then
            "image/webp"
        elif startsWith data [| 0x1Fuy; 0x8Buy; 0x08uy |] then
            "application/x-gzip"
        elif startsWith data [| byte 'P'; byte 'K'; 0x03uy; 0x04uy |] then
            "application/zip"
        else
            let binary =
                data |> Array.exists (fun b -> b <= 0x08uy || b = 0x0Buy || (b >= 0x0Euy && b <= 0x1Auy) || (b >= 0x1Cuy && b <= 0x1Fuy))
            if binary then "application/octet-stream" else "text/plain; charset=utf-8"

    let private castagnoli: uint32[] =
        Array.init 256 (fun n ->
            let mutable crc = uint32 n
            for _ in 0..7 do
                crc <- if crc &&& 1u <> 0u then (crc >>> 1) ^^^ 0x82F63B78u else crc >>> 1
            crc)

    /// CRC-32C (Castagnoli), as `crc32.Update(0, castagnoliTable, b)`.
    let crc32c (data: ReadOnlySpan<byte>) : uint32 =
        let mutable crc = ~~~0u
        let mutable i = 0
        if System.Runtime.Intrinsics.Arm.Crc32.Arm64.IsSupported then
            while i + 8 <= data.Length do
                crc <- System.Runtime.Intrinsics.Arm.Crc32.Arm64.ComputeCrc32C(crc, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice i))
                i <- i + 8
        elif System.Runtime.Intrinsics.X86.Sse42.X64.IsSupported then
            while i + 8 <= data.Length do
                crc <- uint32 (System.Runtime.Intrinsics.X86.Sse42.X64.Crc32(uint64 crc, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice i)))
                i <- i + 8
        while i < data.Length do
            crc <- castagnoli[int ((crc ^^^ uint32 data[i]) &&& 0xFFu)] ^^^ (crc >>> 8)
            i <- i + 1
        ~~~crc

    /// `startCompression`'s jitter: a prefix of the padding whose length (1..=n) comes from the
    /// CRC-32C of the start of the body.
    let jitterFor (padding: string) (buffered: ReadOnlySpan<byte>) : string | null =
        if padding = "" then
            null
        else
            let sample = buffered.Slice(0, min buffered.Length JitterBuffer)
            let rng = (let c = crc32c sample in (c <<< 19) ||| (c >>> 13)) ^^^ 0xab0755deu
            let length = 1 + int (rng % uint32 (padding.Length - 1))
            padding.Substring(0, length)

/// A streaming gzip or zstd encoder: `Write` takes the next piece of the body and returns what the
/// encoder has ready (possibly nothing), `Finish` the rest.
[<Sealed>]
type FrontEncoder(encoding: Encoding, jitter: string | null) =
    let output = new MemoryStream()
    let stream: Stream =
        match encoding with
        | Encoding.Zstd -> new CompressionStream(output, 1, 0, true)
        | _ -> new DeflateStream(output, CompressionLevel.Optimal, true)
    let mutable crc = 0u
    let mutable size = 0u
    let mutable started = false

    let take () : byte[] =
        let bytes = output.ToArray()
        output.SetLength 0L
        bytes

    /// gzip's header, with the jitter as its comment (Go's gzip writes one the same way) and the
    /// Unix OS code.
    let header () =
        let comment = match jitter with | null -> Array.empty | j -> Text.Encoding.ASCII.GetBytes j
        let bytes = Array.zeroCreate<byte> (10 + (if comment.Length > 0 then comment.Length + 1 else 0))
        bytes[0] <- 0x1fuy
        bytes[1] <- 0x8buy
        bytes[2] <- 8uy
        if comment.Length > 0 then bytes[3] <- 0x10uy
        bytes[9] <- 3uy
        comment.CopyTo(Span<byte>(bytes, 10, comment.Length))
        bytes

    member _.Write(data: ReadOnlySpan<byte>) : byte[] =
        if encoding = Encoding.Gzip && not started then
            started <- true
            output.Write(ReadOnlySpan<byte>(header ()))
        if data.Length > 0 then
            if encoding = Encoding.Gzip then
                crc <- Deflater.crc32Update crc data
                size <- size + uint32 data.Length
            stream.Write data
        take ()

    member this.Finish() : byte[] =
        match encoding with
        | Encoding.Zstd ->
            stream.Dispose()
            // The jitter goes in a skippable frame (RFC 8878 3.1.2) after the data.
            match jitter with
            | null -> ()
            | jitter ->
                let magic = Array.zeroCreate<byte> 8
                BinaryPrimitives.WriteUInt32LittleEndian(Span<byte>(magic, 0, 4), 0x184D2A50u)
                BinaryPrimitives.WriteUInt32LittleEndian(Span<byte>(magic, 4, 4), uint32 jitter.Length)
                output.Write(ReadOnlySpan<byte> magic)
                output.Write(ReadOnlySpan<byte>(Text.Encoding.ASCII.GetBytes jitter))
            take ()
        | _ ->
            if not started then
                started <- true
                output.Write(ReadOnlySpan<byte>(header ()))
            if size = 0u then
                // .NET's `DeflateStream` writes nothing for no input; zlib writes one empty final block.
                stream.Dispose()
                output.Write(ReadOnlySpan<byte>([| 3uy; 0uy |]))
            else
                stream.Dispose()
            let trailer = Array.zeroCreate<byte> 8
            BinaryPrimitives.WriteUInt32LittleEndian(Span<byte>(trailer, 0, 4), crc)
            BinaryPrimitives.WriteUInt32LittleEndian(Span<byte>(trailer, 4, 4), size)
            output.Write(ReadOnlySpan<byte> trailer)
            take ()

    interface IDisposable with
        member _.Dispose() =
            stream.Dispose()
            output.Dispose()
