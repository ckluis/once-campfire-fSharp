// Port of `server` and `register` in rust/crates/campfire/src/channels.rs
//
// The cable server with `ApplicationCable::Connection` and the channels registered. Mount it with
// `Endpoint.call` at `/cable`.
//
// Registered so far: `ApplicationCable::Channel`, `HeartbeatChannel`, `ReadRoomsChannel` and
// `UnreadRoomsChannel`. `PresenceChannel`, `RoomChannel`, `RoomMessagesChannel`,
// `TypingNotificationsChannel` and the guarded `Turbo::StreamsChannel` come with the rest of
// `rust/crates/campfire/src/channels/` in the channels unit of Phase 5: the stock streams channel is
// left out until its `RoomStreamsAreAuthorized` guard exists, so that no stream is ever unguarded.
module Campfire.App.Channels.Registry

open Microsoft.Extensions.Logging
open Campfire.Cable

/// Registers the channels under their Ruby class names.
let register (builder: ServerBuilder<CableUser>) : ServerBuilder<CableUser> =
    builder
    |> ServerBuilder.channel "ApplicationCable::Channel" (fun () -> Channel.empty)
    |> ServerBuilder.channel "HeartbeatChannel" (fun () -> Channel.empty)
    |> ServerBuilder.channel "ReadRoomsChannel" (fun () -> ReadRooms.channel)
    |> ServerBuilder.channel "UnreadRoomsChannel" (fun () -> UnreadRooms.channel)

/// The cable server with `ApplicationCable::Connection` and every channel registered.
let server (deps: Deps) (config: Config) (logger: ILogger) : Cable =
    let authenticator = SessionAuthenticator(deps.Db, deps.Secrets, deps.Clock, logger)
    Server.builder config authenticator.Connect Gid.connectionIdentifier
    |> register
    |> ServerBuilder.withLogger logger
    |> ServerBuilder.build
