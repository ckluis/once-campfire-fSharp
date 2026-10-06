// Port of rust/crates/views/src/helpers/rooms.rs
/// The parts of `RoomsHelper` and `Rooms::InvolvementsHelper` the sidebar and the profile's
/// memberships use; the rest of the rooms helpers are in `Campfire.Views.Rooms`.
module Campfire.Views.Helpers.RoomsHelper

open Campfire.Routes
open Campfire.Views
open Campfire.Views.Helpers.Tag
open Campfire.Views.Helpers.Turbo
open Campfire.Views.Helpers.Url

/// `link_to_room(room, **attributes) { content }`. `options` is the attribute hash in Ruby
/// order, `data-*` entries included where the `data:` key was.
let inline linkToRoom (w: Out) (roomId: int64) (options: Attrs) ([<InlineIfLambda>] content: Out -> unit) : unit =
    let defaults =
        [| "data-rooms-list-target", Text "room"
           "data-room-id", Text(string roomId)
           "data-badge-dot-target", Text "unread"
           "data-sorted-list-target", Text "item" |]
    let url = Routes.room roomId
    contentTagBlock w "a" (Links.linkOptions url (options.WithDefaultData defaults)) content

/// `HUMANIZE_INVOLVEMENT`.
let humanizeInvolvement (involvement: string) : string =
    match involvement with
    | "mentions" -> "Notifying about @ mentions"
    | "everything" -> "Notifying about all messages"
    | "nothing" -> "Notifications are off"
    | "invisible" -> "Notifications are off and room invisible in sidebar"
    | _ -> ""

/// `next_involvement_for(room, involvement:)`.
let nextInvolvement (direct: bool) (involvement: string) : string =
    let order = if direct then [| "everything"; "nothing" |] else [| "mentions"; "everything"; "nothing"; "invisible" |]
    match Array.tryFindIndex ((=) involvement) order with
    | Some index when index + 1 < order.Length -> order[index + 1]
    | _ -> order[0]

/// A room as the involvement helpers see it.
type InvolvementRoom =
    { Id: int64
      /// `model_name.param_key` of the room's class: "rooms_open", "rooms_closed" or "rooms_direct".
      ParamKey: string
      Direct: bool }

/// `button_to_change_involvement(room, involvement)`.
let buttonToChangeInvolvement (w: Out) (ctx: ViewContext) (room: InvolvementRoom) (involvement: string) : unit =
    let labelId = domId room.ParamKey room.Id (Some "involvement_label")
    let url =
        withQuery (Routes.roomInvolvement room.Id) [ "involvement", One(nextInvolvement room.Direct involvement) ]
    let options =
        attrs()
            .Method("put")
            .Role("checkbox")
            .Aria("checked", true)
            .Aria("labelledby", labelId)
            .Tabindex(0)
            .Class("btn " + involvement)
    Forms.buttonToBlock w url options (fun w ->
        Assets.imageTag w ctx ($"notification-bell-{involvement}.svg") (attrs().AriaHidden().Size 20)
        contentTagText w "span" (attrs().Class("for-screen-reader").Id(labelId)) (humanizeInvolvement involvement))
