// Port of the tests in rust/crates/cable/src/socket.rs
module Campfire.Cable.Tests.SocketTests

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Xunit
open Campfire.Cable
open Campfire.Cable.Socket
open Campfire.Cable.Tests.Support

let private deflated (text: string) : byte[] = deflate (ReadOnlySpan(Encoding.UTF8.GetBytes text))

/// Every message the reader gives before the bytes run out, or its first error.
let private readAll (bytes: byte[]) (deflate: bool) : Task<Result<Incoming, ReadError> list> =
    task {
        let reader = Reader(new MemoryStream(bytes), deflate)
        let out = Collections.Generic.List<Result<Incoming, ReadError>>()
        let mutable reading = true
        while reading do
            match! reader.Next CancellationToken.None with
            | Error(Io(:? EndOfStreamException)) -> reading <- false
            | result ->
                out.Add result
                if Result.isError result then reading <- false
        return List.ofSeq out
    }

let private text (s: string) = Encoding.UTF8.GetBytes s

[<Fact>]
let ``reads text, fragments, compressed messages and control frames`` () =
    task {
        let bytes =
            Array.concat
                [ clientFrame 0x1uy true false (text """{"command":"subscribe"}""")
                  clientFrame 0x1uy false false (text "hel")
                  clientFrame 0x9uy true false (text "p")
                  clientFrame 0x0uy true false (text "lo ☃")
                  clientFrame 0x1uy true true (deflated (String.replicate 100 "compressed "))
                  clientFrame 0x8uy true false (Array.append [| 0x03uy; 0xe9uy |] (text "going away")) ]
        let! read = readAll bytes true
        let read = read |> List.map (function Ok m -> m | Error e -> failwith $"{e}")
        Assert.Equal<Incoming list>(
            [ Text """{"command":"subscribe"}"""
              Ping(text "p")
              Text "hello ☃"
              Text(String.replicate 100 "compressed ")
              Close(Some 1001us) ],
            read
        )
    }

/// Each closes with websocket-driver's code for it.
[<Fact>]
let ``rejects protocol errors`` () =
    task {
        let half = Array.create (MaxMessage / 2 + 1) (byte 'a')
        let cases: (string * byte[] * bool * uint16) list =
            [ "unmasked", [| 0x81uy; 0x02uy; byte 'h'; byte 'i' |], false, Unacceptable
              "unmasked, with a reserved opcode", [| 0x83uy; 0x01uy; byte 'x' |], false, ProtocolError
              "compressed, not negotiated", clientFrame 0x1uy true true (deflated "hi"), false, ProtocolError
              "compressed continuation",
              Array.append (clientFrame 0x1uy false true [||]) (clientFrame 0x0uy true true [||]),
              true,
              ProtocolError
              "compressed ping", clientFrame 0x9uy true true (text "x"), true, ProtocolError
              "invalid deflate data", clientFrame 0x1uy true true (Array.create 8 0xffuy), true, ProtocolError
              "not UTF-8", clientFrame 0x1uy true false [| 0xffuy; 0xfeuy |], false, EncodingError
              "nothing to continue", clientFrame 0x0uy true false (text "x"), false, ProtocolError
              "a new message mid-message",
              Array.append (clientFrame 0x1uy false false (text "x")) (clientFrame 0x1uy true false (text "y")),
              false,
              ProtocolError
              "fragmented control frame", clientFrame 0x9uy false false (text "x"), false, ProtocolError
              "long control frame", clientFrame 0x9uy true false (Array.zeroCreate 126), false, ProtocolError
              "reserved opcode", clientFrame 0x3uy true false (text "x"), false, ProtocolError
              "too large", clientFrame 0x1uy true false (Array.create (MaxMessage + 1) (byte 'a')), false, TooLarge
              "too large in fragments",
              Array.append (clientFrame 0x1uy false false half) (clientFrame 0x0uy true false half),
              false,
              TooLarge
              "too large once inflated", clientFrame 0x1uy true true (deflated (String('a', MaxMessage + 1))), true, TooLarge
              "one-byte close", clientFrame 0x8uy true false [| 0x03uy |], false, ProtocolError
              "reserved close code", clientFrame 0x8uy true false [| 0x03uy; 0xeduy |], false, ProtocolError
              "close reason not UTF-8", clientFrame 0x8uy true false [| 0x03uy; 0xe8uy; 0xffuy |], false, ProtocolError ]
        for (case, bytes, deflate, code) in cases do
            let! read = readAll bytes deflate
            match List.tryLast read with
            | Some(Error(ProtocolViolation closed)) when closed = code -> ()
            | other -> failwith $"{case}: {read} ({other})"
    }

/// A fragment that would make its message too large is refused from its header, before its payload is
/// read.
[<Fact>]
let ``refuses a too large fragment from its header`` () =
    task {
        let half = Array.create (MaxMessage / 2 + 1) (byte 'a')
        let bytes = Array.append (clientFrame 0x1uy false false half) (clientFrame 0x0uy true false half)
        let! read = readAll bytes[.. bytes.Length - half.Length - 1] false
        match List.tryLast read with
        | Some(Error(ProtocolViolation code)) when code = TooLarge -> ()
        | _ -> failwith $"{read}"
    }

