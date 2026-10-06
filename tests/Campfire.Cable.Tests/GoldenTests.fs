// Port of rust/crates/cable/tests/golden.rs
//
// Frame sequences recorded from the reference app's `/cable`, replayed against this server.
//
// `rust/crates/cable/tests/golden/reference.json` holds the fixture values the reference run used
// (user, rooms, signed stream names) and the frames it answered each step with. It was recorded from
// the Docker reference (Thruster, Puma, Redis adapter). The replay reads that file, or
// `tests/Campfire.Cable.Tests/golden/reference.json` when there is one: a re-recording is written there,
// never over rust/ (which is not edited). To re-record, with the reference up (see
// parity/bin/reference) and its fixtures printed by
// `parity/bin/reference runner --port 3141 rust/crates/cable/tests/golden/fixtures.rb`:
//
//   CABLE_REFERENCE_URL=ws://127.0.0.1:3141/cable CABLE_REFERENCE_FIXTURES=$PWD/target/cable-fixtures.json \
//     dotnet test --project tests/Campfire.Cable.Tests -c Release --no-build -- --explicit only --filter-method '*record_reference*'
//
// (The fixtures script starts a fresh session each time, because the script signs it out.)
//
// The replay builds Campfire's channels over the same fixture values, so every frame must match byte
// for byte; only ping timestamps are normalized.
module Campfire.Cable.Tests.GoldenTests

open System
open System.Collections.Generic
open System.IO
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Xunit
open Campfire.RailsCompat
open Campfire.Cable
open Campfire.Cable.Tests.Support

/// Where a re-recording is written: a file this repository owns.
let private recordedPath () = Campfire.Tests.Repo.path "tests/Campfire.Cable.Tests/golden/reference.json"

/// What the replay reads: this repository's re-recording if there is one, else the pinned Rust port's.
let private goldenPath () =
    if File.Exists(recordedPath ()) then recordedPath () else Campfire.Tests.Repo.path "rust/crates/cable/tests/golden/reference.json"

type private Exchange = { Step: string; Frames: string list }

type private Recording =
    { Tokens: Map<string, string>
      Sessions: Map<string, Exchange list> }

let private read (path: string) : Recording =
    use document = JsonDocument.Parse(File.ReadAllText path)
    let root = document.RootElement
    { Tokens = Map [ for p in root.GetProperty("tokens").EnumerateObject() -> p.Name, p.Value.GetString() |> nonNull ]
      Sessions =
        Map
            [ for session in root.GetProperty("sessions").EnumerateObject() ->
                  session.Name,
                  [ for exchange in session.Value.EnumerateArray() ->
                        { Step = exchange.GetProperty("step").GetString() |> nonNull
                          Frames = [ for frame in exchange.GetProperty("frames").EnumerateArray() -> frame.GetString() |> nonNull ] } ] ] }

/// One scripted step: what the client does, then what it collects.
type private Step =
    | Connect of cookie: bool * originOk: bool
    | HttpGet
    | Send of string
    | AwaitPing
    /// Signing out (`reset_remote_connections`), or `Server.Disconnect` on the replay.
    | RemoteDisconnect

let private identifier' (value: Value) : string = Json.generate value
let private str (s: string) = Value.String s

let private subscribe (identifier: string) =
    Json.generate (Value.Object [ "command", str "subscribe"; "identifier", str identifier ])

let private unsubscribe (identifier: string) =
    Json.generate (Value.Object [ "command", str "unsubscribe"; "identifier", str identifier ])

let private perform (identifier: string) (data: Value) =
    Json.generate (Value.Object [ "command", str "message"; "identifier", str identifier; "data", str (Json.generate data) ])

