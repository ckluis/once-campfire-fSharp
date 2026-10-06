// Port of rust/crates/campfire/src/controllers/rooms/tests.rs
//
// Request-level tests for the room controllers, against the `default` parity seed.
//
// `pages_of_messages_go_out_in_parts` can't count the data frames a body came in (a raw HTTP/1.1 client
// sees a stream of bytes, see `Support`), so it checks what the frames carry: the same page, with the same
// ETag, as plain bytes, gzipped, and as a HEAD.
module Campfire.App.Tests.RoomsTests

open System
open System.IO
open System.IO.Compression
open System.Text
open System.Threading.Tasks
open Xunit
open Campfire.App.Tests.Support
open Campfire.Db

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

let private roomId (reply: Reply) : int64 =
    let location = reply.Location.Value
    int64 (location.Substring(location.LastIndexOf '/' + 1))

[<Fact>]
let ``show renders the room and remembers it`` () =
    task {
        use! test = bootSeeded "default"
        let david = david test
        let! reply = david.Get $"/rooms/{ALL_TALK}"
        Assert.True((reply.Status = 200), reply.Text)
        Assert.Equal(Some "text/html; charset=utf-8", reply.ContentType)
        let html = reply.Text
        Assert.Contains("<title>All Talk</title>", html)
        Assert.Contains("""<meta name="current-room-id" content="486777696">""", html)
        Assert.Equal(40, count html replyMarker)
        Assert.True(reply.SetCookies |> List.exists (fun c -> c.StartsWith $"last_room={ALL_TALK}"))
        Assert.Equal(Some "parity", reply.Header "x-version")
    }

[<Fact>]
let ``show at a message pages around it`` () =
    task {
        use! test = bootSeeded "default"
        let! first =
            read test (fun conn -> Message.forRoom conn ALL_TALK |> List.minBy (fun m -> m.CreatedAt.Microsecond))
        let! reply = (david test).Get $"/rooms/{ALL_TALK}/@{first.Id}"
        Assert.Equal(200, reply.Status)
        // The first message and the 40 after it.
        Assert.Equal(41, count reply.Text replyMarker)
    }

[<Fact>]
let ``inaccessible rooms redirect home with an alert`` () =
    task {
        use! test = bootSeeded "default"
        let david = david test
        let! reply = david.Get $"/rooms/{DIRECT_KEVIN_BENDER}"
        Assert.Equal(302, reply.Status)
        Assert.Equal(Some "http://campfire.test/", reply.Location)
        let! reply = david.Get "/rooms/nonsense"
        Assert.Equal(302, reply.Status)
    }

/// `@membership.room` is nil for a membership whose room is gone, and Rails fails on it: a 500,
/// not the 404 of a membership that isn't there.
[<Fact>]
let ``a membership of a missing room is a server error`` () =
    task {
        use! test = bootSeeded "default"
        let missingRoom = 999_999L
        let! written =
            test.App.Db.Write(fun tx ->
                tx.Conn.Execute(
                    "INSERT INTO memberships (room_id, user_id, created_at, updated_at) VALUES (?, ?, '2026-01-01', '2026-01-01')",
                    [| I missingRoom; I DAVID |]
                )
                |> ignore)
        unwrap written
        let david = david test
        let! gone = david.Get $"/rooms/{missingRoom}/messages"
        Assert.Equal(500, gone.Status)
        let! none = david.Get $"/rooms/{missingRoom + 1L}/messages"
        Assert.Equal(404, none.Status)
    }

[<Fact>]
let ``index redirects to the last room`` () =
    task {
        use! test = bootSeeded "default"
        let! reply = (david test).Get "/rooms"
        Assert.Equal(302, reply.Status)
        Assert.StartsWith("http://campfire.test/rooms/", reply.Location.Value)
        // Anonymous: off to sign in.
        let! reply = (anonymous test).Get "/rooms"
        Assert.Equal(Some "http://campfire.test/session/new", reply.Location)
    }

[<Fact>]
let ``undeclared actions`` () =
    task {
        use! test = bootSeeded "default"
        let david = david test
        let! reply = david.Get "/rooms/new"
        Assert.Equal(404, reply.Status)
        let! reply = david.Get $"/rooms/{ALL_TALK}/edit"
        Assert.Equal(404, reply.Status)
        let! direct = david.Get $"/rooms/directs/{DIRECT_DAVID_JASON}"
        Assert.Equal(302, direct.Status)
        Assert.EndsWith($"/rooms/{DIRECT_DAVID_JASON}", direct.Location.Value)
        let! reply = david.Write(request "DELETE" $"/rooms/opens/{HQ}")
        Assert.Equal(500, reply.Status)
    }

