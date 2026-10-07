// Port of rust/crates/db/src/tests/membership_test.rs
// `test/models/membership_test.rb`
module Campfire.Db.Tests.MembershipTests

open Xunit
open Campfire.Db
open Campfire.Db.Tests.Support

let private ttlPlusOne = 61L

let private membership (t: TestDb) : Membership = t.Read(fun c -> Membership.find c (id "david_watercooler"))

/// Runs a Connectable method and returns the updated in-memory membership.
let private run (t: TestDb) (membership: Membership) (f: Tx -> Membership -> Membership) : Membership =
    t.Write(fun tx -> f tx membership)

let private connected (t: TestDb) (m: Membership) : Membership = run t m Membership.connected

let private disconnected (t: TestDb) (m: Membership) : Membership = run t m Membership.disconnected

let private connectedExists (t: TestDb) (m: Membership) : bool =
    let now = t.Now()
    t.Read(fun c -> Membership.connectedExists c m.Id now)

let private disconnectedExists (t: TestDb) (m: Membership) : bool =
    let now = t.Now()
    t.Read(fun c -> Membership.disconnectedExists c m.Id now)

[<Fact>]
let ``connected scope`` () =
    use t = new TestDb()
    let m = connected t (membership t)
    Assert.True(connectedExists t m)

    let m = disconnected t m
    Assert.False(connectedExists t m)

    t.Travel ttlPlusOne
    Assert.False(connectedExists t m)

[<Fact>]
let ``disconnected scope`` () =
    use t = new TestDb()
    let m = disconnected t (membership t)
    Assert.True(disconnectedExists t m)

    let m = connected t m
    Assert.False(disconnectedExists t m)

    t.Travel ttlPlusOne
    Assert.True(disconnectedExists t m)

[<Fact>]
let ``connected is false when connection is stale`` () =
    use t = new TestDb()
    let m = connected t (membership t)
    t.Travel ttlPlusOne
    Assert.False(Membership.isConnected m (t.Now()))

[<Fact>]
let ``connecting`` () =
    use t = new TestDb()
    let m = connected t (membership t)
    Assert.True(Membership.isConnected m (t.Now()))
    Assert.Equal(1L, m.Connections)

    let m = connected t m
    Assert.Equal(2L, m.Connections)
    Assert.Equal(2L, (t.Read(fun c -> Membership.find c m.Id)).Connections)

[<Fact>]
let ``connecting resets stale connection count`` () =
    use t = new TestDb()
    let m = connected t (connected t (membership t))
    Assert.Equal(2L, m.Connections)

    t.Travel ttlPlusOne
    let m = connected t m
    Assert.Equal(1L, m.Connections)

[<Fact>]
let ``disconnecting`` () =
    use t = new TestDb()
    let m = connected t (connected t (membership t))

    let m = disconnected t m
    Assert.True(Membership.isConnected m (t.Now()))
    Assert.Equal(1L, m.Connections)

    let m = disconnected t m
    Assert.False(Membership.isConnected m (t.Now()))
    Assert.Equal(0L, m.Connections)
    Assert.Equal(None, (t.Read(fun c -> Membership.find c m.Id)).ConnectedAt)

[<Fact>]
let ``disconnecting resets stale connection count`` () =
    use t = new TestDb()
    let m = connected t (connected t (membership t))
    Assert.Equal(2L, m.Connections)

    t.Travel ttlPlusOne
    let m = disconnected t m
    Assert.Equal(0L, m.Connections)

[<Fact>]
let ``refreshing the connection`` () =
    use t = new TestDb()
    let m = connected t (membership t)

    t.Travel ttlPlusOne
    Assert.False(Membership.isConnected m (t.Now()))

    let m = run t m Membership.refreshConnection
    Assert.True(Membership.isConnected m (t.Now()))

[<Fact>]
let ``present marks read and counts connections`` () =
    use t = new TestDb()
    let m =
        t.Write(fun tx ->
            tx.Conn.Execute("UPDATE memberships SET unread_at = '2026-01-01 00:00:00' WHERE id = ?", [| I(id "david_watercooler") |]) |> ignore
            let m = Membership.find tx.Conn (id "david_watercooler")
            Membership.present tx m
            m)
    let reloaded = t.Read(fun c -> Membership.find c m.Id)
    Assert.Equal(1L, reloaded.Connections)
    Assert.Equal(None, reloaded.UnreadAt)
    Assert.Equal(m.UpdatedAt, reloaded.UpdatedAt) // Membership.connect doesn't touch updated_at

[<Fact>]
let ``disconnect all resets connected memberships`` () =
    use t = new TestDb()
    let m = connected t (membership t)
    t.Write(fun tx -> Membership.disconnectAll tx |> ignore)
    let reloaded = t.Read(fun c -> Membership.find c m.Id)
    Assert.Equal((0L, None), (reloaded.Connections, reloaded.ConnectedAt))

[<Fact>]
let ``removing a membership resets the users connections`` () =
    use t = new TestDb()
    let m = membership t
    t.Write(fun tx -> Membership.destroy tx m)
    Assert.Equal<Event list>([ DisconnectUser(id "david", true) ], t.Events())

[<Fact>]
let ``reading is a noop when already read`` () =
    use t = new TestDb()
    let m = membership t
    let before = m.UpdatedAt
    let m = run t m Membership.read
    Assert.Equal(before, (t.Read(fun c -> Membership.find c m.Id)).UpdatedAt)
