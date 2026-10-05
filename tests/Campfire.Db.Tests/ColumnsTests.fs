// Port of rust/crates/db/src/tests/columns_test.rs
// The hot models read their columns by position (`Columns`), which must give what reading them by
// name gives, whatever order a table has its columns in.
module Campfire.Db.Tests.ColumnsTests

open System
open System.Diagnostics
open System.IO
open System.Runtime.CompilerServices
open Xunit
open Campfire.RailsCompat.Clock
open Campfire.Db
open Campfire.Db.Tests.Support

/// Where a database's tables got their column order.
type Layout =
    /// `schema.sql`'s alphabetical order, as `db:prepare` loads `schema.rb` into a new database.
    | Alphabetical
    /// The order `20231215043540_create_initial_schema.rb` created them in, which databases Rails
    /// migrated keep.
    | Migrated

let private layouts = [ Alphabetical; Migrated ]

let private migratedColumns : (string * string list) list =
    [ "messages", [ "id"; "room_id"; "creator_id"; "created_at"; "updated_at"; "client_message_id" ]
      "rooms", [ "id"; "name"; "created_at"; "updated_at"; "type"; "creator_id" ]
      "memberships", [ "id"; "room_id"; "user_id"; "created_at"; "updated_at"; "unread_at"; "involvement"; "connections"; "connected_at" ]
      // `bio`, `bot_token` and `status` were added after the initial schema, and `active` removed.
      "users", [ "id"; "name"; "created_at"; "updated_at"; "role"; "email_address"; "password_digest"; "bio"; "bot_token"; "status" ]
      "sessions", [ "id"; "user_id"; "token"; "ip_address"; "user_agent"; "last_active_at"; "created_at"; "updated_at" ]
      "accounts", [ "id"; "name"; "join_code"; "created_at"; "updated_at"; "custom_styles"; "settings"; "singleton_guard" ]
      "boosts", [ "id"; "message_id"; "booster_id"; "content"; "created_at"; "updated_at" ]
      "action_text_rich_texts", [ "id"; "name"; "body"; "record_type"; "record_id"; "created_at"; "updated_at" ] ]

/// Recreates the empty `table` with its columns in `order`, each keeping its type, NOT NULL and
/// default.
let private reorderColumns (conn: Conn) (table: string) (order: string list) : unit =
    let definitions =
        conn.QueryAll(
            $"PRAGMA table_info(\"{table}\")",
            [||],
            fun r ->
                let name = r.Text 1
                let mutable definition = $"\"{name}\" {r.Text 2}"
                if r.Int64 5 <> 0L then definition <- definition + " PRIMARY KEY AUTOINCREMENT"
                if r.Int64 3 <> 0L then definition <- definition + " NOT NULL"
                match r.OptText 4 with
                | Some dflt -> definition <- definition + $" DEFAULT {dflt}"
                | None -> ()
                name, definition
        )
        |> Map.ofList
    Assert.True((definitions.Count = List.length order), $"{table}: {order} names every column once")
    let columns = order |> List.map (fun c -> definitions[c])
    let columnList = String.concat ", " columns
    conn.ExecuteBatch $"DROP TABLE \"{table}\"; CREATE TABLE \"{table}\" ({columnList})"
    let names = conn.QueryAll($"SELECT \"name\" FROM pragma_table_info('{table}')", [||], fun r -> r.Text 0)
    Assert.Equal<string list>(order, names)

let private database (layout: Layout) : Conn =
    let conn = Conn.OpenInMemory()
    Schema.prepare conn "test" (SystemClock()) |> ignore
    match layout with
    | Migrated ->
        for table, order in migratedColumns do
            reorderColumns conn table order
    | Alphabetical -> ()
    conn

let private databaseWithFixtures (layout: Layout) : Conn =
    let conn = database layout
    conn.ExecuteBatch "BEGIN"
    Fixtures.load conn (Fixtures.referenceDir ()) { Now = at 0L; BcryptCost = 4 } |> ignore
    conn.ExecuteBatch "COMMIT"
    conn

