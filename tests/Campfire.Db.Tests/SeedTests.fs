/// The data layer against databases the Rails app wrote: the parity seeds (`parity/bin/seed build`,
/// which loads them through the reference app). Each test copies the seed it needs into a temporary
/// directory (the seeds are in WAL mode, so the `-wal` and `-shm` files come along) and never writes to
/// `parity/.seed`. Without the seeds they skip with a message; `CAMPFIRE_REQUIRE_SEED=1` fails instead.
///
/// The Rust crate has no tests of this kind (its model tests run on the reference fixtures); these check
/// what the fixtures can't: that rows Rails wrote read back through every model, in the formats Rails
/// writes them, and that writes land beside them.
module Campfire.Db.Tests.SeedTests

open System
open System.IO
open System.Text.Json
open Xunit
open Campfire.Db
open Campfire.Db.Tests.Support
open Campfire.RailsCompat.Clock

/// Copies seed `name` into a temporary directory and runs `f` with the copy's database path and that
/// directory (so that `f` can find the seed's other files). Skips when the seed isn't built.
let private withSeed (name: string) (f: string -> string -> unit) : unit =
    match Campfire.Tests.Repo.seed name with
    | None -> Assert.Skip $"parity seed {name} is not built (parity/bin/seed build)"
    | Some seed ->
        use dir = new TempDir()
        let source = Path.Combine(seed, "db")
        for file in [ "production.sqlite3"; "production.sqlite3-wal"; "production.sqlite3-shm" ] do
            let from = Path.Combine(source, file)
            if File.Exists from then File.Copy(from, dir.File file)
        f (dir.File "production.sqlite3") seed

let private label (seed: string) (key: string) : int64 =
    use document = JsonDocument.Parse(File.ReadAllText(Path.Combine(seed, "labels.json")))
    document.RootElement.GetProperty(key).GetInt64()

/// Opens the copy as the app does, with a frozen clock, and hands its database to `f`.
let private openSeed (path: string) (clock: TestClock) (sink: RecordingSink) : Database =
    let config = { Config.create path with Readers = 2 }
    Database.Open(config, testEnv sink clock)

[<Fact>]
let ``a database Rails wrote opens as up to date and gains only the paging index`` () =
    withSeed "default" (fun path _ ->
        let before =
            use conn = Conn.Open path
            conn.QueryAll("SELECT name FROM sqlite_master WHERE type = 'index'", [||], fun r -> r.Text 0) |> Set.ofList
        Assert.DoesNotContain("index_messages_on_room_id_and_created_at", before)

        use db = openSeed path (TestClock()) (RecordingSink())
        let after =
            unwrap (db.ReadBlocking(fun conn -> conn.QueryAll("SELECT name FROM sqlite_master WHERE type = 'index'", [||], fun r -> r.Text 0)))
            |> Set.ofList
        Assert.Equal<string list>([ "index_messages_on_room_id_and_created_at" ], Set.difference after before |> Set.toList)
        Assert.Equal(Schema.UpToDate, (use conn = Conn.Open path in Schema.prepare conn "production" (SystemClock()))))

/// The reference's `db:prepare` and `schema.sql` agree (what `reference-tools/db/differential.sh` checks for
/// the Rust crate), here against the seed's own schema.
[<Fact>]
let ``the schema Rails created is the schema we load`` () =
    withSeed "default" (fun path _ ->
        let schema (conn: Conn) =
            conn.QueryAll(
                "SELECT sql || ';' FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' AND name NOT LIKE 'message_search_index_%' ORDER BY rowid",
                [||],
                fun r -> r.Text 0
            )
        use seed = Conn.Open path
        use fresh = Conn.OpenInMemory()
        Schema.prepare fresh "production" (SystemClock()) |> ignore
        // Ours adds the paging index, last.
        let ours = schema fresh |> List.filter (fun sql -> not (sql.Contains "index_messages_on_room_id_and_created_at"))
        Assert.Equal<string list>(schema seed, ours))

