// Port of rust/crates/campfire/src/channels/tests/revocation_test.rs
//
// Revocation, for every channel that carries a room: subscribe, revoke (remove the membership, deactivate or ban),
// attempt a delivery, reconnect and resubscribe.
module Campfire.App.Tests.RevocationTests

open System.Threading.Tasks
open Xunit
open Campfire.App.Channels
open Campfire.Cable
open Campfire.Db
open Campfire.RailsCompat
open Campfire.App.Tests.ChannelsSupport

let private obj' (entries: (string * Value) list) : Value = Value.Object entries

let private disconnectReconnect = """{"type":"disconnect","reason":"remote","reconnect":true}"""
let private disconnectForGood = """{"type":"disconnect","reason":"remote","reconnect":false}"""
let private unauthorized = """{"type":"disconnect","reason":"unauthorized","reconnect":false}"""

/// Each room-carrying channel's identifier for the designers room, and the broadcasting it streams from.
let private roomChannels (app: TestApp) : Task<(string * string) list> =
    task {
        let! designers = app.Room "designers"
        let gid = GlobalId.toParam (Gid.roomGid designers)
        let messages = $"{gid}:messages"
        let signed = app.SignedStreamName [ gid; "messages" ]
        return
            [ roomIdentifier "RoomChannel" designers.Id, $"room:{gid}"
              roomIdentifier "PresenceChannel" designers.Id, $"presence:{gid}"
              roomIdentifier "TypingNotificationsChannel" designers.Id, $"typing_notifications:{gid}"
              identifier (obj' [ "channel", Value.String "RoomMessagesChannel"; "signed_stream_name", Value.String signed ]), messages ]
    }

type private Revoke =
    | RemoveMembership
    | Deactivate
    | Ban

let private revoke (app: TestApp) (how: Revoke) : Task =
    task {
        let roomId, kevin = id "designers", id "kevin"
        let! result =
            app.Db.Write(fun tx ->
                match how with
                | RemoveMembership -> Room.revokeFrom tx (Room.find tx.Conn roomId) [ kevin ]
                | Deactivate -> User.deactivate tx (User.find tx.Conn kevin) |> ignore
                | Ban -> User.ban tx (User.find tx.Conn kevin) |> ignore)
        match result with
        | Ok() -> ()
        | Error error -> failwith (DbError.display error)
    }

let private subscribedKevin (app: TestApp) : Task<Client * string> =
    task {
        let! cookie = app.CookieFor "kevin"
        let! kevin = app.ConnectWithCookie(Some cookie)
        let! welcome = kevin.NextText()
        Assert.Equal("""{"type":"welcome"}""", welcome)
        let! channels = roomChannels app
        for (channel, _) in channels do
            do! kevin.Confirm channel
        return kevin, cookie
    }

let private assertNoDeliveries (app: TestApp) : Task =
    task {
        let! channels = roomChannels app
        for (_, broadcasting) in channels do
            do!
                eventually (fun () ->
                    Task.FromResult(app.Server.Broadcast(broadcasting, obj' [ "after", Value.String "revocation" ]) = 0))
    }

[<Fact>]
let ``removing the membership disconnects and resubscribing is rejected`` () =
    task {
        use! app = start ()
        let! kevin, cookie = subscribedKevin app
        use _kevin = kevin
        // PresenceChannel's `present` told kevin's reads stream; nothing else is pending.
        do! kevin.AssertSilent()

        do! revoke app RemoveMembership
        let! frames = kevin.UntilClosed()
        Assert.Equal<string list>([ disconnectReconnect ], frames)
        do! assertNoDeliveries app

        // The client reconnects and replays its subscriptions.
        use! kevin = app.ConnectWithCookie(Some cookie)
        let! welcome = kevin.NextText()
        Assert.Equal("""{"type":"welcome"}""", welcome)
        let! channels = roomChannels app
        for (channel, _) in channels do
            do! kevin.Reject channel
        // The stock channel doesn't serve the stream either.
        let! designers = app.Room "designers"
        let stock =
            identifier (
                obj'
                    [ "channel", Value.String "Turbo::StreamsChannel"
                      "signed_stream_name", Value.String(app.SignedStreamName [ GlobalId.toParam (Gid.roomGid designers); "messages" ]) ]
            )
        do! kevin.Reject stock
        // Rooms kevin is still in keep working.
        do! kevin.Confirm(roomIdentifier "RoomChannel" (id "bender_and_kevin"))
    }

[<Fact>]
let ``deactivating disconnects for good`` () =
    task {
        use! app = start ()
        let! kevin, cookie = subscribedKevin app
        use _kevin = kevin

        do! revoke app Deactivate
        let! frames = kevin.UntilClosed()
        Assert.Equal<string list>([ disconnectForGood ], frames)
        do! assertNoDeliveries app

        use! kevin = app.ConnectWithCookie(Some cookie)
        let! frames = kevin.UntilClosed()
        Assert.Equal<string list>([ unauthorized ], frames)
    }

[<Fact>]
let ``banning disconnects for good`` () =
    task {
        use! app = start ()
        let! kevin, cookie = subscribedKevin app
        use _kevin = kevin

        do! revoke app Ban
        let! frames = kevin.UntilClosed()
        Assert.Equal<string list>([ disconnectForGood ], frames)
        do! assertNoDeliveries app

        use! kevin = app.ConnectWithCookie(Some cookie)
        let! frames = kevin.UntilClosed()
        Assert.Equal<string list>([ unauthorized ], frames)
    }

[<Fact>]
let ``only the revoked users connections are closed`` () =
    task {
        use! app = start ()
        let! kevin, _ = subscribedKevin app
        use _kevin = kevin
        use! jz = app.Connect "jz"
        let room = roomIdentifier "RoomChannel" (id "designers")
        do! jz.Confirm room

        do! revoke app RemoveMembership
        let! _ = kevin.UntilClosed()

        let! designers = app.Room "designers"
        app.Server.Broadcast($"room:{GlobalId.toParam (Gid.roomGid designers)}", obj' [ "still", Value.String "here" ]) |> ignore
        let! frame = jz.NextText()
        Assert.Equal(delivery room """{"still":"here"}""", frame)
    }
