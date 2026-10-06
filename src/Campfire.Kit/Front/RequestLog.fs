// No Rust counterpart (Rust's `tracing` formats each line on the request's task and writes it under the stdout lock).
//
// The request log line, written as UTF-8 straight into a batch instead of through `ILogger`: the console logger formats a string, queues
// it on a `BlockingCollection` (a monitor, and a wake-up of its writer thread for every line while that thread is idle) and the writer
// does one `write` call per line. Here a request formats its line into a thread's own buffer, copies it into the pending batch under a
// lock held for the length of one copy, and a thread that wakes every 10 ms writes the batch out, whole lines, in chunks of at most
// 4 KiB (a pipe's `write` of 4 KiB or less is atomic, so a line of another writer to stdout can't land in the middle of one).
// What the line says is `SimpleConsoleFormatter`'s output for `RequestLogEntry` (`<timestamp>info: thruster[0] Request path=... proto=...`,
// a newline in a value a space), which `RequestLogTests` compares against the console logger itself.
namespace Campfire.Kit

open System
open System.Globalization
open System.IO
open System.Net
open System.Text
open System.Threading

/// A batch of request log lines bound for a stream (stdout), written by a thread of its own.
[<Sealed>]
type RequestLines(output: Stream, interval: TimeSpan) =
    // Past this many pending bytes a request writes the batch itself, as a full queue would have blocked it.
    static let backlog = 4 * 1024 * 1024
    static let chunk = 4096
    let gate = obj ()
    let writeGate = obj ()
    let mutable pending: byte[] = Array.zeroCreate 262144
    let mutable pendingLength = 0
    // The batch being written; swapped with `pending` under `gate`, and only touched under `writeGate`.
    let mutable spare: byte[] = Array.zeroCreate 262144
    let mutable stopped = false

    let drain () : unit =
        Monitor.Enter writeGate
        try
            Monitor.Enter gate
            let length = pendingLength
            if length > 0 then
                let batch = pending
                pending <- spare
                spare <- batch
                pendingLength <- 0
            Monitor.Exit gate
            let batch = spare
            let mutable offset = 0
            while offset < length do
                let remaining = length - offset
                let take =
                    if remaining <= chunk then
                        remaining
                    else
                        match ReadOnlySpan(batch, offset, chunk).LastIndexOf(byte '\n') with
                        | -1 ->
                            // a line longer than a chunk goes whole
                            match ReadOnlySpan(batch, offset + chunk, remaining - chunk).IndexOf(byte '\n') with
                            | -1 -> remaining
                            | next -> chunk + next + 1
                        | last -> last + 1
                try
                    output.Write(batch, offset, take)
                with
                | :? IOException
                | :? ObjectDisposedException -> ()
                offset <- offset + take
            // A batch that outgrew its buffer once shouldn't keep a large one.
            if batch.Length > 4 * backlog then spare <- Array.zeroCreate 262144
        finally
            Monitor.Exit writeGate

    do
        let thread =
            Thread(
                (fun () ->
                    while not stopped do
                        Thread.Sleep interval
                        drain ()),
                IsBackground = true,
                Name = "request-log"
            )
        thread.Start()

    /// Adds one whole line (with its newline) to the batch.
    member _.Append(line: ReadOnlySpan<byte>) : unit =
        Monitor.Enter gate
        if pendingLength + line.Length > pending.Length then
            let grown = Array.zeroCreate<byte> (max (pending.Length * 2) (pendingLength + line.Length))
            Buffer.BlockCopy(pending, 0, grown, 0, pendingLength)
            pending <- grown
        line.CopyTo(Span(pending, pendingLength, line.Length))
        pendingLength <- pendingLength + line.Length
        let overfull = pendingLength >= backlog
        Monitor.Exit gate
        if overfull then drain ()

    /// Writes what is pending now.
    member _.Flush() : unit = drain ()

    /// Writes what is pending and stops the writer thread.
    member this.Stop() : unit =
        stopped <- true
        drain ()

/// What a request's log line needs that is only known as it starts (the headers can change after, and the proxy headers do).
[<Sealed>]
type RequestLogCapture
    (
        started: int64,
        path: string,
        query: string | null,
        meth: string,
        proto: string,
        contentLength: int64,
        contentType: string,
        forwardedFor: string,
        remoteIp: IPAddress | null,
        remotePort: int,
        userAgent: string
    ) =
    member _.Started = started
    member _.Path = path
    member _.Query = query
    member _.Method = meth
    member _.Proto = proto
    member _.ContentLength = contentLength
    member _.ContentType = contentType
    member _.ForwardedFor = forwardedFor
    member _.RemoteIp = remoteIp
    member _.RemotePort = remotePort
    member _.UserAgent = userAgent

