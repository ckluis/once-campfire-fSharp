// Port of rust/crates/db/src/models/{user,membership,room,message,boost,webhook}.rs
//
// These models call one another (a user's ban destroys messages, a message touches its room, a room
// destroys its messages), and F# files can't, so they share a recursive namespace.
namespace rec Campfire.Db

open System
open System.Text
open Campfire.RailsCompat
// RailsCompat has a `Timestamp` of its own (an alias of DateTimeOffset); ours is the one models use.
open Campfire.Db

// ---------------------------------------------------------------------------------------------
// User
// `reference/app/models/user.rb` and `user/*.rb` (Role, Bot, Bannable, Mentionable; Avatar
// and Transferable are signed ids, which live in `Campfire.RailsCompat`).
// ---------------------------------------------------------------------------------------------

/// `enum :role, %i[ member administrator bot ]`
type Role =
    | Member = 0
    | Administrator = 1
    | Bot = 2

module Role =
    let name (role: Role) : string =
        match role with
        | Role.Member -> "member"
        | Role.Administrator -> "administrator"
        | _ -> "bot"

    let fromName (name: string) : Role option =
        match name with
        | "member" -> Some Role.Member
        | "administrator" -> Some Role.Administrator
        | "bot" -> Some Role.Bot
        | _ -> None

    let toSql (role: Role) : SqlArg = I(int64 role)

    let ofSql (value: int64) : Role =
        match value with
        | 0L -> Role.Member
        | 1L -> Role.Administrator
        | 2L -> Role.Bot
        | other -> failwith $"role {other} out of range"

/// `enum :status, %i[ active deactivated banned ], default: :active`
type Status =
    | Active = 0
    | Deactivated = 1
    | Banned = 2

module Status =
    let name (status: Status) : string =
        match status with
        | Status.Active -> "active"
        | Status.Deactivated -> "deactivated"
        | _ -> "banned"

    let fromName (name: string) : Status option =
        match name with
        | "active" -> Some Status.Active
        | "deactivated" -> Some Status.Deactivated
        | "banned" -> Some Status.Banned
        | _ -> None

    let toSql (status: Status) : SqlArg = I(int64 status)

    let ofSql (value: int64) : Status =
        match value with
        | 0L -> Status.Active
        | 1L -> Status.Deactivated
        | 2L -> Status.Banned
        | other -> failwith $"status {other} out of range"

type User =
    { Id: int64
      Name: string
      EmailAddress: string option
      PasswordDigest: string option
      Role: Role
      Status: Status
      Bio: string option
      BotToken: string option
      CreatedAt: Timestamp
      UpdatedAt: Timestamp }

/// A password hashed for `password_digest`. Hashing takes about 250 ms at cost 12, so it's done
/// before the write that saves it, never on the writer.
type PasswordDigest = PasswordDigest of string

/// Attributes for `User.create!`.
type NewUser =
    { Name: string
      EmailAddress: string option
      /// `has_secure_password`'s `password=`, already hashed.
      PasswordDigest: PasswordDigest option
      Role: Role
      Bio: string option
      BotToken: string option }

module NewUser =
    let create (name: string) : NewUser =
        { Name = name
          EmailAddress = None
          PasswordDigest = None
          Role = Role.Member
          Bio = None
          BotToken = None }

/// Attributes for `user.update`. `None` leaves an attribute alone.
type UserChanges =
    { Name: string option
      EmailAddress: string option option
      PasswordDigest: PasswordDigest option
      Role: Role option
      Status: Status option
      Bio: string option option
      BotToken: string option option }

module UserChanges =
    let none : UserChanges =
        { Name = None
          EmailAddress = None
          PasswordDigest = None
          Role = None
          Status = None
          Bio = None
          BotToken = None }

module PasswordDigest =
    let intoString (PasswordDigest digest) : string = digest

    /// `BCrypt::Password.create(password, cost:)`, in the `$2a$` format bcrypt-ruby writes.
    let digest (password: string) (cost: int) : string =
        match Password.digestWithCost password cost with
        | Ok digest -> digest
        | Error e -> Err.fail (DbError.other $"{e}")

    /// `BCrypt::Password.create(password, cost:)`. Blocking.
    let create (password: string) (cost: int) : PasswordDigest = PasswordDigest(digest password cost)

    /// `create` on the thread pool.
    let hash (password: string) (cost: int) : System.Threading.Tasks.Task<PasswordDigest> =
        System.Threading.Tasks.Task.Run(fun () -> create password cost)

