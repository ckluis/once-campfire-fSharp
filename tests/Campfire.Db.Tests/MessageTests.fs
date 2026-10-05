// Port of rust/crates/db/src/tests/message_test.rs
// `test/models/message_test.rb`, `message/searchable_test.rb`, `message/attachment_test.rb`
// (the parts that are data, not image processing), and pagination.
module Campfire.Db.Tests.MessageTests

open Xunit
open Campfire.Db
open Campfire.Db.Tests.Support

let private create (t: TestDb) (room: string) (creator: string) (body: string) (clientMessageId: string) : Message =
    let attributes =
        { NewMessage.create (id room) (id creator) with
            ClientMessageId = Some clientMessageId
            Body = Some body }
    t.Write(fun tx -> Message.create tx attributes)

let private search (t: TestDb) (room: string) (query: string) : int64 list =
    let roomId = id room
    t.Read(fun c -> Message.searchInRoom c roomId query) |> List.map (fun m -> m.Id)

[<Fact>]
let ``creating a message enqueues to push later`` () =
    use t = new TestDb()
    let message = create t "designers" "jason" "Hello" "123"
    let pushes = t.Events() |> List.filter (fun e -> match e with PushMessage _ -> true | _ -> false)
    Assert.Equal<Event list>([ PushMessage(id "designers", message.Id) ], pushes)

/// Which mentioned users are mentionees; reading the mentions is `Campfire.RichText`'s.
[<Fact>]
let ``mentionees`` () =
    use t = new TestDb()
    let pets = id "pets"
    let mentionees (userIds: int64 list) =
        t.Read(fun c -> Message.mentioneesInRoom c pets userIds) |> List.map (fun u -> u.Id)

    Assert.Equal<int64 list>([ id "david" ], mentionees [ id "david" ])
    Assert.Equal<int64 list>([ id "david" ], mentionees [ id "david"; id "david" ])
    // Kevin isn't in All Pets.
    Assert.True(List.isEmpty (mentionees [ id "kevin" ]))

[<Fact>]
let ``message body is indexed and searchable`` () =
    use t = new TestDb()
    let message = create t "designers" "david" "My hovercraft is full of eels" "earth"
    Assert.Equal<int64 list>([ message.Id ], search t "designers" "eel")

    let message = t.Write(fun tx -> Message.updateBody tx message "My hovercraft is full of sharks")
    Assert.Equal<int64 list>([ message.Id ], search t "designers" "sharks")

    t.Write(fun tx -> Message.destroy tx message)
    Assert.True(List.isEmpty (search t "designers" "sharks"))

[<Fact>]
let ``search words are never query syntax`` () =
    use t = new TestDb()
    let message = create t "designers" "jason" "Do NOT feed the eel OR the shark" "c1"
    for query in [ "NOT"; "OR"; "AND"; "NEAR"; "eel NOT"; "NOT eel"; "shark OR"; "\""; "*"; ""; "a\000b"; "\000" ] do
        search t "designers" query |> ignore
    Assert.Equal<int64 list>([ message.Id ], search t "designers" "NOT eel")
    Assert.True(List.isEmpty (search t "designers" "eel dolphin"))
    Assert.Equal<int64 list>([ message.Id ], search t "designers" "eel\000shark")

[<Fact>]
let ``search results are returned in message order`` () =
    use t = new TestDb()
    let ids =
        [ "first cat"; "second cat"; "third cat"; "cat cat cat" ]
        |> List.map (fun body ->
            t.Travel 1L
            (create t "designers" "david" body body).Id)
    Assert.Equal<int64 list>(ids, search t "designers" "cat")

[<Fact>]
let ``rich text body is converted to plain text for indexing`` () =
    use t = new TestDb()
    let message = create t "designers" "david" "<span>My hovercraft is full of eels</span>" "earth"
    Assert.True(List.isEmpty (search t "designers" "span"))
    Assert.Equal<int64 list>([ message.Id ], search t "designers" "eel")

[<Fact>]
let ``search reachable only finds messages in the users rooms`` () =
    use t = new TestDb()
    let message = create t "designers" "david" "hovercraft" "earth"
    let found = t.Read(fun c -> Message.searchReachable c (id "kevin") "hovercraft")
    Assert.Equal<int64 list>([ message.Id ], found |> List.map (fun m -> m.Id))
    Assert.True(List.isEmpty (t.Read(fun c -> Message.searchReachable c (id "bender") "hovercraft")))

[<Fact>]
let ``creating a blank message with attachment uses filename as plain text body`` () =
    use t = new TestDb()
    let message =
        t.Write(fun tx ->
            let blob =
                Blob.create
                    tx
                    { Id = 0L
                      Key = "abc123"
                      Filename = "moon.jpg"
                      ContentType = Some "image/jpeg"
                      Metadata = Some """{"identified":true}"""
                      ServiceName = "local"
                      ByteSize = 1L
                      Checksum = None
                      CreatedAt = tx.Now() }
            Message.create
                tx
                { NewMessage.create (id "hq") (id "david") with
                    ClientMessageId = Some "message"
                    AttachmentBlobId = Some blob.Id })
    let richText = BasicRichText() :> RichText
    Assert.Equal("moon.jpg", t.Read(fun c -> Message.plainTextBody c richText message))
    Assert.Equal(ContentType.Attachment, t.Read(fun c -> Message.contentType c richText message))
    Assert.Equal<int64 list>([ message.Id ], search t "hq" "moon")

    t.Write(fun tx -> Message.destroy tx message)
    Assert.True(t.Events() |> List.exists (fun e -> match e with PurgeBlob _ -> true | _ -> false))
    Assert.Equal(0L, t.Read(fun c -> c.Count("SELECT COUNT(*) FROM active_storage_attachments", [||])))

