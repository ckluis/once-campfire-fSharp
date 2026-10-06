// Port of rust/crates/campfire/src/integrations/web_push/tests.rs and the tests of web_push/encryption.rs
module Campfire.App.Tests.WebPushTests

open System
open System.Collections.Generic
open System.Net
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open Campfire.App
open Campfire.App.Integrations
open Campfire.App.Tests.IntegrationsSupport
open Campfire.App.Tests.Support
open Campfire.Db
open Campfire.Tests

/// `DnsTestHelper::WEB_PUSH_PUBLIC_TEST_IP`
[<Literal>]
let private PublicIp = "142.250.185.206"

/// parity/.env.reference
[<Literal>]
let private VapidPublicKey = "BEYXTBB5_jNhNzXDmx5KEU55Vbbd-u--Lk9rM5OFQvUkPIBwZJ9QzAq0zdEzFw6yTV8cTriz_qYBVicY02_VxTQ="

[<Literal>]
let private VapidPrivateKey = "qfXLHghuG1rSHZUVo9SscNRI-0EIHRbIrfeGCqbAwak="

/// `WebPush::Notification#vapid_identification`, which the gem's vectors were made with.
[<Literal>]
let private ReferenceSubject = "mailto:support@37signals.com"

let private decode64 (s: string) : byte[] =
    match WebPushEncoding.decode64 s with
    | Ok bytes -> bytes
    | Error error -> failwith (EncryptionError.message error)

let private encode64 (bytes: byte[]) : string = WebPushEncoding.encode64Nopad bytes

let private expected () : JsonElement =
    let text = IO.File.ReadAllText(IO.Path.Combine(AppContext.BaseDirectory, "testdata", "web_push_expected.json"))
    (JsonDocument.Parse text).RootElement.Clone()

let private vapid () : VapidConfig =
    match VapidConfig.Create(ReferenceSubject, VapidPublicKey, VapidPrivateKey) with
    | Ok vapid -> vapid
    | Error error -> failwith (VapidError.message error)

let private p256 = ECCurve.NamedCurves.nistP256

/// A P-256 key pair from its scalar and its public point (SEC1 uncompressed).
let private keyOf (scalar: byte[]) (publicPoint: byte[]) : ECDiffieHellman =
    P256.keyOf scalar (ECPoint(X = publicPoint[1..32], Y = publicPoint[33..64]))

let private hkdf (salt: byte[]) (ikm: byte[]) (info: byte[]) (length: int) : byte[] =
    HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, length, salt, info)

/// What a user agent does with the message (RFC 8291 section 3.4): for the tests.
let private decrypt (body: byte[]) (receiver: ECDiffieHellman) (auth: byte[]) : (uint32 * byte[]) option =
    try
        let salt = body[..15]
        let recordSize = (uint32 body[16] <<< 24) ||| (uint32 body[17] <<< 16) ||| (uint32 body[18] <<< 8) ||| uint32 body[19]
        let idLength = int body[20]
        let serverPublicBytes = body[21 .. 20 + idLength]
        let ciphertext = body[21 + idLength ..]
        use serverKey =
            ECDiffieHellman.Create(
                ECParameters(Curve = p256, Q = ECPoint(X = serverPublicBytes[1..32], Y = serverPublicBytes[33..64]))
            )
        let shared = receiver.DeriveRawSecretAgreement serverKey.PublicKey
        let info = Array.concat [ Encoding.ASCII.GetBytes "WebPush: info\000"; P256.uncompressed receiver; serverPublicBytes ]
        let prk = hkdf auth shared info 32
        let key = hkdf salt prk (Encoding.ASCII.GetBytes "Content-Encoding: aes128gcm\000") 16
        let nonce = hkdf salt prk (Encoding.ASCII.GetBytes "Content-Encoding: nonce\000") 12
        use cipher = new AesGcm(key, 16)
        let text = ciphertext[.. ciphertext.Length - 17]
        let tag = ciphertext[ciphertext.Length - 16 ..]
        let plaintext = Array.zeroCreate<byte> text.Length
        cipher.Decrypt(nonce, text, tag, plaintext)
        Some(recordSize, plaintext)
    with _ ->
        None

// --- encryption.rs -------------------------------------------------------------------------------------------

