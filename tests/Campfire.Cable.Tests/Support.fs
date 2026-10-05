// Port of rust/crates/cable/tests/support/mod.rs
//
// A tiny app on top of the cable server, and a raw WebSocket client for driving it. The client is
// written here rather than taken from `System.Net.WebSockets` so that tests can send what a browser
// wouldn't (invalid UTF-8, an unmasked frame) and see the extension, the close frames and the 404s
// exactly as the server sent them.
module Campfire.Cable.Tests.Support

open System
open System.Collections.Generic
open System.IO
open System.IO.Compression
open System.Net
open System.Net.Sockets
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Campfire.RailsCompat
open Campfire.Cable

type User = { Id: uint64; RoomIds: uint64 list }

let identify (user: User) : string = $"user-{user.Id}"

/// `Cookie: session_token=<user id>`; users 1 and 2 exist, and both are members of room 1 only.
let cookieAuth (request: ConnectRequest) : Task<User option> =
    let cookie = request.Headers["Cookie"].ToString()
    let user =
        if cookie.StartsWith("session_token=", StringComparison.Ordinal) then
            match UInt64.TryParse(cookie.Substring 14) with
            | true, id when id = 1UL || id = 2UL -> Some { Id = id; RoomIds = [ 1UL ] }
            | _ -> None
        else
            None
    Task.FromResult user

type Log = List<string>

let private uintParam (value: Value option) : uint64 option =
    match value with
    | Some(Value.Int n) when n >= 0L -> Some(uint64 n)
    | Some(Value.UInt n) -> Some n
    | _ -> None

/// Like `RoomChannel` plus `TypingNotificationsChannel`'s actions.
let roomChannel (log: Log) () : Channel<User> =
    let room = ref None
    { Subscribed =
        fun sub ->
            task {
                match uintParam (sub.Param "room_id") |> Option.filter (fun id -> List.contains id sub.CurrentUser.RoomIds) with
                | Some id ->
                    let name = $"room-{id}"
                    sub.StreamFor [ name ]
                    room.Value <- Some name
                | None -> sub.Reject()
                lock log (fun () -> log.Add("subscribed rejected=" + (if sub.Rejected then "true" else "false")))
                return Ok()
            }
      Unsubscribed =
        fun sub ->
            task {
                lock log (fun () -> log.Add("unsubscribed rejected=" + (if sub.Rejected then "true" else "false")))
                return Ok()
            }
      Perform =
        fun action data sub ->
            task {
                match action with
                | "start" ->
                    sub.BroadcastTo(
                        [ room.Value.Value ],
                        Value.Object [ "action", Value.String "start"; "user", Value.Object [ "id", Value.Int(int64 sub.CurrentUser.Id) ] ]
                    )
                    return Ok true
                | "echo" ->
                    sub.Transmit(Value.Object data)
                    return Ok true
                | "receive" ->
                    sub.Transmit(Value.Object [ "received", Value.Object data ])
                    return Ok true
                | _ -> return Ok false
            } }

/// Signed names in tests are `signed(<name>)`.
let testVerifier (signed: string) : string option =
    if signed.StartsWith("signed(", StringComparison.Ordinal) && signed.EndsWith(")", StringComparison.Ordinal) then
        Some(signed.Substring(7, signed.Length - 8))
    else
        None

let testConfig: Config = { Config.defaults with AssumeSsl = false }

