namespace Campfire.Kit

open System
open System.Buffers

/// Writes into a response's pipe with a size hint, so the pipe's segments are as large as its pool allows (`ConnPool`): Kestrel's
/// own pool gives 4 KB whatever the hint, so there it changes nothing.
module BufferWrites =
    /// What a pipe is asked for at most at a time: `ConnPool`'s large block.
    [<Literal>]
    let Segment = 65536

    /// `writer.Write`, but asking for room for the rest of `data` each time.
    let write (writer: IBufferWriter<byte>) (data: ReadOnlySpan<byte>) : unit =
        let mutable rest = data
        while not rest.IsEmpty do
            let span = writer.GetSpan(min rest.Length Segment)
            let n = min rest.Length span.Length
            rest.Slice(0, n).CopyTo span
            writer.Advance n
            rest <- rest.Slice n