let private insertUser (conn: Conn) (id: int64) : unit =
    conn.Execute("""INSERT INTO "users" ("id", "name", "created_at", "updated_at") VALUES (?, 'User', ?, ?)""", [| I id; T(at 0L); T(at 0L) |])
    |> ignore

let private insertRoom (conn: Conn) (room: Room) : unit =
    conn.Execute(
        """INSERT INTO "rooms" ("id", "name", "type", "creator_id", "created_at", "updated_at") VALUES (?, ?, ?, ?, ?, ?)""",
        [| I room.Id; Sql.optS room.Name; RoomType.toSql room.RoomType; I room.CreatorId; T room.CreatedAt; T room.UpdatedAt |]
    )
    |> ignore

let private insertMessage (conn: Conn) (message: Message) : unit =
    conn.Execute(
        """INSERT INTO "messages" ("id", "room_id", "creator_id", "client_message_id", "created_at", "updated_at") VALUES (?, ?, ?, ?, ?, ?)""",
        [| I message.Id; I message.RoomId; I message.CreatorId; S message.ClientMessageId; T message.CreatedAt; T message.UpdatedAt |]
    )
    |> ignore

let private insertMembership (conn: Conn) (membership: Membership) : unit =
    conn.Execute(
        """INSERT INTO "memberships" ("id", "room_id", "user_id", "involvement", "unread_at", "connected_at", "connections", "created_at", "updated_at") VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)""",
        [| I membership.Id
           I membership.RoomId
           I membership.UserId
           Involvement.toSql membership.Involvement
           Sql.optT membership.UnreadAt
           Sql.optT membership.ConnectedAt
           I membership.Connections
           T membership.CreatedAt
           T membership.UpdatedAt |]
    )
    |> ignore

/// A room with a distinct value in every column.
let private distinctRoom () : Room =
    { Id = 2001L
      Name = Some "Room 2002"
      RoomType = Closed
      CreatorId = 2003L
      CreatedAt = at 2004L
      UpdatedAt = at 2005L }

/// A room with every nullable column null.
let private unnamedRoom () : Room =
    { Id = 2011L
      Name = None
      RoomType = Direct
      CreatorId = 2013L
      CreatedAt = at 2014L
      UpdatedAt = at 2015L }

let private userIds (conn: Conn) : int64 list =
    conn.QueryAll("""SELECT "id" FROM "users" ORDER BY "id" """, [||], fun r -> r.Int64 0)

// Messages

/// How `Message.fromRow` read a row before it read by position.
let private messageByName (r: Row) : Message =
    { Id = r.Int64(r.Ordinal "id")
      RoomId = r.Int64(r.Ordinal "room_id")
      CreatorId = r.Int64(r.Ordinal "creator_id")
      ClientMessageId = r.Text(r.Ordinal "client_message_id")
      CreatedAt = r.Timestamp(r.Ordinal "created_at")
      UpdatedAt = r.Timestamp(r.Ordinal "updated_at") }

[<Fact>]
let ``a message reads each column into its own field`` () =
    for layout in layouts do
        use conn = database layout
        let room = distinctRoom ()
        insertUser conn room.CreatorId
        insertRoom conn room
        let message =
            { Id = 1001L
              RoomId = room.Id
              CreatorId = room.CreatorId
              ClientMessageId = "client 1004"
              CreatedAt = at 1005L
              UpdatedAt = at 1006L }
        insertMessage conn message

        Assert.True((Message.find conn message.Id = message), $"{layout}")
        Assert.True((Message.lastPage conn room.Id = [ message ]), $"{layout}")

[<Fact>]
let ``messages read by position as by name`` () =
    for layout in layouts do
        use conn = databaseWithFixtures layout
        let messages = conn.QueryAll("""SELECT * FROM "messages" ORDER BY "id" """, [||], messageByName)
        Assert.True(List.length messages > 5)
        for message in messages do
            Assert.True((Message.find conn message.Id = message), $"{layout}")
            let inRoom = Message.forRoom conn message.RoomId |> List.sortBy (fun m -> m.Id)
            Assert.True((inRoom = (messages |> List.filter (fun m -> m.RoomId = message.RoomId))), $"{layout}")

