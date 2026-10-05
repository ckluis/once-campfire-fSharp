// Port of rust/crates/cable/src/socket.rs
//
// The server side of a WebSocket (RFC 6455) for Action Cable connections, with per-message
// compression (RFC 7692, `permessage-deflate`) when the browser offers it, as browsers do.
//
// Written for fan-out to many sockets. A broadcast reaches every subscriber as the same `Frame`,
// whose payload is shared rather than copied: each socket's write is a few header bytes plus the
// shared payload in one buffer, and nothing is kept per socket once it's written (a general-purpose
// WebSocket copies each frame into a per-socket buffer that then keeps its size). Compression is
// negotiated without context takeover, so every message is compressed on its own, and a broadcast is
// compressed once for all of its subscribers. ASP.NET Core's `WebSocket` compresses per socket, so
// this takes the upgraded stream (`IHttpUpgradeFeature`) and frames it itself, as Rust does with
// the upgraded hyper connection.
//
// Only what Action Cable needs from a client is supported: text messages, fragmented or not,
// compressed or not, and control frames. Protocol errors close the connection, with the code Action
// Cable's websocket-driver would close it with.
module Campfire.Cable.Socket

open System
open System.Buffers
open System.Collections.Generic
open System.IO
open System.IO.Compression
open System.Numerics
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http

/// The largest message a client may send (Action Cable commands are a few hundred bytes), where
/// websocket-driver takes up to 64 MiB (`WebSocket::Driver::MAX_LENGTH`).
[<Literal>]
let MaxMessage = 1048576

/// Smaller frames go out uncompressed even when compression is on: not worth a deflate stream.
[<Literal>]
let internal MinCompressed = 256

/// The extension response: every message compressed on its own, in both directions.
[<Literal>]
let internal DeflateResponse = "permessage-deflate; server_no_context_takeover; client_no_context_takeover"

/// RFC 6455's key suffix.
[<Literal>]
let private AcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"

/// A sync-flushed deflate stream ends with these; RFC 7692 leaves them off the wire.
let private deflateTail = [| 0uy; 0uy; 0xffuy; 0xffuy |]

[<Literal>]
let private OpContinuation = 0x0uy

[<Literal>]
let private OpText = 0x1uy

[<Literal>]
let private OpBinary = 0x2uy

[<Literal>]
let private OpClose = 0x8uy

[<Literal>]
let private OpPing = 0x9uy

[<Literal>]
let private OpPong = 0xAuy

// websocket-driver's codes for what a client can get wrong (`WebSocket::Driver::Hybi::ERRORS`).

/// Anything not covered below.
[<Literal>]
let ProtocolError = 1002us

/// An unmasked frame.
[<Literal>]
let Unacceptable = 1003us

/// A text message that isn't UTF-8.
[<Literal>]
let EncodingError = 1007us

/// A message over `MaxMessage`.
[<Literal>]
let TooLarge = 1009us

/// The client broke the protocol; the connection closes with `code`.
exception internal ProtocolFailure of code: uint16

// --- Frames ------------------------------------------------------------------------------------

/// Raw deflate at level 6, sync-flushed, without the trailing empty block's 4 bytes.
let internal deflate (input: ReadOnlySpan<byte>) : byte[] =
    use output = new MemoryStream(input.Length / 3 + 64)
    use stream = new DeflateStream(output, CompressionLevel.Optimal, true)
    stream.Write input
    stream.Flush()
    // Disposing the stream would append a final block; what was flushed is the message.
    let mutable length = int output.Length
    let buffer = output.GetBuffer()
    if length >= 4 && buffer.AsSpan(length - 4, 4).SequenceEqual(ReadOnlySpan deflateTail) then length <- length - 4
    buffer.AsSpan(0, length).ToArray()

/// A text frame's payload, shared by every connection that sends it and compressed at most once.
[<Sealed>]
type Frame(bytes: byte[]) =
    let mutable deflated: byte[] | null = null

    /// The text, as UTF-8.
    member _.Bytes: byte[] = bytes

    member _.Text: string = Encoding.UTF8.GetString bytes

    /// The payload for a socket with compression on: the deflated bytes when compressing is worth
    /// it, null when it isn't. Compressed on first use, once, whichever connection asks first.
    member this.Deflated: byte[] | null =
        if bytes.Length < MinCompressed then
            null
        else
            match Volatile.Read(&deflated) with
            | null ->
                lock this (fun () ->
                    match deflated with
                    | null -> Volatile.Write(&deflated, deflate (ReadOnlySpan bytes))
                    | _ -> ()
                    deflated)
            | ready -> ready

    static member OfString(text: string) : Frame = Frame(Encoding.UTF8.GetBytes text)

    override this.ToString() = $"Frame({this.Text})"