let private script (tokens: Map<string, string>) : (string * (string * Step) list) list =
    let t (name: string) = tokens[name]
    let roomId = Value.Int(int64 (t "ROOM_ID"))
    let closedRoomId = Value.Int(int64 (t "CLOSED_ROOM_ID"))
    let channel (name: string) (extra: (string * Value) list) = identifier' (Value.Object(("channel", str name) :: extra))
    let heartbeat = channel "HeartbeatChannel" []
    let room = channel "RoomChannel" [ "room_id", roomId ]
    let closedRoom = channel "RoomChannel" [ "room_id", closedRoomId ]
    let typing = channel "TypingNotificationsChannel" [ "room_id", roomId ]
    let turbo (signed: Value) = channel "Turbo::StreamsChannel" [ "signed_stream_name", signed ]
    let step (name: string) (step: Step) = name, step
    [ "authenticated",
      [ step "connect" (Connect(true, true))
        step "subscribe heartbeat" (Send(subscribe heartbeat))
        step "subscribe heartbeat again" (Send(subscribe heartbeat))
        step "subscribe member room" (Send(subscribe room))
        step "subscribe non-member room" (Send(subscribe closedRoom))
        step "subscribe unknown channel" (Send(subscribe (channel "NopeChannel" [])))
        step "subscribe base channel" (Send(subscribe (channel "ApplicationCable::Channel" [])))
        step "subscribe turbo rooms" (Send(subscribe (turbo (str (t "ROOMS_SIGNED")))))
        step "subscribe turbo forged" (Send(subscribe (turbo (str "InJvb21zIg==--0000"))))
        step "subscribe turbo unsigned" (Send(subscribe (channel "Turbo::StreamsChannel" [])))
        step "subscribe turbo guarded room messages" (Send(subscribe (turbo (str (t "ROOM_MESSAGES_SIGNED")))))
        step "subscribe typing" (Send(subscribe typing))
        step "perform typing start" (Send(perform typing (Value.Object [ "action", str "start" ])))
        step "perform unknown action" (Send(perform typing (Value.Object [ "action", str "dance" ])))
        step "perform default receive" (Send(perform typing (Value.Object [ "text", str "hi" ])))
        step "unsubscribe typing" (Send(unsubscribe typing))
        step "perform after unsubscribe" (Send(perform typing (Value.Object [ "action", str "start" ])))
        step "unknown command" (Send(Json.generate (Value.Object [ "command", str "dance" ])))
        step "invalid json" (Send "not json")
        step "ping" AwaitPing ]
      "remote disconnect",
      [ step "connect" (Connect(true, true))
        step "subscribe member room" (Send(subscribe room))
        step "sign out" RemoteDisconnect ]
      "unauthenticated", [ step "connect" (Connect(false, true)) ]
      "cross origin", [ step "connect" (Connect(true, false)) ]
      "plain http", [ step "get" HttpGet ] ]

type private Target =
    { Authority: string
      Origin: string
      Cookie: string
      /// Present on the replay; the reference is disconnected by signing out over HTTP.
      Server: Server<GoldenUser> option }

and private GoldenUser = { Id: uint64; Name: string; RoomIds: uint64 list }

let private normalizePing (text: string) : string =
    use document = JsonDocument.Parse text
    let message = document.RootElement.GetProperty "message"
    Assert.Equal(JsonValueKind.Number, message.ValueKind)
    text.Replace(message.GetRawText(), "<unix>")

/// Frames until the socket has been quiet for a moment (or, when awaiting a ping, the first ping).
/// Pings are dropped otherwise, since when they land is timing, not protocol.
let private collect (client: Client) (awaitPing: bool) : Task<string list> =
    task {
        let frames = List<string>()
        let quiet = if awaitPing then TimeSpan.FromSeconds 4.0 else TimeSpan.FromMilliseconds 400.0
        let mutable collecting = true
        while collecting do
            let next = client.NextIncludingPings()
            let! first = Task.WhenAny(next, Task.Delay quiet)
            if not (obj.ReferenceEquals(first, next)) then
                collecting <- false
            else
                match next.Result with
                | Said text when text.StartsWith("{\"type\":\"ping\"", StringComparison.Ordinal) ->
                    if awaitPing then
                        frames.Add(normalizePing text)
                        collecting <- false
                | Said text -> frames.Add text
                | Closed frame ->
                    frames.Add(
                        match frame with
                        | Some(code, reason) -> $"close Some(({code}, \"{reason}\"))"
                        | None -> "close None"
                    )
                | Failed _
                | Ended ->
                    frames.Add "end"
                    collecting <- false
        return List.ofSeq frames
    }

let private rawHttp (authority: string) (request: string) : Task<string> =
    task {
        use tcp = new TcpClient()
        let i = authority.LastIndexOf ':'
        do! tcp.ConnectAsync(authority.Substring(0, i), int (authority.Substring(i + 1)))
        let stream = tcp.GetStream()
        let bytes = Encoding.ASCII.GetBytes request
        do! stream.WriteAsync(bytes, 0, bytes.Length)
        use memory = new MemoryStream()
        do! stream.CopyToAsync memory
        return Encoding.UTF8.GetString(memory.ToArray())
    }

