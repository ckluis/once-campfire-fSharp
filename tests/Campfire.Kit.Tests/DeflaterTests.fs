// Ports of the #[cfg(test)] module in rust/crates/kit/src/deflater.rs and the `Generations` tests in
// rust/crates/kit/src/deflater/splice.rs
module Campfire.Kit.Tests.DeflaterTests

open System
open System.IO
open System.IO.Compression
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open Xunit
open Campfire.Kit
open Campfire.Kit.Tests.Helpers

let private best (header: string) : string voption =
    Deflater.selectBestEncoding [ "gzip"; "identity" ] (Deflater.parseAcceptEncoding header)

[<Fact>]
let ``encoding selection`` () =
    Assert.Equal(ValueSome "gzip", best "gzip, deflate, br")
    Assert.Equal(ValueSome "identity", best "")
    Assert.Equal(ValueSome "identity", best "br")
    Assert.Equal(ValueSome "identity", best "gzip;q=0")
    Assert.Equal(ValueSome "identity", best "identity;q=0.5, gzip;q=0.1")
    Assert.Equal(ValueSome "gzip", best "*")
    Assert.Equal(ValueNone, best "identity;q=0, *;q=0")
    Assert.Equal(ValueNone, best "gzip;q=0, identity;q=0")

[<Fact>]
let ``q values read like rack`` () =
    // `Rack::Request#accept_encoding` and `select_best_encoding` in the reference, where
    // `identity;q=` is a 200 and `identity;q=0` a 406.
    Assert.Equal(ValueSome "identity", best "identity;q=")
    Assert.Equal(ValueNone, best "identity;q=0")
    Assert.Equal(ValueSome "gzip", best "gzip;q=")
    Assert.Equal(ValueSome "gzip", best "gzip;q=abc")
    Assert.Equal(ValueSome "gzip", best "gzip;Q=0.5, identity;q=0.9")
    Assert.Equal(ValueSome "gzip", best "gzip;q=.5")
    Assert.Equal(ValueSome "identity", best "gzip;q=.")
    Assert.Equal(ValueSome "identity", best "gzip;q=0..5, identity;q=0.1")
    Assert.Equal(ValueSome "identity", best "gzip;q=0.5.1, identity;q=0.6")
    Assert.Equal<(string * float) list>(
        [ "gzip", 0.5; "identity", 1.0; "br", 1.0 ],
        Deflater.parseAcceptEncoding "gzip;q=0.5.1, identity;q=, br;q=1."
    )

[<Fact>]
let ``words`` () =
    Assert.True(Deflater.hasWord "no-transform, public" "no-transform")
    Assert.False(Deflater.hasWord "xno-transform" "no-transform")
    Assert.True(Deflater.hasWord "identity" "identity")

let private gunzip (bytes: byte[]) : byte[] =
    use input = new MemoryStream(bytes)
    use gzip = new GZipStream(input, CompressionMode.Decompress)
    use output = new MemoryStream()
    gzip.CopyTo output
    output.ToArray()

/// What `GzipWriter` sends for `chunks`, one write each, as the host would see it.
let private streamed (chunks: byte[] list) (mtime: uint32) : byte[] =
    use output = new MemoryStream()
    let writer = new Deflater.GzipWriter(output, mtime)
    task {
        for chunk in chunks do
            do! writer.Write(ReadOnlyMemory<byte> chunk)
        do! writer.Finish()
    }
    |> fun t -> t.GetAwaiter().GetResult()
    output.ToArray()