[<Fact>]
let ``every model reads the rows Rails wrote`` () =
    withSeed "default" (fun path seed ->
        use db = openSeed path (TestClock()) (RecordingSink())
        let read f = unwrap (db.ReadBlocking f)

        Assert.Equal(10L, read User.count)
        Assert.Equal(11, List.length (read Room.all))
        Assert.Equal(169L, read Message.count)
        Assert.Equal(39L, read Membership.count)
        Assert.Equal(1L, read Account.count)
        Assert.Equal(3L, read Search.count)
        Assert.Equal(5L, read PushSubscription.count)

        let david = read (fun c -> User.find c (label seed "users.david"))
        Assert.Equal(("David", Role.Administrator, Status.Active), (david.Name, david.Role, david.Status))
        Assert.Equal(Some "david@37signals.com", david.EmailAddress)
        Assert.True(User.authenticate david "secret123456" || david.PasswordDigest.IsSome)

        let rita = read (fun c -> User.find c (label seed "users.rita"))
        Assert.Equal(Status.Deactivated, rita.Status)
        Assert.Contains("-deactivated-", rita.EmailAddress.Value)
        let mallory = read (fun c -> User.find c (label seed "users.mallory"))
        Assert.Equal(Status.Banned, mallory.Status)
        Assert.True(read (fun c -> Ban.banned c "203.0.113.9"))
        Assert.Equal(1, List.length (read (fun c -> Ban.forUser c mallory.Id)))

        // Bots, with and without a webhook.
        let bender = read (fun c -> User.find c (label seed "users.bender"))
        Assert.Equal(Role.Bot, bender.Role)
        Assert.Equal(Some "http://example.com/bender", read (fun c -> User.webhookUrl c bender))
        Assert.True((read (fun c -> User.authenticateBot c (User.botKey bender))).IsSome)
        Assert.Equal(2, List.length (read User.activeBotsOrdered))
        Assert.Equal(7, List.length (read User.activeOrdered)) // not Rita (deactivated), Mallory (banned) or Old Bot (deactivated)

        // Room types, and a user's rooms.
        let rooms = read Room.all
        Assert.Equal<RoomType list>([ Open; Open; Open ], rooms |> List.filter Room.isOpen |> List.map (fun r -> r.RoomType))
        Assert.Equal(4, rooms |> List.filter Room.isDirect |> List.length)
        Assert.True(read (fun c -> Room.forUser c david.Id) |> List.length >= 5)
        let session = read (fun c -> Session.findByToken c "AxJs94fteQ5Autv2VrKsH68c")
        Assert.Equal(david.Id, session.Value.UserId))

[<Fact>]
let ``every datetime Rails wrote reads and writes back as the same text`` () =
    withSeed "default" (fun path _ ->
        use conn = Conn.Open path
        let tables = conn.QueryAll("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE 'message_search_index%'", [||], fun r -> r.Text 0)
        let mutable checkedValues = 0
        for table in tables do
            let columns = conn.QueryAll($"SELECT name FROM pragma_table_info('{table}') WHERE type LIKE 'datetime%%'", [||], fun r -> r.Text 0)
            for column in columns do
                let values = conn.QueryAll($"SELECT \"{column}\" FROM \"{table}\" WHERE \"{column}\" IS NOT NULL", [||], fun r -> r.Text 0)
                for text in values do
                    match Timestamp.ParseDb text with
                    | Some ts -> Assert.Equal(text, ts.ToDb())
                    | None -> failwith $"{table}.{column}: {text} doesn't parse"
                    checkedValues <- checkedValues + 1
        Assert.True(checkedValues > 500, $"{checkedValues} datetimes checked"))