module User =
    /// `SELECT` and the columns `User.fromRow` reads, `FROM "users"`: queries built outside
    /// this module start with it.
    [<Literal>]
    let Select = Selects.User

    [<Literal>]
    let private findByIdSql = Selects.User + " WHERE \"users\".\"id\" = ? LIMIT 1"

    [<Literal>]
    let private findActiveSql = Selects.User + " WHERE \"users\".\"status\" = 0 AND \"users\".\"id\" = ? LIMIT 1"

    [<Literal>]
    let private findByEmailSql = Selects.User + " WHERE \"users\".\"email_address\" = ? LIMIT 1"

    [<Literal>]
    let private activeOrderedSql = Selects.User + " WHERE \"users\".\"status\" = 0 ORDER BY LOWER(name)"

    [<Literal>]
    let private activeSql = Selects.User + " WHERE \"users\".\"status\" = 0"

    [<Literal>]
    let private activeFilteredSql = Selects.User + " WHERE \"users\".\"status\" = 0 AND (name like ?) ORDER BY LOWER(name)"

    [<Literal>]
    let private activeOrderedWithoutBotsSql = Selects.User + " WHERE \"users\".\"status\" = 0 AND \"users\".\"role\" != 2 ORDER BY LOWER(name)"

    [<Literal>]
    let private activeBotsOrderedSql = Selects.User + " WHERE \"users\".\"status\" = 0 AND \"users\".\"role\" = 2 ORDER BY LOWER(name)"

    [<Literal>]
    let private findActiveBotSql = Selects.User + " WHERE \"users\".\"status\" = 0 AND \"users\".\"role\" = 2 AND \"users\".\"id\" = ? LIMIT 1"

    [<Literal>]
    let private findActiveByEmailSql = Selects.User + " WHERE \"users\".\"status\" = 0 AND \"users\".\"email_address\" = ? LIMIT 1"

    [<Literal>]
    let private authenticateBotSql =
        Selects.User + " WHERE \"users\".\"status\" = 0 AND \"users\".\"role\" = 2 AND \"users\".\"id\" = ? AND \"users\".\"bot_token\" = ? LIMIT 1"

    [<Literal>]
    let private insertSql =
        "INSERT INTO \"users\" (\"bio\", \"bot_token\", \"created_at\", \"email_address\", \"name\", \"password_digest\", \"role\", \"status\", \"updated_at\") VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?) RETURNING \"id\""

    /// `MENTION_CONTENT_TYPE`
    [<Literal>]
    let MentionContentType = "application/vnd.campfire.mention"

    let private dummyDigest = "$2a$12$FiKmSp4UhLvSB4Sd/ZUjQunyKP6.NjDRHdr5LnKUVk.BUn4Mq12WS"

    /// Reads a user from `Columns.User`, starting at column `offset`.
    let fromRowAt (r: Row) (offset: int) : User =
        { Id = r.Int64(offset + 0)
          Name = r.Text(offset + 1)
          EmailAddress = r.OptText(offset + 2)
          PasswordDigest = r.OptText(offset + 3)
          Role = Role.ofSql (r.Int64(offset + 4))
          Status = Status.ofSql (r.Int64(offset + 5))
          Bio = r.OptText(offset + 6)
          BotToken = r.OptText(offset + 7)
          CreatedAt = r.Timestamp(offset + 8)
          UpdatedAt = r.Timestamp(offset + 9) }

    let fromRow (r: Row) : User = fromRowAt r 0

    /// `User.generate_bot_token`
    let generateBotToken () : string = Sql.alphanumeric 12

    // Finders and scopes

    /// `User.find_by_sql`: the users a query starting with `User.Select` returns.
    let findBySql (conn: Conn) (sql: string) (args: SqlArg[]) : User list = conn.QueryAll(sql, args, fromRow)

    let findById (conn: Conn) (id: int64) : User option = conn.QueryOne(findByIdSql, [| I id |], fromRow)

    let find (conn: Conn) (id: int64) : User = findById conn id |> Err.orNotFound "User"

    /// `User.active.find(id)`
    let findActive (conn: Conn) (id: int64) : User =
        conn.QueryOne(findActiveSql, [| I id |], fromRow) |> Err.orNotFound "User"

    let findByEmailAddress (conn: Conn) (emailAddress: string) : User option =
        conn.QueryOne(findByEmailSql, [| S emailAddress |], fromRow)

    let all (conn: Conn) : User list = conn.QueryAll(Select, [||], fromRow)

    let count (conn: Conn) : int64 = conn.Count("""SELECT COUNT(*) FROM "users" """, [||])

    /// `User.where(id: ids)`
    let whereIds (conn: Conn) (ids: int64 list) : User list =
        let sql = Selects.User + " WHERE \"users\".\"id\" IN (" + Sql.placeholders (List.length ids) + ")"
        conn.QueryAll(sql, ids |> List.map I |> Array.ofList, fromRow)

    /// `User.active.ordered`
    let activeOrdered (conn: Conn) : User list = conn.QueryAll(activeOrderedSql, [||], fromRow)

    /// `User.active`
    let active (conn: Conn) : User list = conn.QueryAll(activeSql, [||], fromRow)

    /// `User.active.filtered_by(query).ordered`
    let activeFilteredByOrdered (conn: Conn) (query: string) : User list =
        conn.QueryAll(activeFilteredSql, [| S $"%%{query}%%" |], fromRow)

    /// `User.active.ordered.without_bots`
    let activeOrderedWithoutBots (conn: Conn) : User list = conn.QueryAll(activeOrderedWithoutBotsSql, [||], fromRow)

    /// `User.active_bots.ordered`
    let activeBotsOrdered (conn: Conn) : User list = conn.QueryAll(activeBotsOrderedSql, [||], fromRow)

    /// `User.active_bots.find(id)`
    let findActiveBot (conn: Conn) (id: int64) : User =
        conn.QueryOne(findActiveBotSql, [| I id |], fromRow) |> Err.orNotFound "User"

    /// `User.active.find_by(email_address:)`: the lookup half of `authenticate_by`.
    let findActiveByEmailAddress (conn: Conn) (emailAddress: string) : User option =
        conn.QueryOne(findActiveByEmailSql, [| S emailAddress |], fromRow)

    /// `has_secure_password`'s `authenticate`.
    let authenticate (user: User) (password: string) : bool =
        match user.PasswordDigest with
        | Some digest when digest <> "" -> Password.verify password digest
        | _ -> false

    /// The password half of `User.active.authenticate_by(email_address:, password:)`, given
    /// what `findActiveByEmailAddress` found. Blocking (bcrypt), so it runs with no
    /// connection held. A blank password returns nil before the lookup in Rails; callers skip
    /// the lookup for one too.
    let authenticated (candidate: User option) (password: string) : User option =
        if password = "" then
            None
        else
            match candidate with
            | Some user -> if authenticate user password then Some user else None
            | None ->
                // authenticate_by hashes anyway so a missing account takes as long as a wrong password.
                Password.verify password dummyDigest |> ignore
                None

    /// `User.authenticate_bot(bot_key)`: `"#{id}-#{bot_token}"`
    let authenticateBot (conn: Conn) (botKey: string) : User option =
        // Ruby's `split("-")` drops trailing empty fields; a key without a token finds nothing.
        match botKey.Split '-' with
        | parts when parts.Length >= 2 ->
            let id = parts[0]
            let token = parts[1]
            conn.QueryOne(authenticateBotSql, [| S id; S token |], fromRow)
        | _ -> None

    // Creating

    /// `after_create_commit :grant_membership_to_open_rooms`
    let private grantMembershipToOpenRooms (tx: Tx) (userId: int64) : unit =
        let roomIds =
            tx.Conn.QueryAll("""SELECT "rooms"."id" FROM "rooms" WHERE "rooms"."type" = ?""", [| S "Rooms::Open" |], fun r -> r.Int64 0)
        for batch in List.chunkBySize Room.MembershipInsertBatch roomIds do
            let rows = batch |> List.map (fun _ -> $"({Time.SqliteNow}, ?, {Time.SqliteNow}, ?)")
            let sql =
                "INSERT INTO \"memberships\" (\"created_at\",\"room_id\",\"updated_at\",\"user_id\") VALUES "
                + String.concat ", " rows
                + " ON CONFLICT  DO NOTHING RETURNING \"id\""
            let values = batch |> List.collect (fun roomId -> [ I roomId; I userId ]) |> Array.ofList
            tx.Conn.ExecuteUncached(sql, values) |> ignore

    /// `User.create!`: inserts, then grants memberships to every open room after commit.
    let create (tx: Tx) (attributes: NewUser) : User =
        let now = tx.Now()
        let passwordDigest = attributes.PasswordDigest |> Option.map PasswordDigest.intoString
        let id =
            tx.Conn.QueryRow(
                insertSql,
                [| Sql.optS attributes.Bio
                   Sql.optS attributes.BotToken
                   T now
                   Sql.optS attributes.EmailAddress
                   S attributes.Name
                   Sql.optS passwordDigest
                   Role.toSql attributes.Role
                   Status.toSql Status.Active
                   T now |],
                fun r -> r.Int64 0
            )
        tx.AfterCommit(fun tx -> grantMembershipToOpenRooms tx id)
        find tx.Conn id

    /// `User.create_bot!`
    let createBot (tx: Tx) (name: string) (webhookUrl: string option) : User =
        let user =
            create tx { NewUser.create name with BotToken = Some(generateBotToken ()); Role = Role.Bot }
        match webhookUrl with
        | Some url -> Webhook.create tx user.Id (Some url) |> ignore
        | None -> ()
        user

    // Updating

    /// `user.update(attributes)`: writes only what changed, and nothing at all (not even
    /// `updated_at`) when nothing did.
    let update (tx: Tx) (user: User) (changes: UserChanges) : User =
        let mutable updated = user
        let sets = ResizeArray<string * SqlArg>()
        match changes.Name with
        | Some name when name <> user.Name ->
            updated <- { updated with Name = name }
            sets.Add("name", S name)
        | _ -> ()
        match changes.EmailAddress with
        | Some email when email <> user.EmailAddress ->
            updated <- { updated with EmailAddress = email }
            sets.Add("email_address", Sql.optS email)
        | _ -> ()
        match changes.PasswordDigest with
        | Some digest ->
            let digest = PasswordDigest.intoString digest
            updated <- { updated with PasswordDigest = Some digest }
            sets.Add("password_digest", S digest)
        | None -> ()
        match changes.Role with
        | Some role when role <> user.Role ->
            updated <- { updated with Role = role }
            sets.Add("role", Role.toSql role)
        | _ -> ()
        match changes.Status with
        | Some status when status <> user.Status ->
            updated <- { updated with Status = status }
            sets.Add("status", Status.toSql status)
        | _ -> ()
        match changes.Bio with
        | Some bio when bio <> user.Bio ->
            updated <- { updated with Bio = bio }
            sets.Add("bio", Sql.optS bio)
        | _ -> ()
        match changes.BotToken with
        | Some token when token <> user.BotToken ->
            updated <- { updated with BotToken = token }
            sets.Add("bot_token", Sql.optS token)
        | _ -> ()
        if sets.Count = 0 then
            user
        else
            let now = tx.Now()
            updated <- { updated with UpdatedAt = now }
            sets.Add("updated_at", T now)
            let assignments = sets |> Seq.map (fun (c, _) -> $"\"{c}\" = ?") |> String.concat ", "
            let sql = $"UPDATE \"users\" SET {assignments} WHERE \"users\".\"id\" = ?"
            tx.Conn.Execute(sql, Array.append (sets |> Seq.map snd |> Array.ofSeq) [| I user.Id |]) |> ignore
            updated

    /// `update_bot!`: the webhook first, then the user, in one transaction.
    let updateBot (tx: Tx) (user: User) (changes: UserChanges) (webhookUrl: string option) : User =
        let webhook = Webhook.findByUser tx.Conn user.Id
        let url = webhookUrl |> Option.filter (fun u -> not (String.IsNullOrWhiteSpace u))
        match url, webhook with
        | Some url, Some webhook -> Webhook.updateUrl tx webhook url |> ignore
        | Some url, None -> Webhook.create tx user.Id (Some url) |> ignore
        | None, Some webhook -> Webhook.destroy tx webhook
        | None, None -> ()
        update tx user changes

    let resetBotKey (tx: Tx) (user: User) : User =
        update tx user { UserChanges.none with BotToken = Some(Some(generateBotToken ())) }

    /// After commit: a connection authenticating meanwhile then either finds the sessions gone or
    /// is already listening for this (see `Campfire.Cable`'s connection setup).
    let private closeRemoteConnections (tx: Tx) (user: User) (reconnect: bool) : unit =
        tx.EmitAfterCommit(DisconnectUser(user.Id, reconnect))

    /// `reset_remote_connections`: disconnect this user's sockets and let them reconnect.
    let resetRemoteConnections (tx: Tx) (user: User) : unit = closeRemoteConnections tx user true

    let private deactivatedEmailAddress (user: User) : string option =
        let uuid = Sql.uuid ()
        user.EmailAddress |> Option.map (fun e -> e.Replace("@", $"-deactivated-{uuid}@"))

    /// `deactivate`: disconnects sockets first (mid-transaction, as Rails does), then removes
    /// non-direct memberships, push subscriptions, searches and sessions, and scrambles the
    /// email address.
    let deactivate (tx: Tx) (user: User) : User =
        closeRemoteConnections tx user false
        let conn = tx.Conn
        conn.Execute(
            """DELETE FROM "memberships" WHERE ("memberships"."id") IN (SELECT "memberships"."id" FROM "memberships" INNER JOIN "rooms" AS "room" ON "room"."id" = "memberships"."room_id" WHERE "memberships"."user_id" = ? AND "room"."type" != ?)""",
            [| I user.Id; S "Rooms::Direct" |]
        )
        |> ignore
        conn.Execute("""DELETE FROM "push_subscriptions" WHERE "push_subscriptions"."user_id" = ?""", [| I user.Id |]) |> ignore
        conn.Execute("""DELETE FROM "searches" WHERE "searches"."user_id" = ?""", [| I user.Id |]) |> ignore
        conn.Execute("""DELETE FROM "sessions" WHERE "sessions"."user_id" = ?""", [| I user.Id |]) |> ignore
        let email = deactivatedEmailAddress user
        update tx user { UserChanges.none with Status = Some Status.Deactivated; EmailAddress = Some email }

    /// `User::Bannable#ban`
    let ban (tx: Tx) (user: User) : User =
        // create_bans_from_sessions: `sessions.pluck(:ip_address).compact_blank.uniq`
        let ips =
            tx.Conn.QueryAll("""SELECT "sessions"."ip_address" FROM "sessions" WHERE "sessions"."user_id" = ?""", [| I user.Id |], fun r -> r.OptText 0)
        let seen = ResizeArray<string>()
        for ip in ips |> List.choose id |> List.filter (fun ip -> not (String.IsNullOrWhiteSpace ip)) do
            if not (seen.Contains ip) then
                Ban.create tx user.Id ip |> ignore
                seen.Add ip
        // apply_ban
        closeRemoteConnections tx user false
        tx.Conn.Execute("""DELETE FROM "sessions" WHERE "sessions"."user_id" = ?""", [| I user.Id |]) |> ignore
        tx.EmitAfterCommit(RemoveBannedContent user.Id)
        update tx user { UserChanges.none with Status = Some Status.Banned }

    let unban (tx: Tx) (user: User) : User =
        tx.Conn.Execute("""DELETE FROM "bans" WHERE "bans"."user_id" = ?""", [| I user.Id |]) |> ignore
        update tx user { UserChanges.none with Status = Some Status.Active }

    /// `remove_banned_content`: destroys every message the user wrote. Returns them so the
    /// caller can `broadcast_remove` each one.
    let removeBannedContent (tx: Tx) (user: User) : Message list =
        let messages = Message.byCreator tx.Conn user.Id
        for message in messages do
            Message.destroy tx message
        messages

    /// `deliver_webhook_later(message)`: only bots with a webhook.
    let deliverWebhookLater (tx: Tx) (user: User) (messageId: int64) : unit =
        if (Webhook.findByUser tx.Conn user.Id).IsSome then
            tx.EmitAfterCommit(DeliverWebhook(user.Id, messageId))

    // Associations

    let memberships (conn: Conn) (user: User) : Membership list = Membership.forUser conn user.Id

    let sessions (conn: Conn) (user: User) : Session list = Session.forUser conn user.Id

    let webhook (conn: Conn) (user: User) : Webhook option = Webhook.findByUser conn user.Id

    let webhookUrl (conn: Conn) (user: User) : string option = webhook conn user |> Option.bind (fun w -> w.Url)

    // Attributes

    /// `name.scan(/\b\w/).join`. Ruby's `\w` is ASCII-only, but `\b` treats any Unicode
    /// letter or digit as a word character, so "Émile" contributes nothing.
    let initials (user: User) : string =
        let isWordChar (r: Rune) =
            Rune.IsLetter r
            || Rune.IsNumber r
            || r.Value = int '_'
        let result = StringBuilder()
        let mutable previous: Rune option = None
        for c in user.Name.EnumerateRunes() do
            let asciiWord = (c.IsAscii && Rune.IsLetterOrDigit c) || c.Value = int '_'
            let boundary =
                match previous with
                | None -> true
                | Some p -> not (isWordChar p)
            if asciiWord && boundary then result.Append(c.ToString()) |> ignore
            previous <- Some c
        result.ToString()

    /// `[ name, bio ].compact_blank.join(" – ")`
    let title (user: User) : string =
        [ Some user.Name; user.Bio ]
        |> List.choose id
        |> List.filter (fun s -> not (String.IsNullOrWhiteSpace s))
        |> String.concat " – "

    let botKey (user: User) : string = $"{user.Id}-{defaultArg user.BotToken String.Empty}"

    let isMember (user: User) : bool = user.Role = Role.Member

    let isAdministrator (user: User) : bool = user.Role = Role.Administrator

    let isBot (user: User) : bool = user.Role = Role.Bot

    let isActive (user: User) : bool = user.Status = Status.Active

    let isDeactivated (user: User) : bool = user.Status = Status.Deactivated

    let isBanned (user: User) : bool = user.Status = Status.Banned

    /// `can_administer?(record)`: administrators, the record's creator, or a new record.
    let canAdminister (user: User) (recordCreatorId: int64 option) (recordIsNew: bool) : bool =
        isAdministrator user || recordCreatorId = Some user.Id || recordIsNew

    /// `attachable_plain_text_representation`
    let attachablePlainTextRepresentation (user: User) : string = $"@{user.Name}"

    let reload (conn: Conn) (user: User) : User = find conn user.Id

