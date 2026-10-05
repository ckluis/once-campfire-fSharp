// Port of rust/crates/db/src/tests/fixtures_test.rs
// The fixture loader against facts from a Ruby-loaded fixtures database, and (skipped unless
// `CAMPFIRE_RUBY_FIXTURES_DB` is set) a row-for-row comparison with one.
module Campfire.Db.Tests.FixturesTests

open System
open System.IO
open Xunit
open Campfire.Db
open Campfire.Db.Tests.Support

[<Fact>]
let ``ids are label crcs`` () =
    use t = new TestDb()
    Assert.Equal("David", (t.Read(fun c -> User.find c 127326141L)).Name)
    Assert.Equal(Some "Designers", (t.Read(fun c -> Room.find c 654632876L)).Name)

[<Fact>]
let ``associations enums and defaults`` () =
    use t = new TestDb()
    let pets = t.Read(fun c -> Room.find c (id "pets"))
    Assert.Equal((Open, id "david"), (pets.RoomType, pets.CreatorId))

    Assert.Equal(Role.Administrator, (t.Read(fun c -> User.find c (id "david"))).Role)
    Assert.Equal(Role.Member, (t.Read(fun c -> User.find c (id "jz"))).Role)
    let bender = t.Read(fun c -> User.find c (id "bender"))
    Assert.Equal((Role.Bot, Some 12), (bender.Role, bender.BotToken |> Option.map String.length))

    let kevinDesigners = t.Read(fun c -> Membership.find c (id "kevin_designers"))
    Assert.Equal(Some Mentions, kevinDesigners.Involvement) // column default
    Assert.Equal(0L, kevinDesigners.Connections)

    let richText =
        t.Read(fun c ->
            c.QueryRow(
                "SELECT record_id, record_type FROM action_text_rich_texts WHERE id = ?",
                [| I(id "first") |],
                fun r -> r.Int64 0, r.Text 1
            ))
    Assert.Equal((id "first", "Message"), richText)

[<Fact>]
let ``erb times are whole seconds and default timestamps are now`` () =
    use t = new TestDb()
    let first = t.Read(fun c -> Message.find c (id "first"))
    Assert.Equal(0, first.CreatedAt.SubsecMicrosecond)
    Assert.True(first.UpdatedAt > first.CreatedAt)
    let session = t.Read(fun c -> Session.find c (id "david_safari"))
    Assert.Equal(0, session.LastActiveAt.SubsecMicrosecond)
    Assert.True(abs (session.CreatedAt.AsSecond - session.LastActiveAt.AsSecond - 7200L) <= 1L)

[<Fact>]
let ``passwords are bcrypt of secret123456`` () =
    use t = new TestDb()
    let david = t.Read(fun c -> User.find c (id "david"))
    Assert.True(User.authenticate david "secret123456")
    Assert.StartsWith("$2a$", david.PasswordDigest.Value)

/// Dump a table with values normalized where Ruby and F# can't agree (bcrypt salts, bot tokens,
/// and timestamps, which are compared by shape: whole seconds vs. fractional).
let dump (conn: Conn) (table: string) : string list =
    conn.QueryAll(
        $"SELECT * FROM \"{table}\" ORDER BY id",
        [||],
        fun r ->
            let fields =
                [ for i in 0 .. r.FieldCount - 1 ->
                      let name = r.ColumnName i
                      let rendered =
                          match name, r.Arg i with
                          | "password_digest", S s -> $"bcrypt:{s.Substring(0, 4)}"
                          | "bot_token", S s -> $"token:{s.Length}"
                          | n, S s when n.EndsWith "_at" -> if s.Contains '.' then "time:fraction" else $"time:whole:{s.Substring(0, 10)}"
                          | _, Null -> "Null"
                          | _, I v -> $"Integer({v})"
                          | _, R v -> $"Real({v})"
                          | _, S v -> $"Text(\"{v}\")"
                          | _, B v -> $"Blob({v.Length})"
                          | _, T _ -> "Time"
                      name, rendered ]
            fields |> List.sort |> List.map (fun (n, v) -> $"{n}={v}") |> String.concat " "
    )

[<Fact>]
let ``fixtures match ruby row for row`` () =
    match Environment.GetEnvironmentVariable "CAMPFIRE_RUBY_FIXTURES_DB" with
    | null -> Assert.Skip "needs CAMPFIRE_RUBY_FIXTURES_DB, a database the reference app filled with `db:fixtures:load`"
    | path ->
        use ruby = Conn.Open path
        use t = new TestDb()
        let tables =
            [ "accounts"; "action_text_rich_texts"; "boosts"; "memberships"; "messages"; "push_subscriptions"; "rooms"; "searches"; "sessions"; "users"; "webhooks" ]
        let mutable compared = 0
        for table in tables do
            let expected = dump ruby table
            let actual = t.Read(fun c -> dump c table)
            Assert.True((expected = actual), $"{table}\nruby: {expected}\nfsharp: {actual}")
            compared <- compared + List.length expected
        Assert.True(compared > 60, $"{compared} rows compared")

[<Fact>]
let ``export database for rails`` () =
    match Environment.GetEnvironmentVariable "CAMPFIRE_EXPORT_DB" with
    | null -> Assert.Skip "writes a database to CAMPFIRE_EXPORT_DB for the Rails rollback check"
    | path ->
        for suffix in [ ""; "-wal"; "-shm" ] do
            File.Delete(path + suffix)
        let env = { Testing.defaultEnv () with BcryptCost = 4 }
        let config = { Config.create path with Environment = "test" }
        use db = Database.Open(config, env)
        unwrap (
            db.WriteBlocking(fun tx ->
                Fixtures.load tx.Conn (Fixtures.referenceDir ()) { Now = tx.Now(); BcryptCost = 4 } |> ignore)
        )
        unwrap (
            db.WriteBlocking(fun tx ->
                let message =
                    Message.create
                        tx
                        { NewMessage.create (id "designers") (id "david") with
                            ClientMessageId = Some "rust-1"
                            Body = Some "Written by <b>Rust</b> hovercraft" }
                Boost.create tx message.Id (id "jason") "\U0001F980" |> ignore
                let user =
                    User.create
                        tx
                        { NewUser.create "Rusty" with
                            EmailAddress = Some "rusty@example.com"
                            PasswordDigest = Some(PasswordDigest.create "secret123456" 4) }
                Session.start tx user.Id (Some "ua") (Some "8.8.8.8") |> ignore
                Search.record tx user.Id "hovercraft" |> ignore
                Room.createFor tx Closed (Some "Rust Room") user.Id [ user.Id; id "david" ] |> ignore
                let account = (Account.first tx.Conn).Value
                Account.update tx account None None (Some [ "restrict_room_creation_to_administrators", "true" ]) |> ignore)
        )
