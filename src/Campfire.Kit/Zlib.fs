// The deflate half of .NET's zlib shim (libSystem.IO.Compression.Native, zlib-ng), called directly.
//
// `DeflateStream` builds a zlib stream per instance (window, hash tables, pending buffer: some 250 KB, the hash table
// zeroed) and tears it down on dispose, and offers no way to give it a smaller window. A response of a few KB, gzipped
// once, paid more for the stream's set-up than for the compression. Here the window is the smallest that holds the body
// (matches can reach no further back than the body is long, so the output is the same bytes whatever the window).
// Not a package: the shim's exports are what System.IO.Compression itself calls (pal_zlib.h).
module internal Campfire.Kit.Zlib

#nowarn "9" // the shim takes a pointer to its stream struct and to the buffers

open System
open System.Runtime.InteropServices
open Microsoft.FSharp.NativeInterop

/// `PAL_ZStream` (pal_zlib.h).
[<Struct; StructLayout(LayoutKind.Sequential)>]
type ZStream =
    val mutable NextIn: nativeint
    val mutable NextOut: nativeint
    val mutable Msg: nativeint
    val mutable InternalState: nativeint
    val mutable AvailIn: uint32
    val mutable AvailOut: uint32

[<DllImport("libSystem.IO.Compression.Native", EntryPoint = "CompressionNative_DeflateInit2_")>]
extern int private deflateInit2(ZStream& stream, int level, int ``method``, int windowBits, int memLevel, int strategy)

[<DllImport("libSystem.IO.Compression.Native", EntryPoint = "CompressionNative_Deflate")>]
extern int private deflate(ZStream& stream, int flush)

[<DllImport("libSystem.IO.Compression.Native", EntryPoint = "CompressionNative_DeflateEnd")>]
extern int private deflateEnd(ZStream& stream)

[<Literal>]
let private Ok = 0

[<Literal>]
let private StreamEnd = 1

[<Literal>]
let private BufError = -5

[<Literal>]
let private SyncFlush = 2

[<Literal>]
let private Finish = 4

/// The smallest raw-deflate window (9 to 15 bits) that reaches back over the whole of a body of `length` bytes: zlib matches no
/// further back than the window less 262 (`MIN_LOOKAHEAD`), so with this window the output is what a 32 KB window gives.
let windowFor (length: int) : int =
    let mutable bits = 9
    while bits < 15 && (1 <<< bits) - 262 < length do
        bits <- bits + 1
    bits

[<Literal>]
let private NoFlush = 0

/// Raw deflate of `first` and then `second` as one stream at level 6, ended with a sync flush (so on a byte boundary) and, when
/// `finish` is set, then with the final empty block: what `DeflateStream.Write` of each, `Flush`, and `Dispose` produce. Written
/// into `buffer` from `offset`: the number of bytes, or -1 when `buffer` is too small. `windowBits` is 9 to 15 (`windowFor` of
/// the whole input for the output a 32 KB window gives); `memLevel` is 8, zlib's default, which the block boundaries depend on.
let deflateInto (first: ReadOnlySpan<byte>) (second: ReadOnlySpan<byte>) (windowBits: int) (finish: bool) (buffer: byte[]) (offset: int) : int =
    let mutable z = ZStream()
    // Level 6 is `CompressionLevel.Optimal`'s; method 8 is deflate, strategy 0 the default; a negative window is a raw stream.
    if deflateInit2 (&z, 6, 8, -windowBits, 8, 0) <> Ok then failwith "deflateInit2 failed"
    try
        use input1 = fixed &MemoryMarshal.GetReference first
        use input2 = fixed &MemoryMarshal.GetReference second
        use out = fixed &buffer[offset]
        z.NextOut <- NativePtr.toNativeInt out
        z.AvailOut <- uint32 (buffer.Length - offset)
        let mutable code = Ok
        if first.Length > 0 then
            z.NextIn <- NativePtr.toNativeInt input1
            z.AvailIn <- uint32 first.Length
            code <- deflate (&z, NoFlush)
        if code = Ok && z.AvailIn = 0u && second.Length > 0 then
            z.NextIn <- NativePtr.toNativeInt input2
            z.AvailIn <- uint32 second.Length
            code <- deflate (&z, NoFlush)
        if code <> Ok && code <> BufError then failwithf "deflate failed (%d)" code
        // The sync flush takes in what is left and ends on a byte boundary; the finish adds the final empty block.
        let mutable result = -1
        if code = Ok && z.AvailIn = 0u then
            code <- deflate (&z, SyncFlush)
            if code = Ok && z.AvailOut <> 0u then
                if not finish then
                    result <- buffer.Length - offset - int z.AvailOut
                else
                    code <- deflate (&z, Finish)
                    if code = StreamEnd then result <- buffer.Length - offset - int z.AvailOut
                    elif code <> Ok && code <> BufError then failwithf "deflate failed (%d)" code
            elif code <> Ok && code <> BufError then failwithf "deflate failed (%d)" code
        result
    finally
        deflateEnd &z |> ignore