// --- Handshake ---------------------------------------------------------------------------------

/// The server's side of an accepted opening handshake.
type Handshake = { Accept: string; Deflate: bool }

module Handshake =
    /// A `permessage-deflate` offer we can answer with `DeflateResponse`: any parameters but a
    /// server window smaller than zlib's 15 bits (browsers don't ask for one).
    let private acceptableDeflateOffer (offer: string) : bool =
        let parts = offer.Split ';' |> Array.map (fun p -> p.Trim())
        if parts[0] <> "permessage-deflate" then
            false
        else
            parts
            |> Array.skip 1
            |> Array.forall (fun param ->
                let name, (value: string | null) =
                    match param.IndexOf '=' with
                    | -1 -> param, null
                    | eq -> param.Substring(0, eq).Trim(), param.Substring(eq + 1).Trim().Trim('"')
                match name with
                | "server_no_context_takeover"
                | "client_no_context_takeover" -> isNull value
                | "client_max_window_bits" ->
                    match value with
                    | null -> true
                    | value ->
                        match Byte.TryParse(value, Globalization.NumberStyles.AllowLeadingSign, Globalization.CultureInfo.InvariantCulture) with
                        | true, bits -> bits >= 8uy && bits <= 15uy
                        | _ -> false
                | "server_max_window_bits" -> value = "15"
                | _ -> false)

    /// Checks the client's handshake (version 13 and a key; the caller has checked the method and
    /// `Upgrade`/`Connection` headers) and picks compression if one of its offers suits.
    let accept (headers: IHeaderDictionary) : Handshake option =
        let version = headers["Sec-WebSocket-Version"]
        let key = headers["Sec-WebSocket-Key"]
        if version.Count = 0 || key.Count = 0 then
            None
        else
            let key = nonNull key[0]
            // The 16 random bytes, in canonical padded Base64 (the `base64` crate's STANDARD).
            let validKey =
                key.Length = 24
                && (let decoded = Array.zeroCreate<byte> 18
                    let mutable written = 0
                    Convert.TryFromBase64String(key, decoded, &written)
                    && written = 16
                    && Convert.ToBase64String(decoded, 0, 16) = key)
            if version[0] <> "13" || not validKey then
                None
            else
                let input = Encoding.ASCII.GetBytes(key + AcceptGuid)
                let accept = Convert.ToBase64String(SHA1.HashData input)
                let extensions = headers["Sec-WebSocket-Extensions"]
                let mutable deflate = false
                for line in extensions do
                    match line with
                    | null -> ()
                    | line ->
                        for offer in line.Split ',' do
                            if acceptableDeflateOffer offer then deflate <- true
                Some { Accept = accept; Deflate = deflate }

    /// The 101 response's WebSocket headers (the caller adds `Sec-WebSocket-Protocol`).
    let responseHeaders (handshake: Handshake) (headers: IHeaderDictionary) : unit =
        headers["Upgrade"] <- "websocket"
        headers["Connection"] <- "upgrade"
        headers["Sec-WebSocket-Accept"] <- handshake.Accept
        if handshake.Deflate then headers["Sec-WebSocket-Extensions"] <- DeflateResponse

// --- Reading -----------------------------------------------------------------------------------

/// What a client sent.
type Incoming =
    | Text of string
    | Binary
    | Ping of byte[]
    | Pong
    /// A close frame, with its status code if it had one.
    | Close of code: uint16 option

/// Why a `Reader` stopped.
type ReadError =
    /// Reading the socket failed, or the client went away (`EndOfStreamException`).
    | Io of exn
    /// The client broke the protocol: the connection closes with `code`, the one websocket-driver
    /// (under Action Cable) closes with for the same failure.
    | ProtocolViolation of code: uint16

let private fail (code: uint16) : 'a = raise (ProtocolFailure code)

