// Not in the Rust crate: fan-out of a broadcast to many local subscribers, measured in process, with
// sockets that take bytes and say nothing (so what is measured is the hub, the connections and the
// writer, not Kestrel or the kernel). bench/cable measures the same through Kestrel and real sockets,
// against the Rust server. The numbers go to stderr; the thresholds are generous ceilings.
module Campfire.Cable.Tests.FanOutTests

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Xunit
open Campfire.RailsCompat
open Campfire.Cable
open Campfire.Cable.Socket
open Campfire.Cable.Tests.Support

/// A socket that is silent after the client's `script` and swallows what is written to it.
type private QuietSocket(script: byte[]) =
    inherit Stream()
    let mutable offset = 0
    let mutable written = 0L
    let mutable writes = 0L

    member _.Written = Interlocked.Read(&written)
    member _.Writes = Interlocked.Read(&writes)

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = true
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = raise (NotSupportedException())
        and set _ = raise (NotSupportedException())

    override _.Flush() = ()
    override _.Read(_, _, _) = raise (NotSupportedException())
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength _ = raise (NotSupportedException())
    override _.Write(_, _, _) = raise (NotSupportedException())

    override _.ReadAsync(buffer: Memory<byte>, ct: CancellationToken) : ValueTask<int> =
        if offset < script.Length then
            let n = min buffer.Length (script.Length - offset)
            script.AsMemory(offset, n).CopyTo buffer
            offset <- offset + n
            ValueTask<int> n
        else
            let never = TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
            ct.Register(fun () -> never.TrySetCanceled ct |> ignore) |> ignore
            ValueTask<int> never.Task

    override _.WriteAsync(buffer: ReadOnlyMemory<byte>, _: CancellationToken) : ValueTask =
        Interlocked.Add(&written, int64 buffer.Length) |> ignore
        Interlocked.Increment(&writes) |> ignore
        ValueTask()

let private subscribeScript (identifier: string) (compressed: bool) =
    let command = Json.generate (Value.Object [ "command", Value.String "subscribe"; "identifier", Value.String identifier ])
    let payload = Encoding.UTF8.GetBytes command
    if compressed then clientFrame 0x1uy true true (deflateMessage payload) else clientFrame 0x1uy true false payload

let private wireSize (payload: int) = payload + (if payload < 126 then 2 elif payload <= 65535 then 4 else 10)

let private filler = "<p>Hello there, see you at 10:30 &amp; bring the <b>slides</b></p>"

let private html (seq: int) (bytes: int) : string =
    let body = StringBuilder($"<div class=\"message\" data-seq=\"{seq}\">")
    while body.Length < bytes do
        body.Append filler |> ignore
    body.Append("</div>").ToString()

/// Starts `connections` connections on quiet sockets, subscribed to "bench"; returns them once confirmed.
let private connectAll (server: Server<string>) (connections: int) (compressed: bool) =
    task {
        let identifier = Json.generate (Value.Object [ "channel", Value.String "BenchChannel" ])
        let welcome = wireSize (Protocol.welcome().Length)
        let confirm = wireSize (Protocol.confirmation identifier).Length
        let sockets = [| for _ in 1..connections -> new QuietSocket(subscribeScript identifier compressed) |]
        let request = { Uri = "/cable"; Headers = HeaderDictionary() }
        let runs = [| for socket in sockets -> Connection.run server socket compressed request ignore |]
        let mutable waited = 0
        while sockets |> Array.exists (fun s -> s.Written < int64 (welcome + confirm)) && waited < 400 do
            do! Task.Delay 25
            waited <- waited + 1
        Assert.True(sockets |> Array.forall (fun s -> s.Written = int64 (welcome + confirm)), "every connection was welcomed and confirmed")
        return sockets, runs, int64 (welcome + confirm)
    }

let private benchChannel () : Channel<string> =
    { Channel.empty with
        Subscribed =
            fun sub ->
                sub.StreamFrom "bench"
                Channel.ok }

