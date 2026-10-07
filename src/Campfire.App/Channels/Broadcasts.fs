// Port of rust/crates/campfire/src/channels/broadcasts.rs
//
// Every broadcast the Rails app makes, with the stream names, targets and `<turbo-stream>`
// markup its Turbo and Action Cable calls produce. The HTML inside comes from `IPartials`.
//
// Stream names: records are their GID param (`turbo_stream_from @room, :messages` is
// `<room gid param>:messages`), symbols themselves. Targets are `dom_id`s: STI rooms use their
// own `param_key` (`messages_rooms_open_1`), and a message's `to_key` is its
// `client_message_id` (`message_<uuid>`).
namespace Campfire.App.Channels

open Campfire.Cable
open Campfire.Cable.Turbo
open Campfire.Db
open Campfire.RailsCompat

/// The partials Turbo renders for broadcasts (`ApplicationController.render(partial:, locals:)`,
/// html format, no request). Each returns the rendered HTML; the fragment-backed ones (a message and a boost, cached bytes) as the
/// UTF-8 they were rendered to, which the broadcast escapes as JSON without making a string of them.
type IPartials =
    /// `messages/_message` with `message:`.
    abstract Message: Message -> System.ReadOnlyMemory<byte>
    /// `messages/_presentation` with `message:`.
    abstract MessagePresentation: Message -> string
    /// `messages/boosts/_boost` with `boost:`.
    abstract Boost: Boost -> System.ReadOnlyMemory<byte>
    /// `users/sidebars/rooms/_shared` with `room:`.
    abstract SharedRoom: Room -> string
    /// `users/sidebars/rooms/_direct` with `membership:`.
    abstract DirectRoom: Membership -> string

/// `nil.inquiry`: NoMethodError.
type NilInquiry =
    | NilInquiry

    override _.ToString() = "undefined method 'inquiry' for nil"

module BroadcastNames =
    /// `dom_id(record, prefix)`.
    let domId (paramKey: string) (key: obj) (prefix: string option) : string =
        match prefix with
        | Some prefix -> $"{prefix}_{paramKey}_{key}"
        | None -> $"{paramKey}_{key}"

    /// `Room.model_name.param_key` for the room's STI class: `Rooms::Open` is `rooms_open`.
    let roomParamKey (room: Room) : string =
        (RoomType.className room.RoomType).Replace("::", "_").ToLowerInvariant()

    /// `dom_id(room, prefix)`.
    let roomDomId (room: Room) (prefix: string) : string = domId (roomParamKey room) room.Id (Some prefix)

    /// `dom_id(message, prefix)`: messages are keyed by `client_message_id`.
    let messageDomId (message: Message) (prefix: string option) : string = domId "message" message.ClientMessageId prefix

    [<Literal>]
    let Rooms = "rooms"

    [<Literal>]
    let Messages = "messages"

    let internal maintainScroll: (string * string option) list = [ "maintain_scroll", Some "true" ]

    /// `ActionCable.server.broadcast "user_#{id}_reads", { room_id: }`
    /// (reference/app/channels/presence_channel.rb).
    let readRoom (server: IServer) (userId: int64) (roomId: int64) : int =
        server.Broadcast(ReadRooms.streamNameFor userId, Value.Object [ "room_id", Value.Int roomId ])