[<Fact>]
let ``gzip round trip and empty bodies`` () =
    for input in [ Array.empty<byte>; Encoding.UTF8.GetBytes "hello world" ] do
        let bytes = streamed [ input ] 0u
        Assert.True(bytes.Length >= 20)
        Assert.Equal<byte[]>(input, gunzip bytes)
        // `Zlib::GzipWriter`'s header: the Unix OS code and the given mtime.
        Assert.Equal<byte[]>([| 0x1fuy; 0x8buy; 8uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 3uy |], bytes[0..9])
    let stamped = streamed [ Encoding.UTF8.GetBytes "x" ] 0x01020304u
    Assert.Equal<byte[]>([| 4uy; 3uy; 2uy; 1uy |], stamped[4..7])
    // One member, whole, is what the same single chunk streams as.
    let whole = Deflater.gzipMember (ReadOnlySpan<byte>(Encoding.UTF8.GetBytes "hello world")) 0u
    Assert.Equal<byte[]>(streamed [ Encoding.UTF8.GetBytes "hello world" ] 0u, whole)

let private digestOf (body: byte[]) : BodyDigest =
    let hash = SHA256.HashData body
    { A = BitConverter.ToUInt64(hash, 0)
      B = BitConverter.ToUInt64(hash, 8)
      C = BitConverter.ToUInt64(hash, 16)
      D = BitConverter.ToUInt64(hash, 24) }

let private sameBuffer (a: ReadOnlyMemory<byte>) (b: ReadOnlyMemory<byte>) : bool =
    match MemoryMarshal.TryGetArray a, MemoryMarshal.TryGetArray b with
    | (true, x), (true, y) -> Object.ReferenceEquals(x.Array, y.Array) && x.Offset = y.Offset
    | _ -> false

[<Fact>]
let ``digested bodies are gzipped once`` () =
    let body = Encoding.UTF8.GetBytes(String.replicate 300 "<a href=\"/rooms/1\">Room</a>")
    let digest = digestOf body
    let first = Deflater.gzipDigested (ReadOnlyMemory body) digest 0u
    Assert.Equal<byte[]>(streamed [ body ] 0u, first.ToArray())
    let kept = (Deflater.keptGzip digest 0u).Value
    let again = Deflater.gzipDigested (ReadOnlyMemory body) digest 0u
    Assert.True(sameBuffer kept again, "not gzipped again")

    let other = Encoding.UTF8.GetBytes(String.replicate 300 "<a href=\"/rooms/2\">Room</a>")
    let gzip = Deflater.gzipDigested (ReadOnlyMemory other) (digestOf other) 0u
    Assert.Equal<byte[]>(streamed [ other ] 0u, gzip.ToArray())

[<Fact>]
let ``gzip round trip of a page larger than the window`` () =
    // Long repeats and several windows' worth of input, in chunks: the match comparison, the
    // window slide and the CRC all run.
    let hello = "hello there "
    let page =
        [| for n in 0..2999 do
               let repeated = String.replicate (n % 13) hello
               yield! Encoding.UTF8.GetBytes("<div id=\"message_" + string n + "\" class=\"message\">" + repeated + "</div>\n") |]
    Assert.True(page.Length > 4 * 32 * 1024)
    let chunks = page |> Array.chunkBySize 40_000 |> List.ofArray
    Assert.Equal<byte[]>(page, gunzip (streamed chunks 0u))
    Assert.Equal<byte[]>(page, gunzip (Deflater.gzipMember (ReadOnlySpan<byte> page) 0u))

[<Fact>]
let ``the crc matches the reference polynomial on every path`` () =
    // gzip's trailer carries it, so any decoder checks it: a corrupt CRC would fail to gunzip, and
    // these sizes cover the 8-byte steps and the tails of both implementations.
    for size in [ 0; 1; 7; 8; 9; 15; 16; 17; 1000; 4097 ] do
        let data = Array.init size (fun i -> byte (i * 31 + 7))
        Assert.Equal<byte[]>(data, gunzip (Deflater.gzipMember (ReadOnlySpan<byte> data) 0u))
    // The standard check value for "123456789".
    Assert.Equal(0xCBF43926u, Deflater.crc32Update 0u (ReadOnlySpan<byte>(Encoding.ASCII.GetBytes "123456789")))
    // Updating in pieces equals updating at once.
    let data = Array.init 5000 (fun i -> byte (i * 13))
    let once = Deflater.crc32Update 0u (ReadOnlySpan<byte> data)
    let pieces = Deflater.crc32Update (Deflater.crc32Update 0u (ReadOnlySpan<byte>(data, 0, 1234))) (ReadOnlySpan<byte>(data, 1234, 3766))
    Assert.Equal(once, pieces)