// RFC 8291, section 5.
[<Literal>]
let private Plaintext = "V2hlbiBJIGdyb3cgdXAsIEkgd2FudCB0byBiZSBhIHdhdGVybWVsb24"

[<Literal>]
let private AsPrivate = "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw"

[<Literal>]
let private UaPublic = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4"

[<Literal>]
let private UaPrivate = "q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94"

[<Literal>]
let private Salt = "DGv6ra1nlYgDCS1FRnbzlw"

[<Literal>]
let private Auth = "BTBZMqHH6r4Tts7J_aSIgg"

[<Literal>]
let private RfcMessage =
    "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN"

[<Fact>]
let ``matches the rfc 8291 test vector`` () =
    // The application server's public key is the key id of the recorded message.
    let message = decode64 RfcMessage
    use serverKey = keyOf (decode64 AsPrivate) message[21..85]
    let layout: Encryption.Layout = { RecordSize = Some 4096u; Padding = [| 2uy |] }
    match Encryption.encryptWith (decode64 Plaintext) (Some UaPublic) (Some Auth) serverKey (decode64 Salt) layout with
    | Ok body -> Assert.Equal(RfcMessage, encode64 body)
    | Error error -> failwith (EncryptionError.message error)

    use receiver = keyOf (decode64 UaPrivate) (decode64 UaPublic)
    match decrypt message receiver (decode64 Auth) with
    | Some(recordSize, plaintext) ->
        Assert.Equal(4096u, recordSize)
        Assert.Equal<byte[]>(Array.append (decode64 Plaintext) [| 2uy |], plaintext)
    | None -> failwith "the user agent couldn't decrypt it"

/// A user agent's key and auth secret.
type private Receiver() =
    let key = ECDiffieHellman.Create p256
    let auth = RandomNumberGenerator.GetBytes 16

    member _.Key = key
    member _.Auth = auth

    member _.Subscription(id: int64, endpoint: string) : PushSubscription =
        let p256dh = encode64 (P256.uncompressed key)
        { PushSubscription.build 1L (Some endpoint) (Some p256dh) (Some(encode64 auth)) None with Id = id }

    member _.Open(body: byte[]) : string =
        match decrypt body key auth with
        | Some(_, plaintext) ->
            Assert.True(plaintext.Length >= 2 && plaintext[plaintext.Length - 2] = 2uy && plaintext[plaintext.Length - 1] = 0uy, "gem padding")
            Encoding.UTF8.GetString plaintext[.. plaintext.Length - 3]
        | None -> failwith "decrypts"

[<Fact>]
let ``round trips with the gems framing`` () =
    let receiver = Receiver()
    let p256dh = encode64 (P256.uncompressed receiver.Key)
    match Encryption.encrypt (Encoding.UTF8.GetBytes """{"title":"hi"}""") (Some p256dh) (Some(encode64 receiver.Auth)) with
    | Ok body ->
        match decrypt body receiver.Key receiver.Auth with
        | Some(recordSize, plaintext) ->
            Assert.Equal<byte[]>(Encoding.UTF8.GetBytes "{\"title\":\"hi\"}\u0002\u0000", plaintext)
            Assert.Equal(body.Length - 86, int recordSize)
        | None -> failwith "decrypts"
    | Error error -> failwith (EncryptionError.message error)

[<Fact>]
let ``decrypts what the gem encrypted`` () =
    let expected = expected ()
    let receiver = keyOf (decode64 (str (expected.GetProperty "receiver_private_key"))) (decode64 (str (expected.GetProperty "p256dh")))
    let body = decode64 (str (expected.GetProperty "ciphertext"))
    match decrypt body receiver (decode64 (str (expected.GetProperty "auth"))) with
    | Some(recordSize, plaintext) ->
        Assert.Equal(body.Length - 86, int recordSize)
        let message = str (expected.GetProperty "message")
        Assert.Equal<byte[]>(Array.append (Encoding.UTF8.GetBytes message) [| 2uy; 0uy |], plaintext)
    | None -> failwith "decrypts"