// ---------------------------------------------------------------------------------------------
// Membership
// `reference/app/models/membership.rb` and `membership/connectable.rb`.
// ---------------------------------------------------------------------------------------------

/// `enum :involvement, %w[ invisible nothing mentions everything ].index_by(&:itself)`
type Involvement =
    | Invisible
    | Nothing
    | Mentions
    | Everything

module Involvement =
    let name (involvement: Involvement) : string =
        match involvement with
        | Invisible -> "invisible"
        | Nothing -> "nothing"
        | Mentions -> "mentions"
        | Everything -> "everything"

    let fromName (name: string) : Involvement option =
        match name with
        | "invisible" -> Some Invisible
        | "nothing" -> Some Nothing
        | "mentions" -> Some Mentions
        | "everything" -> Some Everything
        | _ -> None

    let toSql (involvement: Involvement option) : SqlArg =
        match involvement with
        | Some i -> S(name i)
        | None -> Null

type Membership =
    { Id: int64
      RoomId: int64
      UserId: int64
      /// Nullable in the schema (default "mentions").
      Involvement: Involvement option
      UnreadAt: Timestamp option
      ConnectedAt: Timestamp option
      Connections: int64
      CreatedAt: Timestamp
      UpdatedAt: Timestamp }

module Membership =
    /// `Membership::Connectable::CONNECTION_TTL`
    let connectionTtl : TimeSpan = TimeSpan.FromSeconds 60.0

    [<Literal>]
    let private findSql = Selects.Membership + " WHERE \"memberships\".\"id\" = ? LIMIT 1"

    [<Literal>]
    let private forUserSql = Selects.Membership + " WHERE \"memberships\".\"user_id\" = ?"

    [<Literal>]
    let private forRoomSql = Selects.Membership + " WHERE \"memberships\".\"room_id\" = ?"

    [<Literal>]
    let private findByRoomAndUserSql = Selects.Membership + " WHERE \"memberships\".\"room_id\" = ? AND \"memberships\".\"user_id\" = ? LIMIT 1"

    [<Literal>]
    let private visibleWithOrderedRoomSql =
        "SELECT "
        + Columns.Membership
        + ", "
        + Columns.Room
        + " FROM \"memberships\" INNER JOIN \"rooms\" ON \"rooms\".\"id\" = \"memberships\".\"room_id\" WHERE \"memberships\".\"user_id\" = ? AND \"memberships\".\"involvement\" != 'invisible' ORDER BY LOWER(rooms.name)"

    [<Literal>]
    let private withOrderedRoomSql =
        "SELECT "
        + Columns.Membership
        + ", "
        + Columns.Room
        + " FROM \"memberships\" INNER JOIN \"rooms\" ON \"rooms\".\"id\" = \"memberships\".\"room_id\" WHERE \"memberships\".\"user_id\" = ? ORDER BY LOWER(rooms.name)"

    let private involvementOf (r: Row) (i: int) : Involvement option =
        match r.OptText i with
        | None -> None
        | Some name ->
            match Involvement.fromName name with
            | Some involvement -> Some involvement
            | None -> failwith $"unknown involvement \"{name}\""

    let fromRowAt (r: Row) (offset: int) : Membership =
        { Id = r.Int64(offset + 0)
          RoomId = r.Int64(offset + 1)
          UserId = r.Int64(offset + 2)
          Involvement = involvementOf r (offset + 3)
          UnreadAt = r.OptTimestamp(offset + 4)
          ConnectedAt = r.OptTimestamp(offset + 5)
          Connections = r.Int64(offset + 6)
          CreatedAt = r.Timestamp(offset + 7)
          UpdatedAt = r.Timestamp(offset + 8) }

    let fromRow (r: Row) : Membership = fromRowAt r 0

    /// How many columns `Columns.Membership` lists, where a joined room's columns begin.
    [<Literal>]
    let private ColumnCount = 9

    /// A membership and its room, from `Columns.Membership` followed by `Columns.Room`.
    let private withRoom (r: Row) : Membership * Room = fromRow r, Room.fromRowAt r ColumnCount

    let find (conn: Conn) (id: int64) : Membership =
        conn.QueryOne(findSql, [| I id |], fromRow) |> Err.orNotFound "Membership"

    let count (conn: Conn) : int64 = conn.Count("""SELECT COUNT(*) FROM "memberships" """, [||])

    let forUser (conn: Conn) (userId: int64) : Membership list = conn.QueryAll(forUserSql, [| I userId |], fromRow)

    let forRoom (conn: Conn) (roomId: int64) : Membership list = conn.QueryAll(forRoomSql, [| I roomId |], fromRow)

    /// `room.memberships.find_by(user:)` / `user.memberships.find_by(room_id:)`
    let findByRoomAndUser (conn: Conn) (roomId: int64) (userId: int64) : Membership option =
        conn.QueryOne(findByRoomAndUserSql, [| I roomId; I userId |], fromRow)

    /// `user.memberships.visible.with_ordered_room`, each with its room.
    let visibleWithOrderedRoom (conn: Conn) (userId: int64) : (Membership * Room) list =
        conn.QueryAll(visibleWithOrderedRoomSql, [| I userId |], withRoom)

    /// `user.memberships.with_ordered_room` (invisible included).
    let withOrderedRoom (conn: Conn) (userId: int64) : (Membership * Room) list =
        conn.QueryAll(withOrderedRoomSql, [| I userId |], withRoom)

    /// `user.memberships.without_direct_rooms.count`
    let countWithoutDirectRooms (conn: Conn) (userId: int64) : int64 =
        conn.Count(
            """SELECT COUNT(*) FROM "memberships" INNER JOIN "rooms" "room" ON "room"."id" = "memberships"."room_id" WHERE "memberships"."user_id" = ? AND "room"."type" != 'Rooms::Direct'""",
            [| I userId |]
        )

    /// `user.memberships.unread.count`: the push badge.
    let unreadCount (conn: Conn) (userId: int64) : int64 =
        conn.Count(
            """SELECT COUNT(*) FROM "memberships" WHERE "memberships"."user_id" = ? AND "memberships"."unread_at" IS NOT NULL""",
            [| I userId |]
        )

    /// `CONNECTION_TTL.ago`
    let connectionCutoff (now: Timestamp) : Timestamp = now.Ago connectionTtl

    /// `Membership.connected.exists?(id)`
    let connectedExists (conn: Conn) (id: int64) (now: Timestamp) : bool =
        conn.Exists(
            """SELECT 1 FROM "memberships" WHERE "memberships"."connected_at" >= ? AND "memberships"."id" = ? LIMIT 1""",
            [| T(connectionCutoff now); I id |]
        )

    /// `Membership.disconnected.exists?(id)`
    let disconnectedExists (conn: Conn) (id: int64) (now: Timestamp) : bool =
        conn.Exists(
            """SELECT 1 FROM "memberships" WHERE ("memberships"."connected_at" IS NULL OR "memberships"."connected_at" < ?) AND "memberships"."id" = ? LIMIT 1""",
            [| T(connectionCutoff now); I id |]
        )

    let room (conn: Conn) (membership: Membership) : Room = Room.find conn membership.RoomId

    let user (conn: Conn) (membership: Membership) : User = User.find conn membership.UserId

    // Involvement and read state

    let involvedIn (membership: Membership) (involvement: Involvement) : bool = membership.Involvement = Some involvement

    /// `update!(involvement:)`
    let updateInvolvement (tx: Tx) (membership: Membership) (involvement: Involvement option) : Membership =
        if membership.Involvement = involvement then
            membership
        else
            let now = tx.Now()
            tx.Conn.Execute(
                """UPDATE "memberships" SET "involvement" = ?, "updated_at" = ? WHERE "memberships"."id" = ?""",
                [| Involvement.toSql involvement; T now; I membership.Id |]
            )
            |> ignore
            { membership with Involvement = involvement; UpdatedAt = now }

    /// `read`: `update!(unread_at: nil)`
    let read (tx: Tx) (membership: Membership) : Membership =
        if membership.UnreadAt.IsNone then
            membership
        else
            let now = tx.Now()
            tx.Conn.Execute(
                """UPDATE "memberships" SET "unread_at" = ?, "updated_at" = ? WHERE "memberships"."id" = ?""",
                [| Null; T now; I membership.Id |]
            )
            |> ignore
            { membership with UnreadAt = None; UpdatedAt = now }

    let unread (membership: Membership) : bool = membership.UnreadAt.IsSome

    /// `destroy`: the user's sockets reconnect after commit, so their subscriptions to this
    /// room are dropped.
    let destroy (tx: Tx) (membership: Membership) : unit =
        tx.Conn.Execute("""DELETE FROM "memberships" WHERE "memberships"."id" = ?""", [| I membership.Id |]) |> ignore
        let userId = membership.UserId
        tx.AfterCommit(fun tx -> User.resetRemoteConnections tx (User.find tx.Conn userId))

    // Membership::Connectable

    /// `Membership.disconnect_all`
    let disconnectAll (tx: Tx) : int =
        let now = tx.Now()
        tx.Conn.Execute(
            """UPDATE "memberships" SET "connected_at" = ?, "connections" = ?, "updated_at" = ? WHERE "memberships"."connected_at" >= ?""",
            [| Null; I 0L; T now; T(connectionCutoff now) |]
        )

    /// `Membership.connect(membership, connections)`: no `updated_at`.
    let connect (tx: Tx) (id: int64) (connections: int64) : unit =
        tx.Conn.Execute(
            """UPDATE "memberships" SET "connections" = ?, "connected_at" = ?, "unread_at" = ? WHERE "memberships"."id" = ?""",
            [| I connections; T(tx.Now()); Null; I id |]
        )
        |> ignore

    /// `connected?`
    let isConnected (membership: Membership) (now: Timestamp) : bool =
        match membership.ConnectedAt with
        | Some at -> at >= connectionCutoff now
        | None -> false

    /// `present`
    let present (tx: Tx) (membership: Membership) : unit =
        let connections = if isConnected membership (tx.Now()) then membership.Connections + 1L else 1L
        connect tx membership.Id connections

    /// `increment!(:connections, touch: true)` / `decrement!`
    let private updateCounter (tx: Tx) (membership: Membership) (by: int64) : Membership =
        let now = tx.Now()
        let sql =
            if by >= 0L then
                """UPDATE "memberships" SET "connections" = COALESCE("memberships"."connections", 0) + ?, "updated_at" = ? WHERE "memberships"."id" = ?"""
            else
                """UPDATE "memberships" SET "connections" = COALESCE("memberships"."connections", 0) - ?, "updated_at" = ? WHERE "memberships"."id" = ?"""
        tx.Conn.Execute(sql, [| I(abs by); T now; I membership.Id |]) |> ignore
        { membership with Connections = membership.Connections + by; UpdatedAt = now }

    /// `update!(connections:)`, a no-op when unchanged.
    let private updateConnections (tx: Tx) (membership: Membership) (connections: int64) : Membership =
        if membership.Connections = connections then
            membership
        else
            let now = tx.Now()
            tx.Conn.Execute(
                """UPDATE "memberships" SET "connections" = ?, "updated_at" = ? WHERE "memberships"."id" = ?""",
                [| I connections; T now; I membership.Id |]
            )
            |> ignore
            { membership with Connections = connections; UpdatedAt = now }

    let private incrementConnections (tx: Tx) (membership: Membership) : Membership =
        if isConnected membership (tx.Now()) then updateCounter tx membership 1L else updateConnections tx membership 1L

    let private decrementConnections (tx: Tx) (membership: Membership) : Membership =
        if isConnected membership (tx.Now()) then updateCounter tx membership -1L else updateConnections tx membership 0L

    /// `touch :connected_at`
    let private touchConnectedAt (tx: Tx) (membership: Membership) : Membership =
        let now = tx.Now()
        tx.Conn.Execute(
            """UPDATE "memberships" SET "updated_at" = ?, "connected_at" = ? WHERE "memberships"."id" = ?""",
            [| T now; T now; I membership.Id |]
        )
        |> ignore
        { membership with ConnectedAt = Some now; UpdatedAt = now }

    /// `connected`
    let connected (tx: Tx) (membership: Membership) : Membership =
        incrementConnections tx membership |> touchConnectedAt tx

    /// `disconnected`
    let disconnected (tx: Tx) (membership: Membership) : Membership =
        let membership = decrementConnections tx membership
        if membership.Connections < 1L && membership.ConnectedAt.IsSome then
            let now = tx.Now()
            tx.Conn.Execute(
                """UPDATE "memberships" SET "connected_at" = ?, "updated_at" = ? WHERE "memberships"."id" = ?""",
                [| Null; T now; I membership.Id |]
            )
            |> ignore
            { membership with ConnectedAt = None; UpdatedAt = now }
        else
            membership

    /// `refresh_connection`
    let refreshConnection (tx: Tx) (membership: Membership) : Membership =
        let membership = if not (isConnected membership (tx.Now())) then incrementConnections tx membership else membership
        touchConnectedAt tx membership

    let reload (conn: Conn) (membership: Membership) : Membership = find conn membership.Id