/// Kestrel on a loopback port, with `server` mounted at /cable.
let host (server: Server<'U>) : Task<WebApplication * string> =
    task {
        let builder = WebApplication.CreateSlimBuilder()
        builder.Logging.ClearProviders() |> ignore
        builder.WebHost.UseUrls "http://127.0.0.1:0" |> ignore
        let app = builder.Build()
        Endpoint.map Protocol.DefaultMountPath server app
        do! app.StartAsync()
        let address = (nonNull (app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<IServerAddressesFeature>())).Addresses |> Seq.head
        return app, address
    }

type TestServer =
    { Server: Server<User>
      Host: WebApplication
      /// host:port
      Authority: string
      Origin: string
      Log: Log }

    member this.Url = $"ws://{this.Authority}/cable"

    interface IAsyncDisposable with
        member this.DisposeAsync() =
            task {
                (this.Server :> IDisposable).Dispose()
                do! this.Host.DisposeAsync()
            }
            |> ValueTask

let start (config: Config) : Task<TestServer> =
    task {
        let log = Log()
        let server =
            Server.builder config cookieAuth identify
            |> ServerBuilder.channel "RoomChannel" (roomChannel log)
            |> ServerBuilder.channel "HeartbeatChannel" (fun () -> Channel.empty)
            |> ServerBuilder.channel Turbo.StreamsChannelName (fun () ->
                Turbo.StreamsChannel.withVerifier testVerifier
                |> Turbo.StreamsChannel.guardedBy (fun name ->
                    match name.IndexOf ':' with
                    | -1 -> false
                    | i -> name.Substring(i + 1) = "messages")
                |> Turbo.StreamsChannel.channel)
            |> ServerBuilder.build
        let! app, address = host server
        let authority = (Uri address).Authority
        return { Server = server; Host = app; Authority = authority; Origin = $"http://{authority}"; Log = log }
    }

let identifier (value: Value) : string = Json.generate value

let object' (entries: (string * Value) list) : Value = Value.Object entries

// --- A raw WebSocket client --------------------------------------------------------------------

/// What the server sent, as the Rust tests' `Frame`.
type Got =
    | Said of string
    | Closed of (uint16 * string) option
    | Failed of string
    | Ended

type Http =
    { Status: int
      Headers: (string * string) list
      Body: string }

    member this.Header(name: string) : string option =
        this.Headers |> List.tryFind (fun (k, _) -> String.Equals(k, name, StringComparison.OrdinalIgnoreCase)) |> Option.map snd

let private timeout () = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)

let private readExact (stream: Stream) (buffer: byte[]) (count: int) : Task =
    task {
        use limit = timeout ()
        let mutable got = 0
        while got < count do
            let! n = stream.ReadAsync(Memory<byte>(buffer, got, count - got), limit.Token)
            if n = 0 then raise (EndOfStreamException())
            got <- got + n
    }

/// Reads an HTTP response head (and a Content-Length body, if there is one) off `stream`.
let readHttp (stream: Stream) : Task<Http> =
    task {
        let head = StringBuilder()
        let one = Array.zeroCreate<byte> 1
        let mutable fin = false
        while not fin do
            do! readExact stream one 1
            head.Append(char one[0]) |> ignore
            fin <- head.Length >= 4 && head.ToString(head.Length - 4, 4) = "\r\n\r\n"
        let lines = head.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
        let status = int ((lines[0].Split ' ')[1])
        let headers =
            [ for line in lines |> Array.skip 1 ->
                  let i = line.IndexOf ':'
                  line.Substring(0, i), line.Substring(i + 1).Trim() ]
        let length =
            headers
            |> List.tryFind (fun (k, _) -> k.Equals("content-length", StringComparison.OrdinalIgnoreCase))
            |> Option.map (snd >> int)
            |> Option.defaultValue 0
        let body = Array.zeroCreate<byte> length
        if length > 0 then do! readExact stream body length
        return { Status = status; Headers = headers; Body = Encoding.UTF8.GetString body }
    }

let private tail = [| 0uy; 0uy; 0xffuy; 0xffuy |]

/// A deflate stream as `permessage-deflate` sends it: sync-flushed, without the trailing 4 bytes.
let deflateMessage (text: byte[]) : byte[] =
    use output = new MemoryStream()
    use z = new DeflateStream(output, CompressionLevel.Optimal, true)
    z.Write(text, 0, text.Length)
    z.Flush()
    let bytes = output.ToArray()
    if bytes.Length >= 4 && bytes[bytes.Length - 4 ..] = tail then bytes[.. bytes.Length - 5] else bytes

let inflateMessage (data: byte[]) : byte[] =
    use input = new MemoryStream(Array.append data tail)
    use z = new DeflateStream(input, CompressionMode.Decompress)
    use output = new MemoryStream()
    z.CopyTo output
    output.ToArray()

