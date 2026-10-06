// Port of rust/crates/views/templates/rooms/show/_nav.html (reference/app/views/rooms/show/_nav.html.erb)
/// `rooms/show/_nav.html.erb` (`content_for :nav`): the room's name, its settings link and the notifications bell.
module Campfire.Views.Templates.Rooms.Show._Nav

open Campfire.Views
open Campfire.Views.Templates.Rooms
open Campfire.Views.Helpers
open Campfire.Views.Rooms

let private t0 = Utf8.lit "\n  "
let private t1 = Utf8.lit "\n\n  <span class=\"btn btn--reversed btn--faux room--current\">\n    <h1 class=\"room__contents txt-medium overflow-ellipsis\">"
let private t2 = Utf8.lit "\n        <span class=\"for-screen-reader\">Ping with</span>"
let private t3 = Utf8.lit "\n\n      "
let private t4 = Utf8.lit "\n    </h1>\n</span>\n\n  <a class=\"btn\" style=\"view-transition-name: edit-room-"
let private t5 = Utf8.lit "\" data-room-id=\""
let private t6 = Utf8.lit "\" href=\""
let private t7 = Utf8.lit "\">\n    <img aria-hidden=\"true\" src=\""
let private t8 = Utf8.lit "\" width=\"20\" height=\"20\" />\n    <span class=\"for-screen-reader\">Settings for this "
let private t9 = Utf8.lit "</span>\n</a>\n\n  "

let render (w: Out) (ctx: ViewContext) (room: RoomView) : unit =
    if ctx.Account.HasLogo then
        w.Lit t0
        UsersHelper.accountLogoTag w ctx None
    w.Lit t1
    if room.IsDirect then
        w.Lit t2
    w.Lit t3
    w.Text room.DisplayName
    w.Lit t4
    w.Int room.Id
    w.Lit t5
    w.Int room.Id
    w.Lit t6
    w.Text room.EditPath
    w.Lit t7
    w.Text(ctx.Asset "menu-dots-horizontal.svg")
    w.Lit t8
    w.Text room.Noun
    w.Lit t9
    Involvements._Bell.render w ctx room