// Rooms

/// How `Room.fromRow` read a row before it read by position.
let private roomByName (r: Row) : Room =
    { Id = r.Int64(r.Ordinal "id")
      Name = r.OptText(r.Ordinal "name")
      RoomType = (RoomType.fromClassName (r.Text(r.Ordinal "type"))).Value
      CreatorId = r.Int64(r.Ordinal "creator_id")
      CreatedAt = r.Timestamp(r.Ordinal "created_at")
      UpdatedAt = r.Timestamp(r.Ordinal "updated_at") }

[<Fact>]
let ``a room reads each column into its own field`` () =
    for layout in layouts do
        use conn = database layout
        let room = distinctRoom ()
        let unnamed = unnamedRoom ()
        insertRoom conn room
        insertRoom conn unnamed

        Assert.True((Room.find conn room.Id = room), $"{layout}")
        Assert.True((Room.find conn unnamed.Id = unnamed), $"{layout}")
        Assert.True((Room.all conn = [ room; unnamed ]), $"{layout}")

[<Fact>]
let ``rooms read by position as by name`` () =
    for layout in layouts do
        use conn = databaseWithFixtures layout
        let rooms = conn.QueryAll("""SELECT * FROM "rooms" ORDER BY "id" """, [||], roomByName)
        Assert.True(List.length rooms > 3)
        for room in rooms do
            Assert.True((Room.find conn room.Id = room), $"{layout}")
        for userId in userIds conn do
            let forUser = Room.forUser conn userId |> List.sortBy (fun r -> r.Id)
            let byName =
                conn.QueryAll(
                    """SELECT "rooms".* FROM "rooms" INNER JOIN "memberships" ON "rooms"."id" = "memberships"."room_id" WHERE "memberships"."user_id" = ? ORDER BY "rooms"."id" """,
                    [| I userId |],
                    roomByName
                )
            Assert.True((forUser = byName), $"{layout}")

// Memberships, alone and with their rooms

/// How `Membership.fromRow` read a row before it read by position.
let private membershipByName (r: Row) : Membership =
    { Id = r.Int64(r.Ordinal "id")
      RoomId = r.Int64(r.Ordinal "room_id")
      UserId = r.Int64(r.Ordinal "user_id")
      Involvement = r.OptText(r.Ordinal "involvement") |> Option.bind Involvement.fromName
      UnreadAt = r.OptTimestamp(r.Ordinal "unread_at")
      ConnectedAt = r.OptTimestamp(r.Ordinal "connected_at")
      Connections = r.Int64(r.Ordinal "connections")
      CreatedAt = r.Timestamp(r.Ordinal "created_at")
      UpdatedAt = r.Timestamp(r.Ordinal "updated_at") }

/// How `withOrderedRoom` selected a membership with its room before, the room's columns aliased so
/// that they could be read by name.
let private withRoomByNameSql =
    """SELECT "memberships".*, "rooms"."id" AS r_id, "rooms"."created_at" AS r_created_at, "rooms"."creator_id" AS r_creator_id, "rooms"."name" AS r_name, "rooms"."type" AS r_type, "rooms"."updated_at" AS r_updated_at FROM "memberships" INNER JOIN "rooms" ON "rooms"."id" = "memberships"."room_id" WHERE "memberships"."user_id" = ?"""

let private membershipWithRoomByName (r: Row) : Membership * Room =
    let room =
        { Id = r.Int64(r.Ordinal "r_id")
          Name = r.OptText(r.Ordinal "r_name")
          RoomType = (RoomType.fromClassName (r.Text(r.Ordinal "r_type"))).Value
          CreatorId = r.Int64(r.Ordinal "r_creator_id")
          CreatedAt = r.Timestamp(r.Ordinal "r_created_at")
          UpdatedAt = r.Timestamp(r.Ordinal "r_updated_at") }
    membershipByName r, room