[<Fact>]
let ``open rooms are created edited and updated`` () =
    task {
        use! test = bootSeeded "default"
        let david = david test
        let! ``new`` = david.Get "/rooms/opens/new"
        Assert.Equal(200, ``new``.Status)
        Assert.Contains("New chat room", ``new``.Text)

        let! created = david.Write((request "POST" "/rooms/opens").Form(formBody [ "room[name]", "Watercooler" ]))
        Assert.True((created.Status = 302), created.Text)
        let id = roomId created
        let! room = read test (fun conn -> Room.find conn id)
        Assert.Equal((Some "Watercooler", RoomType.Open), (room.Name, room.RoomType))
        // Rooms::Open grants every active user after commit.
        let! members = read test (fun conn -> Membership.forRoom conn id)
        Assert.True(members.Length > 3)

        let! edit = david.Get $"/rooms/opens/{id}/edit"
        Assert.Equal(200, edit.Status)
        let! shown = david.Get $"/rooms/opens/{id}"
        Assert.Equal(Some $"http://campfire.test/rooms/{id}", shown.Location)

        let! updated =
            david.Write(
                (request "PATCH" $"/rooms/closeds/{id}")
                    .Form(formBody [ "room[name]", "Private"; "user_ids[]", string DAVID ])
            )
        Assert.True((updated.Status = 302), updated.Text)
        let! room = read test (fun conn -> Room.find conn id)
        Assert.Equal((Some "Private", RoomType.Closed), (room.Name, room.RoomType))
        let! members = read test (fun conn -> Membership.forRoom conn id)
        Assert.Equal<int64 list>([ DAVID ], members |> List.map (fun m -> m.UserId))

        let! missingParam = david.Write((request "POST" "/rooms/opens").Form(formBody [ "name", "x" ]))
        Assert.Equal(400, missingParam.Status)
    }

[<Fact>]
let ``closed rooms are created with the selected users`` () =
    task {
        use! test = bootSeeded "default"
        let david = david test
        let! ``new`` = david.Get "/rooms/closeds/new"
        Assert.Equal(200, ``new``.Status)
        let! created =
            david.Write(
                (request "POST" "/rooms/closeds")
                    .Form(formBody [ "room[name]", "Secret"; "user_ids[]", string DAVID; "user_ids[]", string JASON; "user_ids[]", "999" ])
            )
        Assert.True((created.Status = 302), created.Text)
        let id = roomId created
        let! members = read test (fun conn -> Membership.forRoom conn id)
        Assert.Equal<int64 list>([ DAVID; JASON ] |> List.sort, members |> List.map (fun m -> m.UserId) |> List.sort)
        let! edit = david.Get $"/rooms/closeds/{id}/edit"
        Assert.Equal(200, edit.Status)
    }

[<Fact>]
let ``only administrators or creators update rooms`` () =
    task {
        use! test = bootSeeded "default"
        // Jason (an administrator in the seed) isn't needed: David is an admin, so check the scope instead:
        // direct rooms are out of reach of the open/closed controllers.
        let! reply = (david test).Get $"/rooms/opens/{DIRECT_DAVID_JASON}/edit"
        Assert.Equal(302, reply.Status)
        Assert.Equal(Some "http://campfire.test/", reply.Location)
    }

[<Fact>]
let ``direct rooms are found or created`` () =
    task {
        use! test = bootSeeded "default"
        let david = david test
        let! ``new`` = david.Get "/rooms/directs/new"
        Assert.Equal(200, ``new``.Status)
        let! existing = david.Write((request "POST" "/rooms/directs").Form(formBody [ "user_ids[]", string JASON ]))
        Assert.Equal(Some $"http://campfire.test/rooms/{DIRECT_DAVID_JASON}", existing.Location)
        let! created = david.Write((request "POST" "/rooms/directs").Form(formBody [ "user_ids[]", string KEVIN ]))
        let id = roomId created
        let! room = read test (fun conn -> Room.find conn id)
        Assert.Equal(RoomType.Direct, room.RoomType)

        let! edit = david.Get $"/rooms/directs/{id}/edit"
        Assert.Equal(200, edit.Status)
        let! destroyed = david.Write(request "DELETE" $"/rooms/directs/{id}")
        Assert.Equal(Some "http://campfire.test/", destroyed.Location)
        let! gone = read test (fun conn -> Room.findById conn id)
        Assert.True gone.IsNone
    }