[<Fact>]
let ``sound messages`` () =
    use t = new TestDb()
    let richText = BasicRichText() :> RichText
    let message = create t "designers" "david" "/play trombone" "x"
    Assert.Equal(ContentType.Sound, t.Read(fun c -> Message.contentType c richText message))
    Assert.Equal("trombone", (t.Read(fun c -> Message.sound c richText message)).Value.Name)
    let message = create t "designers" "david" "/play nosuchsound" "y"
    Assert.Equal(ContentType.Text, t.Read(fun c -> Message.contentType c richText message))

/// `create_message` answers with what `Message.create` returns, without reading it back, so that
/// has to be the row as stored: after the commit's hooks, at the microseconds the columns keep.
[<Fact>]
let ``create returns the row as stored`` () =
    use t = new TestDb()
    let blobId (key: string) =
        let blob =
            { Id = 0L
              Key = key
              Filename = "moon.jpg"
              ContentType = Some "image/jpeg"
              Metadata = None
              ServiceName = "local"
              ByteSize = 1L
              Checksum = None
              CreatedAt = t.Now() }
        t.Write(fun tx -> (Blob.create tx blob).Id)
    let make (body: string option) (attachmentBlobId: int64 option) (clientMessageId: string option) : NewMessage =
        { NewMessage.create (id "designers") (id "david") with
            ClientMessageId = clientMessageId
            Body = body
            AttachmentBlobId = attachmentBlobId }
    let cases =
        [ make (Some "<p>Hello <strong>there</strong></p>") None (Some "text")
          make (Some "") None (Some "empty body")
          make None None (Some "nothing")
          make None (Some(blobId "attachment")) (Some "attachment")
          make (Some "With a picture") (Some(blobId "both")) (Some "both")
          make (Some "no client id") None None ]
    cases
    |> List.iteri (fun n attributes ->
        if n = 1 then
            // A frozen clock with nanoseconds, which the columns don't keep.
            t.Clock.TravelTo(System.DateTimeOffset.UnixEpoch.AddSeconds(1_900_000_000.0).AddTicks(1_234_567L))
        let created = t.Write(fun tx -> Message.create tx attributes)
        Assert.True((created = t.Read(fun c -> Message.find c created.Id)), $"case {n}")
    )

[<Fact>]
let ``client message id defaults to a uuid`` () =
    use t = new TestDb()
    let message =
        t.Write(fun tx -> Message.create tx { NewMessage.create (id "hq") (id "bender") with Body = Some "beep" })
    Assert.Equal(36, message.ClientMessageId.Length)

[<Fact>]
let ``pagination`` () =
    use t = new TestDb()
    let watercooler = id "watercooler"
    let all = t.Read(fun c -> Message.lastPage c watercooler)
    Assert.Equal(10, List.length all) // watercooler fixtures
    Assert.True(all |> List.pairwise |> List.forall (fun (a, b) -> a.CreatedAt <= b.CreatedAt))
    Assert.False(t.Read(fun c -> Message.paged c watercooler))

    let middle = all[5]
    let before = t.Read(fun c -> Message.pageBefore c watercooler middle)
    let after = t.Read(fun c -> Message.pageAfter c watercooler middle)
    Assert.Equal<Message list>(List.take 5 all, before)
    Assert.Equal<Message list>(List.skip 6 all, after)
    let around = t.Read(fun c -> Message.pageAround c watercooler middle)
    Assert.Equal<Message list>(all, around)
    Assert.True(t.Read(fun c -> Message.existsBefore c watercooler middle))
    Assert.False(t.Read(fun c -> Message.existsAfter c watercooler all[9]))

    for n in 0L .. Message.PageSize - 1L do
        t.Travel 1L
        create t "watercooler" "david" $"message {n}" $"c{n}" |> ignore
    Assert.True(t.Read(fun c -> Message.paged c watercooler))
    let last = t.Read(fun c -> Message.lastPage c watercooler)
    Assert.Equal(Message.PageSize, int64 (List.length last))
    Assert.Equal(all[0], (t.Read(fun c -> Message.firstPage c watercooler))[0])

    let since = all[9].CreatedAt
    let created = t.Read(fun c -> Message.pageCreatedSince c watercooler since)
    Assert.Equal(Message.PageSize, int64 (List.length created))
    let createdIds = created |> List.map (fun m -> m.Id)
    let updated = t.Read(fun c -> Message.pageUpdatedSince c watercooler since createdIds)
    Assert.True(updated |> List.forall (fun m -> not (List.contains m.Id createdIds)))
