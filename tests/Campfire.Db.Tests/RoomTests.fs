// Port of rust/crates/db/src/tests/room_test.rs
// `test/models/room_test.rb`, `rooms/direct_test.rb`, `rooms/open_test.rb`
module Campfire.Db.Tests.RoomTests

open Xunit
open Campfire.Db
open Campfire.Db.Tests.Support

let private memberIds (t: TestDb) (roomId: int64) : int64 list = t.Read(fun c -> Room.userIds c (Room.find c roomId))

let private room (t: TestDb) (label: string) : Room =
    let roomId = id label
    t.Read(fun c -> Room.find c roomId)

[<Fact>]
let ``grant membership to user`` () =
    use t = new TestDb()
    let watercooler = room t "watercooler"
    t.Write(fun tx -> Room.grantTo tx watercooler [ id "kevin" ])
    Assert.Contains(id "kevin", memberIds t (id "watercooler"))

[<Fact>]
let ``revoke membership from user`` () =
    use t = new TestDb()
    let watercooler = room t "watercooler"
    t.Write(fun tx -> Room.revokeFrom tx watercooler [ id "david" ])
    Assert.DoesNotContain(id "david", memberIds t (id "watercooler"))
    Assert.Equal<Event list>([ DisconnectUser(id "david", true) ], t.Events())

[<Fact>]
let ``revise memberships`` () =
    use t = new TestDb()
    let watercooler = room t "watercooler"
    t.Write(fun tx -> Room.revise tx watercooler [ id "kevin" ] [ id "david" ])
    let members = memberIds t (id "watercooler")
    Assert.Contains(id "kevin", members)
    Assert.DoesNotContain(id "david", members)

[<Fact>]
let ``create for users by giving them immediate membership`` () =
    use t = new TestDb()
    let room = t.Write(fun tx -> Room.createFor tx Closed (Some "Hello!") (id "david") [ id "kevin"; id "david" ])
    let members = memberIds t room.Id
    Assert.True(List.contains (id "kevin") members && List.contains (id "david") members)

[<Fact>]
let ``type predicates`` () =
    use t = new TestDb()
    Assert.True(Room.isOpen (room t "pets"))
    Assert.False(Room.isDirect (room t "pets"))
    Assert.True(Room.isDirect (room t "david_and_jason"))
    Assert.True(Room.isClosed (room t "designers"))

[<Fact>]
let ``default involvement for new users`` () =
    use t = new TestDb()
    let room = t.Write(fun tx -> Room.createFor tx Closed (Some "Hello!") (id "david") [ id "kevin"; id "david" ])
    let memberships = t.Read(fun c -> Room.memberships c room)
    Assert.Equal(2, List.length memberships)
    Assert.True(memberships |> List.forall (fun m -> Membership.involvedIn m Mentions))

[<Fact>]
let ``granted memberships are stamped by sqlite`` () =
    // insert_all lets SQLite fill the timestamps: STRFTIME('%Y-%m-%d %H:%M:%f', 'NOW').
    use t = new TestDb()
    let room = t.Write(fun tx -> Room.createFor tx Closed (Some "Hello!") (id "david") [ id "kevin" ])
    let createdAt =
        t.Read(fun c -> c.QueryRow("SELECT created_at FROM memberships WHERE room_id = ?", [| I room.Id |], fun r -> r.Text 0))
    Assert.True((createdAt.Length = "2026-09-26 12:25:26.826".Length), createdAt)

[<Fact>]
let ``direct rooms keep their type`` () =
    use t = new TestDb()
    let direct = room t "david_and_jason"
    match t.TryWrite(fun tx -> Room.update tx direct None (Some Open)) with
    | Error(RecordInvalid errors) -> Assert.Equal<string list>([ "can't be changed for a direct room" ], Errors.on "type" errors)
    | other -> failwith $"expected invalid, got {other}"

