// Port of rust/crates/db/src/tests/differential_test.rs
//
// Runs the same scenario as `scenario.rb` (reproduced below) through the F# models and compares
// every table with the database the reference app produced from the same fixtures. Skipped unless
// `CAMPFIRE_RUBY_SCENARIO_DB` points at that database.
//
//     m = rooms(:designers).messages.create!(body: "Hello <b>there</b>", client_message_id: "s1", creator: david)
//     b = m.boosts.create!(content: "hi", booster: jason)
//     m.boosts.create!(content: "yo", booster: kevin)
//     b.destroy!
//     m.update!(body: "Edited hovercraft")
//     m2 = rooms(:hq).messages.create!(body: "Doomed", client_message_id: "s2", creator: jason)
//     m2.boosts.create!(content: "x", booster: david)
//     m2.destroy!
//     memberships(:kevin_designers).destroy
//     u = User.create!(name: "User", email_address: "u@example.com", password: "secret123456")
//     Rooms::Open.create!(name: "Open!", creator: david)
//     Rooms::Closed.create_for({ name: "Hello!", creator: david }, users: [kevin, david])
//     r = rooms(:watercooler).becomes!(Rooms::Open); r.save!
//     rooms(:pets).update!(name: "Pets2")
//     memberships(:jason_pets).update!(involvement: "invisible")
//     mm = memberships(:david_hq); mm.connected; mm.connected; mm.disconnected
//     Rooms::Direct.find_or_create_for([jz, kevin])        # Current.user = david
//     kevin.sessions.create!(ip_address: "8.8.8.8", user_agent: "x")
//     kevin.ban
//     kevin.unban
//     jz.remove_banned_content
//     jz.deactivate
//     12.times { |n| david.searches.record("q#{n}") }
//     bot = User.create_bot!(name: "Bot", webhook_url: "http://x")
//     bot.update_bot!(name: "Bot2", webhook_url: "http://y")
//     a = Account.first; a.settings.restrict_room_creation_to_administrators = true; a.save!
//     rooms(:hq).messages.create!(body: "Last one", client_message_id: "s3", creator: kevin)
module Campfire.Db.Tests.DifferentialTests

open System
open Xunit
open Campfire.Db
open Campfire.Db.Tests.Support

let private message (room: string) (creator: string) (body: string) (clientMessageId: string) : NewMessage =
    { NewMessage.create (id room) (id creator) with
        ClientMessageId = Some clientMessageId
        Body = Some body }

let private runScenario (t: TestDb) : unit =
    // Each step is its own write, as each Rails call is its own transaction.
    let m = t.Write(fun tx -> Message.create tx (message "designers" "david" "Hello <b>there</b>" "s1"))
    let b = t.Write(fun tx -> Boost.create tx m.Id (id "jason") "hi")
    t.Write(fun tx -> Boost.create tx m.Id (id "kevin") "yo" |> ignore)
    t.Write(fun tx -> Boost.destroy tx b)
    t.Write(fun tx -> Message.updateBody tx (Message.find tx.Conn m.Id) "Edited hovercraft" |> ignore)
    let m2 = t.Write(fun tx -> Message.create tx (message "hq" "jason" "Doomed" "s2"))
    t.Write(fun tx -> Boost.create tx m2.Id (id "david") "x" |> ignore)
    t.Write(fun tx -> Message.destroy tx (Message.find tx.Conn m2.Id))
    t.Write(fun tx -> Membership.destroy tx (Membership.find tx.Conn (id "kevin_designers")))
    t.Write(fun tx ->
        User.create
            tx
            { NewUser.create "User" with
                EmailAddress = Some "u@example.com"
                PasswordDigest = Some(PasswordDigest.create "secret123456" 4) }
        |> ignore)
    t.Write(fun tx -> Room.create tx Open (Some "Open!") (id "david") |> ignore)
    t.Write(fun tx -> Room.createFor tx Closed (Some "Hello!") (id "david") [ id "kevin"; id "david" ] |> ignore)
    t.Write(fun tx -> Room.update tx (Room.find tx.Conn (id "watercooler")) None (Some Open) |> ignore)
    t.Write(fun tx -> Room.update tx (Room.find tx.Conn (id "pets")) (Some(Some "Pets2")) None |> ignore)
    t.Write(fun tx -> Membership.updateInvolvement tx (Membership.find tx.Conn (id "jason_pets")) (Some Invisible) |> ignore)
    t.Write(fun tx ->
        Membership.find tx.Conn (id "david_hq")
        |> Membership.connected tx
        |> Membership.connected tx
        |> Membership.disconnected tx
        |> ignore)
    t.Write(fun tx -> Room.findOrCreateDirectFor tx [ id "jz"; id "kevin" ] (id "david") |> ignore)
    t.Write(fun tx -> Session.start tx (id "kevin") (Some "x") (Some "8.8.8.8") |> ignore)
    t.Write(fun tx -> User.ban tx (User.find tx.Conn (id "kevin")) |> ignore)
    t.Write(fun tx -> User.unban tx (User.find tx.Conn (id "kevin")) |> ignore)
    t.Write(fun tx -> User.removeBannedContent tx (User.find tx.Conn (id "jz")) |> ignore)
    t.Write(fun tx -> User.deactivate tx (User.find tx.Conn (id "jz")) |> ignore)
    for n in 0..11 do
        t.Write(fun tx -> Search.record tx (id "david") $"q{n}" |> ignore)
    let bot = t.Write(fun tx -> User.createBot tx "Bot" (Some "http://x"))
    t.Write(fun tx -> User.updateBot tx bot { UserChanges.none with Name = Some "Bot2" } (Some "http://y") |> ignore)
    t.Write(fun tx ->
        Account.update tx (Account.first tx.Conn).Value None None (Some [ "restrict_room_creation_to_administrators", "true" ])
        |> ignore)
    t.Write(fun tx -> Message.create tx (message "hq" "kevin" "Last one" "s3") |> ignore)

