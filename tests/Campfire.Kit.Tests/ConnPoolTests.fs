module Campfire.Kit.Tests.ConnPoolTests

open System
open System.Buffers
open System.IO.Pipelines
open Xunit
open Campfire.Kit

[<Fact>]
let ``blocks come in two sizes, are reused, and go back once`` () =
    use pool = new ConnPool(4096, 65536)
    Assert.Equal(65536, pool.MaxBufferSize)
    let small = pool.Rent -1
    Assert.Equal(4096, small.Memory.Length)
    let large = pool.Rent 10000
    Assert.Equal(65536, large.Memory.Length)
    Assert.Throws<ArgumentOutOfRangeException>(fun () -> pool.Rent 65537 |> ignore) |> ignore
    let bytes = (match Runtime.InteropServices.MemoryMarshal.TryGetArray(Memory.op_Implicit small.Memory : ReadOnlyMemory<byte>) with | true, seg -> seg.Array | _ -> null)
    small.Dispose()
    small.Dispose()
    // Disposing twice returned it once: two rents are two different blocks, the first the one returned.
    let a = pool.Rent 100
    let b = pool.Rent 100
    let arrayOf (o: IMemoryOwner<byte>) = (match Runtime.InteropServices.MemoryMarshal.TryGetArray(Memory.op_Implicit o.Memory : ReadOnlyMemory<byte>) with | true, seg -> seg.Array | _ -> null)
    Assert.Same(bytes, arrayOf a)
    Assert.NotSame(arrayOf a, arrayOf b)
    a.Dispose()
    b.Dispose()
    large.Dispose()

[<Fact>]
let ``a write with a size hint gets large segments from the pool and keeps every byte`` () =
    use pool = new ConnPool(4096, 65536)
    let options = PipeOptions(pool = pool, minimumSegmentSize = 4096)
    let pipe = Pipe options
    let data = Array.init 300000 (fun i -> byte (i * 31 % 251))
    BufferWrites.write pipe.Writer (ReadOnlySpan<byte> data)
    pipe.Writer.Complete()
    let result = pipe.Reader.ReadAsync().AsTask().GetAwaiter().GetResult()
    let buffer = result.Buffer
    let mutable segments = 0
    for _ in buffer do
        segments <- segments + 1
    Assert.Equal<byte[]>(data, buffer.ToArray())
    Assert.True(segments <= 6, sprintf "%d segments for 300,000 bytes" segments)
    pipe.Reader.AdvanceTo buffer.End
    pipe.Reader.Complete()