// ---------------------------------------------------------------------------------------------
// Room
// `reference/app/models/room.rb`, `rooms/*.rb` and `room/message_pusher.rb`'s queries.
// ---------------------------------------------------------------------------------------------

/// The STI `type` column.
type RoomType =
    | Open
    | Closed
    | Direct

module RoomType =
    let className (roomType: RoomType) : string =
        match roomType with
        | Open -> "Rooms::Open"
        | Closed -> "Rooms::Closed"
        | Direct -> "Rooms::Direct"

    let fromClassName (name: string) : RoomType option =
        match name with
        | "Rooms::Open" -> Some Open
        | "Rooms::Closed" -> Some Closed
        | "Rooms::Direct" -> Some Direct
        | _ -> None

    /// `default_involvement`: "everything" in direct rooms, "mentions" elsewhere.
    let defaultInvolvement (roomType: RoomType) : string =
        match roomType with
        | Direct -> "everything"
        | _ -> "mentions"

    let toSql (roomType: RoomType) : SqlArg = S(className roomType)

type Room =
    { Id: int64
      Name: string option
      RoomType: RoomType
      CreatorId: int64
      CreatedAt: Timestamp
      UpdatedAt: Timestamp }

module Room =
    /// Rows per `INSERT` of memberships, well under SQLite's bound-variable limit.
    [<Literal>]
    let MembershipInsertBatch = 1_000

    [<Literal>]
    let private findSql = Selects.Room + " WHERE \"rooms\".\"id\" = ? LIMIT 1"

    [<Literal>]
    let private selectForUser =
        "SELECT "
        + Columns.Room
        + " FROM \"rooms\" INNER JOIN \"memberships\" ON \"rooms\".\"id\" = \"memberships\".\"room_id\" WHERE \"memberships\".\"user_id\" = ?"

    [<Literal>]
    let private usersJoin =
        Selects.User + " INNER JOIN \"memberships\" ON \"users\".\"id\" = \"memberships\".\"user_id\" WHERE \"memberships\".\"room_id\" = ?"

    let private roomTypeOf (r: Row) (i: int) : RoomType =
        let name = r.Text i
        match RoomType.fromClassName name with
        | Some roomType -> roomType
        | None -> failwith $"unknown room type \"{name}\""

    let fromRowAt (r: Row) (offset: int) : Room =
        { Id = r.Int64(offset + 0)
          Name = r.OptText(offset + 1)
          RoomType = roomTypeOf r (offset + 2)
          CreatorId = r.Int64(offset + 3)
          CreatedAt = r.Timestamp(offset + 4)
          UpdatedAt = r.Timestamp(offset + 5) }

    let fromRow (r: Row) : Room = fromRowAt r 0

    let findById (conn: Conn) (id: int64) : Room option = conn.QueryOne(findSql, [| I id |], fromRow)

    let find (conn: Conn) (id: int64) : Room = findById conn id |> Err.orNotFound "Room"

    let all (conn: Conn) : Room list = conn.QueryAll(Selects.Room, [||], fromRow)

    /// `Room.opens` / `closeds` / `directs`
    let ofType (conn: Conn) (roomType: RoomType) : Room list =
        conn.QueryAll(Selects.Room + " WHERE \"rooms\".\"type\" = ?", [| RoomType.toSql roomType |], fromRow)

    let countOfType (conn: Conn) (roomType: RoomType) : int64 =
        conn.Count("""SELECT COUNT(*) FROM "rooms" WHERE "rooms"."type" = ?""", [| RoomType.toSql roomType |])

    /// `Room.original`: the oldest room.
    let original (conn: Conn) : Room option =
        conn.QueryOne(Selects.Room + " ORDER BY \"rooms\".\"created_at\" ASC LIMIT 1", [||], fromRow)

    // `Current.user.rooms` and its scopes

    /// `user.rooms`
    let forUser (conn: Conn) (userId: int64) : Room list = conn.QueryAll(selectForUser, [| I userId |], fromRow)

    /// `user.rooms.find_by(id:)`
    let findForUser (conn: Conn) (userId: int64) (roomId: int64) : Room option =
        conn.QueryOne(selectForUser + " AND \"rooms\".\"id\" = ? LIMIT 1", [| I userId; I roomId |], fromRow)

    /// `user.rooms.directs` / `.opens` / `.closeds`
    let forUserOfType (conn: Conn) (userId: int64) (roomType: RoomType) : Room list =
        conn.QueryAll(selectForUser + " AND \"rooms\".\"type\" = ?", [| I userId; RoomType.toSql roomType |], fromRow)

    /// `user.rooms.without_directs`
    let forUserWithoutDirects (conn: Conn) (userId: int64) : Room list =
        conn.QueryAll(selectForUser + " AND \"rooms\".\"type\" != ?", [| I userId; RoomType.toSql Direct |], fromRow)

    /// `user.rooms.original`
    let originalForUser (conn: Conn) (userId: int64) : Room option =
        conn.QueryOne(selectForUser + " ORDER BY \"rooms\".\"created_at\" ASC LIMIT 1", [| I userId |], fromRow)

    /// `user.rooms.last`
    let lastForUser (conn: Conn) (userId: int64) : Room option =
        conn.QueryOne(selectForUser + " ORDER BY \"rooms\".\"id\" DESC LIMIT 1", [| I userId |], fromRow)

    // Memberships

    let memberships (conn: Conn) (room: Room) : Membership list = Membership.forRoom conn room.Id

    /// `Membership.insert_all(... { room_id:, user_id:, involvement: })`
    let private insertMemberships (tx: Tx) (roomId: int64) (involvement: string) (userIds: int64 list) : unit =
        // In batches: SQLite binds at most 32,766 variables per statement, 3 per row here.
        for batch in List.chunkBySize MembershipInsertBatch userIds do
            let rows = batch |> List.map (fun _ -> $"({Time.SqliteNow}, ?, ?, {Time.SqliteNow}, ?)")
            let sql =
                "INSERT INTO \"memberships\" (\"created_at\",\"involvement\",\"room_id\",\"updated_at\",\"user_id\") VALUES "
                + String.concat ", " rows
                + " ON CONFLICT  DO NOTHING RETURNING \"id\""
            let values = batch |> List.collect (fun userId -> [ S involvement; I roomId; I userId ]) |> Array.ofList
            tx.Conn.ExecuteUncached(sql, values) |> ignore

    /// `memberships.grant_to(users)`: `Membership.insert_all` with the room's default
    /// involvement, skipping existing members.
    let grantTo (tx: Tx) (room: Room) (userIds: int64 list) : unit =
        insertMemberships tx room.Id (RoomType.defaultInvolvement room.RoomType) userIds

    /// `memberships.grant_to(User.active)`, from `Rooms::Open`'s `after_save_commit`.
    let private grantToActiveUsers (tx: Tx) (roomId: int64) : unit =
        let userIds = tx.Conn.QueryAll("""SELECT "users"."id" FROM "users" WHERE "users"."status" = ?""", [| I 0L |], fun r -> r.Int64 0)
        let room = find tx.Conn roomId
        insertMemberships tx roomId (RoomType.defaultInvolvement room.RoomType) userIds

    // Creating

    /// `Rooms::<Type>.create!(name:, creator:)`. An open room grants itself to every active
    /// user after commit (`Rooms::Open#grant_access_to_all_users`).
    let create (tx: Tx) (roomType: RoomType) (name: string option) (creatorId: int64) : Room =
        let now = tx.Now()
        let id =
            tx.Conn.QueryRow(
                """INSERT INTO "rooms" ("created_at", "creator_id", "name", "type", "updated_at") VALUES (?, ?, ?, ?, ?) RETURNING "id" """,
                [| T now; I creatorId; Sql.optS name; RoomType.toSql roomType; T now |],
                fun r -> r.Int64 0
            )
        if roomType = Open then tx.AfterCommit(fun tx -> grantToActiveUsers tx id)
        find tx.Conn id

    /// `Room.create_for(attributes, users:)`
    let createFor (tx: Tx) (roomType: RoomType) (name: string option) (creatorId: int64) (userIds: int64 list) : Room =
        let room = create tx roomType name creatorId
        grantTo tx room userIds
        room

    /// `room.user_ids`
    let userIds (conn: Conn) (room: Room) : int64 list =
        conn.QueryAll(
            """SELECT "users"."id" FROM "users" INNER JOIN "memberships" ON "users"."id" = "memberships"."user_id" WHERE "memberships"."room_id" = ?""",
            [| I room.Id |],
            fun r -> r.Int64 0
        )

    /// `Rooms::Direct.find_for`: walks the direct rooms (joined to their users, as Rails
    /// does, so rooms repeat) and returns the first with the same set of members.
    let findDirectFor (conn: Conn) (userIds': int64 list) : Room option =
        let wanted = Set.ofList userIds'
        let candidates =
            conn.QueryAll(
                "SELECT "
                + Columns.Room
                + " FROM \"rooms\" INNER JOIN \"memberships\" ON \"memberships\".\"room_id\" = \"rooms\".\"id\" INNER JOIN \"users\" ON \"users\".\"id\" = \"memberships\".\"user_id\" WHERE \"rooms\".\"type\" = ?",
                [| RoomType.toSql Direct |],
                fromRow
            )
        candidates |> List.tryFind (fun room -> Set.ofList (userIds conn room) = wanted)

    /// `Rooms::Direct.find_or_create_for(users)`: the direct room whose members are exactly
    /// `user_ids`, created (by `creator_id`, i.e. `Current.user`) if there isn't one.
    let findOrCreateDirectFor (tx: Tx) (userIds: int64 list) (creatorId: int64) : Room =
        match findDirectFor tx.Conn userIds with
        | Some room -> room
        | None -> createFor tx Direct None creatorId userIds

    // Updating

    /// `room.update!(name:, type:)`. A direct room can't change type
    /// (`direct_rooms_keep_their_type`). Becoming open grants every active user after commit.
    let update (tx: Tx) (room: Room) (name: string option option) (roomType: RoomType option) : Room =
        let name = name |> Option.filter (fun n -> n <> room.Name)
        let roomType = roomType |> Option.filter (fun t -> t <> room.RoomType)
        if roomType.IsSome && room.RoomType = Direct then
            Err.validate (Errors.empty |> Errors.add "type" "can't be changed for a direct room")
        if name.IsNone && roomType.IsNone then
            room
        else
            let now = tx.Now()
            let updated =
                { room with
                    Name = (match name with Some n -> n | None -> room.Name)
                    RoomType = defaultArg roomType room.RoomType
                    UpdatedAt = now }
            tx.Conn.Execute(
                """UPDATE "rooms" SET "name" = ?, "type" = ?, "updated_at" = ? WHERE "rooms"."id" = ?""",
                [| Sql.optS updated.Name; RoomType.toSql updated.RoomType; T now; I room.Id |]
            )
            |> ignore
            if roomType = Some Open then
                let id = room.Id
                tx.AfterCommit(fun tx -> grantToActiveUsers tx id)
            updated

    /// `touch`: `belongs_to :room, touch: true` on messages.
    let touch (tx: Tx) (roomId: int64) : unit =
        tx.Conn.Execute("""UPDATE "rooms" SET "updated_at" = ? WHERE "rooms"."id" = ?""", [| T(tx.Now()); I roomId |]) |> ignore

    /// `room.destroy`: memberships are deleted without callbacks, messages are destroyed.
    let destroy (tx: Tx) (room: Room) : unit =
        tx.Conn.Execute("""DELETE FROM "memberships" WHERE "memberships"."room_id" = ?""", [| I room.Id |]) |> ignore
        for message in Message.forRoom tx.Conn room.Id do
            Message.destroy tx message
        tx.Conn.Execute("""DELETE FROM "rooms" WHERE "rooms"."id" = ?""", [| I room.Id |]) |> ignore

    /// `memberships.revoke_from(users)`: `destroy_by`, so each removed member is disconnected
    /// (with reconnect) after commit.
    let revokeFrom (tx: Tx) (room: Room) (userIds: int64 list) : unit =
        let sql =
            Selects.Membership
            + " WHERE \"memberships\".\"room_id\" = ? AND \"memberships\".\"user_id\" IN ("
            + Sql.placeholders (List.length userIds)
            + ")"
        let values = Array.ofList (I room.Id :: (userIds |> List.map I))
        for membership in tx.Conn.QueryAll(sql, values, Membership.fromRow) do
            Membership.destroy tx membership

    /// `memberships.revise(granted:, revoked:)`
    let revise (tx: Tx) (room: Room) (granted: int64 list) (revoked: int64 list) : unit =
        if not (List.isEmpty granted) then grantTo tx room granted
        if not (List.isEmpty revoked) then revokeFrom tx room revoked

    /// `room.users`
    let users (conn: Conn) (room: Room) : User list = conn.QueryAll(usersJoin, [| I room.Id |], User.fromRow)

    /// `room.users.active_bots`
    let activeBots (conn: Conn) (room: Room) : User list =
        conn.QueryAll(usersJoin + " AND \"users\".\"status\" = 0 AND \"users\".\"role\" = 2", [| I room.Id |], User.fromRow)

    let private unreadMemberships (tx: Tx) (roomId: int64) (message: Message) : unit =
        let now = tx.Now()
        tx.Conn.Execute(
            """UPDATE "memberships" SET "unread_at" = ?, "updated_at" = ? WHERE "memberships"."room_id" = ? AND "memberships"."involvement" != ? AND ("memberships"."connected_at" IS NULL OR "memberships"."connected_at" < ?) AND "memberships"."user_id" != ?""",
            [| T message.CreatedAt; T now; I roomId; S "invisible"; T(Membership.connectionCutoff now); I message.CreatorId |]
        )
        |> ignore

    /// `room.receive(message)`, from the message's `after_create_commit`: marks members
    /// unread, then enqueues the push.
    let receive (tx: Tx) (roomId: int64) (message: Message) : unit =
        unreadMemberships tx roomId message
        tx.EmitAfterCommit(PushMessage(roomId, message.Id))

    let isOpen (room: Room) : bool = room.RoomType = Open

    let isClosed (room: Room) : bool = room.RoomType = Closed

    let isDirect (room: Room) : bool = room.RoomType = Direct

    let defaultInvolvement (room: Room) : string = RoomType.defaultInvolvement room.RoomType

    let reload (conn: Conn) (room: Room) : Room = find conn room.Id

// ---------------------------------------------------------------------------------------------
// Message
// `reference/app/models/message.rb` and `message/*.rb` (Attachment, Mentionee, Pagination,
// Searchable; Broadcasts belong to the app).
// ---------------------------------------------------------------------------------------------

type Message =
    { Id: int64
      RoomId: int64
      CreatorId: int64
      ClientMessageId: string
      CreatedAt: Timestamp
      UpdatedAt: Timestamp }

/// Attributes for `room.messages.create!` / `create_with_attachment!`.
type NewMessage =
    { RoomId: int64
      CreatorId: int64
      /// Defaults to a random UUID (`before_create`; "Bots don't care").
      ClientMessageId: string option
      /// The stored Action Text body, if one was assigned.
      Body: string option
      /// An already-saved blob to attach as `attachment`.
      AttachmentBlobId: int64 option }

module NewMessage =
    let create (roomId: int64) (creatorId: int64) : NewMessage =
        { RoomId = roomId
          CreatorId = creatorId
          ClientMessageId = None
          Body = None
          AttachmentBlobId = None }

type ContentType =
    | Attachment
    | Sound
    | Text

module ContentType =
    let name (contentType: ContentType) : string =
        match contentType with
        | Attachment -> "attachment"
        | Sound -> "sound"
        | Text -> "text"

module Message =
    /// `Message::Pagination::PAGE_SIZE`
    [<Literal>]
    let PageSize = 40L

    [<Literal>]
    let private RecordType = "Message"

    [<Literal>]
    let private selectInRoom = Selects.Message + " WHERE \"messages\".\"room_id\" = ?"

    [<Literal>]
    let private selectReachable =
        Selects.Message
        + " INNER JOIN \"rooms\" ON \"messages\".\"room_id\" = \"rooms\".\"id\" INNER JOIN \"memberships\" ON \"rooms\".\"id\" = \"memberships\".\"room_id\""

    let fromRow (r: Row) : Message =
        { Id = r.Int64 0
          RoomId = r.Int64 1
          CreatorId = r.Int64 2
          ClientMessageId = r.Text 3
          CreatedAt = r.Timestamp 4
          UpdatedAt = r.Timestamp 5 }

    let findById (conn: Conn) (id: int64) : Message option =
        conn.QueryOne(Selects.Message + " WHERE \"messages\".\"id\" = ? LIMIT 1", [| I id |], fromRow)

    let find (conn: Conn) (id: int64) : Message = findById conn id |> Err.orNotFound "Message"

    /// `Message.last`
    let last (conn: Conn) : Message option =
        conn.QueryOne(Selects.Message + " ORDER BY \"messages\".\"id\" DESC LIMIT 1", [||], fromRow)

    let count (conn: Conn) : int64 = conn.Count("""SELECT COUNT(*) FROM "messages" """, [||])

    /// `user.messages`
    let byCreator (conn: Conn) (creatorId: int64) : Message list =
        conn.QueryAll(Selects.Message + " WHERE \"messages\".\"creator_id\" = ?", [| I creatorId |], fromRow)

    /// `room.messages`
    let forRoom (conn: Conn) (roomId: int64) : Message list = conn.QueryAll(selectInRoom, [| I roomId |], fromRow)

    /// `room.messages.find(id)`
    let findInRoom (conn: Conn) (roomId: int64) (id: int64) : Message =
        conn.QueryOne(selectInRoom + " AND \"messages\".\"id\" = ? LIMIT 1", [| I roomId; I id |], fromRow)
        |> Err.orNotFound "Message"

    /// `room.messages.count`
    let countInRoom (conn: Conn) (roomId: int64) : int64 =
        conn.Count("""SELECT COUNT(*) FROM "messages" WHERE "messages"."room_id" = ?""", [| I roomId |])

    /// `Current.user.reachable_messages.find(id)`
    let findReachable (conn: Conn) (userId: int64) (id: int64) : Message =
        conn.QueryOne(
            selectReachable + " WHERE \"memberships\".\"user_id\" = ? AND \"messages\".\"id\" = ? LIMIT 1",
            [| I userId; I id |],
            fromRow
        )
        |> Err.orNotFound "Message"

    // Message::Pagination

    /// `room.messages.last_page`: the newest 40, oldest first.
    let lastPage (conn: Conn) (roomId: int64) : Message list =
        conn.QueryAll(selectInRoom + " ORDER BY \"messages\".\"created_at\" DESC LIMIT 40", [| I roomId |], fromRow) |> List.rev

    /// `room.messages.first_page`
    let firstPage (conn: Conn) (roomId: int64) : Message list =
        conn.QueryAll(selectInRoom + " ORDER BY \"messages\".\"created_at\" ASC LIMIT 40", [| I roomId |], fromRow)

    /// `room.messages.page_before(message)`
    let pageBefore (conn: Conn) (roomId: int64) (message: Message) : Message list =
        conn.QueryAll(
            selectInRoom + " AND (created_at < ?) ORDER BY \"messages\".\"created_at\" DESC LIMIT 40",
            [| I roomId; T message.CreatedAt |],
            fromRow
        )
        |> List.rev

    /// `room.messages.page_after(message)`
    let pageAfter (conn: Conn) (roomId: int64) (message: Message) : Message list =
        conn.QueryAll(
            selectInRoom + " AND (created_at > ?) ORDER BY \"messages\".\"created_at\" ASC LIMIT 40",
            [| I roomId; T message.CreatedAt |],
            fromRow
        )

    /// `room.messages.page_around(message)`: up to 40 before, the message, up to 40 after.
    let pageAround (conn: Conn) (roomId: int64) (message: Message) : Message list =
        pageBefore conn roomId message @ [ message ] @ pageAfter conn roomId message

    /// `room.messages.page_created_since(time)`
    let pageCreatedSince (conn: Conn) (roomId: int64) (time: Timestamp) : Message list =
        conn.QueryAll(
            selectInRoom + " AND (created_at > ?) ORDER BY \"messages\".\"created_at\" ASC LIMIT 40",
            [| I roomId; T time |],
            fromRow
        )

    /// `room.messages.without(excluding).page_updated_since(time)`
    let pageUpdatedSince (conn: Conn) (roomId: int64) (time: Timestamp) (excluding: int64 list) : Message list =
        let without =
            if List.isEmpty excluding then
                ""
            else
                " AND \"messages\".\"id\" NOT IN (" + Sql.placeholders (List.length excluding) + ")"
        let sql = selectInRoom + without + " AND (updated_at > ?) ORDER BY \"messages\".\"created_at\" DESC LIMIT 40"
        let values = Array.ofList (I roomId :: (excluding |> List.map I) @ [ T time ])
        conn.QueryAll(sql, values, fromRow) |> List.rev

    /// `room.messages.before(message).exists?`
    let existsBefore (conn: Conn) (roomId: int64) (message: Message) : bool =
        conn.Exists(
            """SELECT 1 FROM "messages" WHERE "messages"."room_id" = ? AND (created_at < ?) LIMIT 1""",
            [| I roomId; T message.CreatedAt |]
        )

    /// `room.messages.after(message).exists?`
    let existsAfter (conn: Conn) (roomId: int64) (message: Message) : bool =
        conn.Exists(
            """SELECT 1 FROM "messages" WHERE "messages"."room_id" = ? AND (created_at > ?) LIMIT 1""",
            [| I roomId; T message.CreatedAt |]
        )

    /// `room.messages.paged?`: more than a page (`count > PAGE_SIZE`). Asks whether a row exists
    /// past the first page rather than counting the whole room, which grows without bound.
    let paged (conn: Conn) (roomId: int64) : bool =
        conn.Exists("""SELECT 1 FROM "messages" WHERE "messages"."room_id" = ? LIMIT 1 OFFSET 40""", [| I roomId |])

    // Message::Searchable

    /// Each word of a search as an FTS5 string, so every word must appear and none is read as query
    /// syntax. Rails passes the words straight to `MATCH`, where `NOT`, `AND`, `OR` or `NEAR` in the
    /// wrong place is a syntax error (a 500).
    /// NULs separate words too: SQLite would end the query string at one.
    let private matchTerms (query: string) : string =
        query.Split([| '\000' |])
        |> Array.collect (fun part -> part.Split(Array.empty<char>, StringSplitOptions.RemoveEmptyEntries))
        |> Array.map (fun word -> "\"" + word.Replace("\"", "\"\"") + "\"")
        |> String.concat " "

    /// `room.messages.search(query)`: FTS5 `MATCH`, in message order.
    let searchInRoom (conn: Conn) (roomId: int64) (query: string) : Message list =
        let query = matchTerms query
        if query = "" then
            []
        else
            conn.QueryAll(
                Selects.Message
                + " join message_search_index idx on messages.id = idx.rowid WHERE \"messages\".\"room_id\" = ? AND (idx.body match ?) ORDER BY \"messages\".\"created_at\" ASC",
                [| I roomId; S query |],
                fromRow
            )

    /// `Current.user.reachable_messages.search(query).last(100)`
    let searchReachable (conn: Conn) (userId: int64) (query: string) : Message list =
        let query = matchTerms query
        if query = "" then
            []
        else
            conn.QueryAll(
                selectReachable
                + " join message_search_index idx on messages.id = idx.rowid WHERE \"memberships\".\"user_id\" = ? AND (idx.body match ?) ORDER BY \"messages\".\"created_at\" DESC LIMIT 100",
                [| I userId; S query |],
                fromRow
            )
            |> List.rev

    // Attributes and associations

    let room (conn: Conn) (message: Message) : Room = Room.find conn message.RoomId

    let creator (conn: Conn) (message: Message) : User = User.find conn message.CreatorId

    let boosts (conn: Conn) (message: Message) : Boost list = Boost.forMessageOrdered conn message.Id

    let body (conn: Conn) (message: Message) : RichTextRecord option = RichTextRecord.findFor conn RecordType message.Id "body"

    /// `message.body.body` as stored HTML, if any.
    let bodyHtml (conn: Conn) (message: Message) : string option = body conn message |> Option.bind (fun b -> b.Body)

    /// The attachment and its blob, if attached.
    let attachment (conn: Conn) (message: Message) : (Attachment * Blob) option =
        match Attachment.findFor conn RecordType message.Id "attachment" with
        | Some attachment -> Some(attachment, Attachment.blob conn attachment)
        | None -> None

    /// `plain_text_body`: `body.to_plain_text.presence || attachment&.filename&.to_s || ""`
    let plainTextBody (conn: Conn) (richText: RichText) (message: Message) : string =
        let fromBody =
            match bodyHtml conn message with
            | Some html ->
                let text = richText.ToPlainText(conn, html)
                if not (String.IsNullOrWhiteSpace text) then Some text else None
            | None -> None
        match fromBody with
        | Some text -> text
        | None ->
            match attachment conn message with
            | Some(_, blob) -> blob.Filename
            | None -> ""

    /// `plain_text_body.match(/\A\/play (?<name>\w+)\z/)` then `Sound.find_by_name`.
    let soundIn (plainText: string) : Sound option =
        if not (plainText.StartsWith("/play ", StringComparison.Ordinal)) then
            None
        else
            let name = plainText.Substring 6
            // `\z` allows no trailing newline; `\w` is ASCII word characters.
            if name = "" || not (name |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '_')) then None else Sound.findByName name

    /// `sound`: a body of exactly `/play <name>` naming a built-in sound.
    let sound (conn: Conn) (richText: RichText) (message: Message) : Sound option = soundIn (plainTextBody conn richText message)

    /// `content_type`
    let contentType (conn: Conn) (richText: RichText) (message: Message) : ContentType =
        if (attachment conn message).IsSome then ContentType.Attachment
        elif (sound conn richText message).IsSome then ContentType.Sound
        else ContentType.Text

    /// `room.users.where(id: ids)`
    let mentioneesInRoom (conn: Conn) (roomId: int64) (userIds: int64 list) : User list =
        if List.isEmpty userIds then
            []
        else
            let sql =
                User.Select
                + " INNER JOIN \"memberships\" ON \"users\".\"id\" = \"memberships\".\"user_id\" WHERE \"memberships\".\"room_id\" = ? AND \"users\".\"id\" IN ("
                + Sql.placeholders (List.length userIds)
                + ")"
            conn.QueryAll(sql, Array.ofList (I roomId :: (userIds |> List.map I)), User.fromRow)

    /// `mentionees`: mentioned users who are members of the room.
    let mentionees (conn: Conn) (richText: RichText) (message: Message) : User list =
        let ids =
            match bodyHtml conn message with
            | Some html -> richText.MentionedUserIds(conn, html)
            | None -> []
        mentioneesInRoom conn message.RoomId ids

    // Creating, updating, destroying

    let private createInIndex (tx: Tx) (message: Message) : unit =
        let body = plainTextBody tx.Conn tx.RichText message
        tx.Conn.Execute("insert into message_search_index(rowid, body) values (?, ?)", [| I message.Id; S body |]) |> ignore

    let private updateInIndex (tx: Tx) (message: Message) : unit =
        let body = plainTextBody tx.Conn tx.RichText message
        tx.Conn.Execute("update message_search_index set body = ? where rowid = ?", [| S body; I message.Id |]) |> ignore

    let private removeFromIndex (tx: Tx) (id: int64) : unit =
        tx.Conn.Execute("delete from message_search_index where rowid = ?", [| I id |]) |> ignore

    let private touchRow (tx: Tx) (id: int64) : Timestamp =
        let now = tx.Now()
        tx.Conn.Execute("""UPDATE "messages" SET "updated_at" = ? WHERE "messages"."id" = ?""", [| T now; I id |]) |> ignore
        now

    /// `room.messages.create!` (or `create_with_attachment!` given a blob). Inside the
    /// transaction: the message, its body (which touches the message), its attachment, and
    /// the room touch. After commit: the search index, then `room.receive` (unread
    /// memberships and the push job).
    let create (tx: Tx) (attributes: NewMessage) : Message =
        let now = tx.Now()
        let clientMessageId = attributes.ClientMessageId |> Option.defaultWith Sql.uuid
        let id =
            tx.Conn.QueryRow(
                """INSERT INTO "messages" ("client_message_id", "created_at", "creator_id", "room_id", "updated_at") VALUES (?, ?, ?, ?, ?) RETURNING "id" """,
                [| S clientMessageId; T now; I attributes.CreatorId; I attributes.RoomId; T now |],
                fun r -> r.Int64 0
            )
        let mutable message =
            { Id = id
              RoomId = attributes.RoomId
              CreatorId = attributes.CreatorId
              ClientMessageId = clientMessageId
              CreatedAt = now
              UpdatedAt = now }

        let mutable touched = false
        match attributes.Body with
        | Some body ->
            RichTextRecord.create tx RecordType id "body" body |> ignore
            touched <- true
        | None -> ()
        match attributes.AttachmentBlobId with
        | Some blobId ->
            Attachment.create tx RecordType id "attachment" blobId |> ignore
            touched <- true
        | None -> ()
        if touched then message <- { message with UpdatedAt = touchRow tx id }
        Room.touch tx message.RoomId

        let committed = message
        tx.AfterCommit(fun tx ->
            createInIndex tx committed
            Room.receive tx committed.RoomId committed)
        message

    /// `touch` (from a boost, or the body): the message, then its room, then a reindex
    /// after commit.
    let touch (tx: Tx) (message: Message) : Message =
        let touched = { message with UpdatedAt = touchRow tx message.Id }
        Room.touch tx message.RoomId
        let id = message.Id
        tx.AfterCommit(fun tx -> updateInIndex tx (find tx.Conn id))
        touched

    /// `message.update!(body:)`: the body changes, which touches the message and its room;
    /// the search index follows after commit.
    let updateBody (tx: Tx) (message: Message) (body: string) : Message =
        match RichTextRecord.findFor tx.Conn RecordType message.Id "body" with
        | Some record when record.Body = Some body -> message
        | Some record ->
            RichTextRecord.updateBody tx record body |> ignore
            touch tx message
        | None ->
            RichTextRecord.create tx RecordType message.Id "body" body |> ignore
            touch tx message

    /// `update!(attachment: blob or nil)`: `has_one_attached` replaces the attachment. The old
    /// one is destroyed (its blob purged after commit, `dependent: :purge_later`) and each
    /// attachment change touches the message (`belongs_to :record, touch: true`), and so its room.
    let replaceAttachment (tx: Tx) (message: Message) (blobId: int64 option) : Message =
        let mutable message = message
        match Attachment.findFor tx.Conn RecordType message.Id "attachment" with
        | Some attachment ->
            Attachment.delete tx attachment
            tx.EmitAfterCommit(PurgeBlob attachment.BlobId)
            message <- touch tx message
        | None -> ()
        match blobId with
        | Some blobId ->
            Attachment.create tx RecordType message.Id "attachment" blobId |> ignore
            message <- touch tx message
        | None -> ()
        message

    /// `destroy`: its attachment (blob purged later), boosts and body, then the message, and
    /// the room is touched. The search index entry goes after commit.
    let destroy (tx: Tx) (message: Message) : unit =
        match Attachment.findFor tx.Conn RecordType message.Id "attachment" with
        | Some attachment ->
            Attachment.delete tx attachment
            tx.EmitAfterCommit(PurgeBlob attachment.BlobId)
        | None -> ()
        for boost in Boost.forMessage tx.Conn message.Id do
            Boost.deleteRow tx boost
        match RichTextRecord.findFor tx.Conn RecordType message.Id "body" with
        | Some body -> RichTextRecord.delete tx body
        | None -> ()
        tx.Conn.Execute("""DELETE FROM "messages" WHERE "messages"."id" = ?""", [| I message.Id |]) |> ignore
        Room.touch tx message.RoomId
        let id = message.Id
        tx.AfterCommit(fun tx -> removeFromIndex tx id)

    let reload (conn: Conn) (message: Message) : Message = find conn message.Id

