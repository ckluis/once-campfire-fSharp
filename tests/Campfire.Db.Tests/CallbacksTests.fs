// Port of rust/crates/db/src/tests/callbacks_test.rs
// Callback side effects, checked against what the reference app's SQL log shows for the
// same operations (see the crate report for the scenarios).
module Campfire.Db.Tests.CallbacksTests

open System
open System.Threading.Tasks
open Xunit
open Campfire.Db
open Campfire.Db.Tests.Support

let private ftsBody (t: TestDb) (messageId: int64) : string option =
    t.Read(fun c -> c.QueryOne("SELECT body FROM message_search_index WHERE rowid = ?", [| I messageId |], fun r -> r.Text 0))

[<Fact>]
let ``creating a message touches the room marks disconnected members unread and indexes after commit`` () =
    use t = new TestDb()
    t.Write(fun tx -> Membership.find tx.Conn (id "jz_designers") |> Membership.connected tx |> ignore)
    let roomBefore = t.Read(fun c -> Room.find c (id "designers"))
    t.Travel 1L

    let attributes =
        { NewMessage.create (id "designers") (id "david") with
            ClientMessageId = Some "abc"
            Body = Some "Hello <b>there</b>" }
    let message = t.Write(fun tx -> Message.create tx attributes)

    // The body touches the message; the room is touched too.
    Assert.True(message.UpdatedAt >= message.CreatedAt)
    Assert.True((t.Read(fun c -> Room.find c (id "designers"))).UpdatedAt > roomBefore.UpdatedAt)

    // Unread: visible, disconnected, not the creator; unread_at is the message's created_at.
    let unread (label: string) = (t.Read(fun c -> Membership.find c (id label))).UnreadAt
    Assert.Equal(Some message.CreatedAt, unread "jason_designers")
    Assert.Equal(Some message.CreatedAt, unread "kevin_designers")
    Assert.Equal(None, unread "jz_designers") // connected
    Assert.Equal(None, unread "david_designers") // creator

    Assert.Equal(Some "Hello there", ftsBody t message.Id)
    Assert.Equal<Event list>([ PushMessage(id "designers", message.Id) ], t.Events())

    let body =
        t.Read(fun c ->
            c.QueryRow(
                "SELECT body, record_type, record_id FROM action_text_rich_texts WHERE record_id = ?",
                [| I message.Id |],
                fun r -> r.Text 0, r.Text 1, r.Int64 2
            ))
    Assert.Equal(("Hello <b>there</b>", "Message", message.Id), body)

[<Fact>]
let ``invisible members are not marked unread`` () =
    use t = new TestDb()
    t.Write(fun tx -> Membership.find tx.Conn (id "kevin_designers") |> (fun m -> Membership.updateInvolvement tx m (Some Invisible)) |> ignore)
    let attributes = { NewMessage.create (id "designers") (id "david") with Body = Some "x" }
    t.Write(fun tx -> Message.create tx attributes |> ignore)
    Assert.Equal(None, (t.Read(fun c -> Membership.find c (id "kevin_designers"))).UnreadAt)

[<Fact>]
let ``rolled back writes emit nothing after commit`` () =
    use t = new TestDb()
    let attributes = { NewMessage.create (id "designers") (id "david") with Body = Some "x" }
    let result =
        t.TryWrite(fun tx ->
            Message.create tx attributes |> ignore
            Err.fail (DbError.other "boom"))
    Assert.True(Result.isError result)
    Assert.True(List.isEmpty (t.Events()))
    Assert.Equal(13L, t.Read Message.count)

[<Fact>]
let ``boosting touches the message and room and reindexes`` () =
    use t = new TestDb()
    let first = t.Read(fun c -> Message.find c (id "first"))
    let roomBefore = t.Read(fun c -> Room.find c (id "designers"))
    t.Travel 1L
    let boost = t.Write(fun tx -> Boost.create tx (id "first") (id "jason") "hi")
    let touched = t.Read(fun c -> Message.find c (id "first"))
    Assert.True(touched.UpdatedAt > first.UpdatedAt)
    Assert.True((t.Read(fun c -> Room.find c (id "designers"))).UpdatedAt > roomBefore.UpdatedAt)
    // The fixture message was never indexed; the after_commit update finds no row, as in Rails.
    Assert.Equal(None, ftsBody t first.Id)

    t.Travel 1L
    t.Write(fun tx -> Boost.destroy tx boost)
    Assert.True((t.Read(fun c -> Message.find c (id "first"))).UpdatedAt > touched.UpdatedAt)
    Assert.Equal(1, List.length (t.Read(fun c -> Boost.forMessage c (id "first"))))

