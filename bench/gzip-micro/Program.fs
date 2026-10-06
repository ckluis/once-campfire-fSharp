// Tier 1 micro-benchmark of Deflater.gzipMember on small bodies (Phase 7, unit 7.4).
//
//   bench/micro-linux gzip-micro [SECONDS]
//
// Bodies of 0.5 to 32 KB (ERB from the reference views, as the app's small answers are), gzipped by the DeflateStream version the
// kit used before and by Deflater.gzipMember now: the two must decode to the same bytes (and, where the window does not matter, be the
// same bytes); then ns and bytes allocated per call on this thread, best of 5 windows.
module GzipMicro

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Buffers.Binary
open Campfire.Kit

let private table =
    Array.init 256 (fun n ->
        let mutable c = uint32 n
        for _ in 0..7 do c <- if c &&& 1u <> 0u then 0xEDB88320u ^^^ (c >>> 1) else c >>> 1
        c)

let private crc32 (data: ReadOnlySpan<byte>) : uint32 =
    let mutable c = 0xFFFFFFFFu
    for b in data do c <- table[int ((c ^^^ uint32 b) &&& 0xFFu)] ^^^ (c >>> 8)
    ~~~c

let private before (body: ReadOnlySpan<byte>) (mtime: uint32) : byte[] =
    use output = new MemoryStream(body.Length / 3 + 64)
    let h = Array.zeroCreate<byte> 10
    h[0] <- 0x1fuy
    h[1] <- 0x8buy
    h[2] <- 8uy
    BinaryPrimitives.WriteUInt32LittleEndian(Span<byte>(h, 4, 4), mtime)
    h[9] <- 3uy
    output.Write h
    use deflate = new DeflateStream(output, CompressionLevel.Optimal, true)
    deflate.Write body
    deflate.Flush()
    deflate.Dispose()
    let t = Array.zeroCreate<byte> 8
    BinaryPrimitives.WriteUInt32LittleEndian(Span<byte>(t, 0, 4), crc32 body)
    BinaryPrimitives.WriteUInt32LittleEndian(Span<byte>(t, 4, 4), uint32 body.Length)
    output.Write t
    output.ToArray()

let private gunzip (bytes: byte[]) : byte[] =
    use input = new MemoryStream(bytes)
    use stream = new GZipStream(input, CompressionMode.Decompress)
    use output = new MemoryStream()
    stream.CopyTo output
    output.ToArray()

let private measure (secs: float) (f: unit -> byte[]) : float * float =
    for _ in 1..2000 do f () |> ignore
    let mutable best = Double.MaxValue
    let mutable bytes = 0.0
    for _ in 1..5 do
        let a0 = GC.GetAllocatedBytesForCurrentThread()
        let sw = Stopwatch.StartNew()
        let mutable n = 0
        while sw.Elapsed.TotalSeconds < secs / 5.0 do
            for _ in 1..200 do f () |> ignore
            n <- n + 200
        let ns = sw.Elapsed.TotalMilliseconds * 1e6 / float n
        let alloc = float (GC.GetAllocatedBytesForCurrentThread() - a0) / float n
        if ns < best then
            best <- ns
            bytes <- alloc
    best, bytes

[<EntryPoint>]
let main argv =
    let secs = if argv.Length > 0 then float argv[0] else 2.0
    let repo = Directory.GetCurrentDirectory()
    let dir = Path.Combine(repo, "reference/app/views")
    let text = String.Join("\n", Directory.GetFiles(dir, "*.erb", SearchOption.AllDirectories) |> Array.sort |> Array.map File.ReadAllText)
    let all = Text.Encoding.UTF8.GetBytes text
    printfn "corpus %d bytes" all.Length
    for size in [ 512; 2000; 8000; 32000 ] do
        let body = all[10000 .. 10000 + size - 1]
        let a = before (ReadOnlySpan body) 0u
        let b = Deflater.gzipMember (ReadOnlySpan body) 0u
        let same = a = b
        if gunzip a <> body || gunzip b <> body then failwith "round trip"
        let ns0, b0 = measure secs (fun () -> before (ReadOnlySpan body) 0u)
        let ns1, b1 = measure secs (fun () -> Deflater.gzipMember (ReadOnlySpan body) 0u)
        printfn "%6d B  DeflateStream %7.0f ns %6.0f B alloc (%d out)   gzipMember %7.0f ns %6.0f B alloc (%d out)  identical=%b" size ns0 b0 a.Length ns1 b1 b.Length same
    0
