// Port of rust/crates/db/src/tests/user_test.rs
// `test/models/user_test.rb`, `user/bot_test.rb`, `user/role_test.rb`, plus Bannable.
module Campfire.Db.Tests.UserTests

open Xunit
open Campfire.Db
open Campfire.Db.Tests.Support

let private user (t: TestDb) (label: string) : User =
    let userId = id label
    t.Read(fun c -> User.find c userId)

let private createNewUser (t: TestDb) : User =
    t.Write(fun tx ->
        User.create
            tx
            { NewUser.create "User" with
                EmailAddress = Some "user@example.com"
                PasswordDigest = Some(PasswordDigest.create "secret123456" 4) })

[<Fact>]
let ``user does not prevent very long passwords`` () =
    use t = new TestDb()
    let david = user t "david"
    let long = String.replicate 50 "secret"
    t.Write(fun tx ->
        User.update tx david { UserChanges.none with PasswordDigest = Some(PasswordDigest.create long 4) } |> ignore)
    Assert.True(User.authenticate (user t "david") long)

[<Fact>]
let ``creating users grants membership to the open rooms`` () =
    use t = new TestDb()
    let before = t.Read Membership.count
    let openRooms = t.Read(fun c -> Room.countOfType c Open)
    let user = createNewUser t
    Assert.Equal(before + openRooms, t.Read Membership.count)
    // grant_membership_to_open_rooms leaves involvement to the column default.
    Assert.True(t.Read(fun c -> User.memberships c user) |> List.forall (fun m -> Membership.involvedIn m Mentions))

[<Fact>]
let ``creating subsequent users makes them members`` () =
    use t = new TestDb()
    let user = createNewUser t
    Assert.True(User.isMember user)
    Assert.True(User.isActive user)
    Assert.StartsWith("$2a$04$", user.PasswordDigest.Value)

[<Fact>]
let ``deactivating a user deletes push subscriptions searches memberships for non direct rooms and changes their email address`` () =
    use t = new TestDb()
    let david = id "david"
    let memberships = t.Read Membership.count
    let withoutDirects = t.Read(fun c -> Membership.countWithoutDirectRooms c david)
    let subscriptions = t.Read PushSubscription.count
    let davidsSubscriptions = int64 (List.length (t.Read(fun c -> PushSubscription.forUser c david)))
    let searches = t.Read Search.count
    let davidsSearches = t.Read(fun c -> Search.countForUser c david)

    let user = user t "david"
    t.Write(fun tx -> User.deactivate tx user |> ignore)

    Assert.Equal(memberships - withoutDirects, t.Read Membership.count)
    Assert.Equal(subscriptions - davidsSubscriptions, t.Read PushSubscription.count)
    Assert.Equal(searches - davidsSearches, t.Read Search.count)

    let reloaded = t.Read(fun c -> User.find c david)
    let email = reloaded.EmailAddress.Value
    Assert.True(email.StartsWith "david-deactivated-" && email.EndsWith "@37signals.com", email)
    Assert.Equal("david-deactivated-2e7de450-cf04-4fa8-9b02-ff5ab2d733e7@37signals.com".Length, email.Length)
    Assert.Equal(Status.Deactivated, reloaded.Status)
    Assert.Equal<Event list>([ DisconnectUser(david, false) ], t.Events())

[<Fact>]
let ``deactivating a user deletes their sessions`` () =
    use t = new TestDb()
    Assert.Equal(1L, t.Read(fun c -> Session.countForUser c (id "david")))
    let david = user t "david"
    t.Write(fun tx -> User.deactivate tx david |> ignore)
    Assert.Equal(0L, t.Read(fun c -> Session.countForUser c (id "david")))

[<Fact>]
let ``initials and title`` () =
    use t = new TestDb()
    let jz = user t "jz"
    Assert.Equal("J", User.initials jz)
    Assert.Equal("JZ – Designer", User.title jz)
    Assert.Equal("BB", User.initials (user t "bender"))
    let jz = { jz with Name = "Émile Zola" }
    Assert.Equal("Z", User.initials jz) // Ruby's \b sees É as a word character, \w doesn't
    let jz = { jz with Bio = Some "  " }
    Assert.Equal("Émile Zola", User.title jz)

