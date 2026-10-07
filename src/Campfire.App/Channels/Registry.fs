// Port of `server` and `register` in rust/crates/campfire/src/channels.rs
//
// The cable server with `ApplicationCable::Connection` and every channel registered. Mount it with
// `Endpoint.call` at `/cable`.
module Campfire.App.Channels.Registry

open Microsoft.Extensions.Logging
open Campfire.Cable
open Campfire.Cable.Turbo

/// Registers the channels under their Ruby class names. `streams` verifies signed stream names
/// (`Turbo.signed_stream_verifier`); `RoomMessagesChannel` verifies with it too, and `Turbo::StreamsChannel`
/// gets it with `RoomStreamsAreAuthorized` prepended.
let register (builder: ServerBuilder<CableUser>) (db: Campfire.Db.Database) (streams: StreamsChannel) : ServerBuilder<CableUser> =
    let stock = streams |> StreamsChannel.guardedBy RoomMessages.guardedStream
    builder
    |> ServerBuilder.channel "ApplicationCable::Channel" (fun () -> Channel.empty)
    |> ServerBuilder.channel "HeartbeatChannel" (fun () -> Channel.empty)
    |> ServerBuilder.channel "PresenceChannel" (fun () -> Presence.channel db)
    |> ServerBuilder.channel "ReadRoomsChannel" (fun () -> ReadRooms.channel)
    |> ServerBuilder.channel "RoomChannel" (fun () -> RoomChannel.channel db)
    |> ServerBuilder.channel "RoomMessagesChannel" (fun () -> RoomMessages.channel db streams)
    |> ServerBuilder.channel "TypingNotificationsChannel" (fun () -> TypingNotifications.channel db)
    |> ServerBuilder.channel "UnreadRoomsChannel" (fun () -> UnreadRooms.channel)
    |> ServerBuilder.channel StreamsChannelName (fun () -> StreamsChannel.channel stock)

/// The cable server with `ApplicationCable::Connection` and every channel registered.
let server (deps: Deps) (config: Config) (logger: ILogger) : Cable =
    let authenticator = SessionAuthenticator(deps.Db, deps.Secrets, deps.Clock, logger)
    let streams = StreamsChannel.create deps.Secrets
    Server.builder config authenticator.Connect Gid.connectionIdentifier
    |> fun builder -> register builder deps.Db streams
    |> ServerBuilder.withLogger logger
    |> ServerBuilder.build
