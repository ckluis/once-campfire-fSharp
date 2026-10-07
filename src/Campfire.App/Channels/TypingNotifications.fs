// Port of rust/crates/campfire/src/channels/typing_notifications.rs
//
// `TypingNotificationsChannel` (reference/app/channels/typing_notifications_channel.rb).
module Campfire.App.Channels.TypingNotifications

open Campfire.Cable
open Campfire.Db
open Campfire.RailsCompat

let channel (db: Database) : Channel<CableUser> =
    let room: Room option ref = ref None

    /// `broadcast_to @room, action:, user: current_user.slice(:id, :name)`.
    let broadcast (action: string) (sub: Subscription<CableUser>) : ChannelResult<unit> =
        match room.Value with
        // `@room` is only nil when `subscribed` failed, and Rails raises NoMethodError.
        | None -> Error(Channel.error "undefined method 'to_gid_param' for nil")
        | Some room ->
            let user = sub.CurrentUser
            let payload =
                Value.Object [ "action", Value.String action; "user", Value.Object [ "id", Value.Int user.Id; "name", Value.String user.Name ] ]
            sub.BroadcastTo([ GlobalId.toParam (Gid.roomGid room) ], payload)
            Ok()

    let resubscribe sub =
        task {
            match! RoomChannel.subscribe db sub with
            | Ok found ->
                room.Value <- found
                return Ok()
            | Error error -> return Error error
        }

    { Channel.empty with
        Subscribed = resubscribe
        Perform =
            fun action _ sub ->
                task {
                    if sub.Rejected then
                        return Ok false
                    else
                        match action with
                        | "start"
                        | "stop" -> return broadcast action sub |> Result.map (fun () -> true)
                        | "subscribed" ->
                            let! result = resubscribe sub
                            return result |> Result.map (fun () -> true)
                        | _ -> return Ok false
                } }