// ---------------------------------------------------------------------------------------------
// Boost
// `reference/app/models/boost.rb`
// ---------------------------------------------------------------------------------------------

type Boost =
    { Id: int64
      MessageId: int64
      BoosterId: int64
      Content: string
      CreatedAt: Timestamp
      UpdatedAt: Timestamp }

module Boost =
    let private fromRow (r: Row) : Boost =
        { Id = r.Int64 0
          MessageId = r.Int64 1
          BoosterId = r.Int64 2
          Content = r.Text 3
          CreatedAt = r.Timestamp 4
          UpdatedAt = r.Timestamp 5 }

    let find (conn: Conn) (id: int64) : Boost =
        conn.QueryOne(Selects.Boost + " WHERE \"boosts\".\"id\" = ? LIMIT 1", [| I id |], fromRow) |> Err.orNotFound "Boost"

    let forMessage (conn: Conn) (messageId: int64) : Boost list =
        conn.QueryAll(Selects.Boost + " WHERE \"boosts\".\"message_id\" = ?", [| I messageId |], fromRow)

    /// `message.boosts.ordered`
    let forMessageOrdered (conn: Conn) (messageId: int64) : Boost list =
        conn.QueryAll(Selects.Boost + " WHERE \"boosts\".\"message_id\" = ? ORDER BY \"boosts\".\"created_at\" ASC", [| I messageId |], fromRow)

    /// `message.boosts.find_by!(id:, booster:)`
    let findByMessageAndBooster (conn: Conn) (messageId: int64) (id: int64) (boosterId: int64) : Boost =
        conn.QueryOne(
            Selects.Boost + " WHERE \"boosts\".\"message_id\" = ? AND \"boosts\".\"id\" = ? AND \"boosts\".\"booster_id\" = ? LIMIT 1",
            [| I messageId; I id; I boosterId |],
            fromRow
        )
        |> Err.orNotFound "Boost"

    /// `message.boosts.create!(content:, booster:)`: touches the message (and so the room).
    let create (tx: Tx) (messageId: int64) (boosterId: int64) (content: string) : Boost =
        let now = tx.Now()
        let id =
            tx.Conn.QueryRow(
                """INSERT INTO "boosts" ("booster_id", "content", "created_at", "message_id", "updated_at") VALUES (?, ?, ?, ?, ?) RETURNING "id" """,
                [| I boosterId; S content; T now; I messageId; T now |],
                fun r -> r.Int64 0
            )
        Message.touch tx (Message.find tx.Conn messageId) |> ignore
        { Id = id
          MessageId = messageId
          BoosterId = boosterId
          Content = content
          CreatedAt = now
          UpdatedAt = now }

    /// The delete alone, for a message being destroyed (its touch is moot).
    let deleteRow (tx: Tx) (boost: Boost) : unit =
        tx.Conn.Execute("""DELETE FROM "boosts" WHERE "boosts"."id" = ?""", [| I boost.Id |]) |> ignore

    /// `destroy!`: touches the message (and so the room).
    let destroy (tx: Tx) (boost: Boost) : unit =
        deleteRow tx boost
        Message.touch tx (Message.find tx.Conn boost.MessageId) |> ignore

