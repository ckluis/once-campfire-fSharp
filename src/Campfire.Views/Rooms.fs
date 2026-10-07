// Port of rust/crates/views/src/rooms.rs (the view-models and helpers; the templates they feed are
// modules under Templates/Rooms)
/// Views for `reference/app/views/rooms`, plus `RoomsHelper`, `Rooms::InvolvementsHelper` and the
/// `MessagesHelper` tags the room screen uses.
module Campfire.Views.Rooms

open System
open Campfire.RailsCompat
open Campfire.Routes
open Campfire.Views.Helpers
open Campfire.Views.Messages

/// `room_display_name(room, for_user:)`: a direct room is named after its other members
/// (`room.users.without(for_user).pluck(:name).to_sentence`), falling back to the user's own
/// name when they're alone in it.
let roomDisplayName (name: string option) (direct: bool) (otherMemberNames: string list) (forUserName: string option) : string =
    if direct then
        let sentence = Application.toSentence otherMemberNames " and "
        if String.IsNullOrWhiteSpace sentence then defaultArg forUserName "" else sentence
    else
        defaultArg name ""

/// `mention_prompt_tag(room)`'s `src`: `autocompletable_users_path(room_id: room.id)`.
let mentionPromptSrc (roomId: int64) : string = $"{Routes.autocompletableUsers ()}?room_id={roomId}"

/// A persisted room.
type RoomView =
    { Id: int64
      Kind: RoomKind
      Name: string option
      /// `room_display_name(room)` for `Current.user`.
      DisplayName: string }

    member this.DomId(prefix: string) : string = roomDomId this.Kind this.Id prefix

    member this.IsDirect: bool = RoomKind.isDirect this.Kind

    /// `edit_polymorphic_path(room)`: `/rooms/opens/1/edit` and so on.
    member this.EditPath: string =
        match this.Kind with
        | Open -> Routes.editRoomsOpen this.Id
        | Closed -> Routes.editRoomsClosed this.Id
        | Direct -> Routes.editRoomsDirect this.Id

    /// "Ping" for direct rooms, "room" otherwise.
    member this.Noun: string = if this.IsDirect then "Ping" else "room"

/// What `rooms/show` shows.
type ShowView =
    { Room: RoomView
      /// `room.updated_at`, the refresh controller's `loaded_at`.
      UpdatedAt: Timestamp
      /// `Current.user`, for the client-side message template.
      User: UserView
      Messages: MessageItem list
      /// `@room == Room.original && !@room.messages.paged?` (`rooms/show/_invitation`).
      Invitation: bool
      /// `Current.account.join_code`, for the invitation's join link.
      JoinCode: string
      /// `Turbo::StreamsChannel.signed_stream_name([room, :messages])`.
      MessagesStreamName: string }

/// `Membership#involvement`.
type InvolvementView =
    { RoomId: int64
      Kind: RoomKind
      /// "mentions", "everything", "nothing" or "invisible".
      Involvement: string }

/// What `rooms/refreshes/show` streams: messages created and updated since the client loaded.
type RefreshView =
    { RoomId: int64
      RoomKind: RoomKind
      NewMessages: MessageItem list
      UpdatedMessages: MessageItem list }

/// The room being created or edited by the open and closed room forms. `Id` is `None` for a
/// new record.
type FormRoom =
    { Id: int64 option
      Name: string option }

    /// `form_with model: room`'s action for an open or closed room.
    member this.Action(kind: RoomKind) : string =
        match this.Id, kind with
        | Some id, Open -> Routes.roomsOpen id
        | Some id, _ -> Routes.roomsClosed id
        | None, Open -> Routes.roomsOpens ()
        | None, _ -> Routes.roomsCloseds ()

    member this.DisplayName: string = defaultArg this.Name ""

/// `rooms/opens/{new,edit}`.
type OpenFormView =
    { Room: FormRoom
      /// `Current.user.can_administer?(room)`: administrators, the creator, or a new room.
      CanAdminister: bool
      /// `User.active.ordered`.
      Users: UserView list }

/// `rooms/closeds/{new,edit}`.
type ClosedFormView =
    { Room: FormRoom
      CanAdminister: bool
      CurrentUserId: int64
      /// Active users with access (none for a new room).
      SelectedUsers: UserView list
      /// The other active users.
      UnselectedUsers: UserView list }

type DirectEditView =
    { RoomId: int64
      /// `room_display_name(@room)` for `Current.user`.
      DisplayName: string
      /// `@room.users.many? ? @room.users.without(Current.user) : @room.users`.
      Users: UserView list }

let private trashIcon = Utf8.lit "<img aria-hidden=\"true\" src=\""
let private trashIconEnd = Utf8.lit "\" width=\"20\" height=\"20\" /><span class=\"overflow-ellipsis\">"
let private trashEnd = Utf8.lit "</span>"

/// `button_to_delete_room(room)`.
let buttonToDeleteRoom (w: Out) (ctx: ViewContext) (roomId: int64) (displayName: string) : unit =
    let url = ctx.Url(Routes.room roomId)
    let options =
        Tag.attrs()
            .Method("delete")
            .Class("btn btn--negative max-width")
            .Aria("label", $"Delete {displayName}")
            .Data("turbo_confirm", "Are you sure you want to delete this room and all messages in it? This can’t be undone.")
    Forms.buttonToBlock w url options (fun w ->
        w.Lit trashIcon
        w.Text(ctx.Asset "trash.svg")
        w.Lit trashIconEnd
        w.Text displayName
        w.Lit trashEnd)