/// A client frame, masked. `payload` is what goes on the wire (already compressed when `rsv1`).
let clientFrame (opcode: byte) (fin: bool) (rsv1: bool) (payload: byte[]) : byte[] =
    let mask = [| 0x12uy; 0x34uy; 0x56uy; 0x78uy |]
    use frame = new MemoryStream()
    frame.WriteByte((if fin then 0x80uy else 0uy) ||| (if rsv1 then 0x40uy else 0uy) ||| opcode)
    match payload.Length with
    | len when len < 126 -> frame.WriteByte(0x80uy ||| byte len)
    | len when len <= 65535 ->
        frame.WriteByte(0x80uy ||| 126uy)
        frame.WriteByte(byte (len >>> 8))
        frame.WriteByte(byte len)
    | len ->
        frame.WriteByte(0x80uy ||| 127uy)
        for shift in 7 .. -1 .. 0 do
            frame.WriteByte(byte (uint64 len >>> (shift * 8)))
    frame.Write(mask, 0, 4)
    frame.Write(payload |> Array.mapi (fun i b -> b ^^^ mask[i % 4]), 0, payload.Length)
    frame.ToArray()

type Client(tcp: TcpClient, protocol: string option, extensions: string option) =
    let stream = tcp.GetStream()
    let gate = obj ()
    let mutable closed = false
    let mutable pendingRaw: Task<Got> = Unchecked.defaultof<_>
    let mutable pendingNext: Task<Got> = Unchecked.defaultof<_>
    let deflate = extensions.IsSome

    member _.Protocol = protocol
    member _.Extensions = extensions
    member _.Stream = stream

    static member Open(authority: string, headers: (string * string) list) : Task<Choice<Client, Http>> =
        task {
            let host, port =
                let i = authority.LastIndexOf ':'
                authority.Substring(0, i), int (authority.Substring(i + 1))
            let tcp = new TcpClient()
            do! tcp.ConnectAsync(host, port)
            tcp.NoDelay <- true
            let stream = tcp.GetStream()
            let key = Convert.ToBase64String(RandomNumberGenerator.GetBytes 16)
            let request = StringBuilder()
            request.Append($"GET /cable HTTP/1.1\r\nHost: {authority}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n") |> ignore
            for (name, value) in headers do
                request.Append($"{name}: {value}\r\n") |> ignore
            request.Append "\r\n" |> ignore
            let bytes = Encoding.ASCII.GetBytes(request.ToString())
            do! stream.WriteAsync(bytes, 0, bytes.Length)
            let! response = readHttp stream
            if response.Status = 101 then
                let accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")))
                if response.Header "Sec-WebSocket-Accept" <> Some accept then failwith "wrong Sec-WebSocket-Accept"
                return Choice1Of2(new Client(tcp, response.Header "Sec-WebSocket-Protocol", response.Header "Sec-WebSocket-Extensions"))
            else
                tcp.Dispose()
                return Choice2Of2 response
        }

    member _.SendBytes(bytes: byte[]) : Task =
        task {
            do! stream.WriteAsync(bytes, 0, bytes.Length)
            do! stream.FlushAsync()
        }

    member this.SendText(text: string) : Task =
        let payload = Encoding.UTF8.GetBytes text
        if deflate then this.SendBytes(clientFrame 0x1uy true true (deflateMessage payload))
        else this.SendBytes(clientFrame 0x1uy true false payload)

    member this.Send(command: Value) : Task = this.SendText(Json.generate command)

    member this.Subscribe(identifier: string) : Task =
        this.Send(object' [ "command", Value.String "subscribe"; "identifier", Value.String identifier ])

    member this.Unsubscribe(identifier: string) : Task =
        this.Send(object' [ "command", Value.String "unsubscribe"; "identifier", Value.String identifier ])

    member this.Perform(identifier: string, data: Value) : Task =
        this.Send(object' [ "command", Value.String "message"; "identifier", Value.String identifier; "data", Value.String(Json.generate data) ])

    member private this.ReadFrame() : Task<Got> =
        task {
            if closed then
                return Ended
            else
                try
                    let head = Array.zeroCreate<byte> 8
                    let mutable result = ValueNone
                    while result.IsNone do
                        do! readExact stream head 2
                        let fin, rsv1, opcode = head[0] &&& 0x80uy <> 0uy, head[0] &&& 0x40uy <> 0uy, head[0] &&& 0x0fuy
                        if head[1] &&& 0x80uy <> 0uy then failwith "server frames are unmasked"
                        let! length =
                            task {
                                match head[1] &&& 0x7fuy with
                                | 126uy ->
                                    do! readExact stream head 2
                                    return (int head[0] <<< 8) ||| int head[1]
                                | 127uy ->
                                    do! readExact stream head 8
                                    return int (Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(ReadOnlySpan head))
                                | n -> return int n
                            }
                        let payload = Array.zeroCreate<byte> length
                        if length > 0 then do! readExact stream payload length
                        ignore fin
                        match opcode with
                        | 0x1uy ->
                            let payload = if rsv1 then inflateMessage payload else payload
                            result <- ValueSome(Said(Encoding.UTF8.GetString payload))
                        | 0x8uy ->
                            closed <- true
                            // Complete the closing handshake, as a WebSocket library does on reading a Close.
                            try
                                do! this.SendBytes(clientFrame 0x8uy true false payload)
                            with _ ->
                                ()
                            result <-
                                ValueSome(
                                    Closed(
                                        if payload.Length >= 2 then
                                            Some((uint16 payload[0] <<< 8) ||| uint16 payload[1], Encoding.UTF8.GetString(payload, 2, payload.Length - 2))
                                        else
                                            None
                                    )
                                )
                        | 0x9uy
                        | 0xauy -> ()
                        | other -> failwith $"unexpected opcode {other}"
                    return result.Value
                with
                | :? EndOfStreamException
                | :? IOException ->
                    closed <- true
                    return Ended
                | :? OperationCanceledException -> return failwith "no frame within 5s"
        }

    /// The next frame. A read that was given up on (see `AssertSilent`) is picked up by the next call,
    /// so the socket is only ever read by one.
    member this.NextIncludingPings() : Task<Got> =
        lock gate (fun () ->
            if isNull (box pendingRaw) then
                let read =
                    task {
                        let! got = this.ReadFrame()
                        lock gate (fun () -> pendingRaw <- Unchecked.defaultof<_>)
                        return got
                    }
                // One that finished already cleared nothing yet to clear.
                if not read.IsCompleted then pendingRaw <- read
                read
            else
                pendingRaw)

    /// The next frame, skipping pings.
    member this.Next() : Task<Got> =
        lock gate (fun () ->
            if isNull (box pendingNext) then
                let read =
                    task {
                        let mutable result = ValueNone
                        while result.IsNone do
                            match! this.NextIncludingPings() with
                            | Said text when text.StartsWith("{\"type\":\"ping\"", StringComparison.Ordinal) -> ()
                            | got -> result <- ValueSome got
                        lock gate (fun () -> pendingNext <- Unchecked.defaultof<_>)
                        return result.Value
                    }
                if not read.IsCompleted then pendingNext <- read
                read
            else
                pendingNext)

    member this.NextText() : Task<string> =
        task {
            match! this.Next() with
            | Said text -> return text
            | other -> return failwith $"expected a text frame, got {other}"
        }

    /// Asserts nothing arrives (pings aside) for a little while.
    member this.AssertSilent() : Task =
        task {
            let pending = this.Next()
            let! finished = Task.WhenAny(pending, Task.Delay 200)
            if obj.ReferenceEquals(finished, pending) then failwith $"expected no frame, got {pending.Result}" 
        }

    interface IDisposable with
        member _.Dispose() = tcp.Dispose()

let connectWith (server: TestServer) (userId: uint64 option) (origin: string) : Task<Client> =
    task {
        let headers =
            [ "Origin", origin
              "Sec-WebSocket-Protocol", "actioncable-v1-json, actioncable-unsupported" ]
            @ (match userId with
               | Some id -> [ "Cookie", $"session_token={id}" ]
               | None -> [])
        match! Client.Open(server.Authority, headers) with
        | Choice1Of2 client -> return client
        | Choice2Of2 response -> return failwith $"upgrade refused: {response.Status}"
    }

let connect (server: TestServer) (userId: uint64) : Task<Client> = connectWith server (Some userId) server.Origin

/// A plain GET to `/cable`, for what a non-WebSocket request is answered with.
let httpGet (authority: string) (headers: (string * string) list) : Task<Http> =
    task {
        use tcp = new TcpClient()
        let host, port =
            let i = authority.LastIndexOf ':'
            authority.Substring(0, i), int (authority.Substring(i + 1))
        do! tcp.ConnectAsync(host, port)
        let stream = tcp.GetStream()
        let request =
            $"GET /cable HTTP/1.1\r\nHost: {authority}\r\nConnection: close\r\n"
            + String.Join("", headers |> List.map (fun (k, v) -> $"{k}: {v}\r\n"))
            + "\r\n"
        let bytes = Encoding.ASCII.GetBytes request
        do! stream.WriteAsync(bytes, 0, bytes.Length)
        return! readHttp stream
    }