[<Fact>]
let ``rejects what the gem rejects`` () =
    use key = ECDiffieHellman.Create p256
    let ok = encode64 (P256.uncompressed key)
    let encrypt (message: byte[]) (p256dh: string option) (auth: string option) = Encryption.encrypt message p256dh auth
    let m = [| byte 'm' |]
    let isArgument result = match result with Error(EncryptionError.Argument _) -> true | _ -> false
    let isInvalidKey result = match result with Error(EncryptionError.InvalidKey _) -> true | _ -> false
    Assert.True(isArgument (encrypt m None (Some "YXV0aA")))
    Assert.True(isArgument (encrypt m (Some ok) (Some "")))
    Assert.True(isArgument (encrypt m (Some "not base64!") (Some "YXV0aA")))
    Assert.True(isInvalidKey (encrypt m (Some "dGVzdF9rZXk") (Some "YXV0aA")))
    Assert.True(isArgument (encrypt (Array.create 4100 (byte 'x')) (Some ok) (Some "YXV0aA")))

// --- tests.rs --------------------------------------------------------------------------------------------------

type private PushService =
    { Server: FakeServer
      Resolver: FakeResolver
      Dialer: MappingDialer
      Net: Network }

let private pushService (status: int) (reason: string) : PushService =
    let route = { Route.create "POST" "*" "" status with Reason = reason }
    let routes =
        [ "/fcm/send/abc"; "/fcm/send/123"; "/fcm/send/456"; "/fcm/send/567"; "/fcm/send/789" ]
        |> List.map (fun path -> { route with Path = path })
    let server = FakeServer.StartTls routes
    let resolver = FakeResolver([ "fcm.googleapis.com", [ PublicIp ] ])
    let dialer = MappingDialer(HashSet [ IPAddress.Parse PublicIp ], server.Addr)
    { Server = server
      Resolver = resolver
      Dialer = dialer
      Net = network resolver dialer }

let private notification (subscription: PushSubscription) : Notification =
    { Title = "t"
      Body = "b"
      Path = "/"
      Badge = 0L
      Subscription = subscription }

[<Fact>]
let ``encodes the message like json generate`` () =
    let notification =
        { Title = "Designers <&> \"quotes\" é 😀"
          Body = "Kevin: line\nbreak\ttab   \u001f / \\ "
          Path = "/rooms/1"
          Badge = 3L
          Subscription = PushSubscription.build 1L None None None None }
    Assert.Equal(str ((expected ()).GetProperty "message"), WebPush.Notification.encodedMessage notification)

[<Fact>]
let ``the longest payload fits a push message`` () =
    let receiver = Receiver()
    let notification =
        { Title = String.replicate (PushSubscription.MaxPayloadTitleBytes / 2) "\""
          Body = String.replicate (PushSubscription.MaxPayloadBodyBytes / 6) "\u0001"
          Path = $"/rooms/{Int64.MinValue}"
          Badge = Int64.MinValue
          Subscription = receiver.Subscription(1L, "https://fcm.googleapis.com/fcm/send/abc") }
    let subscription = notification.Subscription
    let message = WebPush.Notification.encodedMessage notification
    match Encryption.encrypt (Encoding.UTF8.GetBytes message) subscription.P256dhKey subscription.AuthKey with
    | Ok body -> Assert.Equal(message, receiver.Open body)
    | Error error -> failwith (EncryptionError.message error)

[<Fact>]
let ``signs the vapid header like the gem`` () =
    let expected = expected ()
    let now = expected.GetProperty("now").GetInt64()
    let authorization = (vapid ()).Authorization("https://fcm.googleapis.com", now)
    Assert.StartsWith("vapid t=", authorization)
    let t, k = (let rest = authorization.Substring 8 in let at = rest.IndexOf ",k=" in rest.Substring(0, at), rest.Substring(at + 3))
    Assert.Equal(str (expected.GetProperty "authorization_k"), k)
    let segments = t.Split '.'
    Assert.Equal(str (expected.GetProperty "jwt_header_segment"), segments[0])
    Assert.Equal(str (expected.GetProperty "jwt_payload_segment"), segments[1])

    let publicKey = decode64 VapidPublicKey
    use key = ECDsa.Create(ECParameters(Curve = p256, Q = ECPoint(X = publicKey[1..32], Y = publicKey[33..64])))
    Assert.True(
        key.VerifyData(
            Encoding.ASCII.GetBytes($"{segments[0]}.{segments[1]}"),
            decode64 segments[2],
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation
        )
    )

