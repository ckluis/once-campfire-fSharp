// Port of rust/crates/campfire/src/channels.rs (the types, and the GlobalIDs the channels and broadcasts name things by)
//
// Action Cable: `reference/app/channels`, the broadcasts the app makes (`Broadcasts`), and revocation
// (`Revocation`). `Registry.server` builds the cable server with these channels, and the app routes the
// models' events to `Broadcasts` and `Revocation`.
//
// Every channel matches its Ruby class: identifier, streams, payloads, and callback order
// (`on_subscribe` runs after `subscribed`, before the confirmation). Ruby makes every public
// method an action, including a publicly redefined `subscribed`, so those are performable here
// too.
namespace Campfire.App.Channels

open Campfire.Cable
open Campfire.Db
open Campfire.RailsCompat
open Campfire.RailsCompat.Clock

/// `identified_by :current_user`: the user as loaded when the connection opened.
type CableUser = { Id: int64; Name: string }

/// The cable server, identified by `current_user`.
type Cable = Server<CableUser>

/// What the cable server needs from the app.
type Deps =
    { Db: Database
      Secrets: Secrets
      Clock: SharedClock }

module Gid =
    let userGid (userId: int64) : GlobalId = GlobalId.create "User" (string userId)

    /// A room's GlobalID names its STI class (`gid://campfire/Rooms::Open/1`).
    let roomGid (room: Room) : GlobalId = GlobalId.create (RoomType.className room.RoomType) (string room.Id)

    /// `connection_gid`: the user's GlobalID, which `remote_connections.where(current_user:)` matches.
    let connectionIdentifier (user: CableUser) : string = string (userGid user.Id)
