// Port of rust/crates/views/templates/rooms/involvements/show.html (reference/app/views/rooms/involvements/show.html.erb)
/// `rooms/involvements/show.html.erb`, with `Rooms::InvolvementsHelper`.
module Campfire.Views.Templates.Rooms.Involvements.Show

open Campfire.Routes
open Campfire.Views
open Campfire.Views.Helpers
open Campfire.Views.Messages
open Campfire.Views.Rooms

let private involvementPrefix = Utf8.lit "involvement_"

let private t0 = Utf8.lit "<turbo-frame data-controller=\"turbo-frame\" data-action=\"notifications:ready@window-&gt;turbo-frame#load\" data-turbo-frame-url-param=\""
let private t1 = Utf8.lit "\" id=\""
let private t2 = Utf8.lit "\">\n  "
let private t3 = Utf8.lit "\n</turbo-frame>"

let render (w: Out) (ctx: ViewContext) (involvement: InvolvementView) : unit =
    w.Lit t0
    w.Text(Routes.roomInvolvement involvement.RoomId)
    w.Lit t1
    writeRoomDomId w involvementPrefix involvement.Kind involvement.RoomId
    w.Lit t2
    let room: RoomsHelper.InvolvementRoom =
        { Id = involvement.RoomId
          ParamKey = RoomKind.paramKey involvement.Kind
          Direct = RoomKind.isDirect involvement.Kind }
    RoomsHelper.buttonToChangeInvolvement w ctx room involvement.Involvement
    w.Lit t3