[<Fact>]
let ``writes shared frames, compressed only when worth it`` () =
    task {
        let small = Frame.OfString """{"type":"ping","message":1}"""
        let big = Frame.OfString("{\"identifier\":\"x\",\"message\":\"" + String.replicate 200 "<div>message</div>" + "\"}")
        for deflate in [ false; true ] do
            let out = new MemoryStream()
            do! Writer(out, deflate).Send [| small; big |]
            let out = out.ToArray()
            // Read them back as a client would: unmasked frames, RSV1 for compressed ones.
            let mutable at = 0
            let texts = Collections.Generic.List<bool * string>()
            while at < out.Length do
                let b0, b1 = out[at], out[at + 1]
                Assert.Equal(0x80uy, b0 &&& 0x80uy)
                Assert.Equal(0uy, b1 &&& 0x80uy) // server frames are unmasked
                let length, start =
                    match b1 &&& 0x7fuy with
                    | 126uy -> (int out[at + 2] <<< 8) ||| int out[at + 3], at + 4
                    | 127uy -> int (Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(ReadOnlySpan(out, at + 2, 8))), at + 10
                    | length -> int length, at + 2
                let payload = out[start .. start + length - 1]
                let compressed = b0 &&& 0x40uy <> 0uy
                texts.Add((compressed, Encoding.UTF8.GetString(if compressed then inflateMessage payload else payload)))
                at <- start + length
            Assert.Equal<(bool * string) list>([ false, small.Text; deflate, big.Text ], List.ofSeq texts)
        let compressed = nonNull big.Deflated
        Assert.True(compressed.Length < big.Bytes.Length / 10, $"{compressed.Length} bytes")
        Assert.Same(compressed, nonNull big.Deflated) // compressed once, shared by every socket that sends it
    }

let private handshake (extensions: string list) : Handshake option =
    let headers = HeaderDictionary()
    headers["Sec-WebSocket-Version"] <- "13"
    headers["Sec-WebSocket-Key"] <- "dGhlIHNhbXBsZSBub25jZQ=="
    if not extensions.IsEmpty then headers["Sec-WebSocket-Extensions"] <- Microsoft.Extensions.Primitives.StringValues(Array.ofList extensions)
    Handshake.accept headers

[<Fact>]
let ``handshakes and negotiates compression`` () =
    // RFC 6455's example key.
    Assert.Equal("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", (handshake []).Value.Accept)
    Assert.False (handshake []).Value.Deflate
    // What Chrome, Firefox and Safari offer.
    Assert.True (handshake [ "permessage-deflate; client_max_window_bits" ]).Value.Deflate
    Assert.True (handshake [ "x-webkit-deflate-frame"; "permessage-deflate" ]).Value.Deflate
    Assert.True (handshake [ "permessage-deflate; server_no_context_takeover; client_max_window_bits=10" ]).Value.Deflate
    Assert.False (handshake [ "permessage-deflate; server_max_window_bits=10" ]).Value.Deflate
    Assert.False (handshake [ "permessage-deflate; unknown" ]).Value.Deflate

    let headers = HeaderDictionary()
    headers["Sec-WebSocket-Version"] <- "8"
    headers["Sec-WebSocket-Key"] <- "dGhlIHNhbXBsZSBub25jZQ=="
    Assert.True((Handshake.accept headers).IsNone)

// Not in the Rust tests.

[<Fact>]
let ``a handshake with a key that isn't 16 bytes of canonical Base64 is refused`` () =
    for key in [ ""; "dGhlIHNhbXBsZSBub25jZQ"; "dGhlIHNhbXBsZSBub25jZR=="; "dGhlIHNhbXBsZQ==" ] do
        let headers = HeaderDictionary()
        headers["Sec-WebSocket-Version"] <- "13"
        headers["Sec-WebSocket-Key"] <- key
        Assert.True((Handshake.accept headers).IsNone, key)

[<Fact>]
let ``compression is concurrent-safe: one deflate for any number of writers`` () =
    let frame = Frame.OfString(String.replicate 400 "abc ")
    let seen = Array.zeroCreate<byte[]> 64
    Parallel.For(0, seen.Length, fun i -> seen[i] <- nonNull frame.Deflated) |> ignore
    for deflated in seen do
        Assert.Same(seen[0], deflated)

[<Fact>]
let ``a write that never completes is cut and fails after the timeout`` () =
    task {
        let aborted = ref false
        let stuck =
            { new Stream() with
                member _.CanRead = false
                member _.CanSeek = false
                member _.CanWrite = true
                member _.Length = 0L
                member _.Position with get () = 0L and set _ = ()
                member _.Flush() = ()
                member _.Read(_, _, _) = 0
                member _.Seek(_, _) = 0L
                member _.SetLength _ = ()
                member _.Write(_, _, _) = ()
                member _.WriteAsync(_: ReadOnlyMemory<byte>, ct: CancellationToken) = ValueTask(Task.Delay(Timeout.Infinite, ct)) }
        let writer = Writer(stuck, false, TimeSpan.FromMilliseconds 100.0, (fun () -> aborted.Value <- true))
        let! ex = Assert.ThrowsAsync<TimeoutException>(fun () -> (writer.Send [| Frame.OfString "x" |]).AsTask())
        ignore ex
        Assert.True aborted.Value
    }