let private benchServer () : Server<string> =
    Server.builder Config.defaults (fun _ -> Task.FromResult(Some "bench")) id
    |> ServerBuilder.channel "BenchChannel" benchChannel
    |> ServerBuilder.build

/// What the clients should have been sent for `messages` broadcasts of `bytes` of HTML each.
let private expectedBytes (messages: int) (bytes: int) (compressed: bool) : int64 =
    let identifier = Json.encode (Value.String(Json.generate (Value.Object [ "channel", Value.String "BenchChannel" ])))
    let mutable total = 0L
    for seq in 0 .. messages - 1 do
        let tag = Turbo.actionTag Turbo.Append (Turbo.Target "messages") (Some(html seq bytes)) []
        let text = Protocol.message identifier (Json.encode (Value.String tag))
        let frame = Frame.OfString text
        let payload =
            if compressed then
                match frame.Deflated with
                | null -> frame.Bytes.Length
                | deflated -> deflated.Length
            else
                frame.Bytes.Length
        total <- total + int64 (wireSize payload)
    total

let private fanOut (connections: int) (messages: int) (bytes: int) (compressed: bool) : Task =
    task {
        let server = benchServer ()
        try
            let! sockets, runs, baseline = connectAll server connections compressed
            // A first round, to get the code compiled and the pools warm, as a server that's been up is.
            Turbo.broadcastAppendTo server [ "bench" ] "messages" (html 0 bytes) |> ignore
            let mutable waited = 0
            while sockets |> Array.exists (fun s -> s.Written < baseline + expectedBytes 1 bytes compressed) && waited < 400 do
                do! Task.Delay 10
                waited <- waited + 1

            let allocated0 = GC.GetTotalAllocatedBytes true
            let cpu0 = Process.GetCurrentProcess().TotalProcessorTime
            let clock = Stopwatch.StartNew()
            for seq in 0 .. messages - 1 do
                let received = Turbo.broadcastAppendTo server [ "bench" ] "messages" (html seq bytes)
                Assert.Equal(connections, received)
            let published = clock.Elapsed
            let target = baseline + expectedBytes 1 bytes compressed + expectedBytes messages bytes compressed
            waited <- 0
            while sockets |> Array.exists (fun s -> s.Written < target) && waited < 2000 do
                do! Task.Delay 1
                waited <- waited + 1
            let elapsed = clock.Elapsed
            let cpu = Process.GetCurrentProcess().TotalProcessorTime - cpu0
            let allocated = GC.GetTotalAllocatedBytes true - allocated0

            for socket in sockets do
                Assert.Equal(target, socket.Written)
            let deliveries = float (connections * messages)
            let writes = sockets |> Array.sumBy (fun s -> s.Writes)
            let mode = if compressed then " (deflate)" else ""
            Console.Error.WriteLine(
                $"cable fan-out: {connections} subscribers x {messages} messages of {bytes} B{mode}: "
                + $"publish {published.TotalMilliseconds / float messages:F3} ms/message, delivered in {elapsed.TotalMilliseconds:F1} ms, "
                + $"{cpu.TotalMilliseconds * 1000.0 / deliveries:F2} us CPU and {float allocated / deliveries:F0} B allocated per delivery, "
                + $"{float writes / float connections:F1} writes per connection"
            )
            // Ceilings that only a change that makes fan-out several times more expensive crosses.
            Assert.True(cpu.TotalMilliseconds * 1000.0 / deliveries < 50.0, "CPU per delivery")
            Assert.True(float allocated / deliveries < 4096.0, "allocation per delivery")

            // Closing every socket releases every subscription.
            (server :> IDisposable).Dispose()
            for socket in sockets do
                socket.Dispose()
            ignore runs
        finally
            ()
    }

[<Fact>]
let ``fan-out to 1000 local subscribers`` () = fanOut 1000 100 600 false

[<Fact>]
let ``fan-out to 1000 local subscribers with compression`` () = fanOut 1000 100 600 true