[<Fact>]
let ``refresh streams messages since a time`` () =
    task {
        use! test = bootSeeded "default"
        let david = david test
        let! reply =
            david.Send((get $"/rooms/{ALL_TALK}/refresh?since=0").With("accept", "text/vnd.turbo-stream.html, text/html"))
        Assert.True((reply.Status = 200), reply.Text)
        Assert.Equal(Some "text/vnd.turbo-stream.html; charset=utf-8", reply.ContentType)
        Assert.StartsWith("""<turbo-stream action="append" target="messages_rooms_closed_486777696">""", reply.Text)

        let! htmlOnly = david.Get $"/rooms/{ALL_TALK}/refresh?since=0"
        Assert.Equal(406, htmlOnly.Status)
        let! other = david.Get $"/rooms/{DIRECT_KEVIN_BENDER}/refresh"
        Assert.Equal(404, other.Status)
    }

[<Fact>]
let ``involvement is shown and changed`` () =
    task {
        use! test = bootSeeded "default"
        let david = david test
        let! shown = david.Get $"/rooms/{ALL_TALK}/involvement"
        Assert.Equal(200, shown.Status)
        Assert.Contains("turbo-frame", shown.Text)
        let! updated = david.Write((request "PATCH" $"/rooms/{ALL_TALK}/involvement").Form(formBody [ "involvement", "invisible" ]))
        Assert.Equal(Some $"http://campfire.test/rooms/{ALL_TALK}/involvement", updated.Location)
        let! membership = read test (fun conn -> (Membership.findByRoomAndUser conn ALL_TALK DAVID).Value)
        Assert.Equal(Some Involvement.Invisible, membership.Involvement)
        let! invalid = david.Write((request "PATCH" $"/rooms/{ALL_TALK}/involvement").Form(formBody [ "involvement", "loud" ]))
        Assert.Equal(500, invalid.Status)

        // A missing (or blank) involvement is stored as nil, like the enum casts it.
        let! missing = david.Write(request "PATCH" $"/rooms/{ALL_TALK}/involvement")
        Assert.Equal(302, missing.Status)
        let! membership = read test (fun conn -> (Membership.findByRoomAndUser conn ALL_TALK DAVID).Value)
        Assert.Equal(None, membership.Involvement)
    }

[<Fact>]
let ``rooms are destroyed by administrators`` () =
    task {
        use! test = bootSeeded "default"
        let! reply = (david test).Write(request "DELETE" $"/rooms/{QUIET_CORNER}")
        Assert.Equal(Some "http://campfire.test/", reply.Location)
        let! gone = read test (fun conn -> Room.findById conn QUIET_CORNER)
        Assert.True gone.IsNone
    }

[<Fact>]
let ``cross site writes are refused`` () =
    task {
        use! test = bootSeeded "default"
        let request = ((request "POST" "/rooms/opens").Form(formBody [ "room[name]", "x" ])).With("sec-fetch-site", "cross-site")
        let! reply = (david test).Send request
        Assert.Equal(422, reply.Status)
    }

[<Fact>]
let ``the last room cookie is set only when it changes`` () =
    task {
        use! test = bootSeeded "default"
        let david = david test
        let lastRoom (reply: Reply) = reply.SetCookies |> List.exists (fun c -> c.StartsWith "last_room=")
        let! first = david.Get $"/rooms/{HQ}"
        Assert.True(lastRoom first)
        let! again = david.Get $"/rooms/{HQ}"
        Assert.False(lastRoom again, "the same room again")
        let! other = david.Get $"/rooms/{ALL_TALK}"
        Assert.True(lastRoom other)
    }

/// Where a page's `link_back_to_last_room_visited` goes.
let private backLink (html: string) : string =
    let link = html.Substring(0, html.IndexOf "Go Back</span></a>")
    let tag = link.Substring(link.LastIndexOf "<a ")
    let href = tag.Substring(tag.IndexOf "href=\"" + 6)
    href.Substring(0, href.IndexOf '"')

