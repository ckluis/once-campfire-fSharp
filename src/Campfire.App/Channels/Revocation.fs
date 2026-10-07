// Port of rust/crates/campfire/src/channels/revocation.rs
//
// Revocation: `ActionCable.server.remote_connections.where(current_user: user).disconnect(reconnect:)`
// (reference/app/models/user.rb `close_remote_connections`).
//
// The models emit it as `Campfire.Db.Event.DisconnectUser`:
// - membership destroyed (`after_destroy_commit { user.reset_remote_connections }`, which covers
//   `revoke_from`, `revise` and room destroy) and sign-out: `reconnect: true`. The client
//   reconnects and replays its subscriptions, and the channels turn away the rooms it lost.
// - `User#deactivate` and `User::Bannable#ban`: `reconnect: false`, once their transaction has
//   committed and the sessions are gone, so a reconnect is refused. (Rails sends it inside the
//   transaction; a connection authenticating just then could miss it and stay open. The cable
//   connection also re-checks its session once it's listening for this.)
//
// Each connection of the user gets `{"type":"disconnect","reason":"remote","reconnect":..}` and
// is closed; closing unsubscribes every channel (so `PresenceChannel#absent` runs).
module Campfire.App.Channels.Revocation

open Campfire.Db

/// Disconnects every connection identified by this user. Returns the number of connections
/// listening (0 if the user has none open).
let disconnectUser (server: Cable) (userId: int64) (reconnect: bool) : int =
    server.Disconnect(string (Gid.userGid userId), reconnect)

/// Handles the events that belong to the cable server; returns false for the rest. (The app's
/// `Jobs` sink does this itself, alongside enqueuing jobs.)
let handleEvent (server: Cable) (event: Event) : bool =
    match event with
    | Event.DisconnectUser(userId, reconnect) ->
        disconnectUser server userId reconnect |> ignore
        true
    | _ -> false