let private byMembershipId (pairs: (Membership * Room) list) : (Membership * Room) list = pairs |> List.sortBy (fun (m, _) -> m.Id)

[<Fact>]
let ``a membership and its room read each column into their own fields`` () =
    for layout in layouts do
        use conn = database layout
        let room, unnamed = distinctRoom (), unnamedRoom ()
        let membership =
            { Id = 3001L
              RoomId = room.Id
              UserId = 3003L
              Involvement = Some Everything
              UnreadAt = Some(at 3004L)
              ConnectedAt = Some(at 3005L)
              Connections = 3006L
              CreatedAt = at 3007L
              UpdatedAt = at 3008L }
        let unset =
            { Id = 3011L
              RoomId = unnamed.Id
              UserId = membership.UserId
              Involvement = None
              UnreadAt = None
              ConnectedAt = None
              Connections = 3016L
              CreatedAt = at 3017L
              UpdatedAt = at 3018L }
        for m, r in [ membership, room; unset, unnamed ] do
            insertRoom conn r
            insertMembership conn m

        Assert.True((Membership.find conn membership.Id = membership), $"{layout}")
        Assert.True((Membership.find conn unset.Id = unset), $"{layout}")
        // Ordered by room name, nulls first.
        Assert.True((Membership.withOrderedRoom conn membership.UserId = [ unset, unnamed; membership, room ]), $"{layout}")
        // A null involvement isn't `!= 'invisible'`.
        Assert.True((Membership.visibleWithOrderedRoom conn membership.UserId = [ membership, room ]), $"{layout}")

[<Fact>]
let ``memberships read by position as by name`` () =
    for layout in layouts do
        use conn = databaseWithFixtures layout
        let memberships = conn.QueryAll("""SELECT * FROM "memberships" ORDER BY "id" """, [||], membershipByName)
        Assert.True(List.length memberships > 5)
        for membership in memberships do
            Assert.True((Membership.find conn membership.Id = membership), $"{layout}")
        for userId in userIds conn do
            let withRoom = conn.QueryAll(withRoomByNameSql, [| I userId |], membershipWithRoomByName)
            let visible =
                conn.QueryAll(withRoomByNameSql + """ AND "memberships"."involvement" != 'invisible'""", [| I userId |], membershipWithRoomByName)
            Assert.True((byMembershipId (Membership.withOrderedRoom conn userId) = byMembershipId withRoom), $"{layout}")
            Assert.True((byMembershipId (Membership.visibleWithOrderedRoom conn userId) = byMembershipId visible), $"{layout}")

// Users, sessions, accounts, boosts and rich texts