[<Fact>]
let ``messages page, search and read their bodies and attachments as Rails stored them`` () =
    withSeed "default" (fun path seed ->
        use db = openSeed path (TestClock()) (RecordingSink())
        let read f = unwrap (db.ReadBlocking f)
        let richText = BasicRichText() :> RichText

        // Every room's pages agree with SQL, and the paging index serves them.
        for room in read Room.all do
            let page = read (fun c -> Message.lastPage c room.Id)
            let expected =
                read (fun c ->
                    c.QueryAll(
                        "SELECT id FROM messages WHERE room_id = ? ORDER BY created_at DESC LIMIT 40",
                        [| I room.Id |],
                        fun r -> r.Int64 0
                    ))
                |> List.rev
            Assert.Equal<int64 list>(expected, page |> List.map (fun m -> m.Id))
            Assert.Equal(read (fun c -> Message.countInRoom c room.Id) > 40L, read (fun c -> Message.paged c room.Id))

        // The full-text index Rails fills, searched as the app does.
        let indexed = read (fun c -> c.QueryAll("SELECT rowid, body FROM message_search_index WHERE length(body) > 20 LIMIT 20", [||], fun r -> r.Int64 0, r.Text 1))
        Assert.True(List.length indexed > 5)
        let mutable searched = 0
        for messageId, body in indexed do
            match System.Text.RegularExpressions.Regex.Match(body, "[A-Za-z]{5,}") with
            | m when m.Success ->
                let message = read (fun c -> Message.find c messageId)
                let found = read (fun c -> Message.searchInRoom c message.RoomId m.Value)
                Assert.True(found |> List.exists (fun f -> f.Id = messageId), $"\"{m.Value}\" finds message {messageId}")
                searched <- searched + 1
            | _ -> ()
        Assert.True(searched > 5, $"{searched} searches")

        // Plain text and attachments.
        let withAttachment =
            read (fun c ->
                c.QueryAll("SELECT record_id FROM active_storage_attachments WHERE record_type = 'Message' AND name = 'attachment'", [||], fun r -> r.Int64 0))
        Assert.Equal(5, List.length withAttachment)
        for messageId in withAttachment do
            let message = read (fun c -> Message.find c messageId)
            let attachment = read (fun c -> Message.attachment c message)
            let _, blob = attachment.Value
            Assert.False(String.IsNullOrEmpty blob.Filename)
            Assert.Equal(ContentType.Attachment, read (fun c -> Message.contentType c richText message))
            Assert.False(String.IsNullOrEmpty(read (fun c -> Message.plainTextBody c richText message)))

        // Boosts, in the order they were made.
        let boosted =
            read (fun c -> c.QueryAll("SELECT DISTINCT message_id FROM boosts", [||], fun r -> r.Int64 0))
            |> List.map (fun messageId -> read (fun c -> Message.boosts c (Message.find c messageId)))
        Assert.Equal(12, List.sumBy List.length boosted)
        for boosts in boosted do
            Assert.Equal<Timestamp list>(boosts |> List.map (fun b -> b.CreatedAt) |> List.sort, boosts |> List.map (fun b -> b.CreatedAt))

        // A seeded message reads by its label.
        Assert.Equal(label seed "messages.first", (read (fun c -> Message.find c (label seed "messages.first"))).Id))

[<Fact>]
let ``memberships Rails wrote read with every involvement and connection state`` () =
    withSeed "default" (fun path _ ->
        use db = openSeed path (TestClock()) (RecordingSink())
        let memberships = unwrap (db.ReadBlocking(fun c -> c.QueryAll("SELECT id FROM memberships", [||], fun r -> r.Int64 0) |> List.map (Membership.find c)))
        let involvements = memberships |> List.map (fun m -> m.Involvement) |> List.distinct |> Set.ofList
        Assert.Equal<Involvement option Set>(Set.ofList [ Some Everything; Some Mentions; Some Nothing; Some Invisible ], involvements)
        let user = memberships[0].UserId
        let withRooms = unwrap (db.ReadBlocking(fun c -> Membership.withOrderedRoom c user))
        Assert.False(List.isEmpty withRooms)
        let visible = unwrap (db.ReadBlocking(fun c -> Membership.visibleWithOrderedRoom c user))
        Assert.True(List.length visible <= List.length withRooms))