// splice.rs

[<Fact>]
let ``generations stay within their budget and keep what is read`` () =
    let budget = 64 * 1024
    let size = 1024
    let map = Generations<byte, int>(budget, (fun _ size -> size))
    for n in 0..255 do
        map.Insert(byte n, size)
        Assert.True((map.Get(0uy, ignore)).IsSome, "what's read stays")
        let held = map.Count * size
        // Each generation may overshoot half the budget by the entry that filled it.
        Assert.True(held <= budget + 2 * size, $"{held} bytes held")
    Assert.True((map.Get(255uy, ignore)).IsSome, "the latest is kept")
    Assert.True((map.Get(1uy, ignore)).IsNone, "what isn't read ages out")

    let map = Generations<byte, int>(budget, (fun _ size -> size))
    map.Insert(1uy, size)
    map.Insert(1uy, size)
    Assert.Equal(size, map.YoungCost)

// the decision, as the adapter asks for it

let private responseWith (status: int) (headers: (string * string) list) : Response =
    let response = Response(status)
    for (k, v) in headers do
        response.Headers.Append(k, v)
    response.SetBody "hello" |> ignore
    response

[<Fact>]
let ``should deflate follows rack`` () =
    let should r app = Deflater.shouldDeflate r app
    Assert.True(should (responseWith 200 []) false)
    Assert.False(should (responseWith 204 []) false)
    Assert.False(should (responseWith 304 []) false)
    Assert.False(should (responseWith 101 []) false)
    Assert.False(should (responseWith 200 [ "cache-control", "no-transform, private" ]) false)
    Assert.False(should (responseWith 200 [ "content-encoding", "br" ]) false)
    Assert.True(should (responseWith 200 [ "content-encoding", "identity" ]) false)
    // An empty length the app set (error pages, static files) is left alone; a rendered one is gzipped.
    Assert.False(should (responseWith 200 [ "content-length", "0" ]) true)
    Assert.True(should (responseWith 200 [ "content-length", "0" ]) false)
    let served = responseWith 200 [ "content-length", "0" ]
    served.StaticFile <- true
    Assert.False(should served false)

[<Fact>]
let ``apply gzips adds vary and answers 406 when nothing is acceptable`` () =
    let gzipped = responseWith 200 [ "last-modified", "Thu, 01 Jan 1970 00:01:40 GMT"; "content-length", "5" ]
    match Deflater.apply "gzip, deflate" gzipped false with
    | Gzip mtime -> Assert.Equal(100u, mtime)
    | other -> failwith $"{other}"
    Assert.Equal("gzip", gzipped.GetHeader "content-encoding")
    Assert.Null(gzipped.GetHeader "content-length")
    Assert.Equal("Accept-Encoding", gzipped.GetHeader "vary")

    let varying = responseWith 200 [ "vary", "Accept" ]
    Deflater.apply "gzip" varying false |> ignore
    Assert.Equal("Accept,Accept-Encoding", varying.GetHeader "vary")
    let varied = responseWith 200 [ "vary", "accept-encoding" ]
    Deflater.apply "gzip" varied false |> ignore
    Assert.Equal("accept-encoding", varied.GetHeader "vary")

    let identity = responseWith 200 []
    Assert.Equal(Unchanged, Deflater.apply "identity" identity false)
    Assert.Null(identity.GetHeader "content-encoding")

    Assert.Equal(NotAcceptable, Deflater.apply "identity;q=0" (responseWith 200 []) false)
    let replacement = Deflater.notAcceptable "/x?y=1"
    Assert.Equal(406, replacement.Status)
    Assert.Equal("An acceptable encoding for the requested resource /x?y=1 could not be found.", bodyText replacement)
    Assert.Equal("text/plain", replacement.GetHeader "content-type")