[<Fact>]
let ``signs with the configured subject`` () =
    let vapid =
        match VapidConfig.Create("mailto:ops@example.com", VapidPublicKey, VapidPrivateKey) with
        | Ok vapid -> vapid
        | Error error -> failwith (VapidError.message error)
    let authorization = vapid.Authorization("https://fcm.googleapis.com", 0L)
    let jwt = (authorization.Substring(8).Split ',')[0]
    let payloadSegment = (jwt.Split '.').[1]
    use claims = JsonDocument.Parse(Encoding.UTF8.GetString(decode64 payloadSegment))
    Assert.Equal("mailto:ops@example.com", str (claims.RootElement.GetProperty "sub"))

[<Fact>]
let ``rejects bad vapid keys up front`` () =
    use other = ECDiffieHellman.Create p256
    let otherPublicKey = encode64 (P256.uncompressed other)
    let keys (publicKey: string) (privateKey: string) = VapidConfig.Create(ReferenceSubject, publicKey, privateKey) |> Result.map ignore
    Assert.Equal(Error InvalidPublicKey, keys "" VapidPrivateKey)
    Assert.Equal(Error InvalidPublicKey, keys "dGVzdF9rZXk" VapidPrivateKey)
    Assert.Equal(Error InvalidPrivateKey, keys VapidPublicKey "not base64!")
    Assert.Equal(Error InvalidPrivateKey, keys VapidPublicKey (encode64 (Array.create 32 0xffuy)))
    Assert.Equal(Error Mismatched, keys otherPublicKey VapidPrivateKey)
    Assert.Equal(Ok(), keys (VapidPublicKey.TrimEnd '=') (VapidPrivateKey.TrimEnd '='))

let private config (publicKey: string option) (privateKey: string option) : AppConfig =
    let vars = dict [ "SECRET_KEY_BASE_DUMMY", "1" ]
    match AppConfig.fromLookup (fun name -> match vars.TryGetValue name with | true, v -> v | _ -> null) with
    | Ok config ->
        { config with
            VapidPublicKey = publicKey
            VapidPrivateKey = privateKey
            VapidSubject = ReferenceSubject }
    | Error message -> failwith message

[<Fact>]
let ``web push is off without a valid key pair`` () =
    task {
        use t = new TestDb()
        let pool publicKey privateKey = IntegrationJobs.webPushPool (config publicKey privateKey) t.Db NullLogger.Instance
        Assert.True((pool None (Some VapidPrivateKey)).IsNone)
        Assert.True((pool (Some "dGVzdF9rZXk") (Some VapidPrivateKey)).IsNone)
        let on = pool (Some VapidPublicKey) (Some VapidPrivateKey)
        Assert.True on.IsSome
        do! on.Value.Shutdown()
        Assert.Equal(Error Missing, VapidConfig.FromConfig(config (Some VapidPublicKey) None) |> Result.map ignore)
    }

[<Fact>]
let ``delivers to the pinned address with the gems headers`` () =
    task {
        let service = pushService 201 "Created"
        use _server = service.Server
        let receiver = Receiver()
        let notification = notification (receiver.Subscription(1L, "https://fcm.googleapis.com/fcm/send/abc"))

        let! delivered = WebPush.deliver service.Net (vapid ()) notification
        Assert.Equal(Ok(Some 201), delivered |> Result.mapError DeliveryError.message |> Result.mapError (fun _ -> ()))

        Assert.Equal<string list>([ "fcm.googleapis.com" ], service.Resolver.Lookups)
        Assert.Equal<IPEndPoint list>([ IPEndPoint(IPAddress.Parse PublicIp, 443) ], service.Dialer.Dialed)
        let received = service.Server.Received()
        Assert.Equal(1, received.Length)
        let request = received[0]
        Assert.Equal(("POST", "/fcm/send/abc"), (request.Method, request.Target))

        let expected = expected ()
        let expectedHeaders =
            [ for pair in expected.GetProperty("headers").EnumerateArray() ->
                  let name, value = str pair[0], str pair[1]
                  name, (if name = "Content-Length" then string request.Body.Length else value) ]
        let names = request.Headers |> List.map fst
        Assert.Equal<string list>(
            [ "Content-Type"; "Ttl"; "Urgency"; "Content-Encoding"; "Content-Length"; "Authorization"; "Accept-Encoding"; "Accept"; "User-Agent"; "Connection"; "Host" ],
            names
        )
        for (name, value) in expectedHeaders do
            Assert.Equal(Some value, request.Header name)
        Assert.Equal(Some "fcm.googleapis.com", request.Header "Host")
        Assert.Equal(Some "Ruby", request.Header "User-Agent")
        let headerSegment = str (expected.GetProperty "jwt_header_segment")
        Assert.StartsWith($"vapid t={headerSegment}.", (request.Header "Authorization").Value)
        Assert.Equal(WebPush.Notification.encodedMessage notification, receiver.Open request.Body)
    }