/// A compressed message's text. Invalid deflate data is a protocol error: Action Cable doesn't
/// negotiate compression, so there's no websocket-driver code to match.
let internal inflate (data: ReadOnlySpan<byte>) : byte[] =
    let input = Array.zeroCreate<byte> (data.Length + 4)
    data.CopyTo(Span input)
    Buffer.BlockCopy(deflateTail, 0, input, data.Length, 4)
    use source = new MemoryStream(input, false)
    use stream = new DeflateStream(source, CompressionMode.Decompress)
    let mutable out = ArrayPool<byte>.Shared.Rent(max 256 (data.Length * 4))
    let mutable length = 0
    try
        try
            let mutable reading = true
            while reading do
                if out.Length - length < 1024 then
                    let grown = ArrayPool<byte>.Shared.Rent(out.Length * 2)
                    Buffer.BlockCopy(out, 0, grown, 0, length)
                    ArrayPool<byte>.Shared.Return out
                    out <- grown
                match stream.Read(out, length, out.Length - length) with
                | 0 -> reading <- false
                | n ->
                    length <- length + n
                    if length > MaxMessage then fail TooLarge
            out.AsSpan(0, length).ToArray()
        with :? InvalidDataException -> fail ProtocolError
    finally
        ArrayPool<byte>.Shared.Return out

/// A close frame's status code (RFC 6455 section 5.5.1, section 7.4): no payload, or a code a peer may send
/// followed by a UTF-8 reason.
let private closeCode (buffer: byte[]) (length: int) : uint16 option =
    let payload = ReadOnlySpan(buffer, 0, length)
    if payload.Length = 0 then
        None
    elif payload.Length = 1 then
        fail ProtocolError
    else
        let code = (uint16 payload[0] <<< 8) ||| uint16 payload[1]
        let valid = (code >= 1000us && code <= 1003us) || (code >= 1007us && code <= 1014us) || (code >= 3000us && code <= 4999us)
        let utf8 = System.Text.Unicode.Utf8.IsValid(payload.Slice 2)
        if not valid || not utf8 then fail ProtocolError else Some code

let private strictUtf8 = UTF8Encoding(false, true)

/// XORs `buffer[0..length)` with the 4-byte `mask`, repeating.
let private unmask (buffer: byte[]) (length: int) (mask: byte[]) : unit =
    let mutable i = 0
    if Vector.IsHardwareAccelerated && length >= Vector<byte>.Count then
        let width = Vector<byte>.Count
        let repeated = Array.init width (fun j -> mask[j % 4])
        let key = Vector<byte>(repeated)
        while i + width <= length do
            (Vector<byte>(buffer, i) ^^^ key).CopyTo(buffer, i)
            i <- i + width
    while i < length do
        buffer[i] <- buffer[i] ^^^ mask[i % 4]
        i <- i + 1

/// A fragmented message so far.
[<Sealed>]
type private Partial(opcode: byte, compressed: bool, data: byte[], length: int) =
    member val Opcode = opcode
    member val Compressed = compressed
    member val Data = data with get, set
    member val Length = length with get, set

    /// Adds a continuation frame's payload.
    member this.Append(payload: byte[], payloadLength: int) : unit =
        if this.Data.Length - this.Length < payloadLength then
            let grown = ArrayPool<byte>.Shared.Rent(max (this.Data.Length * 2) (this.Length + payloadLength))
            Buffer.BlockCopy(this.Data, 0, grown, 0, this.Length)
            ArrayPool<byte>.Shared.Return this.Data
            this.Data <- grown
        Buffer.BlockCopy(payload, 0, this.Data, this.Length, payloadLength)
        this.Length <- this.Length + payloadLength

/// A complete message: inflated if it was compressed, and for text, decoded as strict UTF-8.
let private finishMessage (message: Partial) : Incoming =
    let data = ReadOnlySpan(message.Data, 0, message.Length)
    let inflated: byte[] | null = if message.Compressed then inflate data else null
    let bytes =
        match inflated with
        | null -> data
        | inflated -> ReadOnlySpan inflated
    match message.Opcode with
    | OpText ->
        try
            Text(strictUtf8.GetString bytes)
        with :? DecoderFallbackException ->
            fail EncodingError
    | _ -> Binary

