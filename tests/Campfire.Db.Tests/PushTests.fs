// Port of rust/crates/db/src/tests/push_test.rs
// `test/models/room/push_test.rb` (who gets pushed; delivery is the app's) and
// `test/models/push/subscription_test.rb` (validation and endpoint pinning).
module Campfire.Db.Tests.PushTests

open Xunit
open Campfire.Db
open Campfire.Db.Tests.Support

[<Literal>]
let private WebPushPublicTestIp = "142.250.185.206"

/// A body's rich text, mentioning these users.
type private Mentions(ids: int64 list) =
    interface RichText with
        member _.ToPlainText(conn: Conn, html: string) : string = (BasicRichText() :> RichText).ToPlainText(conn, html)
        member _.MentionedUserIds(_conn: Conn, _html: string) : int64 list = ids

/// How many deliveries `Room::MessagePusher#push` would queue for a new message.
let private deliveries (t: TestDb) (room: string) (body: string) (mentioned: int64 list) : int =
    let attributes =
        { NewMessage.create (id room) (id "david") with
            ClientMessageId = Some "earth"
            Body = Some body }
    let message = t.Write(fun tx -> Message.create tx attributes)
    let now = t.Now()
    let richText = Mentions mentioned :> RichText
    let _, everything, mentions = t.Read(fun c -> PushSubscription.pushesFor c richText message now)
    List.length everything + List.length mentions

[<Fact>]
let ``deliver new message to other room users with push subscriptions`` () =
    use t = new TestDb()
    let all = t.Read PushSubscription.count
    let davids = int64 (List.length (t.Read(fun c -> PushSubscription.forUser c (id "david"))))
    Assert.Equal(all - davids, int64 (deliveries t "hq" "This is from earth" []))

[<Fact>]
let ``notifies subscribed users`` () =
    use t = new TestDb()
    Assert.Equal(2, deliveries t "designers" "This is from earth" [])
    Assert.Equal(3, deliveries t "designers" "Hey @Kevin" [ id "kevin" ])

[<Fact>]
let ``does not notify for connected rooms`` () =
    use t = new TestDb()
    t.Write(fun tx -> Membership.find tx.Conn (id "kevin_designers") |> Membership.connected tx |> ignore)
    Assert.Equal(2, deliveries t "designers" "Hey @Kevin" [ id "kevin" ])

[<Fact>]
let ``does not notify for invisible rooms`` () =
    use t = new TestDb()
    t.Write(fun tx -> Membership.updateInvolvement tx (Membership.find tx.Conn (id "kevin_designers")) (Some Invisible) |> ignore)
    Assert.Equal(2, deliveries t "designers" "Hey @Kevin" [ id "kevin" ])

[<Fact>]
let ``payloads`` () =
    use t = new TestDb()
    let richText = BasicRichText() :> RichText
    let attributes = { NewMessage.create (id "designers") (id "david") with Body = Some "Hi" }
    let message = t.Write(fun tx -> Message.create tx attributes)
    let room = t.Read(fun c -> Message.room c message)
    let payload = t.Read(fun c -> PushSubscription.payloadFor c richText room message)
    Assert.Equal(("Designers", "David: Hi"), (payload.Title, payload.Body))
    Assert.Equal("/rooms/" + string (id "designers"), payload.Path)

    let attributes = { NewMessage.create (id "david_and_jason") (id "david") with Body = Some "Hi" }
    let message = t.Write(fun tx -> Message.create tx attributes)
    let room = t.Read(fun c -> Message.room c message)
    let payload = t.Read(fun c -> PushSubscription.payloadFor c richText room message)
    Assert.Equal(("David", "Hi"), (payload.Title, payload.Body))

[<Fact>]
let ``long payloads are cut short to fit a push message`` () =
    use t = new TestDb()
    let richText = BasicRichText() :> RichText
    let body = String.replicate 1000 "é" + "\"" + String.replicate 2000 "x"
    let attributes = { NewMessage.create (id "designers") (id "david") with Body = Some body }
    let message = t.Write(fun tx -> Message.create tx attributes)
    let room = t.Read(fun c -> Message.room c message)
    let payload = t.Read(fun c -> PushSubscription.payloadFor c richText room message)

    // `serde_json::to_string(text).len() - 2`
    let jsonLen (text: string) = (Campfire.RailsCompat.Json.generate (Campfire.RailsCompat.Value.String text) |> System.Text.Encoding.UTF8.GetByteCount) - 2
    Assert.StartsWith("David: éé", payload.Body)
    Assert.EndsWith("xx…", payload.Body)
    Assert.True(jsonLen payload.Body <= PushSubscription.MaxPayloadBodyBytes)
    Assert.True(jsonLen payload.Body > PushSubscription.MaxPayloadBodyBytes - 4)