[<Fact>]
let ``skips endpoints it may not deliver to`` () =
    task {
        let service = pushService 201 "Created"
        use _server = service.Server
        service.Resolver.Set("updates.push.services.mozilla.com", [ [ IPAddress.Parse "10.0.0.5" ] ])
        let receiver = Receiver()
        for endpoint in
            [ "https://updates.push.services.mozilla.com/wpush/v2/x" // resolves privately
              "https://web.push.apple.com/QaBC123" // doesn't resolve
              "https://attacker.example.com/collect"
              "https://fcm.googleapis.com:22/fcm/send/abc"
              "http://fcm.googleapis.com/fcm/send/abc"
              "https://evilfcm.googleapis.com.attacker.example/webhook" ] do
            let! delivered = WebPush.deliver service.Net (vapid ()) (notification (receiver.Subscription(1L, endpoint)))
            match delivered with
            | Ok None -> ()
            | other -> failwith $"{endpoint}: {other}"
        Assert.True(service.Server.Received().IsEmpty)
        Assert.Equal<string list>([ "updates.push.services.mozilla.com"; "web.push.apple.com" ], service.Resolver.Lookups)
    }

[<Fact>]
let ``raises what the gem raises`` () =
    task {
        let receiver = Receiver()
        let subscription = receiver.Subscription(1L, "https://fcm.googleapis.com/fcm/send/abc")
        for (status, reason, kind, invalidates) in
            [ 410, "Gone", "WebPush::ExpiredSubscription", true
              404, "Not Found", "WebPush::InvalidSubscription", true
              403, "Forbidden", "WebPush::Unauthorized", false
              400, "UnauthorizedRegistration", "WebPush::Unauthorized", false
              400, "Bad Request", "WebPush::ResponseError", false
              413, "Payload Too Large", "WebPush::PayloadTooLarge", false
              429, "Too Many Requests", "WebPush::TooManyRequests", false
              503, "Service Unavailable", "WebPush::PushServiceError", false
              302, "Found", "WebPush::ResponseError", false ] do
            let service = pushService status reason
            use _server = service.Server
            match! WebPush.deliver service.Net (vapid ()) (notification subscription) with
            | Error error -> Assert.Equal((kind, invalidates), (DeliveryError.className error, DeliveryError.invalidatesSubscription error))
            | Ok delivered -> failwith $"{status} {reason}: {delivered}"
    }

[<Fact>]
let ``only the subscriptions own faults invalidate it`` () =
    task {
        // A key that isn't a point on the curve (as in the fixtures) can never be delivered to.
        let service = pushService 201 "Created"
        use _server = service.Server
        let badKey =
            PushSubscription.build 1L (Some "https://fcm.googleapis.com/fcm/send/abc") (Some "dGVzdF9rZXk") (Some "dGVzdF9hdXRo") None
        match! WebPush.deliver service.Net (vapid ()) (notification badKey) with
        | Error error ->
            Assert.Equal(("OpenSSL::PKey::EC::Point::Error", true), (DeliveryError.className error, DeliveryError.invalidatesSubscription error))
        | Ok delivered -> failwith $"{delivered}"

        // A certificate that doesn't verify may be our fault (an empty CA store, a skewed clock).
        let untrusted = { service.Net with Tls = CustomRoots(Security.Cryptography.X509Certificates.X509Certificate2Collection()) }
        let receiver = Receiver()
        match! WebPush.deliver untrusted (vapid ()) (notification (receiver.Subscription(1L, "https://fcm.googleapis.com/fcm/send/abc"))) with
        | Error error ->
            Assert.Equal(("OpenSSL::SSL::SSLError", false), (DeliveryError.className error, DeliveryError.invalidatesSubscription error))
        | Ok delivered -> failwith $"{delivered}"

        let blank = PushSubscription.build 1L (Some "https://fcm.googleapis.com/fcm/send/abc") (Some "") (Some "dGVzdF9hdXRo") None
        match! WebPush.deliver service.Net (vapid ()) (notification blank) with
        | Error error -> Assert.Equal(("ArgumentError", false), (DeliveryError.className error, DeliveryError.invalidatesSubscription error))
        | Ok delivered -> failwith $"{delivered}"
    }

