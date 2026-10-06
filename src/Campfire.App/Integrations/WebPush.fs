// Port of rust/crates/campfire/src/integrations/web_push.rs
//
// Web Push: `WebPush::Notification` and `WebPush::Pool` (reference/lib/web_push), the gem request they make
// (web-push 3.1.0 with the `WebPush::PersistentRequest` patch in reference/config/initializers/web_push.rb), and
// `Room::MessagePusher#push` (`WebPushPool.pushMessage`).
//
// Delivery resolves the endpoint on the worker, only for a permitted `https://...:443` push service, through the
// private network guard, and connects to that address with no proxy.
namespace Campfire.App.Integrations

open System
open System.Net
open System.Threading
open System.Threading.Tasks
open Campfire.Db
open Campfire.RailsCompat
open Campfire.RichText

/// `Push::Subscription#notification(title:, body:, path:)`: built on the enqueuing side, with the badge counted
/// there. The endpoint is resolved only when it's delivered.
type Notification =
    { Title: string
      Body: string
      Path: string
      Badge: int64
      Subscription: PushSubscription }

/// What `WebPush.payload_send` raised.
type DeliveryError =
    /// `WebPush::ExpiredSubscription` (410) and `WebPush::InvalidSubscription` (404): the push service doesn't
    /// know the subscription (any more).
    | SubscriptionGone of kind: string * host: string * status: int
    /// The gem's other `ResponseError`s: 401/403 and 400 "UnauthorizedRegistration" (`Unauthorized`), 413, 429,
    /// 5xx, and any other non-2xx.
    | Response of kind: string * host: string * status: int
    /// `OpenSSL::PKey::EC::Point::Error`: the subscription's key isn't a P-256 point.
    | InvalidSubscriptionKey of string
    /// `ArgumentError`: blank or malformed keys, an oversized payload.
    | Argument of string
    /// `OpenSSL::SSL::SSLError`: a failed TLS session, which may well be our side's fault (a clock or CA store
    /// problem).
    | Tls of string
    /// Connection failures and timeouts.
    | Http of HttpError

module DeliveryError =
    let message (error: DeliveryError) : string =
        match error with
        | SubscriptionGone(kind, host, status)
        | Response(kind, host, status) -> $"{kind}: host: {host}, status: {status}"
        | InvalidSubscriptionKey message
        | Argument message
        | Tls message -> message
        | Http error -> HttpError.message error

    /// Whether the subscription can never be delivered to, so the pool destroys it. `WebPush::Pool#deliver` also
    /// destroys it for a 410 and any `OpenSSL::OpenSSLError`, which includes TLS failures and a bad VAPID key;
    /// those say nothing about the subscription, and a 404 does (RFC 8030, section 7.3).
    let invalidatesSubscription (error: DeliveryError) : bool =
        match error with
        | SubscriptionGone _
        | InvalidSubscriptionKey _ -> true
        | _ -> false

    /// The Ruby exception class, for the pool's log line.
    let className (error: DeliveryError) : string =
        match error with
        | SubscriptionGone(kind, _, _)
        | Response(kind, _, _) -> kind
        | InvalidSubscriptionKey _ -> "OpenSSL::PKey::EC::Point::Error"
        | Argument _ -> "ArgumentError"
        | Tls _ -> "OpenSSL::SSL::SSLError"
        | Http OpenTimeout -> "Net::OpenTimeout"
        | Http ReadTimeout -> "Net::ReadTimeout"
        | Http _ -> "SystemCallError"

    let ofEncryption (error: EncryptionError) : DeliveryError =
        match error with
        | EncryptionError.Argument message -> Argument message
        | EncryptionError.InvalidKey message -> InvalidSubscriptionKey message

    let ofHttp (error: HttpError) : DeliveryError =
        match error with
        | HttpError.Tls message -> Tls message
        | other -> Http other

    /// The error as an exception, for the places that raise it.
    let toExn (error: DeliveryError) : exn = Exception($"{className error}: {message error}")