/// The layout's `last_room_visited`: the `last_room` cookie's room when the user is in it, else
/// their first room.
[<Fact>]
let ``back links go to the last room visited`` () =
    task {
        use! test = bootSeeded "default"
        let! original = read test (fun conn -> (Room.originalForUser conn DAVID).Value.Id)
        Assert.NotEqual(QUIET_CORNER, original)
        let david = david test
        let backLinkOf (path: string) : Task<string> =
            task {
                let! reply = david.Get path
                Assert.True((reply.Status = 200), reply.Text)
                return backLink reply.Text
            }
        let! link = backLinkOf "/rooms/opens/new"
        Assert.Equal($"/rooms/{original}", link) // no cookie
        let! _ = david.Get $"/rooms/{QUIET_CORNER}"
        let! link = backLinkOf "/rooms/opens/new"
        Assert.Equal($"/rooms/{QUIET_CORNER}", link)
        let! link = backLinkOf "/account/edit"
        Assert.Equal($"/rooms/{QUIET_CORNER}", link)
        david.SetCookie("last_room", string DIRECT_KEVIN_BENDER)
        let! link = backLinkOf "/rooms/opens/new"
        Assert.Equal($"/rooms/{original}", link) // a room he isn't in
        david.SetCookie("last_room", "nonsense")
        let! link = backLinkOf "/rooms/opens/new"
        Assert.Equal($"/rooms/{original}", link)
    }

[<Fact>]
let ``a room page has the same etag cold and warm`` () =
    task {
        use! test = bootSeeded "default"
        let david = david test
        // The first render stores the page's messages in the fragment cache; the second reads them.
        let! cold = david.Get $"/rooms/{HQ}"
        let! warm = david.Get $"/rooms/{HQ}"
        Assert.Equal(cold.Text, warm.Text)
        Assert.True((cold.Header "etag").IsSome)
        Assert.Equal(cold.Header "etag", warm.Header "etag")
    }

/// A page of messages goes out in parts that are never joined: as they are to a client without
/// gzip, gzipped from their stored pieces, and as just its length for HEAD. It's the same page with
/// the same ETag every way (a room page, its Turbo-Frame version, and a page of older messages).
[<Fact>]
let ``pages of messages go out in parts`` () =
    task {
        use! test = bootSeeded "default"
        let david = david test
        let! messages =
            read test (fun conn ->
                Message.forRoom conn ALL_TALK |> List.sortBy (fun m -> m.CreatedAt.Microsecond, m.Id))
        let pages =
            [ $"/rooms/{ALL_TALK}", None
              $"/rooms/{ALL_TALK}", Some "messages"
              $"/rooms/{ALL_TALK}/messages?before={messages[60].Id}", None ]
        for path, frame in pages do
            let send (meth: string) (gzip: bool) : Req =
                let mutable req = request meth path
                if gzip then req <- req.With("accept-encoding", "gzip")
                match frame with
                | Some frame -> req <- req.With("turbo-frame", frame)
                | None -> ()
                req
            let! plain = david.Send(send "GET" false)
            Assert.True((plain.Status = 200), path)
            Assert.True((count plain.Text replyMarker = 40), path)
            Assert.Equal(Some(string plain.Body.Length), plain.Header "content-length")

            let! gzipped = david.Send(send "GET" true)
            Assert.Equal(Some "gzip", gzipped.Header "content-encoding")
            Assert.Equal(None, gzipped.Header "content-length")
            use decoder = new GZipStream(new MemoryStream(gzipped.Body), CompressionMode.Decompress)
            use decoded = new MemoryStream()
            decoder.CopyTo decoded
            Assert.True((decoded.ToArray() = plain.Body), $"{path}: gzip decodes to the plain page")

            for gzip in [ false; true ] do
                let! head = david.Send(send "HEAD" gzip)
                Assert.True((head.Status = 200), path)
                Assert.Equal(plain.Header "etag", head.Header "etag")
                if not gzip then
                    Assert.True(head.Body.Length = 0, path)
                    Assert.Equal(plain.Header "content-length", head.Header "content-length")
            Assert.True((plain.Header "etag" |> Option.exists (fun etag -> etag.StartsWith "W/")), path)
            Assert.Equal(plain.Header "etag", gzipped.Header "etag")
    }
