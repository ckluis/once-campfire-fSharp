// Port of rust/crates/cable/tests/protocol.rs
//
// The Action Cable protocol, driven over a real socket.
module Campfire.Cable.Tests.ProtocolTests

open System
open System.Diagnostics
open System.Threading.Tasks
open Xunit
open Campfire.RailsCompat
open Campfire.Cable
open Campfire.Cable.Tests.Support

[<Literal>]
let private Welcome = """{"type":"welcome"}"""

let private str (s: string) = Value.String s
let private int' (n: int) = Value.Int(int64 n)

let private room (id: int) : string =
    identifier (object' [ "channel", str "RoomChannel"; "room_id", int' id ])

let private heartbeatId = identifier (object' [ "channel", str "HeartbeatChannel" ])

let private confirm (identifier: string) : string =
    $"""{{"identifier":{Json.generate (str identifier)},"type":"confirm_subscription"}}"""

let private reject (identifier: string) : string =
    $"""{{"identifier":{Json.generate (str identifier)},"type":"reject_subscription"}}"""

let private message (identifier: string) (message: string) : string =
    $"""{{"identifier":{Json.generate (str identifier)},"message":{message}}}"""

let private withServer (config: Config) (body: TestServer -> Task) : Task =
    task {
        let! app = start config
        try
            do! (body app).WaitAsync(TimeSpan.FromSeconds 30.0)
        finally
            (app :> IAsyncDisposable).DisposeAsync().AsTask().Wait()
    }

let private eq (expected: 'a) (actual: 'a) = Assert.Equal<'a>(expected, actual)

/// The next text frame is `expected`.
let private expectText (expected: string) (client: Client) : Task =
    task {
        let! text = client.NextText()
        eq expected text
    }

let private expectGot (expected: Got) (client: Client) : Task =
    task {
        let! got = client.Next()
        eq expected got
    }

let private logOf (app: TestServer) : string list = lock app.Log (fun () -> List.ofSeq app.Log)

[<Fact>]
let ``non-websocket requests get Rails 404`` () =
    withServer testConfig (fun app ->
        task {
            let! response = httpGet app.Authority []
            eq 404 response.Status
            eq (Some "text/plain; charset=utf-8") (response.Header "content-type")
            eq "Page not found" response.Body
        })

[<Fact>]
let ``cross-origin upgrades get Rails 404`` () =
    withServer testConfig (fun app ->
        task {
            let! result = Client.Open(app.Authority, [ "Origin", "http://evil.example"; "Cookie", "session_token=1" ])
            match result with
            | Choice2Of2 response -> eq 404 response.Status
            | Choice1Of2 _ -> failwith "expected an HTTP 404, got an upgrade"
        })

[<Fact>]
let ``same-origin under assume_ssl means https`` () =
    withServer Config.defaults (fun app ->
        task {
            let! result = Client.Open(app.Authority, [ "Origin", app.Origin ])
            Assert.True((match result with Choice2Of2 _ -> true | _ -> false), "http:// origin must be refused when SSL is assumed")
            let httpsOrigin = app.Origin.Replace("http://", "https://")
            let! client = connectWith app (Some 1UL) httpsOrigin
            do! expectText Welcome client
        })

[<Fact>]
let ``negotiates the actioncable subprotocol and welcomes`` () =
    withServer testConfig (fun app ->
        task {
            let! client = connect app 1UL
            eq (Some "actioncable-v1-json") client.Protocol
            do! expectText Welcome client
        })

[<Fact>]
let ``unauthorized connections are told not to reconnect and closed`` () =
    withServer testConfig (fun app ->
        task {
            let! client = connectWith app None app.Origin
            do! expectText """{"type":"disconnect","reason":"unauthorized","reconnect":false}""" client
            do! expectGot (Closed(Some(1000us, ""))) client
        })

[<Fact>]
let ``subscribe confirms and duplicates are ignored`` () =
    withServer testConfig (fun app ->
        task {
            let! client = connect app 1UL
            do! expectText Welcome client

            let room = room 1
            do! client.Subscribe room
            do! expectText (confirm room) client

            // Same identifier string: no reply at all.
            do! client.Subscribe room
            do! client.Subscribe heartbeatId
            do! expectText (confirm heartbeatId) client

            // A differently spelled identifier for the same room is a separate subscription.
            let respelled = """{"room_id":1,"channel":"RoomChannel"}"""
            do! client.Subscribe respelled
            do! expectText (confirm respelled) client
        })

[<Fact>]
let ``rejection runs unsubscribed and can be retried`` () =
    withServer testConfig (fun app ->
        task {
            let! client = connect app 1UL
            let! _ = client.NextText()

            let room = room 2
            do! client.Subscribe room
            do! expectText (reject room) client
            eq [ "subscribed rejected=true"; "unsubscribed rejected=true" ] (logOf app)

            // The rejected subscription was removed, so subscribing again is answered again.
            do! client.Subscribe room
            do! expectText (reject room) client
        })

[<Fact>]
let ``unknown channels and malformed commands get no reply`` () =
    withServer testConfig (fun app ->
        task {
            let! client = connect app 1UL
            let! _ = client.NextText()

            do! client.Subscribe(identifier (object' [ "channel", str "NopeChannel" ]))
            do! client.Subscribe "not json"
            do! client.SendText "not json"
            do! client.Send(object' [ "command", str "dance" ])
            do! client.Unsubscribe(room 1)
            do! client.Perform(room 1, object' [ "action", str "echo" ])
            do! client.AssertSilent()

            // The connection is still usable afterwards.
            let room = room 1
            do! client.Subscribe room
            do! expectText (confirm room) client
        })

[<Fact>]
let ``leading colons resolve like safe_constantize`` () =
    withServer testConfig (fun app ->
        task {
            let! client = connect app 1UL
            let! _ = client.NextText()
            let heartbeat = identifier (object' [ "channel", str "::HeartbeatChannel" ])
            do! client.Subscribe heartbeat
            do! expectText (confirm heartbeat) client
        })

/// A channel's broadcastings are named after its class, not after how the client spelled it.
[<Fact>]
let ``leading colons stream and broadcast under the class name`` () =
    withServer testConfig (fun app ->
        task {
            let! prefixed = connect app 1UL
            let! plain = connect app 1UL
            let! _ = prefixed.NextText()
            let! _ = plain.NextText()
            let prefixedRoom = identifier (object' [ "channel", str "::RoomChannel"; "room_id", int' 1 ])
            do! prefixed.Subscribe prefixedRoom
            do! expectText (confirm prefixedRoom) prefixed
            let plainRoom = room 1
            do! plain.Subscribe plainRoom
            do! expectText (confirm plainRoom) plain

            eq 2 (app.Server.BroadcastTo("RoomChannel", [ "room-1" ], object' [ "roomId", int' 1 ]))
            do! expectText (message prefixedRoom """{"roomId":1}""") prefixed
            do! expectText (message plainRoom """{"roomId":1}""") plain

            do! prefixed.Perform(prefixedRoom, object' [ "action", str "start" ])
            let start = """{"action":"start","user":{"id":1}}"""
            do! expectText (message plainRoom start) plain
            do! expectText (message prefixedRoom start) prefixed
        })

[<Fact>]
let ``broadcasts reach subscribers as escaped JSON`` () =
    withServer testConfig (fun app ->
        task {
            let! client = connect app 1UL
            let! _ = client.NextText()
            let room = room 1
            do! client.Subscribe room
            let! _ = client.NextText()

            eq 1 (app.Server.BroadcastTo("RoomChannel", [ "room-1" ], object' [ "roomId", int' 1 ]))
            do! expectText (message room """{"roomId":1}""") client

            app.Server.Broadcast("room:room-1", str "<b>&</b>") |> ignore
            do! expectText (message room "\"\\u003cb\\u003e\\u0026\\u003c/b\\u003e\"") client
        })

[<Fact>]
let ``perform dispatches actions and defaults to receive`` () =
    withServer testConfig (fun app ->
        task {
            let! client = connect app 1UL
            let! other = connect app 2UL
            let! _ = client.NextText()
            let! _ = other.NextText()
            let room = room 1
            do! client.Subscribe room
            do! other.Subscribe room
            let! _ = client.NextText()
            let! _ = other.NextText()

            do! client.Perform(room, object' [ "action", str "echo"; "text", str "hi" ])
            do! expectText (message room """{"action":"echo","text":"hi"}""") client

            do! client.Perform(room, object' [ "text", str "hi" ])
            do! expectText (message room """{"received":{"text":"hi"}}""") client

            do! client.Perform(room, object' [ "action", str "start" ])
            let typing = message room """{"action":"start","user":{"id":1}}"""
            do! expectText typing client
            do! expectText typing other

            do! client.Perform(room, object' [ "action", str "not_an_action" ])
            do! client.AssertSilent()
        })

[<Fact>]
let ``unsubscribe stops delivery silently`` () =
    withServer testConfig (fun app ->
        task {
            let! client = connect app 1UL
            let! _ = client.NextText()
            let room = room 1
            do! client.Subscribe room
            let! _ = client.NextText()

            do! client.Unsubscribe room
            do! client.AssertSilent()
            app.Server.Broadcast("room:room-1", object' []) |> ignore
            do! client.AssertSilent()
            eq 1 app.Server.StreamCount // only the internal channel remains
        })

[<Fact>]
let ``remote disconnect closes every connection for the identifier`` () =
    withServer testConfig (fun app ->
        task {
            let! first = connect app 1UL
            let! second = connect app 1UL
            let! bystander = connect app 2UL
            for client in [ first; second; bystander ] do
                let! _ = client.NextText()
                ()
            let room = room 1
            do! first.Subscribe room
            let! _ = first.NextText()

            eq 2 (app.Server.Disconnect("user-1", true))
            for client in [ first; second ] do
                do! expectText """{"type":"disconnect","reason":"remote","reconnect":true}""" client
                do! expectGot (Closed(Some(1000us, ""))) client
            do! bystander.AssertSilent()

            // Once the client completes the close handshake, the server unsubscribes its channels.
            let! got = first.Next()
            Assert.True((match got with Ended | Failed _ -> true | _ -> false), $"{got}")
            do! Task.Delay 50
            eq "unsubscribed rejected=false" (List.last (logOf app))

            let! again = connect app 1UL
            let! _ = again.NextText()
            app.Server.Disconnect("user-1", false) |> ignore
            do! expectText """{"type":"disconnect","reason":"remote","reconnect":false}""" again
        })

[<Fact>]
let ``restart closes with server_restart`` () =
    withServer testConfig (fun app ->
        task {
            let! client = connect app 1UL
            let! _ = client.NextText()
            app.Server.Restart()
            do! expectText """{"type":"disconnect","reason":"server_restart","reconnect":true}""" client
        })

[<Fact>]
let ``lagging subscribers are disconnected with reconnect`` () =
    withServer { testConfig with StreamCapacity = 1; MaxWriteBatch = 1 } (fun app ->
        task {
            let! client = connect app 1UL
            let! _ = client.NextText()
            let room = room 1
            do! client.Subscribe room
            let! _ = client.NextText()

            for i in 0..9999 do
                app.Server.Broadcast("room:room-1", int' i) |> ignore
            let delivered = Collections.Generic.List<string>()
            let mutable reading = true
            while reading do
                let! frame = client.NextText()
                if frame.Contains "\"type\":\"disconnect\"" then
                    eq """{"type":"disconnect","reason":null,"reconnect":true}""" frame
                    reading <- false
                else
                    delivered.Add frame
            Assert.True(delivered.Count < 10000)
        })

[<Fact>]
let ``pings every three seconds with a unix timestamp`` () =
    withServer testConfig (fun app ->
        task {
            let! client = connect app 1UL
            do! expectText Welcome client
            let started = Stopwatch.StartNew()
            let! got = client.NextIncludingPings()
            let ping =
                match got with
                | Said ping -> ping
                | other -> failwith $"expected a ping, got {other}"
            Assert.True(started.Elapsed <= TimeSpan.FromMilliseconds 3100.0, $"{started.Elapsed}")
            match Json.parse (Text.Encoding.UTF8.GetBytes ping) with
            | Some(Value.Object entries) ->
                eq [ "type"; "message" ] (entries |> List.map fst)
                eq (str "ping") (entries |> List.find (fun (k, _) -> k = "type") |> snd)
                let now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                match entries |> List.find (fun (k, _) -> k = "message") |> snd with
                | Value.Int sent -> Assert.True(abs (sent - now) <= 1L)
                | other -> failwith $"{other}"
            | _ -> failwith ping
        })

[<Fact>]
let ``turbo streams channel verifies and guards stream names`` () =
    withServer testConfig (fun app ->
        task {
            let! client = connect app 1UL
            let! _ = client.NextText()

            let turbo (signed: Value option) =
                identifier (object' ([ "channel", str "Turbo::StreamsChannel" ] @ (signed |> Option.map (fun s -> "signed_stream_name", s) |> Option.toList)))

            let rooms = turbo (Some(str "signed(rooms)"))
            do! client.Subscribe rooms
            do! expectText (confirm rooms) client

            Turbo.broadcastRemoveTo app.Server [ "rooms" ] "list_room_1" |> ignore
            do!
                expectText
                    (message rooms "\"\\u003cturbo-stream action=\\\"remove\\\" target=\\\"list_room_1\\\"\\u003e\\u003c/turbo-stream\\u003e\"")
                    client

            let forged = turbo (Some(str "rooms"))
            do! client.Subscribe forged
            do! expectText (reject forged) client

            let missing = turbo None
            do! client.Subscribe missing
            do! expectText (reject missing) client

            // RoomStreamsAreAuthorized: a validly signed room message stream is still turned away.
            let guarded = turbo (Some(str "signed(Z2lk:messages)"))
            do! client.Subscribe guarded
            do! expectText (reject guarded) client
            Turbo.broadcastAppendTo app.Server [ "Z2lk"; "messages" ] "messages" "<p>x</p>" |> ignore
            do! client.AssertSilent()

            // A non-string name raises in MessageVerifier: neither confirmed nor rejected.
            let numeric = turbo (Some(int' 1))
            do! client.Subscribe numeric
            do! client.AssertSilent()
        })

[<Fact>]
let ``a client close is answered with its code`` () =
    withServer testConfig (fun app ->
        task {
            let! client = connect app 1UL
            do! expectText Welcome client
            do! client.SendBytes(clientFrame 0x8uy true false [| 0x03uy; 0xe9uy |])
            do! expectGot (Closed(Some(1001us, ""))) client
        })

/// websocket-driver's code for the failure, not a blanket 1002.
[<Fact>]
let ``a protocol error closes with its code`` () =
    withServer testConfig (fun app ->
        task {
            let! client = connect app 1UL
            do! expectText Welcome client
            do! client.SendBytes(clientFrame 0x1uy true false [| 0xffuy; 0xfeuy |])
            do! expectGot (Closed(Some(1007us, ""))) client
        })

[<Fact>]
let ``a connection holds a bounded number of subscriptions`` () =
    withServer testConfig (fun app ->
        task {
            let! client = connect app 1UL
            do! expectText Welcome client
            for nonce in 0..63 do
                let heartbeat = identifier (object' [ "channel", str "HeartbeatChannel"; "nonce", int' nonce ])
                do! client.Subscribe heartbeat
                do! expectText (confirm heartbeat) client
            // Past the limit, and an oversized identifier: ignored, no reply.
            do! client.Subscribe(identifier (object' [ "channel", str "HeartbeatChannel"; "nonce", int' 64 ]))
            do! client.AssertSilent()
            let! other = connect app 2UL
            do! expectText Welcome other
            do! other.Subscribe(identifier (object' [ "channel", str "HeartbeatChannel"; "pad", str (String('x', 5000)) ]))
            do! other.AssertSilent()
        })

// Not in the Rust tests.

/// A client that offers permessage-deflate gets compressed frames, which decode to the same messages.
[<Fact>]
let ``a broadcast is compressed once for every client that negotiated compression`` () =
    withServer testConfig (fun app ->
        task {
            let offer =
                [ "Origin", app.Origin
                  "Cookie", "session_token=1"
                  "Sec-WebSocket-Protocol", "actioncable-v1-json"
                  "Sec-WebSocket-Extensions", "permessage-deflate; client_max_window_bits" ]
            let open' () =
                task {
                    match! Client.Open(app.Authority, offer) with
                    | Choice1Of2 client -> return client
                    | Choice2Of2 response -> return failwith $"{response.Status}"
                }
            let! a = open' ()
            let! b = open' ()
            eq (Some "permessage-deflate; server_no_context_takeover; client_no_context_takeover") a.Extensions
            let! plain = connect app 1UL
            let room = room 1
            for client in [ a; b; plain ] do
                let! _ = client.NextText()
                do! client.Subscribe room
                let! _ = client.NextText()
                ()
            let big = String.replicate 300 "<div>message</div>"
            app.Server.BroadcastTo("RoomChannel", [ "room-1" ], str big) |> ignore
            let expected = message room (Json.encode (str big))
            for client in [ a; b; plain ] do
                do! expectText expected client
            // What a compressed client sends (a command, compressed) is read too.
            do! a.Perform(room, object' [ "action", str "echo"; "text", str "hi" ])
            do! expectText (message room """{"action":"echo","text":"hi"}""") a
        })

[<Fact>]
let ``a client's ping is answered with a pong`` () =
    withServer testConfig (fun app ->
        task {
            let! client = connect app 1UL
            do! expectText Welcome client
            do! client.SendBytes(clientFrame 0x9uy true false (Text.Encoding.UTF8.GetBytes "hi"))
            let reply = Array.zeroCreate<byte> 4
            use limit = new Threading.CancellationTokenSource(TimeSpan.FromSeconds 5.0)
            let mutable got = 0
            while got < 4 do
                let! n = client.Stream.ReadAsync(Memory<byte>(reply, got, 4 - got), limit.Token)
                got <- got + n
            eq [| 0x8auy; 0x02uy; byte 'h'; byte 'i' |] reply
        })

[<Fact>]
let ``a stream that stops after the connection closes is released from the hub`` () =
    withServer testConfig (fun app ->
        task {
            let! client = connect app 1UL
            let! _ = client.NextText()
            let room = room 1
            do! client.Subscribe room
            let! _ = client.NextText()
            eq 2 app.Server.StreamCount
            (client :> IDisposable).Dispose()
            let mutable waited = 0
            while app.Server.StreamCount > 0 && waited < 100 do
                do! Task.Delay 50
                waited <- waited + 1
            eq 0 app.Server.StreamCount
            eq "unsubscribed rejected=false" (List.last (logOf app))
        })
