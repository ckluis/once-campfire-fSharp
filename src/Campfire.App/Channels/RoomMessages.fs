// Port of rust/crates/campfire/src/channels/room_messages.rs
//
// `RoomMessagesChannel` (reference/app/channels/room_messages_channel.rb) and the `RoomStreamsAreAuthorized`
// guard prepended onto `Turbo::StreamsChannel`
// (reference/app/channels/concerns/room_streams_are_authorized.rb).
//
// A room's message stream (`<room gid param>:messages`) is only served here, and only to a current member of the
// room the verified stream name points at.
module Campfire.App.Channels.RoomMessages

open System
open System.Threading.Tasks
open Campfire.Cable
open Campfire.Cable.Turbo
open Campfire.Db
open Campfire.RailsCompat

[<Literal>]
let StreamSuffix = "messages"

/// `RoomMessagesChannel.guarded_stream?`: true for the stream names this channel guards, whoever is asking. Also
/// the guard on `Turbo::StreamsChannel`.
let guardedStream (streamName: string) : bool =
    match streamName.IndexOf ':' with
    | -1 -> false
    | colon -> streamName.Substring(colon + 1) = StreamSuffix

/// Constants a GID could name that aren't rooms (`only: Room` turns them away without raising).
let private knownModels =
    set [ "Account"; "Ban"; "Boost"; "Current"; "Membership"; "Message"; "Push::Subscription"; "Search"; "Session"; "User"; "Webhook" ]

/// `GlobalID::Locator.locate gid_param, only: Room`, with `RecordNotFound` as nil.
let private roomFrom (conn: Conn) (gidParam: string) : ChannelResult<Room option> =
    // `GlobalID.parse` takes the URI form or its Base64 param.
    match GlobalId.parse gidParam |> Option.orElse (GlobalId.fromParam gidParam) with
    | None -> Ok None
    | Some gid ->
        // `find_allowed?(gid.model_class, only: Room)`: Room or one of its STI subclasses, whose `find` also
        // requires the type to match.
        let required: Result<RoomType option, ChannelError option> =
            match gid.ModelName with
            | "Room" -> Ok None
            | name ->
                match RoomType.fromClassName name with
                | Some roomType -> Ok(Some roomType)
                | None when knownModels.Contains name -> Error None
                // `model_name.constantize` raises NameError.
                | None -> Error(Some(Channel.error $"uninitialized constant {name}"))
        match required with
        | Error None -> Ok None
        | Error(Some error) -> Error error
        | Ok requiredType ->
            match Int64.TryParse(gid.Id, Globalization.NumberStyles.AllowLeadingSign, Globalization.CultureInfo.InvariantCulture) with
            | false, _ -> Ok None
            | true, id ->
                Room.findById conn id
                |> Option.filter (fun room -> requiredType |> Option.forall (fun t -> t = room.RoomType))
                |> Ok

/// `RoomMessagesChannel.subscribable_room(user, stream_name)`.
let subscribableRoom (conn: Conn) (userId: int64) (streamName: string) : ChannelResult<Room option> =
    match streamName.IndexOf ':' with
    | -1 -> Ok None
    | colon when streamName.Substring(colon + 1) <> StreamSuffix -> Ok None
    | colon ->
        match roomFrom conn (streamName.Substring(0, colon)) with
        | Error error -> Error error
        | Ok(Some room) -> Ok(Room.findForUser conn userId room.Id)
        | Ok None -> Ok None

let channel (db: Database) (streams: StreamsChannel) : Channel<CableUser> =
    /// `authorized_stream_name`: the verified stream name, if present and for a room the user belongs to.
    let authorizedStreamName (sub: Subscription<CableUser>) : Task<ChannelResult<string option>> =
        task {
            match StreamsChannel.verifiedStreamNameFromParams streams (sub.Params()) with
            | Error error -> return Error error
            | Ok None -> return Ok None
            | Ok(Some streamName) when streamName.Trim() = "" -> return Ok None
            | Ok(Some streamName) ->
                let userId = sub.CurrentUser.Id
                match! db.Read(fun conn -> subscribableRoom conn userId streamName) with
                | Error error -> return Error(RoomChannel.dbError error)
                | Ok(Error error) -> return Error error
                | Ok(Ok room) -> return Ok(room |> Option.map (fun _ -> streamName))
        }

    let subscribed (sub: Subscription<CableUser>) : Task<ChannelResult<unit>> =
        task {
            match! authorizedStreamName sub with
            | Error error -> return Error error
            | Ok(Some streamName) ->
                sub.StreamFrom streamName
                return Ok()
            | Ok None ->
                sub.Reject()
                return Ok()
        }

    { Channel.empty with
        Subscribed = subscribed
        // Its public methods: `subscribed`, and `verified_stream_name_from_params` from
        // `include Turbo::Streams::StreamName::ClassMethods` (which returns without transmitting).
        Perform =
            fun action _ sub ->
                task {
                    if sub.Rejected then
                        return Ok false
                    else
                        match action with
                        | "subscribed" ->
                            let! result = subscribed sub
                            return result |> Result.map (fun () -> true)
                        | "verified_stream_name_from_params" ->
                            return StreamsChannel.verifiedStreamNameFromParams streams (sub.Params()) |> Result.map (fun _ -> true)
                        | _ -> return Ok false
                } }
