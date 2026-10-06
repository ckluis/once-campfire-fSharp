// Port of rust/crates/views/templates/rooms/layouts/_form.html (reference/app/views/rooms/layouts/_form.html.erb)
/// `rooms/layouts/_form.html.erb`, used as a block wrapped around the open and closed room forms' fields (`content`).
module Campfire.Views.Templates.Rooms.Layouts._Form

open Campfire.Views
open Campfire.Views.Helpers
open Campfire.Views.Messages
open Campfire.Views.Rooms

let private t0 = Utf8.lit "<form action=\""
let private t1 = Utf8.lit "\" accept-charset=\"UTF-8\" method=\"post\">"
let private t2 = Utf8.lit "<input type=\"hidden\" name=\"_method\" value=\"patch\" />"
let private t3 = Utf8.lit "\n  <div class=\"flex align-center gap\">"
let private t4 = Utf8.lit "\n      "
let private t5 = Utf8.lit "\n\n      <label class=\"flex-item-grow txt-large\">\n        <input name=\"room[name]\" id=\"room_name\" class=\"input full-width\" required=\"required\" autofocus=\"autofocus\" placeholder=\"Name the room\" data-turbo-permanent=\"true\" data-action=\"keydown.enter-&gt;form#submit:prevent\" type=\"text\""
let private t6 = Utf8.lit " value=\""
let private t7 = Utf8.lit "\""
let private t8 = Utf8.lit " />\n        <span class=\"for-screen-reader\">Name this room</span>\n      </label>"
let private t9 = Utf8.lit "\n      <h1 class=\"flex-item-grow txt-x-large\">\n        "
let private t10 = Utf8.lit "\n      </h1>"
let private t11 = Utf8.lit "\n  </div>\n\n  <hr class=\"margin-block borderless\">\n\n  <section class=\"room-access margin-block pad-inline fill-shade border-radius\">\n    "
let private t12 = Utf8.lit "\n  </section>"
let private t13 = Utf8.lit "\n  <button name=\"button\" type=\"submit\" class=\"btn btn--reversed txt-large center\"><img aria-hidden=\"true\" src=\""
let private t14 = Utf8.lit "\" width=\"20\" height=\"20\" /><span class=\"for-screen-reader\">Save</span></button>"
let private t15 = Utf8.lit "\n</form>"

let render (w: Out) (ctx: ViewContext) (room: FormRoom) (canAdminister: bool) (kind: RoomKind) (content: Out -> unit) : unit =
    w.Lit t0
    w.Text(room.Action kind)
    w.Lit t1
    if room.Id.IsSome then
        w.Lit t2
    w.Lit t3
    if canAdminister then
        w.Lit t4
        Translations.translationButton w ctx "room_name"
        w.Lit t5
        match room.Name with
        | Some name ->
            w.Lit t6
            w.Text name
            w.Lit t7
        | None -> ()
        w.Lit t8
    else
        w.Lit t9
        w.Text room.DisplayName
        w.Lit t10
    w.Lit t11
    content w
    w.Lit t12
    if canAdminister then
        w.Lit t13
        w.Text(ctx.Asset "check.svg")
        w.Lit t14
    w.Lit t15