/// Reads client frames. It keeps no buffer between messages: frames are read header first, then
/// exactly their payload, so an idle socket holds a few bytes.
[<Sealed>]
type Reader(io: Stream, deflate: bool) =
    let head = Array.zeroCreate<byte> 14
    let mask = Array.zeroCreate<byte> 4
    /// Kept across the control frames that may arrive between a message's fragments.
    let mutable partial: Partial | null = null

    let readExact (buffer: byte[]) (offset: int) (count: int) (ct: CancellationToken) : Task =
        task {
            let mutable got = 0
            while got < count do
                let! n = io.ReadAsync(Memory<byte>(buffer, offset + got, count - got), ct)
                if n = 0 then raise (EndOfStreamException())
                got <- got + n
        }

    /// A frame's payload, unmasked, in a pooled buffer. It's grown as bytes arrive rather than
    /// allocated from the header's claim, so a client can't make the server hold a megabyte per
    /// socket by announcing a frame it never sends.
    let readPayload (length: int) (ct: CancellationToken) : Task<struct (byte[] * int)> =
        task {
            let mutable buffer = ArrayPool<byte>.Shared.Rent(min length 65536)
            let mutable got = 0
            while got < length do
                if got = buffer.Length then
                    let grown = ArrayPool<byte>.Shared.Rent(min length (buffer.Length * 2))
                    Buffer.BlockCopy(buffer, 0, grown, 0, got)
                    ArrayPool<byte>.Shared.Return buffer
                    buffer <- grown
                let! n = io.ReadAsync(Memory<byte>(buffer, got, min (length - got) (buffer.Length - got)), ct)
                if n = 0 then raise (EndOfStreamException())
                got <- got + n
            unmask buffer length mask
            return struct (buffer, length)
        }

    /// A frame's header, checked in the order websocket-driver checks it (`Hybi#parse_opcode`,
    /// `#parse_length`, `#check_frame_length`), which decides the close code when a frame is wrong
    /// in more than one way. Returns (fin, compressed, opcode, length).
    let readHeader (ct: CancellationToken) : Task<struct (bool * bool * byte * int)> =
        task {
            do! readExact head 0 2 ct
            let fin = head[0] &&& 0x80uy <> 0uy
            let compressed = head[0] &&& 0x40uy <> 0uy
            let opcode = head[0] &&& 0x0fuy
            let control = opcode >= 0x8uy
            // RSV1 may only start a message, and only with compression negotiated.
            let reserved = head[0] &&& 0x30uy <> 0uy || (compressed && not (deflate && (opcode = OpText || opcode = OpBinary)))
            let known =
                opcode = OpContinuation || opcode = OpText || opcode = OpBinary || opcode = OpClose || opcode = OpPing || opcode = OpPong
            let interruptsAMessage = (opcode = OpText || opcode = OpBinary) && not (isNull partial)
            if reserved || not known || (control && not fin) || interruptsAMessage then fail ProtocolError
            if head[1] &&& 0x80uy = 0uy then fail Unacceptable
            let! length =
                task {
                    match head[1] &&& 0x7fuy with
                    | 126uy ->
                        do! readExact head 2 2 ct
                        return uint64 ((uint16 head[2] <<< 8) ||| uint16 head[3])
                    | 127uy ->
                        do! readExact head 2 8 ct
                        return Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(ReadOnlySpan(head, 2, 8))
                    | n -> return uint64 n
                }
            if control && length > 125UL then fail ProtocolError
            let soFar =
                match partial with
                | null -> 0UL
                | p when not control -> uint64 p.Length
                | _ -> 0UL
            // `saturating_add`: a length near u64::MAX is still too large.
            if length > uint64 MaxMessage || soFar + length > uint64 MaxMessage then fail TooLarge
            do! readExact mask 0 4 ct
            return struct (fin, compressed, opcode, int length)
        }

    /// The next message or control frame, reassembling fragments and decompressing. Raises
    /// `ProtocolFailure` for a protocol error and an `IOException`, `EndOfStreamException` or
    /// `OperationCanceledException` for a failed read; `Next` turns them into a `ReadError`.
    member private _.Read(ct: CancellationToken) : Task<Incoming> =
        task {
            let mutable result = ValueNone
            while result.IsNone do
                let! struct (fin, compressed, opcode, length) = readHeader ct
                let! struct (payload, payloadLength) = readPayload length ct
                let mutable keep = false
                try
                    match opcode with
                    | OpClose -> result <- ValueSome(Close(closeCode payload payloadLength))
                    | OpPing -> result <- ValueSome(Ping(payload.AsSpan(0, payloadLength).ToArray()))
                    | OpPong -> result <- ValueSome Pong
                    | OpText
                    | OpBinary ->
                        // The payload buffer becomes the message's, and is returned with it.
                        partial <- Partial(opcode, compressed, payload, payloadLength)
                        keep <- true
                    | _ ->
                        match partial with
                        | null -> fail ProtocolError // a continuation of nothing
                        | p -> p.Append(payload, payloadLength)
                finally
                    if not keep then ArrayPool<byte>.Shared.Return payload
                if result.IsNone && fin then
                    let message = nonNull partial
                    partial <- null
                    try
                        result <- ValueSome(finishMessage message)
                    finally
                        ArrayPool<byte>.Shared.Return message.Data
            return result.Value
        }

    /// The next message or control frame, or why the socket stopped.
    member this.Next(ct: CancellationToken) : Task<Result<Incoming, ReadError>> =
        task {
            try
                let! incoming = this.Read ct
                return Ok incoming
            with
            | ProtocolFailure code -> return Error(ProtocolViolation code)
            | e -> return Error(Io e)
        }

