namespace Campfire.Kit

// The memory Kestrel's connection pipes are made of.
//
// Kestrel hands each connection's pipes 4 KB pinned blocks and builds every response out of them: a 416 KB page is a hundred
// blocks, each rented, copied into, handed to `sendmsg` as one more buffer, and returned. A pipe asks its pool for as much as the
// writer's size hint says up to the pool's `MaxBufferSize`, which for Kestrel's own pool is the block size. This pool keeps the 4 KB
// blocks and also has 64 KB ones, so a writer that gives a size hint (`BufferWrites.write`, in the kit's body writers) gets a few large segments instead of
// many small ones. Blocks are pinned (the socket reads and writes them in place) and kept for reuse, and a timer gives half of the
// idle ones back every few seconds, as Kestrel's pool does.

open System
open System.Buffers
open System.Collections.Concurrent
open System.Runtime.InteropServices
open System.Threading
open Microsoft.AspNetCore.Connections

/// A pinned buffer of one size class. `Dispose` returns it to the pool it came from (once).
[<Sealed>]
type internal PoolBlock(owner: IPoolOwner, bytes: byte[]) =
    let mutable returned = 0
    member _.Bytes = bytes
    member _.Rented() = Volatile.Write(&returned, 0)

    interface IMemoryOwner<byte> with
        member _.Memory = MemoryMarshal.CreateFromPinnedArray(bytes, 0, bytes.Length)

        member this.Dispose() =
            if Interlocked.Exchange(&returned, 1) = 0 then owner.Return this

and internal IPoolOwner =
    abstract Return: PoolBlock -> unit

/// Free blocks of one size.
[<Sealed>]
type internal BlockQueue(size: int, cap: int) =
    let free = ConcurrentQueue<PoolBlock>()
    let mutable count = 0
    member _.Size = size

    member _.TryTake() : PoolBlock voption =
        let mutable block = Unchecked.defaultof<PoolBlock>
        if free.TryDequeue(&block) then
            Interlocked.Decrement(&count) |> ignore
            ValueSome block
        else
            ValueNone

    member _.Give(block: PoolBlock) : unit =
        if Volatile.Read(&count) < cap then
            Interlocked.Increment(&count) |> ignore
            free.Enqueue block

    /// Lets go of half of what is free.
    member _.Trim() : unit =
        let mutable drop = Volatile.Read(&count) / 2
        let mutable block = Unchecked.defaultof<PoolBlock>
        while drop > 0 && free.TryDequeue(&block) do
            Interlocked.Decrement(&count) |> ignore
            drop <- drop - 1

[<Sealed>]
type ConnPool(small: int, large: int) as this =
    inherit MemoryPool<byte>()
    let smallBlocks = BlockQueue(small, 8192)
    let largeBlocks = BlockQueue(large, 512)
    let mutable disposed = false
    let timer = new Timer((fun _ -> this.Trim()), null, TimeSpan.FromSeconds 10.0, TimeSpan.FromSeconds 10.0)

    member private _.Trim() =
        smallBlocks.Trim()
        largeBlocks.Trim()

    member private this.RentFrom(queue: BlockQueue) : IMemoryOwner<byte> =
        let block =
            match queue.TryTake() with
            | ValueSome block -> block
            | ValueNone -> new PoolBlock(this :> IPoolOwner, GC.AllocateUninitializedArray<byte>(queue.Size, true))
        block.Rented()
        block :> IMemoryOwner<byte>

    override _.MaxBufferSize = large

    override this.Rent(minBufferSize: int) : IMemoryOwner<byte> =
        if minBufferSize > large then
            raise (ArgumentOutOfRangeException(nameof minBufferSize))
        elif minBufferSize <= small then
            this.RentFrom smallBlocks
        else
            this.RentFrom largeBlocks

    override _.Dispose(disposing: bool) =
        if disposing && not disposed then
            disposed <- true
            timer.Dispose()
            smallBlocks.Trim()
            largeBlocks.Trim()

    interface IPoolOwner with
        member _.Return(block: PoolBlock) =
            if not disposed then
                if block.Bytes.Length = small then smallBlocks.Give block else largeBlocks.Give block

/// What Kestrel asks for a pool, answered with `ConnPool`s: 4 KB blocks (Kestrel's size) and 64 KB ones.
[<Sealed>]
type ConnPoolFactory() =
    interface IMemoryPoolFactory<byte> with
        member _.Create(_: MemoryPoolOptions) : MemoryPool<byte> = new ConnPool(4096, 65536)