/// `DELETE /session` with a CSRF token from a room page, like the sign-out button.
let private signOut (target: Target) (roomId: string) : Task =
    task {
        let! page =
            rawHttp
                target.Authority
                $"GET /rooms/{roomId} HTTP/1.1\r\nHost: {target.Authority}\r\nCookie: {target.Cookie}\r\nConnection: close\r\n\r\n"
        let marker = "<meta name=\"csrf-token\" content=\""
        let token = page.Substring(page.IndexOf marker + marker.Length).Split('"')[0]
        let sessionPrefix = "set-cookie: _campfire_session="
        let sessionCookie =
            page.Split("\r\n")
            |> Array.pick (fun line ->
                if line.ToLowerInvariant().StartsWith sessionPrefix then Some(line.Substring(sessionPrefix.Length).Split(';')[0]) else None)
        let encoded = token.Replace("+", "%2B").Replace("/", "%2F").Replace("=", "%3D")
        let body = $"_method=delete&authenticity_token={encoded}"
        let! response =
            rawHttp
                target.Authority
                ($"POST /session HTTP/1.1\r\nHost: {target.Authority}\r\nOrigin: {target.Origin}\r\nCookie: {target.Cookie}; _campfire_session={sessionCookie}\r\n"
                 + $"Content-Type: application/x-www-form-urlencoded\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}")
        Assert.True(
            response.StartsWith "HTTP/1.1 302" || response.StartsWith "HTTP/1.1 303",
            $"sign out failed: {response.Substring(0, min 200 response.Length)}"
        )
    }

let private runScript (target: Target) (tokens: Map<string, string>) : Task<Map<string, Exchange list>> =
    task {
        let sessions = Dictionary<string, Exchange list>()
        for (session, steps) in script tokens do
            let mutable client: Client option = None
            let exchanges = List<Exchange>()
            for (name, step) in steps do
                let! frames =
                    task {
                        match step with
                        | Connect(cookie, originOk) ->
                            let origin = if originOk then target.Origin else "http://evil.example"
                            let headers =
                                [ "Origin", origin; "Sec-WebSocket-Protocol", "actioncable-v1-json, actioncable-unsupported" ]
                                @ (if cookie then [ "Cookie", target.Cookie ] else [])
                            match! Client.Open(target.Authority, headers) with
                            | Choice1Of2 opened ->
                                client <- Some opened
                                let! frames = collect opened false
                                let protocol = defaultArg opened.Protocol ""
                                return $"upgrade 101 protocol={protocol}" :: frames
                            | Choice2Of2 response -> return [ $"http {response.Status} {response.Body}" ]
                        | HttpGet ->
                            let! response = httpGet target.Authority []
                            let contentType = defaultArg (response.Header "content-type") ""
                            return [ $"http {response.Status} {contentType} {response.Body}" ]
                        | Send text ->
                            do! client.Value.SendText text
                            return! collect client.Value false
                        | AwaitPing -> return! collect client.Value true
                        | RemoteDisconnect ->
                            match target.Server with
                            | Some server ->
                                let userId = tokens["USER_ID"]
                                server.Disconnect($"gid://campfire/User/{userId}", true) |> ignore
                            | None -> do! signOut target tokens["ROOM_ID"]
                            return! collect client.Value false
                    }
                exchanges.Add { Step = name; Frames = frames }
            sessions[session] <- List.ofSeq exchanges
            match client with
            | Some client -> (client :> IDisposable).Dispose()
            | None -> ()
        return sessions |> Seq.map (fun (KeyValue(k, v)) -> k, v) |> Map.ofSeq
    }

// Campfire's channels (reference/app/channels), over the fixture values the reference used.

/// `RoomChannel#subscribed`: `stream_for` a room the user is a member of, else reject.
let private subscribeToRoom (sub: Subscription<GoldenUser>) : uint64 option =
    let roomId =
        match sub.Param "room_id" with
        | Some(Value.Int n) when n >= 0L -> Some(uint64 n)
        | Some(Value.UInt n) -> Some n
        | _ -> None
        |> Option.filter (fun id -> List.contains id sub.CurrentUser.RoomIds)
    match roomId with
    | Some id -> sub.StreamFor [ $"room-{id}" ]
    | None -> sub.Reject()
    roomId

let private roomChannel () : Channel<GoldenUser> =
    { Channel.empty with
        Subscribed =
            fun sub ->
                subscribeToRoom sub |> ignore
                Channel.ok }

let private typingNotificationsChannel () : Channel<GoldenUser> =
    let room = ref None
    { Channel.empty with
        Subscribed =
            fun sub ->
                room.Value <- subscribeToRoom sub |> Option.map (fun id -> $"room-{id}")
                Channel.ok
        Perform =
            fun action _ sub ->
                if action <> "start" && action <> "stop" then
                    Task.FromResult(Ok false)
                else
                    let user = sub.CurrentUser
                    sub.BroadcastTo(
                        [ room.Value.Value ],
                        Value.Object [ "action", str action; "user", Value.Object [ "id", Value.Int(int64 user.Id); "name", str user.Name ] ]
                    )
                    Task.FromResult(Ok true) }

let private roomGidParam (id: string) : string =
    Naming.gidParam { App = "campfire"; ModelName = "Rooms::Open"; Id = id }

type private Hosted =
    { App: Microsoft.AspNetCore.Builder.WebApplication
      Hosted: Server<GoldenUser> }