module WebPush =
    /// `WebPush::Request#default_options[:ttl]`: four weeks.
    [<Literal>]
    let private TtlSeconds = 2419200

    [<Literal>]
    let private Urgency = "high"

    /// Per connect and read. The web-push gem leaves `Net::HTTP`'s 60 seconds, which let a slow push service hold
    /// one of the pool's few workers for minutes.
    let private timeouts: Timeouts = { Open = TimeSpan.FromSeconds 10.0; Read = TimeSpan.FromSeconds 10.0 }

    /// For a whole delivery, however the push service trickles its reply.
    let private deliveryDeadline = TimeSpan.FromSeconds 30.0

    /// `Rails.application.routes.url_helpers.account_logo_path`
    [<Literal>]
    let private IconPath = "/account/logo"

    module Notification =
        /// `subscription.notification(**payload)`: the badge is `user.memberships.unread.count`.
        let build (conn: Conn) (subscription: PushSubscription) (payload: PushPayload) : Notification =
            { Title = payload.Title
              Body = payload.Body
              Path = payload.Path
              Badge = PushSubscription.badge conn subscription
              Subscription = subscription }

        /// `encoded_message`: `JSON.generate` (plain JSON, no HTML escaping).
        let encodedMessage (notification: Notification) : string =
            Json.generate (
                Value.Object
                    [ "title", Value.String notification.Title
                      "options",
                      Value.Object
                          [ "body", Value.String notification.Body
                            "icon", Value.String IconPath
                            "data", Value.Object [ "path", Value.String notification.Path; "badge", Value.Int notification.Badge ] ] ]
            )

    let private unixNow () : int64 = DateTimeOffset.UtcNow.ToUnixTimeSeconds()

    /// `WebPush::Request#verify_response`
    let private verifyResponse (status: int) (reason: string) (host: string) : Result<int, DeliveryError> =
        let error kind = Error(Response(kind, host, status))
        let gone kind = Error(SubscriptionGone(kind, host, status))
        match status with
        | 410 -> gone "WebPush::ExpiredSubscription"
        | 404 -> gone "WebPush::InvalidSubscription"
        | 401
        | 403 -> error "WebPush::Unauthorized"
        | 400 when reason = "UnauthorizedRegistration" -> error "WebPush::Unauthorized"
        | 413 -> error "WebPush::PayloadTooLarge"
        | 429 -> error "WebPush::TooManyRequests"
        | s when s >= 500 && s <= 599 -> error "WebPush::PushServiceError"
        | s when s >= 200 && s <= 299 -> Ok status
        | _ -> error "WebPush::ResponseError"

    /// `WebPush.payload_send(message:, endpoint:, endpoint_ip:, p256dh:, auth:, vapid:, urgency: "high")` through
    /// the pinned-address branch of `WebPush::PersistentRequest`.
    let private payloadSend
        (net: Network)
        (vapid: VapidConfig)
        (endpoint: string)
        (endpointIp: IPAddress)
        (subscription: PushSubscription)
        (message: byte[])
        (now: int64)
        : Task<Result<int, DeliveryError>> =
        task {
            match RubyUri.parse endpoint with
            | Error _ -> return Error(Argument("bad URI(is not URI?): " + Json.generate (Value.String endpoint)))
            | Ok uri ->
                let host = defaultArg uri.Host ""
                match Encryption.encrypt message subscription.P256dhKey subscription.AuthKey with
                | Error error -> return Error(DeliveryError.ofEncryption error)
                | Ok payload ->
                    let scheme = (defaultArg uri.Scheme "").ToLowerInvariant()
                    let audience = $"{scheme}://{host}"
                    let headers =
                        [ "Content-Type", "application/octet-stream"
                          "Ttl", string TtlSeconds
                          "Urgency", Urgency
                          "Content-Encoding", "aes128gcm"
                          "Content-Length", string payload.Length
                          "Authorization", vapid.Authorization(audience, now) ]
                    let target: Endpoint =
                        { Https = true
                          Host = host
                          Port = (match uri.Port with Some port -> int port | None -> 443)
                          PinnedIp = Some endpointIp }
                    let request =
                        Request.netHttp "POST" (Http.requestUri uri) None headers |> Request.transport true target
                    let request = { request with Body = payload }
                    use deadline = new CancellationTokenSource(deliveryDeadline)
                    let! exchanged =
                        task {
                            try
                                return! Http.exchange net target request timeouts deadline.Token
                            with :? OperationCanceledException ->
                                return Error ReadTimeout
                        }
                    match exchanged with
                    | Error error -> return Error(DeliveryError.ofHttp (if deadline.IsCancellationRequested then ReadTimeout else error))
                    | Ok response ->
                        use response = response
                        return verifyResponse response.Status response.Reason host
        }

    /// `Push::Subscription#resolved_endpoint_ip`
    let private resolvedEndpointIp (net: Network) (subscription: PushSubscription) : Task<IPAddress option> =
        task {
            match PushSubscription.resolvedEndpointIp Some subscription with
            | None -> return None
            | Some host ->
                match! Guard.resolve net.Resolver host with
                | Ok ip -> return Some ip
                | Error _ -> return None
        }

    /// `WebPush::Notification#deliver`: nothing happens when the endpoint isn't a permitted push service or doesn't
    /// resolve to a public address (`Ok None`); otherwise the push service's status.
    let deliver (net: Network) (vapid: VapidConfig) (notification: Notification) : Task<Result<int option, DeliveryError>> =
        task {
            match! resolvedEndpointIp net notification.Subscription with
            | None -> return Ok None
            | Some endpointIp ->
                let endpoint = defaultArg notification.Subscription.Endpoint ""
                let message = Text.Encoding.UTF8.GetBytes(Notification.encodedMessage notification)
                let! sent = payloadSend net vapid endpoint endpointIp notification.Subscription message (unixNow ())
                return sent |> Result.map Some
        }

    /// `Users::PushSubscriptions::TestNotificationsController#create`: a "Campfire Test" notification with a random
    /// body, delivered inline. `path` is `user_push_subscriptions_url` (a full URL); `badge` is the subscriber's
    /// unread count. Errors propagate, as in Rails.
    let deliverTestNotification
        (net: Network)
        (vapid: VapidConfig)
        (subscription: PushSubscription)
        (badge: int64)
        (path: string)
        : Task<Result<unit, DeliveryError>> =
        task {
            let notification =
                { Title = "Campfire Test"
                  Body = Guid.NewGuid().ToString()
                  Path = path
                  Badge = badge
                  Subscription = subscription }
            let! delivered = deliver net vapid notification
            return delivered |> Result.map ignore
        }