[<Fact>]
let ``destroying a room destroys its messages and memberships`` () =
    use t = new TestDb()
    let watercooler = room t "watercooler"
    t.Write(fun tx -> Room.destroy tx watercooler)
    Assert.True(List.isEmpty (t.Read(fun c -> Message.forRoom c (id "watercooler"))))
    Assert.True(List.isEmpty (t.Read(fun c -> Membership.forRoom c (id "watercooler"))))
    Assert.True((t.Read(fun c -> Room.findById c (id "watercooler"))).IsNone)
    Assert.Equal(
        0L,
        t.Read(fun c -> c.Count("SELECT COUNT(*) FROM boosts WHERE message_id IN (?, ?)", [| I(id "thirteenth"); I(id "fourth") |]))
    )

// Rooms::Direct

[<Fact>]
let ``create direct room for same users`` () =
    use t = new TestDb()
    let room = t.Write(fun tx -> Room.findOrCreateDirectFor tx [ id "jz"; id "kevin" ] (id "jz"))
    let members = memberIds t room.Id
    Assert.True(List.contains (id "jz") members && List.contains (id "kevin") members)
    Assert.DoesNotContain(id "jason", members)

[<Fact>]
let ``only one direct room will exist for the same users`` () =
    use t = new TestDb()
    let room1 = t.Write(fun tx -> Room.findOrCreateDirectFor tx [ id "jz"; id "kevin" ] (id "jz"))
    let room2 = t.Write(fun tx -> Room.findOrCreateDirectFor tx [ id "kevin"; id "jz" ] (id "kevin"))
    Assert.Equal(room1.Id, room2.Id)

    let existing = t.Write(fun tx -> Room.findOrCreateDirectFor tx [ id "david"; id "kevin" ] (id "david"))
    Assert.Equal(id "david_and_kevin", existing.Id)

[<Fact>]
let ``direct default involvement for new users`` () =
    use t = new TestDb()
    let room = t.Write(fun tx -> Room.findOrCreateDirectFor tx [ id "jz"; id "kevin" ] (id "jz"))
    Assert.True(t.Read(fun c -> Room.memberships c room) |> List.forall (fun m -> Membership.involvedIn m Everything))

// Rooms::Open

[<Fact>]
let ``open room grants access to all users after creation`` () =
    use t = new TestDb()
    let room = t.Write(fun tx -> Room.create tx Open (Some "My open room with everyone!") (id "david"))
    Assert.Equal(t.Read User.count, int64 (List.length (memberIds t room.Id)))

[<Fact>]
let ``open room grants access to all users after becoming open`` () =
    use t = new TestDb()
    let watercooler = room t "watercooler"
    t.Write(fun tx -> Room.update tx watercooler None (Some Open) |> ignore)
    Assert.Equal(t.Read User.count, int64 (List.length (memberIds t (id "watercooler"))))
    Assert.Equal(Open, (room t "watercooler").RoomType)
    let stored = t.Read(fun c -> c.QueryRow("SELECT type FROM rooms WHERE id = ?", [| I(id "watercooler") |], fun r -> r.Text 0))
    Assert.Equal("Rooms::Open", stored)

[<Fact>]
let ``user room scopes`` () =
    use t = new TestDb()
    let david = id "david"
    Assert.Equal(2, List.length (t.Read(fun c -> Room.forUserOfType c david Direct)))
    Assert.Equal(4, List.length (t.Read(fun c -> Room.forUserWithoutDirects c david)))
    Assert.True((t.Read(fun c -> Room.findForUser c (id "kevin") (id "pets"))).IsNone)
    let ordered = t.Read(fun c -> Membership.visibleWithOrderedRoom c david)
    let names = ordered |> List.map (fun (_, r) -> r.Name)
    Assert.Equal<string option list>([ Some "All Pets"; Some "All Talk"; Some "Designers"; Some "HQ" ], List.skip (List.length names - 4) names)