// --- Writing -----------------------------------------------------------------------------------

/// How long a write may wait for a client to read before it fails. A client that stops reading
/// doesn't hold its connection's task forever.
let WriteTimeout = TimeSpan.FromSeconds 30.0

/// A final frame's header: FIN, RSV1 when `compressed`, `opcode`, and the payload length. Returns
/// how many bytes of `dest` it used.
let private writeHeader (dest: Span<byte>) (opcode: byte) (compressed: bool) (length: int) : int =
    dest[0] <- 0x80uy ||| (if compressed then 0x40uy else 0uy) ||| opcode
    if length < 126 then
        dest[1] <- byte length
        2
    elif length <= int UInt16.MaxValue then
        dest[1] <- 126uy
        dest[2] <- byte (length >>> 8)
        dest[3] <- byte length
        4
    else
        dest[1] <- 127uy
        Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(dest.Slice(2, 8), uint64 length)
        10

/// Writes server frames: unmasked, each as its header plus its (shared) payload, a batch to one
/// write. A write that hasn't completed after `timeout` fails: `abort` is called to cut the
/// connection and the write raises `TimeoutException`.
[<Sealed>]
type Writer(io: Stream, deflate: bool, timeout: TimeSpan, abort: unit -> unit) =
    new(io: Stream, deflate: bool) = Writer(io, deflate, WriteTimeout, ignore)

    member private _.Write(bytes: ReadOnlyMemory<byte>) : Task =
        task {
            let write = io.WriteAsync bytes
            if write.IsCompleted then
                do! write
            else
                use gave = new CancellationTokenSource()
                let pending = write.AsTask()
                let! first = Task.WhenAny(pending, Task.Delay(timeout, gave.Token))
                if obj.ReferenceEquals(first, pending) then
                    gave.Cancel()
                    do! pending
                else
                    abort ()
                    raise (TimeoutException "a socket write timed out")
        }

    /// Writes `frames` as text messages, in order, in one write.
    member this.Send(frames: IReadOnlyList<Frame>) : Task =
        task {
            let payloads = ArrayPool<byte[]>.Shared.Rent frames.Count
            let mutable total = 0
            try
                for i in 0 .. frames.Count - 1 do
                    let frame = frames[i]
                    let payload =
                        if deflate then
                            match frame.Deflated with
                            | null -> frame.Bytes
                            | deflated -> deflated
                        else
                            frame.Bytes
                    payloads[i] <- payload
                    total <- total + 10 + payload.Length
                let buffer = ArrayPool<byte>.Shared.Rent total
                try
                    let mutable at = 0
                    for i in 0 .. frames.Count - 1 do
                        let payload = payloads[i]
                        let compressed = deflate && not (obj.ReferenceEquals(payload, frames[i].Bytes))
                        at <- at + writeHeader (Span(buffer, at, 10)) OpText compressed payload.Length
                        Buffer.BlockCopy(payload, 0, buffer, at, payload.Length)
                        at <- at + payload.Length
                    do! this.Write(ReadOnlyMemory(buffer, 0, at))
                finally
                    ArrayPool<byte>.Shared.Return buffer
            finally
                ArrayPool<byte[]>.Shared.Return(payloads, true)
        }

    member private this.Control(opcode: byte, payload: byte[]) : Task =
        let buffer = Array.zeroCreate<byte> (10 + payload.Length)
        let used = writeHeader (Span buffer) opcode false payload.Length
        Buffer.BlockCopy(payload, 0, buffer, used, payload.Length)
        this.Write(ReadOnlyMemory(buffer, 0, used + payload.Length))

    member this.Pong(payload: byte[]) : Task = this.Control(OpPong, payload)

    /// A close frame with `code` and no reason.
    member this.Close(code: uint16) : Task = this.Control(OpClose, [| byte (code >>> 8); byte code |])

    /// The reply to a client's close frame, echoing its code (none if it sent none), which
    /// completes the closing handshake.
    member this.CloseReply(code: uint16 option) : Task =
        match code with
        | Some code -> this.Close code
        | None -> this.Control(OpClose, [||])