[<Fact>]
let ``users sessions accounts boosts and rich texts read each column into its own field`` () =
    for layout in layouts do
        use conn = database layout
        let user =
            { Id = 4001L
              Name = "User 4002"
              EmailAddress = Some "4003@example.com"
              PasswordDigest = Some "digest 4004"
              Role = Role.Bot
              Status = Status.Banned
              Bio = Some "bio 4005"
              BotToken = Some "token 4006"
              CreatedAt = at 4007L
              UpdatedAt = at 4008L }
        conn.Execute(
            """INSERT INTO "users" ("id", "name", "email_address", "password_digest", "role", "status", "bio", "bot_token", "created_at", "updated_at") VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)""",
            [| I user.Id
               S user.Name
               Sql.optS user.EmailAddress
               Sql.optS user.PasswordDigest
               Role.toSql user.Role
               Status.toSql user.Status
               Sql.optS user.Bio
               Sql.optS user.BotToken
               T user.CreatedAt
               T user.UpdatedAt |]
        )
        |> ignore
        let session =
            { Id = 5001L
              UserId = user.Id
              Token = "token 5003"
              IpAddress = Some "10.0.50.4"
              UserAgent = Some "agent 5005"
              LastActiveAt = at 5006L
              CreatedAt = at 5007L
              UpdatedAt = at 5008L }
        conn.Execute(
            """INSERT INTO "sessions" ("id", "user_id", "token", "ip_address", "user_agent", "last_active_at", "created_at", "updated_at") VALUES (?, ?, ?, ?, ?, ?, ?, ?)""",
            [| I session.Id
               I session.UserId
               S session.Token
               Sql.optS session.IpAddress
               Sql.optS session.UserAgent
               T session.LastActiveAt
               T session.CreatedAt
               T session.UpdatedAt |]
        )
        |> ignore
        let account =
            { Id = 6001L
              Name = "Account 6002"
              JoinCode = "code-6003"
              CustomStyles = Some "/* 6004 */"
              SettingsJson = Some """{"n":6005}"""
              SingletonGuard = 6006L
              CreatedAt = at 6007L
              UpdatedAt = at 6008L }
        conn.Execute(
            """INSERT INTO "accounts" ("id", "name", "join_code", "custom_styles", "settings", "singleton_guard", "created_at", "updated_at") VALUES (?, ?, ?, ?, ?, ?, ?, ?)""",
            [| I account.Id
               S account.Name
               S account.JoinCode
               Sql.optS account.CustomStyles
               Sql.optS account.SettingsJson
               I account.SingletonGuard
               T account.CreatedAt
               T account.UpdatedAt |]
        )
        |> ignore
        let room = { distinctRoom () with CreatorId = user.Id }
        insertRoom conn room
        let message =
            { Id = 7002L
              RoomId = room.Id
              CreatorId = user.Id
              ClientMessageId = "7002"
              CreatedAt = at 0L
              UpdatedAt = at 0L }
        insertMessage conn message
        let boost =
            { Id = 7001L
              MessageId = message.Id
              BoosterId = user.Id
              Content = "\U0001F44D 7004"
              CreatedAt = at 7005L
              UpdatedAt = at 7006L }
        conn.Execute(
            """INSERT INTO "boosts" ("id", "message_id", "booster_id", "content", "created_at", "updated_at") VALUES (?, ?, ?, ?, ?, ?)""",
            [| I boost.Id; I boost.MessageId; I boost.BoosterId; S boost.Content; T boost.CreatedAt; T boost.UpdatedAt |]
        )
        |> ignore
        let richText =
            { Id = 8001L
              Name = "body"
              Body = Some "<p>8003</p>"
              RecordType = "Message"
              RecordId = 8005L
              CreatedAt = at 8006L
              UpdatedAt = at 8007L }
        conn.Execute(
            """INSERT INTO "action_text_rich_texts" ("id", "name", "body", "record_type", "record_id", "created_at", "updated_at") VALUES (?, ?, ?, ?, ?, ?, ?)""",
            [| I richText.Id; S richText.Name; Sql.optS richText.Body; S richText.RecordType; I richText.RecordId; T richText.CreatedAt; T richText.UpdatedAt |]
        )
        |> ignore

        Assert.True((User.find conn user.Id = user), $"{layout}")
        Assert.True((Session.findByToken conn session.Token = Some session), $"{layout}")
        Assert.True((Account.first conn = Some account), $"{layout}")
        Assert.True((Boost.forMessageOrdered conn boost.MessageId = [ boost ]), $"{layout}")
        Assert.True((RichTextRecord.findFor conn "Message" richText.RecordId "body" = Some richText), $"{layout}")