let private startCampfireLikeServer (tokens: Map<string, string>) : Task<Target * Hosted> =
    task {
        let cookie = "session_token=replay"
        let signed =
            Map
                [ tokens["ROOMS_SIGNED"], "rooms"
                  tokens["ROOM_MESSAGES_SIGNED"], roomGidParam tokens["ROOM_ID"] + ":messages" ]
        let turbo =
            Turbo.StreamsChannel.withVerifier (fun name -> Map.tryFind name signed)
            |> Turbo.StreamsChannel.guardedBy (fun name ->
                match name.IndexOf ':' with
                | -1 -> false
                | i -> name.Substring(i + 1) = "messages")
        let authenticate (request: ConnectRequest) =
            Task.FromResult(
                if request.Headers["Cookie"].ToString() = cookie then
                    Some
                        { Id = uint64 tokens["USER_ID"]
                          Name = tokens["USER_NAME"]
                          RoomIds = [ uint64 tokens["ROOM_ID"] ] }
                else
                    None
            )
        let server =
            Server.builder testConfig authenticate (fun user -> $"gid://campfire/User/{user.Id}")
            |> ServerBuilder.channel "ApplicationCable::Channel" (fun () -> Channel.empty)
            |> ServerBuilder.channel "HeartbeatChannel" (fun () -> Channel.empty)
            |> ServerBuilder.channel "RoomChannel" roomChannel
            |> ServerBuilder.channel "TypingNotificationsChannel" typingNotificationsChannel
            |> ServerBuilder.channel Turbo.StreamsChannelName (fun () -> Turbo.StreamsChannel.channel turbo)
            |> ServerBuilder.build
        let! app, address = host server
        let authority = (Uri address).Authority
        return
            { Authority = authority; Origin = $"http://{authority}"; Cookie = cookie; Server = Some server },
            { App = app; Hosted = server }
    }

let private countFrames (sessions: Map<string, Exchange list>) =
    sessions |> Map.toList |> List.sumBy (fun (_, exchanges) -> exchanges |> List.sumBy (fun e -> e.Frames.Length))

[<Fact>]
let ``replays reference frames`` () =
    task {
        let golden = read (goldenPath ())
        let! target, handle = startCampfireLikeServer golden.Tokens
        try
            let! sessions = (runScript target golden.Tokens).WaitAsync(TimeSpan.FromSeconds 60.0)
            for KeyValue(session, expected) in golden.Sessions do
                let actual = sessions[session]
                for (expected, actual) in List.zip expected actual do
                    Assert.True((expected = actual), $"session {session}, step {expected.Step}: expected {expected.Frames}, got {actual.Frames}")
            // Every recorded exchange and frame was replayed and compared, none skipped.
            Assert.Equal(5, golden.Sessions.Count)
            Assert.Equal(5, sessions.Count)
            Assert.Equal(26, golden.Sessions |> Map.toList |> List.sumBy (fun (_, e) -> e.Length))
            Assert.Equal(golden.Sessions |> Map.toList |> List.sumBy (fun (_, e) -> e.Length), sessions |> Map.toList |> List.sumBy (fun (_, e) -> e.Length))
            Assert.Equal(countFrames golden.Sessions, countFrames sessions)
            Assert.Equal(25, countFrames golden.Sessions)
        finally
            (handle.Hosted :> IDisposable).Dispose()
            handle.App.DisposeAsync().AsTask().Wait()
    }

[<Fact(Explicit = true)>]
let ``record_reference`` () =
    task {
        let url = Environment.GetEnvironmentVariable "CABLE_REFERENCE_URL" |> nonNull
        let fixturesPath = Environment.GetEnvironmentVariable "CABLE_REFERENCE_FIXTURES" |> nonNull
        use fixtures = JsonDocument.Parse(File.ReadAllText fixturesPath)
        let tokens =
            Map [ for p in fixtures.RootElement.GetProperty("tokens").EnumerateObject() -> p.Name, p.Value.GetString() |> nonNull ]
        let uri = Uri(url.Replace("ws://", "http://"))
        let authority = uri.Authority
        let target =
            { Authority = authority
              Origin = $"http://{authority}"
              Cookie = fixtures.RootElement.GetProperty("cookie").GetString() |> nonNull
              Server = None }
        let! sessions = runScript target tokens
        let json =
            JsonSerializer.Serialize(
                {| tokens = tokens
                   sessions = sessions |> Map.map (fun _ exchanges -> exchanges |> List.map (fun e -> {| step = e.Step; frames = e.Frames |})) |},
                JsonSerializerOptions(WriteIndented = true)
            )
        Directory.CreateDirectory(Path.GetDirectoryName(recordedPath ()) |> nonNull) |> ignore
        File.WriteAllText(recordedPath (), json + "\n")
    }