// User::Bot

[<Fact>]
let ``create bot`` () =
    use t = new TestDb()
    let bot = t.Write(fun tx -> User.createBot tx "Bender" None)
    let token = bot.BotToken.Value
    Assert.Equal(12, token.Length)
    Assert.Equal($"{bot.Id}-{token}", User.botKey bot)
    Assert.Equal(Role.Bot, bot.Role)
    Assert.True(bot.PasswordDigest.IsNone)

[<Fact>]
let ``create bot with webhook`` () =
    use t = new TestDb()
    let bot = t.Write(fun tx -> User.createBot tx "Bot" (Some "http://x"))
    Assert.Equal(Some "http://x", t.Read(fun c -> User.webhookUrl c bot))

    t.Write(fun tx -> User.updateBot tx bot { UserChanges.none with Name = Some "Bot2" } (Some "") |> ignore)
    Assert.True((t.Read(fun c -> Webhook.findByUser c bot.Id)).IsNone)
    Assert.Equal("Bot2", (t.Read(fun c -> User.find c bot.Id)).Name)

[<Fact>]
let ``reset bot key`` () =
    use t = new TestDb()
    let bot = t.Write(fun tx -> User.createBot tx "Bender" None)
    let first = User.botKey bot
    let second = t.Write(fun tx -> User.botKey (User.resetBotKey tx bot))
    Assert.NotEqual<string>(first, second)
    Assert.True((t.Read(fun c -> User.authenticateBot c first)).IsNone)
    Assert.True((t.Read(fun c -> User.authenticateBot c second)).IsSome)

[<Fact>]
let ``authenticate bot`` () =
    use t = new TestDb()
    let bot = t.Write(fun tx -> User.createBot tx "Bender" None)
    Assert.Equal(bot.Id, (t.Read(fun c -> User.authenticateBot c (User.botKey bot))).Value.Id)
    Assert.True((t.Read(fun c -> User.authenticateBot c "nonsense")).IsNone)
    Assert.True((t.Read(fun c -> User.authenticateBot c $"{bot.Id}-")).IsNone)

[<Fact>]
let ``deliver message by webhook`` () =
    use t = new TestDb()
    let bender = user t "bender"
    t.Write(fun tx -> User.deliverWebhookLater tx bender (id "first"))
    Assert.Equal<Event list>([ DeliverWebhook(id "bender", id "first") ], t.Events())

    // No webhook, no job.
    let jz = user t "jz"
    t.Write(fun tx -> User.deliverWebhookLater tx jz (id "first"))
    Assert.Equal(1, List.length (t.Events()))

[<Fact>]
let ``webhook payload`` () =
    use t = new TestDb()
    let richText = BasicRichText() :> RichText
    let message = t.Read(fun c -> Message.find c (id "first"))
    let webhook = t.Read(fun c -> (Webhook.findByUser c (id "bender")).Value)
    let payload = t.Read(fun c -> Webhook.payload c richText webhook message "/rooms/1/bot/key/messages" "/rooms/1/@2")
    let jason, designers = id "jason", id "designers"
    Assert.Equal(
        $"""{{"user":{{"id":{jason},"name":"Jason"}},"room":{{"id":{designers},"name":"Designers","path":"/rooms/1/bot/key/messages"}},"message":{{"id":{message.Id},"body":{{"html":"First post!","plain":"First post!"}},"path":"/rooms/1/@2"}}}}""",
        payload
    )