[<Fact>]
let ``boosts are ordered by creation`` () =
    use t = new TestDb()
    t.Travel 1L
    t.Write(fun tx -> Boost.create tx (id "first") (id "jason") "2" |> ignore)
    let contents = t.Read(fun c -> Message.boosts c (Message.find c (id "first"))) |> List.map (fun b -> b.Content)
    Assert.Equal<string list>([ "Hello"; "2" ], contents)

[<Fact>]
let ``session start and resume`` () =
    use t = new TestDb()
    let david = id "david"
    let session = t.Write(fun tx -> Session.start tx david (Some "ua") (Some "1.2.3.4"))
    Assert.Equal(24, session.Token.Length)
    Assert.True(session.Token |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c && not ("0OIl".Contains c)))
    Assert.Equal(session.Id, (t.Read(fun c -> Session.findByToken c session.Token)).Value.Id)

    // Within the hour: nothing is written.
    let resumed = t.Write(fun tx -> Session.resume tx session (Some "ua2") (Some "9.9.9.9"))
    Assert.Equal(Some "ua", (t.Read(fun c -> Session.find c session.Id)).UserAgent)

    t.Clock.Travel(TimeSpan.FromHours 1.0 + TimeSpan.FromSeconds 1.0)
    t.Write(fun tx -> Session.resume tx resumed (Some "ua2") (Some "9.9.9.9") |> ignore)
    let reloaded = t.Read(fun c -> Session.find c session.Id)
    Assert.Equal((Some "ua2", Some "9.9.9.9"), (reloaded.UserAgent, reloaded.IpAddress))
    Assert.True(reloaded.LastActiveAt > session.LastActiveAt)

[<Fact>]
let ``fixture session resumes because it is two hours old`` () =
    use t = new TestDb()
    let session = t.Read(fun c -> Session.find c (id "david_safari"))
    t.Write(fun tx -> Session.resume tx session (Some "ua") (Some "9.9.9.9") |> ignore)
    Assert.Equal(Some "9.9.9.9", (t.Read(fun c -> Session.find c (id "david_safari"))).IpAddress)

[<Fact>]
let ``recording searches keeps the ten most recent`` () =
    use t = new TestDb()
    let david = id "david"
    for n in 0..11 do
        t.Travel 1L
        t.Write(fun tx -> Search.record tx david $"query {n}" |> ignore)
    let searches = t.Read(fun c -> Search.orderedForUser c david)
    // The 12th creation trims to ten before its touch.
    Assert.Equal(10, List.length searches)
    Assert.Equal("query 11", searches[0].Query)
    Assert.False(searches |> List.exists (fun s -> s.Query = "pizza"))

    t.Travel 1L
    t.Write(fun tx -> Search.record tx david "query 5" |> ignore)
    Assert.Equal("query 5", (List.head (t.Read(fun c -> Search.orderedForUser c david))).Query)

[<Fact>]
let ``timestamps are written like active record`` () =
    use t = new TestDb()
    t.Clock.TravelTo((Timestamp.ParseDb "2026-09-26 12:34:56.123456").Value.ToDateTimeOffset())
    let attributes = { NewMessage.create (id "hq") (id "david") with Body = Some "x" }
    let message = t.Write(fun tx -> Message.create tx attributes)
    let createdAt, typeofCreated =
        t.Read(fun c ->
            c.QueryRow("SELECT created_at, typeof(created_at) FROM messages WHERE id = ?", [| I message.Id |], fun r -> r.Text 0, r.Text 1))
    Assert.Equal(("2026-09-26 12:34:56.123456", "text"), (createdAt, typeofCreated))

    t.Clock.TravelTo((Timestamp.ParseDb "2026-09-26 12:00:00").Value.ToDateTimeOffset())
    let attributes = { NewMessage.create (id "hq") (id "david") with Body = Some "y" }
    let message = t.Write(fun tx -> Message.create tx attributes)
    let createdAt = t.Read(fun c -> c.QueryRow("SELECT created_at FROM messages WHERE id = ?", [| I message.Id |], fun r -> r.Text 0))
    Assert.Equal("2026-09-26 12:00:00", createdAt) // no fraction when microseconds are zero

[<Fact>]
let ``async writes and reads`` () =
    task {
        use t = new TestDb()
        let writes =
            [ for n in 0..19 ->
                  t.Db.Write(fun tx -> Search.record tx (id "jason") $"q{n}" |> ignore) ]
        let! results = Task.WhenAll writes
        for result in results do
            unwrap result
        let! count = t.Db.Read(fun c -> Search.countForUser c (id "jason"))
        Assert.Equal(10L, unwrap count)
    }