[<Sealed>]
type Broadcasts(server: Cable) =
    static member private RoomMessages(room: Room) : string list = [ GlobalId.toParam (Gid.roomGid room); BroadcastNames.Messages ]

    static member private UserRooms(userId: int64) : string list = [ GlobalId.toParam (Gid.userGid userId); BroadcastNames.Rooms ]

    member private _.To(streamables: string list, action: Action, target: string, html: string option, attributes: (string * string option) list) : unit =
        let template =
            match html with
            | Some html -> Html html
            | None -> NoTemplate
        broadcastActionTo server streamables action (Target target) template attributes |> ignore

    member private _.ToUtf8(streamables: string list, action: Action, target: string, html: System.ReadOnlyMemory<byte>, attributes: (string * string option) list) : unit =
        broadcastActionTo server streamables action (Target target) (HtmlUtf8 html) attributes |> ignore

    // Message::Broadcasts (reference/app/models/message/broadcasts.rb)

    /// `message.broadcast_create`: append the message to the room, then tell every member's
    /// unread stream (`room.memberships.pluck(:user_id)`). Used by MessagesController#create,
    /// Webhook replies, and `Messages::ByBotsController`.
    member this.MessageCreate(conn: Conn, room: Room, message: Message, partials: IPartials) : unit =
        let html = partials.Message message
        this.ToUtf8(Broadcasts.RoomMessages room, Append, BroadcastNames.roomDomId room BroadcastNames.Messages, html, [])
        this.UnreadRoom(conn, room)

    /// `broadcast_unread_room`: `{ roomId: }` to each member's `user_<id>_unreads`.
    member _.UnreadRoom(conn: Conn, room: Room) : unit =
        // The same message to every member's stream: encoded once.
        let json = System.Text.Encoding.UTF8.GetBytes(Json.encode (Value.Object [ "roomId", Value.Int room.Id ]))
        for membership in Membership.forRoom conn room.Id do
            server.BroadcastEncoded(UnreadRooms.streamNameFor membership.UserId, System.ReadOnlySpan json) |> ignore

    /// `message.broadcast_remove`: MessagesController#destroy and `User#remove_banned_content`.
    member this.MessageRemove(room: Room, message: Message) : unit =
        this.To(Broadcasts.RoomMessages room, Remove, BroadcastNames.messageDomId message None, None, [])

    /// MessagesController#update: replace `[message, :presentation]` with
    /// `messages/_presentation`, keeping the scroll position.
    member this.MessageReplace(room: Room, message: Message, partials: IPartials) : unit =
        let html = partials.MessagePresentation message
        let target = BroadcastNames.messageDomId message (Some "presentation")
        this.To(Broadcasts.RoomMessages room, Replace, target, Some html, BroadcastNames.maintainScroll)

    // Messages::BoostsController (and its ByBots subclass)

    /// `broadcast_create`: append to `boosts_message_<client_message_id>`.
    member this.BoostCreate(room: Room, message: Message, boost: Boost, partials: IPartials) : unit =
        let html = partials.Boost boost
        let target = $"boosts_message_{message.ClientMessageId}"
        this.ToUtf8(Broadcasts.RoomMessages room, Append, target, html, BroadcastNames.maintainScroll)

    /// `broadcast_remove`: `dom_id(boost)`.
    member this.BoostRemove(room: Room, boost: Boost) : unit =
        this.To(Broadcasts.RoomMessages room, Remove, BroadcastNames.domId "boost" boost.Id None, None, [])

    // The sidebar's room lists (users/sidebars/show.html.erb streams from `:rooms` and
    // `[Current.user, :rooms]`).

    /// RoomsController#destroy: remove `[room, :list]` from everyone's `:rooms`.
    member this.RoomRemove(room: Room) : unit =
        this.To([ BroadcastNames.Rooms ], Remove, BroadcastNames.roomDomId room "list", None, [])

    /// Rooms::OpensController#create: prepend to everyone's `shared_rooms`.
    member this.OpenRoomCreate(room: Room, partials: IPartials) : unit =
        let html = partials.SharedRoom room
        this.To([ BroadcastNames.Rooms ], Prepend, "shared_rooms", Some html, [])

    /// Rooms::OpensController#update: replace `[room, :list]` on `:rooms`. `room` is the room as
    /// an open room (`becomes!(Rooms::Open)`), so the target names that class even when the
    /// room was closed before.
    member this.OpenRoomUpdate(room: Room, partials: IPartials) : unit =
        let html = partials.SharedRoom room
        this.To([ BroadcastNames.Rooms ], Replace, BroadcastNames.roomDomId room "list", Some html, [])

    /// Rooms::ClosedsController#create: render once, prepend to each member's own stream
    /// (`room.users`).
    member this.ClosedRoomCreate(conn: Conn, room: Room, partials: IPartials) : unit =
        let html = partials.SharedRoom room
        for userId in Room.userIds conn room do
            this.To(Broadcasts.UserRooms userId, Prepend, "shared_rooms", Some html, [])

    /// Rooms::ClosedsController#update: after `memberships.revise`, replace `[room, :list]` for
    /// each remaining member (`room` as a closed room).
    member this.ClosedRoomUpdate(conn: Conn, room: Room, partials: IPartials) : unit =
        let html = partials.SharedRoom room
        let target = BroadcastNames.roomDomId room "list"
        for userId in Room.userIds conn room do
            this.To(Broadcasts.UserRooms userId, Replace, target, Some html, [])

    /// Rooms::DirectsController#create: prepend `users/sidebars/rooms/_direct` to each member's
    /// `direct_rooms`, rendered per membership.
    member this.DirectRoomCreate(conn: Conn, room: Room, partials: IPartials) : unit =
        for membership in Room.memberships conn room do
            let html = partials.DirectRoom membership
            this.To(Broadcasts.UserRooms membership.UserId, Prepend, "direct_rooms", Some html, [])

    /// Rooms::InvolvementsController#update (`broadcast_visibility_changes`). `previous` is
    /// `involvement_previously_was` (the current value when the update changed nothing). Rails
    /// raises NoMethodError on a nil previous involvement (`nil.inquiry`) after the update has
    /// saved; that's the `Error`.
    member this.InvolvementChange(room: Room, membership: Membership, previous: Involvement option, partials: IPartials) : Result<unit, NilInquiry> =
        if Room.isDirect room then
            Ok()
        else
            let streamables = Broadcasts.UserRooms membership.UserId
            if Membership.involvedIn membership Involvement.Invisible then
                this.To(streamables, Remove, BroadcastNames.roomDomId room "list", None, [])
                Ok()
            else
                match previous with
                | None -> Error NilInquiry
                | Some Involvement.Invisible ->
                    let html = partials.SharedRoom room
                    this.To(streamables, Prepend, "shared_rooms", Some html, [])
                    Ok()
                | Some _ -> Ok()