/// `ActiveSupport::JSON` escapes `<`, `>` and `&`, and an unnamed room's name is `null`.
[<Fact>]
let ``webhook payload escapes html entities`` () =
    use t = new TestDb()
    let richText = BasicRichText() :> RichText
    let first, designers, jason = id "first", id "designers", id "jason"
    t.Write(fun tx ->
        tx.Conn.Execute("UPDATE rooms SET name = NULL WHERE id = ?", [| I designers |]) |> ignore
        tx.Conn.Execute(
            "UPDATE action_text_rich_texts SET body = '<p>Tom & Jerry</p>' WHERE record_type = 'Message' AND record_id = ?",
            [| I first |]
        )
        |> ignore)
    let message = t.Read(fun c -> Message.find c first)
    let webhook = t.Read(fun c -> (Webhook.findByUser c (id "bender")).Value)
    let payload = t.Read(fun c -> Webhook.payload c richText webhook message "/rooms/1/bot/key/messages" "/rooms/1/@2")
    // Regular strings, whose \u escapes would otherwise be read here as characters.
    let html = "\\u003cp\\u003eTom \\u0026 Jerry\\u003c/p\\u003e"
    let plain = "Tom \\u0026 Jerry"
    Assert.Equal(
        $"""{{"user":{{"id":{jason},"name":"Jason"}},"room":{{"id":{designers},"name":null,"path":"/rooms/1/bot/key/messages"}},"message":{{"id":{first},"body":{{"html":"{html}","plain":"{plain}"}},"path":"/rooms/1/@2"}}}}""",
        payload
    )

// User::Role

[<Fact>]
let ``can administer`` () =
    use t = new TestDb()
    let admin = user t "david"
    Assert.True(User.canAdminister admin None false)
    let admin = { admin with Role = Role.Member }
    Assert.False(User.canAdminister admin None false)

    let m = user t "kevin"
    Assert.True(User.canAdminister m (Some m.Id) false, "creator")
    Assert.True(User.canAdminister m (Some(id "jz")) true, "new record")
    let designers = t.Read(fun c -> Room.find c (id "designers"))
    Assert.False(User.canAdminister m (Some designers.CreatorId) false)

// User::Bannable

[<Fact>]
let ``ban creates bans from session ips and removes sessions`` () =
    use t = new TestDb()
    let kevin = id "kevin"
    t.Write(fun tx ->
        Session.start tx kevin (Some "ua") (Some "8.8.8.8") |> ignore
        Session.start tx kevin (Some "ua") (Some "8.8.8.8") |> ignore
        Session.start tx kevin (Some "ua") (Some "") |> ignore)
    t.Sink.Take() |> ignore

    let u = user t "kevin"
    t.Write(fun tx -> User.ban tx u |> ignore)

    Assert.Equal<string list>([ "8.8.8.8" ], t.Read(fun c -> Ban.forUser c kevin) |> List.map (fun b -> b.IpAddress))
    Assert.True(t.Read(fun c -> Ban.banned c "8.8.8.8"))
    Assert.Equal(0L, t.Read(fun c -> Session.countForUser c kevin))
    Assert.Equal(Status.Banned, (t.Read(fun c -> User.find c kevin)).Status)
    Assert.Equal<Event list>([ DisconnectUser(kevin, false); RemoveBannedContent kevin ], t.Events())

    let u = t.Read(fun c -> User.find c kevin)
    t.Write(fun tx -> User.unban tx u |> ignore)
    Assert.False(t.Read(fun c -> Ban.banned c "8.8.8.8"))
    Assert.Equal(Status.Active, (t.Read(fun c -> User.find c kevin)).Status)

[<Fact>]
let ``ban rejects private session ips`` () =
    use t = new TestDb()
    let kevin = id "kevin"
    t.Write(fun tx -> Session.start tx kevin None (Some "192.168.1.1") |> ignore)
    let u = user t "kevin"
    Assert.True(t.TryWrite(fun tx -> User.ban tx u) |> Result.isError)
    Assert.Equal(Status.Active, (t.Read(fun c -> User.find c kevin)).Status) // rolled back

[<Fact>]
let ``remove banned content`` () =
    use t = new TestDb()
    let jz = user t "jz"
    let removed = t.Write(fun tx -> User.removeBannedContent tx jz)
    Assert.Equal(5, List.length removed)
    Assert.True(List.isEmpty (t.Read(fun c -> Message.byCreator c (id "jz"))))
