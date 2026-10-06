// Port of rust/crates/campfire/src/channels/presence.rs
//
// `PresenceChannel` (reference/app/channels/presence_channel.rb): a `RoomChannel` that marks the membership
// connected while subscribed (`Membership::Connectable`) and tells the user's other windows the room has been
// read.
module Campfire.App.Channels.Presence

open System.Threading.Tasks
open Campfire.Cable
open Campfire.Db

let private nilMembership: ChannelError = Channel.error "undefined method for nil (membership)"

let channel (db: Database) : Channel<CableUser> =
    let room: Room option ref = ref None

    // `@room` is only nil after a rejection, when these callbacks don't run.
    let ids (sub: Subscription<CableUser>) : ChannelResult<int64 * int64> =
        match room.Value with
        | Some room -> Ok(room.Id, sub.CurrentUser.Id)
        | None -> Error(Channel.error "undefined method 'memberships' for nil")

    /// `@room.memberships.find_by(user: current_user)`, which is nil (and so raises NoMethodError) once the
    /// membership is gone.
    let membership (sub: Subscription<CableUser>) : Task<ChannelResult<Membership>> =
        task {
            match ids sub with
            | Error error -> return Error error
            | Ok(roomId, userId) ->
                match! db.Read(fun conn -> Membership.findByRoomAndUser conn roomId userId) with
                | Error error -> return Error(RoomChannel.dbError error)
                | Ok None -> return Error nilMembership
                | Ok(Some membership) -> return Ok membership
        }

    let withMembership (sub: Subscription<CableUser>) (change: Tx -> Membership -> unit) : Task<ChannelResult<unit>> =
        task {
            match ids sub with
            | Error error -> return Error error
            | Ok(roomId, userId) ->
                let! found =
                    db.Write(fun tx ->
                        match Membership.findByRoomAndUser tx.Conn roomId userId with
                        | Some membership ->
                            change tx membership
                            true
                        | None -> false)
                match found with
                | Error error -> return Error(RoomChannel.dbError error)
                | Ok true -> return Ok()
                | Ok false -> return Error nilMembership
        }

    /// `present`: `membership.present`, then `broadcast_read_room`.
    let present (sub: Subscription<CableUser>) : Task<ChannelResult<unit>> =
        task {
            match! withMembership sub (fun tx membership -> Membership.present tx membership) with
            | Error error -> return Error error
            | Ok() ->
                // `membership.room_id` finds the membership again.
                match! membership sub with
                | Error error -> return Error error
                | Ok membership ->
                    BroadcastNames.readRoom sub.Server sub.CurrentUser.Id membership.RoomId |> ignore
                    return Ok()
        }

    /// `absent`: `membership.disconnected`.
    let absent (sub: Subscription<CableUser>) : Task<ChannelResult<unit>> =
        withMembership sub (fun tx membership -> Membership.disconnected tx membership |> ignore)

    /// `refresh`: `membership.refresh_connection`.
    let refresh (sub: Subscription<CableUser>) : Task<ChannelResult<unit>> =
        withMembership sub (fun tx membership -> Membership.refreshConnection tx membership |> ignore)

    let resubscribe (sub: Subscription<CableUser>) : Task<ChannelResult<unit>> =
        task {
            match! RoomChannel.subscribe db sub with
            | Ok found ->
                room.Value <- found
                return Ok()
            | Error error -> return Error error
        }

    { Channel.empty with
        // `subscribed`, then `on_subscribe :present, unless: :subscription_rejected?`.
        Subscribed =
            fun sub ->
                task {
                    match! resubscribe sub with
                    | Error error -> return Error error
                    | Ok() -> return! (if sub.Rejected then Channel.ok else present sub)
                }
        // `on_unsubscribe :absent, unless: :subscription_rejected?`.
        Unsubscribed = fun sub -> if sub.Rejected then Channel.ok else absent sub
        Perform =
            fun action _ sub ->
                task {
                    if sub.Rejected then
                        return Ok false
                    else
                        let ran (result: ChannelResult<unit>) = result |> Result.map (fun () -> true)
                        match action with
                        | "present" ->
                            let! result = present sub
                            return ran result
                        | "absent" ->
                            let! result = absent sub
                            return ran result
                        | "refresh" ->
                            let! result = refresh sub
                            return ran result
                        | "subscribed" ->
                            let! result = resubscribe sub
                            return ran result
                        | _ -> return Ok false
                } }