[<Fact>]
let ``test notification`` () =
    task {
        let service = pushService 201 "Created"
        use _server = service.Server
        let receiver = Receiver()
        let subscription = receiver.Subscription(1L, "https://fcm.googleapis.com/fcm/send/abc")
        match! WebPush.deliverTestNotification service.Net (vapid ()) subscription 4L "http://example.com/users/me/push_subscriptions" with
        | Ok() -> ()
        | Error error -> failwith (DeliveryError.message error)

        use message = JsonDocument.Parse(receiver.Open (List.head (service.Server.Received())).Body)
        let root = message.RootElement
        Assert.Equal("Campfire Test", str (root.GetProperty "title"))
        let options = root.GetProperty "options"
        Assert.True(Guid.TryParse(str (options.GetProperty "body")) |> fst)
        Assert.Equal("/account/logo", str (options.GetProperty "icon"))
        let data = options.GetProperty "data"
        Assert.Equal("http://example.com/users/me/push_subscriptions", str (data.GetProperty "path"))
        Assert.Equal(4L, data.GetProperty("badge").GetInt64())

        let expired = pushService 410 "Gone"
        use _expired = expired.Server
        let! result = WebPush.deliverTestNotification expired.Net (vapid ()) subscription 4L "/"
        Assert.True result.IsError
    }

/// `test/models/room/push_test.rb`: "deliver new message ..." and "destroys invalid subscriptions".
[<Fact>]
let ``pushes messages and destroys expired subscriptions`` () =
    task {
        use t = new TestDb()
        let db = t.Db
        let labels = [ "david_chrome"; "jason_chrome"; "jz_chrome"; "kevin_chrome" ]
        let receivers = [ for label in labels -> TestDb.Id label, Receiver() ]
        let! prepared =
            db.Write(fun tx ->
                for (id, receiver) in receivers do
                    tx.Conn.Execute(
                        "UPDATE push_subscriptions SET p256dh_key = ?, auth_key = ? WHERE id = ?",
                        [| S(encode64 (P256.uncompressed receiver.Key)); S(encode64 receiver.Auth); I id |]
                    )
                    |> ignore
                let membership = Membership.find tx.Conn (TestDb.Id "kevin_designers")
                Membership.updateInvolvement tx membership (Some Invisible) |> ignore)
        Assert.True prepared.IsOk

        let create () =
            task {
                let attributes: NewMessage =
                    { RoomId = TestDb.Id "designers"
                      CreatorId = TestDb.Id "david"
                      ClientMessageId = Some "earth"
                      Body = Some "Hey @kevin"
                      AttachmentBlobId = None }
                match! db.Write(fun tx -> Message.create tx attributes) with
                | Ok message -> return message
                | Error error -> return failwith (DbError.display error)
            }

        // Delivered: nothing is destroyed.
        let ok = pushService 201 "Created"
        use _ok = ok.Server
        let destroyed = List<int64>()
        let pool =
            WebPushPool(ok.Net, vapid (), (fun id -> lock destroyed (fun () -> destroyed.Add id); Ok()), NullLogger.Instance)
        let! message = create ()
        let! pushed =
            db.Read(fun conn -> WebPushPool.pushMessage pool conn db.Env.RichText message (db.Env.Now()))
        let payload =
            match pushed with
            | Ok payload -> payload
            | Error error -> failwith (DbError.display error)
        Assert.Equal("Designers", payload.Title)
        Assert.Equal("David: Hey @kevin", payload.Body)
        do! pool.Shutdown()
        let received = ok.Server.Received()
        Assert.Equal(2, received.Length)
        let suffixes = dict [ "david_chrome", "123"; "jason_chrome", "567"; "jz_chrome", "456"; "kevin_chrome", "789" ]
        for request in received do
            let _, receiver =
                receivers |> List.find (fun (id, _) -> request.Target.EndsWith $"/fcm/send/{suffixes[labels |> List.find (fun l -> TestDb.Id l = id)]}")
            use opened = JsonDocument.Parse(receiver.Open request.Body)
            Assert.Equal("Designers", str (opened.RootElement.GetProperty "title"))
            Assert.Equal(payload.Path, str (opened.RootElement.GetProperty("options").GetProperty("data").GetProperty "path"))
        Assert.True(destroyed.Count = 0)

        // Expired: both subscriptions are destroyed by the handler, in the pool's worker thread.
        let gone = pushService 410 "Gone"
        use _gone = gone.Server
        let handler (id: int64) : Result<unit, string> =
            match db.WriteBlocking(fun tx -> PushSubscription.destroy tx (PushSubscription.find tx.Conn id)) with
            | Ok()
            | Error(RecordNotFound _) -> Ok()
            | Error error -> Error(DbError.display error)
        let pool = WebPushPool(gone.Net, vapid (), handler, NullLogger.Instance)
        let! before = db.Read(fun conn -> PushSubscription.count conn)
        let! message = create ()
        let! _ = db.Read(fun conn -> WebPushPool.pushMessage pool conn db.Env.RichText message (db.Env.Now()))
        do! pool.Shutdown()
        let! after = db.Read(fun conn -> PushSubscription.count conn)
        let count (result: Result<int64, DbError>) = match result with Ok n -> n | Error error -> failwith (DbError.display error)
        Assert.Equal(count before - 2L, count after)
    }