[<Fact>]
let ``every column list names the columns its model reads`` () =
    let names (columns: string) = Columns.names columns
    Assert.Equal<string list>([ "id"; "room_id"; "creator_id"; "client_message_id"; "created_at"; "updated_at" ], names Columns.Message)
    Assert.Equal<string list>([ "id"; "name"; "type"; "creator_id"; "created_at"; "updated_at" ], names Columns.Room)
    Assert.Equal(10, List.length (names Columns.User))
    Assert.Equal(9, List.length (names Columns.Membership))
    for layout in layouts do
        use conn = database layout
        for columns, table in [ Columns.User, "users"; Columns.Message, "messages"; Columns.Room, "rooms"; Columns.Membership, "memberships"; Columns.Account, "accounts"; Columns.Boost, "boosts"; Columns.RichTextRecord, "action_text_rich_texts"; Columns.Session, "sessions"; Columns.Blob, "active_storage_blobs"; Columns.Attachment, "active_storage_attachments"; Columns.Ban, "bans"; Columns.PushSubscription, "push_subscriptions"; Columns.Search, "searches"; Columns.Webhook, "webhooks" ] do
            // The list selects what it names, in its order, from the table.
            let selected =
                conn.QueryAll($"SELECT {columns} FROM \"{table}\"", [||], fun _ -> ())
            Assert.Empty selected

// Timing

/// A room page's worth of rows and more: 200 messages in room 1, and user 1 in 12 rooms.
let private timingDatabase () : Conn =
    let conn = database Alphabetical
    insertUser conn 1L
    for id in 1L .. 12L do
        let createdAt, updatedAt = at id, at (id + 1L)
        insertRoom
            conn
            { Id = id
              Name = Some $"Room {id}"
              RoomType = Open
              CreatorId = 1L
              CreatedAt = createdAt
              UpdatedAt = updatedAt }
        let unreadAt = if id % 2L = 0L then Some createdAt else None
        insertMembership
            conn
            { Id = id
              RoomId = id
              UserId = 1L
              Involvement = Some Mentions
              UnreadAt = unreadAt
              ConnectedAt = None
              Connections = 0L
              CreatedAt = createdAt
              UpdatedAt = updatedAt }
    for id in 1L .. 200L do
        insertMessage
            conn
            { Id = id
              RoomId = 1L
              CreatorId = 1L
              ClientMessageId = $"5f0c2b4e-1d7a-4c39-9a57-{id:D12}"
              CreatedAt = at id
              UpdatedAt = at (id + 1L) }
    conn

/// The median over 7 runs of the mean time per call.
let private timePerCall (iterations: int) (call: unit -> unit) : TimeSpan =
    let runs =
        [ for _ in 1..7 ->
              let sw = Stopwatch.StartNew()
              for _ in 1..iterations do
                  call ()
              sw.Elapsed / float iterations ]
        |> List.sort
    runs[runs.Length / 2]

/// `CAMPFIRE_DB_TIMING=1 CAMPFIRE_DB_TIMING_OUT=/tmp/timing.txt dotnet test --project tests/Campfire.Db.Tests -c Release --filter-method '*row reading timing'`
[<Fact>]
let ``row reading timing`` () =
    if Environment.GetEnvironmentVariable "CAMPFIRE_DB_TIMING" <> "1" then
        Assert.Skip "a timing, run by hand in a release build (CAMPFIRE_DB_TIMING=1)"
    use conn = timingDatabase ()
    let before = Message.find conn 161L
    let timings =
        [ "Message.lastPage (40 rows)", timePerCall 20_000 (fun () -> Message.lastPage conn 1L |> ignore)
          "Message.pageBefore (40 rows)", timePerCall 20_000 (fun () -> Message.pageBefore conn 1L before |> ignore)
          "Membership.visibleWithOrderedRoom (12 rows)", timePerCall 20_000 (fun () -> Membership.visibleWithOrderedRoom conn 1L |> ignore)
          "Message.find (1 row)", timePerCall 200_000 (fun () -> Message.find conn 100L |> ignore)
          "Room.find (1 row)", timePerCall 200_000 (fun () -> Room.find conn 5L |> ignore) ]
    let report = timings |> List.map (fun (name, perCall) -> $"{name}: {perCall.TotalMicroseconds:F2} us")
    for line in report do
        TestContext.Current.SendDiagnosticMessage line
    // The runner shows diagnostics only on request; a file is easier to read.
    match Environment.GetEnvironmentVariable "CAMPFIRE_DB_TIMING_OUT" with
    | null -> ()
    | path -> File.WriteAllLines(path, report)
