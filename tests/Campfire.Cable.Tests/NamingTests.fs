// Port of the tests in rust/crates/cable/src/naming.rs
module Campfire.Cable.Tests.NamingTests

open Xunit
open Campfire.RailsCompat
open Campfire.Cable

[<Fact>]
let ``channel names`` () =
    Assert.Equal("room", Naming.channelName "RoomChannel")
    Assert.Equal("typing_notifications", Naming.channelName "TypingNotificationsChannel")
    Assert.Equal("turbo:streams", Naming.channelName "Turbo::StreamsChannel")
    Assert.Equal("html", Naming.channelName "HTMLChannel")
    Assert.Equal("html_parser", Naming.channelName "HTMLParserChannel")

[<Fact>]
let ``broadcastings`` () =
    let room = Naming.gidParam { App = "campfire"; ModelName = "Rooms::Open"; Id = "1" }
    Assert.Equal("Z2lkOi8vY2FtcGZpcmUvUm9vbXM6Ok9wZW4vMQ", room)
    Assert.Equal($"presence:{room}", Naming.broadcastingFor "PresenceChannel" [ room ])
    Assert.Equal($"{room}:messages", Naming.streamNameFrom [ room; "messages" ])
