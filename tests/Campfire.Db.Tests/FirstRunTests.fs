// Port of rust/crates/db/src/tests/first_run_test.rs
// `test/models/first_run_test.rb`
module Campfire.Db.Tests.FirstRunTests

open Xunit
open Campfire.Db
open Campfire.Db.Tests.Support

let private fresh () : TestDb =
    let t = new TestDb()
    // Account.destroy_all, Room.destroy_all, User.destroy_all
    t.Write(fun tx ->
        tx.Conn.ExecuteBatch
            "DELETE FROM accounts; DELETE FROM boosts; DELETE FROM action_text_rich_texts; DELETE FROM messages; DELETE FROM memberships;
             DELETE FROM rooms; DELETE FROM sessions; DELETE FROM searches; DELETE FROM push_subscriptions; DELETE FROM webhooks;
             DELETE FROM bans; DELETE FROM users;")
    t

let private createFirstRunUser (t: TestDb) : User =
    let digest = PasswordDigest.create "secret123456" 4
    t.Write(fun tx -> FirstRun.create tx "User" "user@example.com" digest)

[<Fact>]
let ``creating makes first user an administrator`` () =
    use t = fresh ()
    Assert.True(User.isAdministrator (createFirstRunUser t))

[<Fact>]
let ``first user has access to first room`` () =
    use t = fresh ()
    let user = createFirstRunUser t
    let rooms = t.Read(fun c -> Room.forUser c user.Id)
    Assert.Equal(1, List.length rooms)
    Assert.Equal(Some "All Talk", rooms[0].Name)

[<Fact>]
let ``first room is an open room`` () =
    use t = fresh ()
    createFirstRunUser t |> ignore
    Assert.True(Room.isOpen (t.Read(fun c -> (Room.original c).Value)))

[<Fact>]
let ``first user can sign in`` () =
    use t = fresh ()
    createFirstRunUser t |> ignore
    let found = t.Read(fun c -> User.findActiveByEmailAddress c "user@example.com")
    Assert.True((User.authenticated found "secret123456").IsSome)
    Assert.True((User.authenticated found "wrong").IsNone)
    Assert.True((User.authenticated found "").IsNone)
    let missing = t.Read(fun c -> User.findActiveByEmailAddress c "nobody@example.com")
    Assert.True((User.authenticated missing "secret123456").IsNone)