/// A thread's buffer for the line being written.
[<Sealed>]
type internal LineWriter() =
    [<ThreadStatic; DefaultValue>]
    static val mutable private current: LineWriter | null

    [<ThreadStatic; DefaultValue>]
    static val mutable private second: int64

    [<ThreadStatic; DefaultValue>]
    static val mutable private secondPrefix: byte[] | null

    let mutable buffer: byte[] = Array.zeroCreate 1024
    let mutable length = 0
    let addressBytes: byte[] = Array.zeroCreate 16
    let addressChars: char[] = Array.zeroCreate 64

    static member Current: LineWriter =
        match LineWriter.current with
        | null ->
            let made = LineWriter()
            LineWriter.current <- made
            made
        | w -> w

    member _.Length = length
    member _.Span = ReadOnlySpan(buffer, 0, length)

    /// A buffer that grew for one huge value isn't kept.
    member _.Reset() : unit =
        if buffer.Length > 65536 then buffer <- Array.zeroCreate 1024
        length <- 0

    member private _.Room(extra: int) : unit =
        if length + extra > buffer.Length then
            let grown = Array.zeroCreate<byte> (max (buffer.Length * 2) (length + extra))
            Buffer.BlockCopy(buffer, 0, grown, 0, length)
            buffer <- grown

    member this.Bytes(bytes: byte[]) : unit =
        this.Room bytes.Length
        Buffer.BlockCopy(bytes, 0, buffer, length, bytes.Length)
        length <- length + bytes.Length

    member this.Byte(b: byte) : unit =
        this.Room 1
        buffer[length] <- b
        length <- length + 1

    /// A value as the console logger writes it: UTF-8, a newline a space.
    member this.Text(s: string | null) : unit =
        match s with
        | null -> ()
        | s ->
            this.Room(Encoding.UTF8.GetMaxByteCount s.Length)
            let start = length
            length <- length + Encoding.UTF8.GetBytes(s.AsSpan(), Span(buffer, length, buffer.Length - length))
            if s.Contains '\n' then
                for i in start .. length - 1 do
                    if buffer[i] = byte '\n' then buffer[i] <- byte ' '

    member this.Int64(n: int64) : unit =
        this.Room 20
        let mutable written = 0
        n.TryFormat(Span(buffer, length, buffer.Length - length), &written, ReadOnlySpan<char>.Empty, CultureInfo.InvariantCulture) |> ignore
        length <- length + written

    /// `{ip}` as .NET writes it, with an IPv4 address mapped into IPv6 as the IPv4 one.
    member this.Address(ip: IPAddress) : unit =
        this.Room 64
        if ip.IsIPv4MappedToIPv6 then
            let mutable count = 0
            ip.TryWriteBytes(Span addressBytes, &count) |> ignore
            for i in 12..15 do
                this.Int64(int64 addressBytes[i])
                if i < 15 then this.Byte(byte '.')
        else
            let mutable written = 0
            ip.TryFormat(Span addressChars, &written) |> ignore
            for i in 0 .. written - 1 do
                this.Byte(byte addressChars[i])

    /// `yyyy-MM-ddTHH:mm:ss.ffffffZ ` (UTC, the fraction truncated), the second's part made once a second per thread.
    member this.Timestamp() : unit =
        let ticks = DateTime.UtcNow.Ticks
        let second = ticks / TimeSpan.TicksPerSecond
        let prefix =
            match LineWriter.secondPrefix with
            | null -> null
            | p -> if LineWriter.second = second then p else null
        let prefix =
            match prefix with
            | null ->
                let made =
                    Encoding.ASCII.GetBytes(DateTime(second * TimeSpan.TicksPerSecond, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.", CultureInfo.InvariantCulture))
                LineWriter.secondPrefix <- made
                LineWriter.second <- second
                made
            | p -> p
        this.Bytes prefix
        let mutable micros = int (ticks % TimeSpan.TicksPerSecond / 10L)
        this.Room 8
        for i in 5..-1..0 do
            buffer[length + i] <- byte (int '0' + micros % 10)
            micros <- micros / 10
        length <- length + 6
        buffer[length] <- byte 'Z'
        buffer[length + 1] <- byte ' '
        length <- length + 2

module RequestLog =
    let private lit (s: string) : byte[] = Encoding.UTF8.GetBytes s
    let private head = lit "info: thruster[0] Request path="
    let private status = lit " status="
    let private dur = lit " dur="
    let private meth = lit " method="
    let private reqLength = lit " req_content_length="
    let private reqType = lit " req_content_type="
    let private respLength = lit " resp_content_length="
    let private respType = lit " resp_content_type="
    let private remote = lit " remote_addr="
    let private agent = lit " user_agent="
    let private cache = lit " cache="
    let private query = lit " query="
    let private proto = lit " proto="

    /// The line `SimpleConsoleFormatter` makes of `Request path=... proto=...` (`RequestLogEntry.ToString()`), newline included.
    let internal writeLine
        (w: LineWriter)
        (c: RequestLogCapture)
        (statusCode: int)
        (durationMs: int64)
        (respContentLength: int64)
        (respContentType: string)
        (xCache: string)
        : unit =
        w.Reset()
        w.Timestamp()
        w.Bytes head
        w.Text c.Path
        w.Bytes status
        w.Int64(int64 statusCode)
        w.Bytes dur
        w.Int64 durationMs
        w.Bytes meth
        w.Text c.Method
        w.Bytes reqLength
        w.Int64 c.ContentLength
        w.Bytes reqType
        w.Text c.ContentType
        w.Bytes respLength
        w.Int64 respContentLength
        w.Bytes respType
        w.Text respContentType
        w.Bytes remote
        if c.ForwardedFor <> "" then
            w.Text c.ForwardedFor
        else
            match c.RemoteIp with
            | null -> ()
            | ip ->
                w.Address ip
                w.Byte(byte ':')
                w.Int64(int64 c.RemotePort)
        w.Bytes agent
        w.Text c.UserAgent
        w.Bytes cache
        w.Text xCache
        w.Bytes query
        w.Text c.Query
        w.Bytes proto
        w.Text c.Proto
        w.Byte(byte '\n')

    /// The batch for this process's stdout, which flushes at exit. A server given it writes its request lines there as bytes
    /// (`Front.serveWithLines`; one given none logs them through its `ILogger`, as tests and tools do). `Stop()` it at shutdown.
    let stdout () : RequestLines =
        let lines = RequestLines(Console.OpenStandardOutput(), TimeSpan.FromMilliseconds 10.0)
        AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> lines.Flush())
        lines
