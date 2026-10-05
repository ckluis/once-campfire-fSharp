// Port of rust/crates/db/src/models/push_subscription.rs
// `reference/app/models/push/subscription.rb`, and the subscriber queries of
// `reference/app/models/room/message_pusher.rb`. Delivery lives in the app.
namespace Campfire.Db

open System
open System.Text

type PushSubscription =
    { Id: int64
      UserId: int64
      Endpoint: string option
      P256dhKey: string option
      AuthKey: string option
      UserAgent: string option
      CreatedAt: Timestamp
      UpdatedAt: Timestamp }

/// What `Room::MessagePusher#build_payload` sends.
type PushPayload = { Title: string; Body: string; Path: string }

/// The parts of `URI.parse(endpoint)` the validation looks at.
type internal EndpointUri =
    { Scheme: string
      Host: string
      Port: int option }

module PushSubscription =
    /// `Push::Subscription::PERMITTED_ENDPOINT_HOSTS`
    let permittedEndpointHosts : string list =
        [ "jmt17.google.com"; "fcm.googleapis.com"; "updates.push.services.mozilla.com"; "web.push.apple.com"; "notify.windows.com" ]

    /// How long a payload's title and body may be, counted as the bytes they take in the JSON
    /// message. An encrypted Web Push record holds at most 4096 bytes: 4078 of JSON once the padding
    /// and tag are in, and the icon, path and badge take under 150 of that.
    [<Literal>]
    let MaxPayloadTitleBytes = 256

    [<Literal>]
    let MaxPayloadBodyBytes = 3072

    [<Literal>]
    let private select = "SELECT " + Columns.PushSubscription + " FROM \"push_subscriptions\""

    let private fromRow (r: Row) : PushSubscription =
        { Id = r.Int64 0
          UserId = r.Int64 1
          Endpoint = r.OptText 2
          P256dhKey = r.OptText 3
          AuthKey = r.OptText 4
          UserAgent = r.OptText 5
          CreatedAt = r.Timestamp 6
          UpdatedAt = r.Timestamp 7 }

    /// An unsaved subscription, for validation.
    let build
        (userId: int64)
        (endpoint: string option)
        (p256dhKey: string option)
        (authKey: string option)
        (userAgent: string option)
        : PushSubscription =
        { Id = 0L
          UserId = userId
          Endpoint = endpoint
          P256dhKey = p256dhKey
          AuthKey = authKey
          UserAgent = userAgent
          CreatedAt = Timestamp.FromSecond 0L
          UpdatedAt = Timestamp.FromSecond 0L }

    let find (conn: Conn) (id: int64) : PushSubscription =
        conn.QueryOne(select + " WHERE \"push_subscriptions\".\"id\" = ? LIMIT 1", [| I id |], fromRow)
        |> Err.orNotFound "Push::Subscription"

    let count (conn: Conn) : int64 = conn.Count("""SELECT COUNT(*) FROM "push_subscriptions" """, [||])

    let forUser (conn: Conn) (userId: int64) : PushSubscription list =
        conn.QueryAll(select + " WHERE \"push_subscriptions\".\"user_id\" = ?", [| I userId |], fromRow)

    /// `user.push_subscriptions.find_by(endpoint:, p256dh_key:, auth_key:)`
    let findForUserByKeys (conn: Conn) (userId: int64) (endpoint: string) (p256dhKey: string) (authKey: string) : PushSubscription option =
        conn.QueryOne(
            select
            + " WHERE \"push_subscriptions\".\"user_id\" = ? AND \"push_subscriptions\".\"endpoint\" = ? AND \"push_subscriptions\".\"p256dh_key\" = ? AND \"push_subscriptions\".\"auth_key\" = ? LIMIT 1",
            [| I userId; S endpoint; S p256dhKey; S authKey |],
            fromRow
        )

    let private permittedEndpointHost (host: string) : bool =
        let host = host.ToLowerInvariant()
        host <> ""
        && permittedEndpointHosts |> List.exists (fun permitted -> host = permitted || host.EndsWith("." + permitted, StringComparison.Ordinal))

    let internal parseEndpoint (endpoint: string) : EndpointUri option =
        if endpoint = "" || endpoint |> Seq.exists Char.IsWhiteSpace then
            None
        else
            match endpoint.IndexOf "://" with
            | -1 -> None
            | at ->
                let scheme = endpoint.Substring(0, at).ToLowerInvariant()
                let rest = endpoint.Substring(at + 3)
                let authority =
                    match rest.IndexOfAny [| '/'; '?'; '#' |] with
                    | -1 -> rest
                    | i -> rest.Substring(0, i)
                let authority =
                    match authority.LastIndexOf '@' with
                    | -1 -> authority
                    | i -> authority.Substring(i + 1)
                let hostAndPort =
                    if authority.EndsWith("]", StringComparison.Ordinal) then
                        Some(authority, None)
                    else
                        match authority.LastIndexOf ':' with
                        | -1 -> Some(authority, None)
                        | i ->
                            let port = authority.Substring(i + 1)
                            if port.Length > 0 && port.Length <= 5 && port |> Seq.forall Char.IsAsciiDigit && Int32.Parse port <= 65535 then
                                Some(authority.Substring(0, i), Some(Int32.Parse port))
                            else
                                None
                match hostAndPort with
                | None -> None
                | Some(host, port) ->
                    let defaultPort =
                        match scheme with
                        | "https" -> Some 443
                        | "http" -> Some 80
                        | _ -> None
                    Some
                        { Scheme = scheme
                          Host = host
                          Port = (match port with Some p -> Some p | None -> defaultPort) }

    /// `resolved_endpoint_ip`: only for a permitted https:443 endpoint.
    let resolvedEndpointIp (resolve: string -> string option) (subscription: PushSubscription) : string option =
        match subscription.Endpoint |> Option.bind parseEndpoint with
        | Some uri when uri.Scheme = "https" && uri.Port = Some 443 && permittedEndpointHost uri.Host -> resolve uri.Host
        | _ -> None

    /// Validations: endpoint present, then `validate_endpoint_url`. `resolve` is
    /// `RestrictedHTTP::PrivateNetworkGuard.resolve` (None for private or unresolvable).
    let validate (resolve: string -> string option) (subscription: PushSubscription) : Errors =
        let endpoint = defaultArg subscription.Endpoint ""
        let errors = if String.IsNullOrWhiteSpace endpoint then Errors.empty |> Errors.add "endpoint" "can't be blank" else Errors.empty
        match parseEndpoint endpoint with
        | None -> errors |> Errors.add "endpoint" "is not a valid URL"
        | Some uri when uri.Scheme <> "https" -> errors |> Errors.add "endpoint" "must use HTTPS"
        | Some uri when uri.Port <> Some 443 -> errors |> Errors.add "endpoint" "must use the default HTTPS port"
        | Some uri when not (permittedEndpointHost uri.Host) -> errors |> Errors.add "endpoint" "is not a permitted push service"
        | Some _ when (resolvedEndpointIp resolve subscription).IsNone ->
            errors |> Errors.add "endpoint" "resolves to a private or invalid IP address"
        | Some _ -> errors

    /// `create`: validates (see `validate`) then inserts.
    let create (tx: Tx) (subscription: PushSubscription) (resolve: string -> string option) : PushSubscription =
        Err.validate (validate resolve subscription)
        let now = tx.Now()
        let id =
            tx.Conn.QueryRow(
                """INSERT INTO "push_subscriptions" ("auth_key", "created_at", "endpoint", "p256dh_key", "updated_at", "user_agent", "user_id") VALUES (?, ?, ?, ?, ?, ?, ?) RETURNING "id" """,
                [| Sql.optS subscription.AuthKey
                   T now
                   Sql.optS subscription.Endpoint
                   Sql.optS subscription.P256dhKey
                   T now
                   Sql.optS subscription.UserAgent
                   I subscription.UserId |],
                fun r -> r.Int64 0
            )
        { subscription with Id = id; CreatedAt = now; UpdatedAt = now }

    let destroy (tx: Tx) (subscription: PushSubscription) : unit =
        tx.Conn.Execute("""DELETE FROM "push_subscriptions" WHERE "push_subscriptions"."id" = ?""", [| I subscription.Id |]) |> ignore

    /// `Push::Subscription.destroy_by(endpoint:, user_id:)`
    let destroyByEndpoint (tx: Tx) (userId: int64) (endpoint: string) : unit =
        let ids =
            tx.Conn.QueryAll(
                """SELECT "push_subscriptions"."id" FROM "push_subscriptions" WHERE "push_subscriptions"."endpoint" = ? AND "push_subscriptions"."user_id" = ?""",
                [| S endpoint; I userId |],
                fun r -> r.Int64 0
            )
        for id in ids do
            tx.Conn.Execute("""DELETE FROM "push_subscriptions" WHERE "push_subscriptions"."id" = ?""", [| I id |]) |> ignore

    /// The badge for a notification: `user.memberships.unread.count`.
    let badge (conn: Conn) (subscription: PushSubscription) : int64 = Membership.unreadCount conn subscription.UserId

    // Room::MessagePusher

    /// The bytes `c` takes in a JSON string: `"`, `\` and the short escapes take two, other
    /// control characters six (`\u001f`).
    let private jsonLen (c: Rune) : int =
        match c.Value with
        | 0x22
        | 0x5c
        | 0x0a
        | 0x0d
        | 0x09
        | 0x08
        | 0x0c -> 2
        | v when v < 0x20 -> 6
        | _ -> c.Utf8SequenceLength

    /// `text`, cut short with an ellipsis if it takes more than `maxBytes` as a JSON string's
    /// contents.
    let internal truncateJsonString (text: string) (maxBytes: int) : string =
        let runes = text.EnumerateRunes() |> Seq.toArray
        if (runes |> Array.sumBy jsonLen) <= maxBytes then
            text
        else
            let ellipsis = Rune '…'
            let mutable used = ellipsis.Utf8SequenceLength
            let cut =
                runes
                |> Array.tryFindIndex (fun c ->
                    used <- used + jsonLen c
                    used > maxBytes)
                |> Option.defaultValue runes.Length
            let out = StringBuilder()
            for c in Array.sub runes 0 cut do
                out.Append(c.ToString()) |> ignore
            out.Append(ellipsis.ToString()).ToString()

    /// `build_payload`: direct rooms show the sender; others the room and "Sender: body".
    /// Unlike Rails, a long title or body is cut short (with an ellipsis) so the notification
    /// still fits a push message.
    let payloadFor (conn: Conn) (richText: RichText) (room: Room) (message: Message) : PushPayload =
        let creator = Message.creator conn message
        let body = Message.plainTextBody conn richText message
        let path = $"/rooms/{room.Id}"
        let title, body =
            if Room.isDirect room then
                creator.Name, body
            else
                defaultArg room.Name "", $"{creator.Name}: {body}"
        { Title = truncateJsonString title MaxPayloadTitleBytes
          Body = truncateJsonString body MaxPayloadBodyBytes
          Path = path }

    /// `push_subscriptions_for_users_involved_in_everything`
    let forUsersInvolvedInEverything (conn: Conn) (roomId: int64) (creatorId: int64) (now: Timestamp) : PushSubscription list =
        conn.QueryAll(
            "SELECT "
            + Columns.PushSubscription
            + " FROM \"push_subscriptions\" INNER JOIN \"users\" ON \"users\".\"id\" = \"push_subscriptions\".\"user_id\" INNER JOIN \"memberships\" ON \"memberships\".\"user_id\" = \"users\".\"id\" WHERE (\"memberships\".\"connected_at\" IS NULL OR \"memberships\".\"connected_at\" < ?) AND \"memberships\".\"room_id\" = ? AND \"memberships\".\"user_id\" != ? AND \"memberships\".\"involvement\" = 'everything'",
            [| T(Membership.connectionCutoff now); I roomId; I creatorId |],
            fromRow
        )

    /// `push_subscriptions_for_mentionable_users(message.mentionees)`
    let forMentionedUsers (conn: Conn) (roomId: int64) (creatorId: int64) (mentioneeIds: int64 list) (now: Timestamp) : PushSubscription list =
        if List.isEmpty mentioneeIds then
            []
        else
            let sql =
                "SELECT "
                + Columns.PushSubscription
                + " FROM \"push_subscriptions\" INNER JOIN \"users\" ON \"users\".\"id\" = \"push_subscriptions\".\"user_id\" INNER JOIN \"memberships\" ON \"memberships\".\"user_id\" = \"users\".\"id\" WHERE (\"memberships\".\"connected_at\" IS NULL OR \"memberships\".\"connected_at\" < ?) AND \"memberships\".\"room_id\" = ? AND \"memberships\".\"user_id\" != ? AND \"memberships\".\"involvement\" = 'mentions' AND \"push_subscriptions\".\"user_id\" IN ("
                + Sql.placeholders (List.length mentioneeIds)
                + ")"
            let values = Array.ofList (T(Membership.connectionCutoff now) :: I roomId :: I creatorId :: (mentioneeIds |> List.map I))
            conn.QueryAll(sql, values, fromRow)

    /// `Room::MessagePusher#push`: the payload, and the subscriptions it goes to (everything
    /// first, then mentions).
    let pushesFor (conn: Conn) (richText: RichText) (message: Message) (now: Timestamp) : PushPayload * PushSubscription list * PushSubscription list =
        let room = Room.find conn message.RoomId
        let payload = payloadFor conn richText room message
        let everything = forUsersInvolvedInEverything conn room.Id message.CreatorId now
        let mentioneeIds = Message.mentionees conn richText message |> List.map (fun u -> u.Id)
        let mentions = forMentionedUsers conn room.Id message.CreatorId mentioneeIds now
        payload, everything, mentions