let private build (endpoint: string) : PushSubscription =
    PushSubscription.build (id "david") (Some endpoint) (Some "test_key") (Some "test_auth") None

let private ``public`` (_: string) : string option = Some WebPushPublicTestIp

let private ``private`` (_: string) : string option = None

let private errors (subscription: PushSubscription) (resolve: string -> string option) : string list =
    PushSubscription.validate resolve subscription |> Errors.on "endpoint"

[<Fact>]
let ``valid subscription with permitted endpoint`` () =
    Assert.True(List.isEmpty (errors (build "https://fcm.googleapis.com/fcm/send/abc123") ``public``))

[<Fact>]
let ``rejects endpoint with non https scheme`` () =
    Assert.Contains("must use HTTPS", errors (build "http://fcm.googleapis.com/fcm/send/abc123") ``public``)

[<Fact>]
let ``rejects endpoint with non permitted host`` () =
    Assert.Contains("is not a permitted push service", errors (build "https://attacker.example.com/webhook") ``public``)

[<Fact>]
let ``rejects endpoint whose host only suffix matches a permitted host`` () =
    Assert.Contains(
        "is not a permitted push service",
        errors (build "https://evilfcm.googleapis.com.attacker.example/webhook") ``public``
    )

[<Fact>]
let ``rejects blank endpoint`` () =
    Assert.Contains("can't be blank", errors (build "") ``public``)

[<Fact>]
let ``rejects endpoint on a non default port`` () =
    Assert.Contains("must use the default HTTPS port", errors (build "https://fcm.googleapis.com:8443/fcm/send/abc123") ``public``)

[<Fact>]
let ``rejects endpoint that resolves to private ip`` () =
    let subscription = build "https://fcm.googleapis.com/fcm/send/abc123"
    Assert.Contains("resolves to a private or invalid IP address", errors subscription ``private``)
    Assert.Equal(None, PushSubscription.resolvedEndpointIp ``private`` subscription)

[<Fact>]
let ``resolved endpoint ip returns the pinned public ip`` () =
    Assert.Equal(Some WebPushPublicTestIp, PushSubscription.resolvedEndpointIp ``public`` (build "https://fcm.googleapis.com/fcm/send/abc123"))

[<Fact>]
let ``delivery is skipped for a non permitted host or port`` () =
    Assert.Equal(None, PushSubscription.resolvedEndpointIp ``public`` (build "https://attacker.example.com/collect"))
    Assert.Equal(None, PushSubscription.resolvedEndpointIp ``public`` (build "https://fcm.googleapis.com:22/fcm/send/abc123"))

[<Fact>]
let ``accepts all permitted push service domains`` () =
    for endpoint in
        [ "https://fcm.googleapis.com/fcm/send/token123"
          "https://jmt17.google.com/fcm/send/token123"
          "https://updates.push.services.mozilla.com/wpush/v2/token123"
          "https://web.push.apple.com/QaBC123"
          "https://wns2-db5p.notify.windows.com/w/?token=abc123" ] do
        Assert.True(List.isEmpty (errors (build endpoint) ``public``), endpoint)

[<Fact>]
let ``create validates`` () =
    use t = new TestDb()
    Assert.True(t.TryWrite(fun tx -> PushSubscription.create tx (build "http://fcm.googleapis.com/x") ``public``) |> Result.isError)
    let created = t.Write(fun tx -> PushSubscription.create tx (build "https://fcm.googleapis.com/x") ``public``)
    Assert.Equal(Some "https://fcm.googleapis.com/x", (t.Read(fun c -> PushSubscription.find c created.Id)).Endpoint)

[<Fact>]
let ``truncates by json escaped length`` () =
    let truncate (text: string) (max: int) = PushSubscription.truncateJsonString text max
    Assert.Equal("short", truncate "short" 5)
    Assert.Equal("lo…", truncate "longer" 5)
    Assert.Equal(String.replicate 2 "\u0001" + "…", truncate (String.replicate 10 "\u0001") 16)
    Assert.Equal("\"…", truncate "\"\"\"" 5)
    Assert.Equal("\U0001F600…", truncate "\U0001F600\U0001F600" 7)
