// Port of rust/crates/campfire/src/controllers/messages/tests.rs
//
// Request-level tests for the message, boost and bot API controllers, against the `default` parity seed.
module Campfire.App.Tests.MessagesTests

open System
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open Campfire.App.Tests.Support
open Campfire.Db

let private PNG: byte[] =
    [| 137; 80; 78; 71; 13; 10; 26; 10; 0; 0; 0; 13; 73; 72; 68; 82; 0; 0; 0; 4; 0; 0; 0; 3; 8; 2; 0; 0; 0; 59; 150; 57; 145; 0; 0; 0; 16; 73
       68; 65; 84; 120; 156; 99; 248; 207; 192; 0; 71; 12; 56; 57; 0; 245; 49; 11; 245; 53; 123; 251; 130; 0; 0; 0; 0; 73; 69; 78; 68; 174
       66; 96; 130 |]
    |> Array.map byte

let private TURBO_STREAM_ACCEPT = "text/vnd.turbo-stream.html, text/html, application/xhtml+xml"

let private unwrap (result: Result<'T, DbError>) : 'T =
    match result with
    | Ok value -> value
    | Error e -> failwith (DbError.display e)

let private read (test: Test) (f: Conn -> 'T) : Task<'T> =
    task {
        let! result = test.App.Db.Read f
        return unwrap result
    }

let private replyMarker = "data-controller=\"reply\""

let private count (haystack: string) (needle: string) : int =
    let mutable n = 0
    let mutable at = haystack.IndexOf(needle, StringComparison.Ordinal)
    while at >= 0 do
        n <- n + 1
        at <- haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal)
    n

let private messagesIn (test: Test) (roomId: int64) : Task<Message list> =
    read test (fun conn -> Message.forRoom conn roomId |> List.sortBy (fun m -> m.CreatedAt.Microsecond, m.Id))

let private lastMessage (test: Test) (roomId: int64) : Task<Message> =
    task {
        let! messages = messagesIn test roomId
        return List.last messages
    }

[<Fact>]
let ``index pages with conditional gets`` () =
    task {
        use! test = bootSeeded "default"
        let! messages = messagesIn test ALL_TALK
        let david = david test

        let! reply = david.Get $"/rooms/{ALL_TALK}/messages?before={messages[50].Id}"
        Assert.True((reply.Status = 200), reply.Text)
        Assert.Equal(40, count reply.Text replyMarker)
        Assert.DoesNotContain("<html", reply.Text)
        let etag = (reply.Header "etag").Value
        Assert.StartsWith("W/\"", etag)
        Assert.True((reply.Header "last-modified").IsSome)

        let! cached =
            david.Send((get $"/rooms/{ALL_TALK}/messages?before={messages[50].Id}").With("if-none-match", etag))
        Assert.Equal(304, cached.Status)

        let! afterLast = david.Get $"/rooms/{ALL_TALK}/messages?after={(List.last messages).Id}"
        Assert.Equal(204, afterLast.Status)
        let! zero = david.Get $"/rooms/{ALL_TALK}/messages?before=0"
        Assert.Equal(404, zero.Status)
        let! other = david.Get $"/rooms/{DIRECT_KEVIN_BENDER}/messages"
        Assert.Equal(404, other.Status)
        let! bare = david.Get "/messages"
        Assert.Equal(404, bare.Status)
    }

[<Fact>]
let ``create appends the message as a turbo stream`` () =
    task {
        use! test = bootSeeded "default"
        let david = david test
        let! reply =
            david.Write(
                ((request "POST" $"/rooms/{ALL_TALK}/messages").With("accept", TURBO_STREAM_ACCEPT))
                    .Form(formBody [ "message[body]", "<p>Hello <strong>there</strong></p>"; "message[client_message_id]", "abc-123" ])
            )
        Assert.True((reply.Status = 200), reply.Text)
        Assert.Equal(Some "text/vnd.turbo-stream.html; charset=utf-8", reply.ContentType)
        Assert.Contains("""<turbo-stream action="append" target="messages_rooms_closed_486777696">""", reply.Text)
        Assert.Contains("id=\"message_abc-123\"", reply.Text)
        Assert.Contains("Hello <strong>there</strong>", reply.Text)

        let! message = lastMessage test ALL_TALK
        Assert.Equal(("abc-123", DAVID), (message.ClientMessageId, message.CreatorId))
    }

/// The message in a turbo stream: what's inside its `<template>`.
let private template (stream: string) : string =
    let start = stream.IndexOf "<template>" + "<template>".Length
    stream.Substring(start, stream.LastIndexOf "</template>" - start)

/// A text message answers and broadcasts the row `Message.create` returned, not one read back
/// from the database: it has to render as the stored row does. The room page, which reads the
/// message from the database, shows the same fragment (cached under the stored `updated_at`).
///
/// Rust subscribes through a WebSocket to `RoomMessagesChannel`, which isn't ported yet (the channels unit
/// follows): this reads the same broadcast off the cable's hub, where that channel would have read it.
[<Fact>]
let ``a text message is answered and broadcast as it was stored`` () =
    task {
        use! test = bootSeeded "default"
        let! room = read test (fun conn -> Room.find conn ALL_TALK)
        let streamables = [ Campfire.RailsCompat.GlobalId.toParam (Campfire.App.Channels.Gid.roomGid room); "messages" ]
        let wake = Campfire.Cable.Wake()
        use subscriber = test.App.Cable.Hub.Subscribe(Campfire.Cable.Naming.streamNameFrom streamables, null, wake)

        let david = david test
        let! reply =
            david.Write(
                ((request "POST" $"/rooms/{ALL_TALK}/messages").With("accept", TURBO_STREAM_ACCEPT))
                    .Form(formBody [ "message[body]", "<p>Straight from the <em>writer</em></p>"; "message[client_message_id]", "as-stored" ])
            )
        Assert.True((reply.Status = 200), reply.Text)
        let mutable frame = Unchecked.defaultof<Campfire.Cable.Socket.Frame>
        Assert.Equal(Campfire.Cable.RecvStatus.Got, subscriber.TryRecv(&frame))
        // The broadcast's payload is the `<turbo-stream>` tag as a JSON string.
        let broadcast = JsonSerializer.Deserialize<string>(frame.Text) |> nonNull

        let! stored = lastMessage test ALL_TALK
        Assert.Equal(("as-stored", DAVID), (stored.ClientMessageId, stored.CreatorId))
        let response = reply.Text
        let message = template response
        Assert.Equal(message, template broadcast)
        Assert.StartsWith("""<turbo-stream action="append" target="messages_rooms_closed_486777696"><template>""", broadcast)
        Assert.Contains("id=\"message_as-stored\"", message)
        Assert.Contains($"data-message-id=\"{stored.Id}\"", message)
        let epochMs = Campfire.Views.MessagesSupport.epochMs
        Assert.Contains($"data-message-timestamp=\"{epochMs (stored.CreatedAt.ToDateTimeOffset())}\"", message)
        Assert.Contains($"data-message-updated-at=\"{epochMs (stored.UpdatedAt.ToDateTimeOffset())}\"", message)
        // Cached under the stored row's key (its `updated_at` to the microsecond), which is what the
        // room page, reading the message from the database, looks up.
        let cached =
            Campfire.Views.FragmentCache.withCache test.App.FragmentCache (fun () ->
                Campfire.Views.Messages.cachedMessageFragment stored.Id (stored.UpdatedAt.ToDateTimeOffset()))
        Assert.Equal(message, (if isNull cached then "" else cached.ToString()))
        let! page = david.Get $"/rooms/{ALL_TALK}"
        Assert.Contains(message, page.Text) // the room page's copy of the message
    }

[<Fact>]
let ``create in a room you left renders room not found`` () =
    task {
        use! test = bootSeeded "default"
        let david = david test
        let! reply =
            david.Write((request "POST" $"/rooms/{DIRECT_KEVIN_BENDER}/messages").Form(formBody [ "message[body]", "hi" ]))
        Assert.Equal(200, reply.Status)
        Assert.Contains("This room was deleted.", reply.Text)
        Assert.Contains("<html", reply.Text) // in the application layout

        let! missing = david.Write((request "POST" $"/rooms/{ALL_TALK}/messages").Form(formBody [ "body", "hi" ]))
        Assert.Equal(400, missing.Status)
        let! html = david.Write((request "POST" $"/rooms/{ALL_TALK}/messages").Form(formBody [ "message[body]", "hi" ]))
        Assert.True((html.Status = 406), "only a turbo stream template")
    }

[<Fact>]
let ``uploads attach and process the file`` () =
    task {
        use! test = bootSeeded "default"
        let david = david test
        let! reply =
            david.Write(
                multipartReq
                    ((request "POST" $"/rooms/{ALL_TALK}/messages").With("accept", "*/*"))
                    [ "message[client_message_id]", "upload-1" ]
                    ("message[attachment]", "red.png", "image/png", PNG)
            )
        Assert.True((reply.Status = 200), reply.Text)
        Assert.True(reply.Text.Contains "/rails/active_storage/representations/redirect/", reply.Text)
        Assert.True(reply.Text.Contains "width=\"4\" height=\"3\"", $"analyzed dimensions: {reply.Text}")

        let! message = lastMessage test ALL_TALK
        Assert.Equal("upload-1", message.ClientMessageId)
        let! blob = read test (fun conn -> Message.attachment conn message |> Option.get |> snd)
        Assert.Equal("red.png", blob.Filename)
        let! variants =
            read test (fun conn ->
                conn.Count("SELECT count(*) FROM active_storage_variant_records WHERE blob_id = ?", [| I blob.Id |]))
        Assert.True((variants = 1L), "the :thumb variant is processed")
    }

/// Rails raises reading this body's plain text (`ArgumentError: invalid base64`), after the
/// create commits; here it's saved with no plain text, or its attachment's filename (see "Known
/// differences" in the README).
let private MENTION_WITH_A_BAD_SGID =
    """<p>Hey <action-text-attachment sgid="!!!" content-type="application/vnd.campfire.mention"></action-text-attachment></p>"""

let private UNRENDERABLE = "Failed to load message content"

/// What search, a push and a bot's webhook get for a message.
let private plainTexts (test: Test) (message: Message) : Task<string * string * string> =
    read test (fun conn ->
        let richText = test.App.Db.Env.RichText
        let indexed = conn.QueryOne("SELECT body FROM message_search_index WHERE rowid = ?", [| I message.Id |], (fun r -> r.Text 0)).Value
        let push, _, _ = PushSubscription.pushesFor conn richText message (test.App.Db.Env.Now())
        let webhook =
            (Webhook.findByUser conn BENDER).Value |> fun hook -> Webhook.payload conn richText hook message "/bot" "/message"
        let json = JsonDocument.Parse webhook
        indexed, push.Body, str (json.RootElement.GetProperty("message").GetProperty("body").GetProperty "plain"))

[<Fact>]
let ``a message whose plain text raises goes out without one`` () =
    task {
        use! test = bootSeeded "default"
        let david = david test
        let room = $"/rooms/{ALL_TALK}"
        let! before = david.Get room
        let unrenderableBefore = count before.Text UNRENDERABLE

        let! reply =
            david.Write(
                ((request "POST" $"/rooms/{ALL_TALK}/messages").With("accept", TURBO_STREAM_ACCEPT))
                    .Form(formBody [ "message[body]", MENTION_WITH_A_BAD_SGID; "message[client_message_id]", "bad-sgid" ])
            )
        Assert.True((reply.Status = 200), reply.Text)
        Assert.True(reply.Text.Contains UNRENDERABLE, reply.Text)
        let! message = lastMessage test ALL_TALK
        Assert.Equal("bad-sgid", message.ClientMessageId)
        let! texts = plainTexts test message
        Assert.Equal(("", "David: ", ""), texts)

        let! page = david.Get room
        Assert.Equal(200, page.Status)
        Assert.True((count page.Text UNRENDERABLE = unrenderableBefore + 1), page.Text)
        let! shown = david.Get $"{room}/messages/{message.Id}"
        Assert.Equal(200, shown.Status)
        Assert.True(shown.Text.Contains UNRENDERABLE, shown.Text)

        let! reply =
            david.Write(
                multipartReq
                    ((request "POST" $"/rooms/{ALL_TALK}/messages").With("accept", TURBO_STREAM_ACCEPT))
                    [ "message[body]", MENTION_WITH_A_BAD_SGID; "message[client_message_id]", "bad-sgid-upload" ]
                    ("message[attachment]", "red.png", "image/png", PNG)
            )
        Assert.True((reply.Status = 200), reply.Text)
        let! message = lastMessage test ALL_TALK
        Assert.Equal("bad-sgid-upload", message.ClientMessageId)
        let! texts = plainTexts test message
        Assert.Equal(("red.png", "David: red.png", "red.png"), texts)
    }

[<Fact>]
let ``show edit update and destroy`` () =
    task {
        use! test = bootSeeded "default"
        let! messages = messagesIn test ALL_TALK
        let message = messages |> List.rev |> List.find (fun m -> m.CreatorId = DAVID)
        let david = david test
        let path = $"/rooms/{ALL_TALK}/messages/{message.Id}"

        let! shown = david.Get path
        Assert.Equal(200, shown.Status)
        Assert.Contains("<html", shown.Text)
        let! framed = david.Send((get path).With("turbo-frame", "message_x"))
        // MessagesController declares its own layout, so Turbo-Frame requests get the application
        // layout too (not turbo-rails' frame layout).
        Assert.True(framed.Text.StartsWith "<!DOCTYPE html>", framed.Text)

        let! edit = david.Get $"{path}/edit"
        Assert.Equal(200, edit.Status)
        Assert.Contains("<lexxy-editor", edit.Text)

        let! updated = david.Write((request "PATCH" path).Form(formBody [ "message[body]", "<p>Edited</p>" ]))
        Assert.True((updated.Status = 302), updated.Text)
        Assert.Equal(Some $"http://campfire.test{path}", updated.Location)
        let! body = read test (fun conn -> Message.bodyHtml conn (Message.find conn message.Id))
        Assert.Equal(Some "<p>Edited</p>", body)

        let! json = david.Write((request "PATCH" $"{path}.json").Form(formBody [ "message[body]", "x" ]))
        Assert.True((json.Status = 500), "no messages/show.json")

        let! destroyed = david.Write((request "DELETE" path).With("accept", TURBO_STREAM_ACCEPT))
        Assert.Equal(200, destroyed.Status)
        Assert.Equal(
            $"""<turbo-stream action="remove" target="message_{message.ClientMessageId}"></turbo-stream>""",
            destroyed.Text.Trim()
        )
        let! gone = read test (fun conn -> Message.findById conn message.Id)
        Assert.True gone.IsNone
        let! missing = david.Get path
        Assert.Equal(404, missing.Status)
    }

[<Fact>]
let ``boosts are listed created and removed`` () =
    task {
        use! test = bootSeeded "default"
        let! message = lastMessage test ALL_TALK
        let david = david test
        let path = $"/messages/{message.Id}/boosts"
        let! index = david.Get path
        Assert.Equal(200, index.Status)
        let! ``new`` = david.Get $"{path}/new"
        Assert.Equal(200, ``new``.Status)
        let! show = david.Get $"{path}/1"
        Assert.Equal(404, show.Status)

        let! created = david.Write((request "POST" path).Form(formBody [ "boost[content]", "🔥" ]))
        Assert.Equal(Some $"http://campfire.test{path}", created.Location)
        let! boost = read test (fun conn -> Boost.forMessage conn message.Id |> List.last)
        Assert.Equal(("🔥", DAVID), (boost.Content, boost.BoosterId))

        let! destroyed = david.Write(request "DELETE" $"{path}/{boost.Id}")
        Assert.Equal(204, destroyed.Status)
        let! again = david.Write(request "DELETE" $"{path}/{boost.Id}")
        Assert.Equal(404, again.Status)
        let! huge = david.Get $"/messages/{Int64.MaxValue}/boosts"
        Assert.Equal(404, huge.Status)
    }

/// Message fragments and the bot API's JSON are cached for every request, so a request with a
/// forged Host mustn't leave its URLs in them for the next one.
[<Fact>]
let ``a forged host stays out of the caches`` () =
    task {
        use! test = bootSeeded "default"
        let forged (path: string) = (get path).With("x-forwarded-host", "evil.example")

        let bot = anonymous test
        let api = $"/rooms/{ALL_TALK}/{BENDER_KEY}/messages"
        let! evil = bot.Send(forged api)
        Assert.Contains("http://evil.example/", evil.Text)
        let! honest = bot.Get api
        Assert.Equal(200, honest.Status)
        Assert.True((not (honest.Text.Contains "evil.example")), honest.Text)

        let david = david test
        let room = $"/rooms/{ALL_TALK}"
        let! forgedRoom = david.Send(forged room)
        Assert.Equal(200, forgedRoom.Status)
        let! honest = (Support.david test).Get room
        Assert.Equal(200, honest.Status)
        Assert.Contains("data-copy-to-clipboard-url-value=\"/rooms/", honest.Text)
        Assert.DoesNotContain("evil.example", honest.Text)
    }

[<Fact>]
let ``the bot api`` () =
    task {
        use! test = bootSeeded "default"
        let bot = anonymous test
        let b = $"/rooms/{ALL_TALK}/{BENDER_KEY}/messages"

        let! index = bot.Get b
        Assert.True((index.Status = 200), index.Text)
        Assert.Equal(Some "application/json; charset=utf-8", index.ContentType)
        Assert.Equal(Some "131", index.Header "x-total-count")
        use page = index.Json
        let items = page.RootElement
        Assert.Equal(40, items.GetArrayLength())
        let firstId = items[0].GetProperty("id").GetInt64()
        Assert.Equal(Some $"<http://campfire.test{b}?before={firstId}>; rel=\"next\"", index.Header "link")
        let keys = [ for p in items[0].EnumerateObject() -> p.Name ]
        Assert.Equal<string list>([ "id"; "created_at"; "body"; "creator"; "room"; "url" ], keys)

        let! created = bot.Send((request "POST" b).WithBody "Beep boop")
        Assert.True((created.Status = 201), created.Text)
        let! message = lastMessage test ALL_TALK
        Assert.Equal(Some $"http://campfire.test/messages/{message.Id}", created.Location)
        Assert.Equal(BENDER, message.CreatorId)

        let! blank = bot.Send((request "POST" b).WithBody "  \n")
        Assert.Equal(422, blank.Status)
        let! upload = bot.Send(multipartReq (request "POST" b) [] ("attachment", "red.png", "image/png", PNG))
        Assert.Equal(201, upload.Status)

        let! updated = bot.Send((request "PUT" $"{b}/{message.Id}").WithBody "Beep edited")
        Assert.True((updated.Status = 200), updated.Text)
        use updatedJson = updated.Json
        Assert.Equal("Beep edited", updatedJson.RootElement.GetProperty("body").GetProperty("plain_text").GetString())

        // An attachment replaces the message's attachment (and keeps the body), like Rails'
        // `update!(attachment:)`; a second one replaces the first; "" removes it.
        let attached (id: int64) : Task<Campfire.Db.Blob option> =
            read test (fun conn -> Message.attachment conn (Message.find conn id) |> Option.map snd)
        let putFile (name: string) : Req =
            multipartReq (request "PUT" $"{b}/{message.Id}") [] ("attachment", name, "image/png", PNG)
        let! withFile = bot.Send(putFile "red.png")
        Assert.True((withFile.Status = 200), withFile.Text)
        use withFileJson = withFile.Json
        Assert.Equal("Beep edited", withFileJson.RootElement.GetProperty("body").GetProperty("plain_text").GetString())
        let! first = attached message.Id
        Assert.Equal("red.png", first.Value.Filename)
        let! again = bot.Send(putFile "again.png")
        Assert.Equal(200, again.Status)
        let! second = attached message.Id
        Assert.Equal("again.png", second.Value.Filename)
        Assert.NotEqual(first.Value.Id, second.Value.Id)
        let! removed = bot.Send((request "PUT" $"{b}/{message.Id}").Form(formBody [ "attachment", "" ]))
        Assert.True((removed.Status = 200), removed.Text)
        let! none = attached message.Id
        Assert.True none.IsNone

        let! boost = bot.Send((request "POST" $"{b}/{message.Id}/boosts").WithBody "🤖")
        Assert.True((boost.Status = 201), boost.Text)
        use boostJson = boost.Json
        Assert.Equal("🤖", boostJson.RootElement.GetProperty("content").GetString())
        let boostId = boostJson.RootElement.GetProperty("id").GetInt64()
        let! removed = bot.Send(request "DELETE" $"{b}/{message.Id}/boosts/{boostId}")
        Assert.Equal(204, removed.Status)
        let! missing = bot.Send(request "DELETE" $"{b}/{message.Id}/boosts/{boostId}")
        Assert.Equal(404, missing.Status)

        let! destroyed = bot.Send(request "DELETE" $"{b}/{message.Id}")
        Assert.Equal(204, destroyed.Status)

        // Rooms the bot isn't in, other people's messages, and bad keys.
        let! elsewhere = bot.Get $"/rooms/{DIRECT_DAVID_JASON}/{BENDER_KEY}/messages"
        Assert.Equal(404, elsewhere.Status)
        let! messages = messagesIn test ALL_TALK
        let notMine = messages |> List.find (fun m -> m.CreatorId = DAVID)
        let! forbidden = bot.Send(request "DELETE" $"{b}/{notMine.Id}")
        Assert.Equal(403, forbidden.Status)
        let! badKey = bot.Get $"/rooms/{ALL_TALK}/1-nope/messages"
        Assert.Equal(302, badKey.Status)
        // A bot key doesn't open the rest of the app.
        let! denied = bot.Get $"/rooms/{ALL_TALK}/messages?bot_key={BENDER_KEY}"
        Assert.Equal(403, denied.Status)
    }