[<Fact>]
let ``the pool keeps subscriptions it failed to reach`` () =
    task {
        let service = pushService 201 "Created"
        use _server = service.Server
        let untrusted = { service.Net with Tls = CustomRoots(Security.Cryptography.X509Certificates.X509Certificate2Collection()) }
        let destroyed = List<int64>()
        let pool = WebPushPool(untrusted, vapid (), (fun id -> lock destroyed (fun () -> destroyed.Add id); Ok()), NullLogger.Instance)
        pool.DeliverLater(notification ((Receiver()).Subscription(1L, "https://fcm.googleapis.com/fcm/send/abc")))
        do! pool.Shutdown()
        Assert.True(destroyed.Count = 0)
    }

/// Answers a lookup only once let go, with nothing: so deliveries wait in the pool, and finish without a network once
/// they are released. (Rust's test holds its deliveries back by not yielding on a single-threaded runtime.)
type private GatedResolver() =
    let gate = TaskCompletionSource()
    member _.Release() = gate.TrySetResult() |> ignore

    interface IResolver with
        member _.Lookup(_host: string) : Task<Result<IPAddress list, exn>> =
            task {
                do! gate.Task
                return Error(exn "released")
            }

[<Fact>]
let ``the pool drops deliveries past its queue`` () =
    task {
        let resolver = GatedResolver()
        let net = { Resolver = resolver; Dialer = TcpDialer(); Tls = SystemRoots }
        let pool = WebPushPool(net, vapid (), (fun _ -> Ok()), NullLogger.Instance)
        let receiver = Receiver()
        let subscription = receiver.Subscription(1L, "https://fcm.googleapis.com/fcm/send/abc")
        for _ in 1 .. (50 + 10000 + 5) do
            pool.DeliverLater(notification subscription)
        Assert.Equal(10050, pool.Pending)
        resolver.Release()
        do! pool.Shutdown()
    }

type private PanickingResolver() =
    interface IResolver with
        member _.Lookup(host: string) : Task<Result<IPAddress list, exn>> = failwith $"resolving {host} panicked"

/// A delivery that panics still gives its place in the queue back, so panics can't fill it up.
[<Fact>]
let ``a panicking delivery frees its slot`` () =
    task {
        let net = { Resolver = PanickingResolver(); Dialer = TcpDialer(); Tls = SystemRoots }
        let pool = WebPushPool(net, vapid (), (fun _ -> Ok()), NullLogger.Instance)
        pool.DeliverLater(notification ((Receiver()).Subscription(1L, "https://fcm.googleapis.com/fcm/send/abc")))
        do! pool.Shutdown()
        Assert.Equal(0, pool.Pending)
    }