// ---------------------------------------------------------------------------------------------
// Webhook
// `reference/app/models/webhook.rb`: the row and the payload. Delivery (HTTP, replies)
// lives in the app.
// ---------------------------------------------------------------------------------------------

type Webhook =
    { Id: int64
      UserId: int64
      Url: string option
      CreatedAt: Timestamp
      UpdatedAt: Timestamp }

module Webhook =
    /// `Webhook::ENDPOINT_TIMEOUT`
    [<Literal>]
    let EndpointTimeoutSeconds = 7

    let private fromRow (r: Row) : Webhook =
        { Id = r.Int64 0
          UserId = r.Int64 1
          Url = r.OptText 2
          CreatedAt = r.Timestamp 3
          UpdatedAt = r.Timestamp 4 }

    let findByUser (conn: Conn) (userId: int64) : Webhook option =
        conn.QueryOne(Selects.Webhook + " WHERE \"webhooks\".\"user_id\" = ? LIMIT 1", [| I userId |], fromRow)

    /// `create_webhook!(url:)`
    let create (tx: Tx) (userId: int64) (url: string option) : Webhook =
        let now = tx.Now()
        let id =
            tx.Conn.QueryRow(
                """INSERT INTO "webhooks" ("created_at", "updated_at", "url", "user_id") VALUES (?, ?, ?, ?) RETURNING "id" """,
                [| T now; T now; Sql.optS url; I userId |],
                fun r -> r.Int64 0
            )
        { Id = id
          UserId = userId
          Url = url
          CreatedAt = now
          UpdatedAt = now }

    /// `webhook.update!(url:)`
    let updateUrl (tx: Tx) (webhook: Webhook) (url: string) : Webhook =
        if webhook.Url = Some url then
            webhook
        else
            let now = tx.Now()
            tx.Conn.Execute(
                """UPDATE "webhooks" SET "updated_at" = ?, "url" = ? WHERE "webhooks"."id" = ?""",
                [| T now; S url; I webhook.Id |]
            )
            |> ignore
            { webhook with Url = Some url; UpdatedAt = now }

    let destroy (tx: Tx) (webhook: Webhook) : unit =
        tx.Conn.Execute("""DELETE FROM "webhooks" WHERE "webhooks"."id" = ?""", [| I webhook.Id |]) |> ignore

    /// Removes `@Recipient` mentions and leading/trailing (Unicode) whitespace.
    let private withoutRecipientMentions (body: string) (recipient: User) : string =
        body.Replace(User.attachablePlainTextRepresentation recipient, "").Trim()

    /// The JSON body `deliver` posts. `roomBotMessagesPath` and `messagePath` come from
    /// the route helpers (`room_bot_messages_path(room, bot_key)`, `room_at_message_path(room, message)`).
    let payload
        (conn: Conn)
        (richText: RichText)
        (webhook: Webhook)
        (message: Message)
        (roomBotMessagesPath: string)
        (messagePath: string)
        : string =
        let creator = Message.creator conn message
        let room = Room.find conn message.RoomId
        let recipient = User.find conn webhook.UserId
        let html = Message.bodyHtml conn message
        let plain = withoutRecipientMentions (Message.plainTextBody conn richText message) recipient

        let optional (value: string option) =
            match value with
            | Some s -> Value.String s
            | None -> Value.Null

        // Hash order as written in `Webhook#payload`.
        let body =
            Value.Object
                [ "user", Value.Object [ "id", Value.Int creator.Id; "name", Value.String creator.Name ]
                  "room", Value.Object [ "id", Value.Int room.Id; "name", optional room.Name; "path", Value.String roomBotMessagesPath ]
                  "message",
                  Value.Object
                      [ "id", Value.Int message.Id
                        "body", Value.Object [ "html", optional html; "plain", Value.String plain ]
                        "path", Value.String messagePath ] ]
        Json.encode body