/// Every row, with values that are random or clock-dependent reduced to their shape.
let private normalizedDump (conn: Conn) (table: string) (order: string) : string list =
    let columns = if table = "message_search_index" then "rowid AS id, body" else "*"
    conn.QueryAll(
        $"SELECT {columns} FROM \"{table}\" ORDER BY {order}",
        [||],
        fun r ->
            let fields =
                [ for i in 0 .. r.FieldCount - 1 ->
                      let name = r.ColumnName i
                      let rendered =
                          match name, r.Arg i with
                          | "password_digest", S s -> $"bcrypt:{s.Substring(0, 4)}"
                          | ("bot_token" | "token" | "join_code"), S s -> $"random:{s.Length}"
                          | "email_address", S s when s.Contains "-deactivated-" ->
                              let at = s.IndexOf "-deactivated-"
                              let local = s.Substring(0, at)
                              let rest = s.Substring(at + "-deactivated-".Length)
                              let atSign = rest.IndexOf '@'
                              $"{local}-deactivated-<uuid:{atSign}>{rest.Substring atSign}"
                          | n, S s when n.EndsWith "_at" ->
                              let fraction = match s.IndexOf '.' with | -1 -> 0 | dot -> s.Length - dot - 1
                              $"time:{fraction}"
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
let ``scenario matches ruby`` () =
    match Environment.GetEnvironmentVariable "CAMPFIRE_RUBY_SCENARIO_DB" with
    | null -> Assert.Skip "needs CAMPFIRE_RUBY_SCENARIO_DB, the reference app's database after scenario.rb"
    | path ->
        use ruby = Conn.Open path
        use t = new TestDb()
        runScenario t

        let mismatches = ResizeArray<string>()
        let mutable compared = 0
        for table, order in
            [ "accounts", "id"
              "action_text_rich_texts", "id"
              "bans", "id"
              "boosts", "id"
              "memberships", "room_id, user_id"
              "messages", "id"
              "push_subscriptions", "id"
              "rooms", "id"
              "searches", "id"
              "sessions", "id"
              "users", "id"
              "webhooks", "id"
              "message_search_index", "rowid" ] do
            let expected = normalizedDump ruby table order
            let actual = t.Read(fun c -> normalizedDump c table order)
            compared <- compared + List.length expected
            if expected <> actual then
                let onlyRuby = expected |> List.filter (fun r -> not (List.contains r actual))
                let onlyFSharp = actual |> List.filter (fun r -> not (List.contains r expected))
                mismatches.Add($"{table}:\n  ruby only: {onlyRuby}\n  fsharp only: {onlyFSharp}")
        Assert.True(mismatches.Count = 0, String.Join("\n", mismatches))
        Assert.True(compared > 80, $"{compared} rows compared")