[<Fact>]
let ``writes land beside the rows Rails wrote`` () =
    withSeed "default" (fun path seed ->
        let clock = TestClock()
        clock.TravelTo((Timestamp.ParseDb "2026-03-03 09:30:15.123456").Value.ToDateTimeOffset())
        let sink = RecordingSink()
        use db = openSeed path clock sink
        let david, designers = label seed "users.david", label seed "rooms.designers"
        let before = unwrap (db.ReadBlocking(fun c -> Message.countInRoom c designers))

        let message =
            unwrap (
                db.WriteBlocking(fun tx ->
                    let m =
                        Message.create
                            tx
                            { NewMessage.create designers david with
                                ClientMessageId = Some "fsharp-seed-1"
                                Body = Some "Written by <b>F#</b> beside Rails" }
                    Boost.create tx m.Id david "\U0001F680" |> ignore
                    m)
            )
        Assert.Equal<Event list>([ PushMessage(designers, message.Id) ], sink.Events())
        Assert.Equal(before + 1L, unwrap (db.ReadBlocking(fun c -> Message.countInRoom c designers)))
        let last = unwrap (db.ReadBlocking(fun c -> Message.lastPage c designers)) |> List.last
        Assert.Equal(message.Id, last.Id)
        Assert.Equal("2026-03-03 09:30:15.123456", last.CreatedAt.ToDb())
        Assert.Equal<int64 list>([ message.Id ], unwrap (db.ReadBlocking(fun c -> Message.searchInRoom c designers "beside")) |> List.map (fun m -> m.Id))

        // Read back by a connection of our own, as the reference app would.
        (db :> IDisposable).Dispose()
        use raw = Conn.Open path
        Assert.Equal("2026-03-03 09:30:15.123456", raw.QueryRow("SELECT created_at FROM messages WHERE client_message_id = 'fsharp-seed-1'", [||], fun r -> r.Text 0))
        Assert.Equal(1L, raw.Count("SELECT COUNT(*) FROM boosts WHERE message_id = ?", [| I message.Id |])))

[<Fact>]
let ``an account with restricted room creation and an account with custom styles read their settings`` () =
    withSeed "restricted" (fun path _ ->
        use db = openSeed path (TestClock()) (RecordingSink())
        let account = unwrap (db.ReadBlocking(fun c -> (Account.first c).Value))
        Assert.True(AccountSettings.restrictRoomCreationToAdministrators (Account.settings account))
        Assert.Equal(Some """{"restrict_room_creation_to_administrators":true}""", account.SettingsJson))
    withSeed "custom_styles" (fun path _ ->
        use db = openSeed path (TestClock()) (RecordingSink())
        let account = unwrap (db.ReadBlocking(fun c -> (Account.first c).Value))
        Assert.False(AccountSettings.restrictRoomCreationToAdministrators (Account.settings account))
        Assert.True(account.CustomStyles.IsSome && account.CustomStyles.Value.Length > 0)
        // Updating the styles leaves the settings JSON as Rails wrote it, and writes only what changed.
        let updated = unwrap (db.WriteBlocking(fun tx -> Account.update tx account None (Some(Some "body { color: red }")) None))
        Assert.Equal(account.SettingsJson, updated.SettingsJson)
        Assert.Equal(Some "body { color: red }", (unwrap (db.ReadBlocking(fun c -> (Account.first c).Value))).CustomStyles))

[<Fact>]
let ``the first run seed has no account and FirstRun creates one`` () =
    withSeed "first_run" (fun path _ ->
        use db = openSeed path (TestClock()) (RecordingSink())
        Assert.Equal(0L, unwrap (db.ReadBlocking Account.count))
        let digest = PasswordDigest.create "secret123456" 4
        let admin = unwrap (db.WriteBlocking(fun tx -> FirstRun.create tx "First" "first@example.com" digest))
        Assert.True(User.isAdministrator admin)
        let rooms = unwrap (db.ReadBlocking(fun c -> Room.forUser c admin.Id))
        Assert.Equal<string option list>([ Some "All Talk" ], rooms |> List.map (fun r -> r.Name))
        Assert.Equal(1L, unwrap (db.ReadBlocking Account.count)))

[<Fact>]
let ``a crowd of users all join a new open room`` () =
    withSeed "crowd" (fun path seed ->
        use db = openSeed path (TestClock()) (RecordingSink())
        let users = unwrap (db.ReadBlocking User.count)
        Assert.True(users > 500L, $"{users} users")
        let active = unwrap (db.ReadBlocking(fun c -> List.length (User.active c)))
        let room = unwrap (db.WriteBlocking(fun tx -> Room.create tx Open (Some "Everyone") (label seed "users.david")))
        let members = unwrap (db.ReadBlocking(fun c -> Room.userIds c room))
        Assert.Equal(active, List.length members)
        // A new user is granted every open room, in one statement per batch.
        let user = unwrap (db.WriteBlocking(fun tx -> User.create tx (NewUser.create "Latecomer")))
        let openRooms = unwrap (db.ReadBlocking(fun c -> Room.countOfType c Open))
        Assert.Equal(openRooms, int64 (List.length (unwrap (db.ReadBlocking(fun c -> User.memberships c user))))))
