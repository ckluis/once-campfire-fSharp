// Port of rust/crates/views/templates/rooms/opens/_form.html (reference/app/views/rooms/opens/_form.html.erb)
/// `rooms/opens/_form.html.erb`: the access list of an open room, in the shared room form.
module Campfire.Views.Templates.Rooms.Opens._Form

open Campfire.Views
open Campfire.Views.Templates.Rooms
open Campfire.Views.Helpers
open Campfire.Views.Messages
open Campfire.Views.Rooms

let private t0 = Utf8.lit "\n    <li class=\"flex align-center gap margin-none\">\n      <figure class=\"avatar flex-item-no-shrink\" style=\"--avatar-border-radius: 0; --avatar-size: 4ch;\">\n        <img aria-hidden=\"true\" class=\"colorize--black\" style=\"background-color: transparent\" src=\""
let private t1 = Utf8.lit "\" />\n        <span class=\"for-screen-reader\">Everyone</span>\n      </figure>\n\n      <div class=\"min-width\">\n        <div class=\"overflow-ellipsis fill-shade\"><strong>Everyone</strong></div>\n      </div>\n\n      <hr class=\"separator\" aria-hidden=\"true\">"
let private t2 = Utf8.lit "\n        <a class=\"btn--faux flex-inline\" tabindex=\"-1\" data-turbo-action=\"replace\" href=\""
let private t3 = Utf8.lit "\">\n          <label for=\"room_type\" class=\"switch\">\n            <input type=\"checkbox\" id=\"room_type\" class=\"switch__input\" checked=\"checked\">\n            <span class=\"switch__btn round\"></span>\n            <span class=\"for-screen-reader\">Give only some access to this room</span>\n          </label>\n</a>"
let private t4 = Utf8.lit "\n    </li>\n\n    <hr class=\"separator full-width\" style=\"--border-style: solid\">"
let private t5 = Utf8.lit "\n    "
let private t6 = Utf8.lit "\n\n    <div data-filter-target=\"list\" contents>"
let private t7 = Utf8.lit "\n      "
let private t8 = Utf8.lit "\n    </div>\n"

let render (w: Out) (ctx: ViewContext) (form: OpenFormView) (typeChangePath: string) : unit =
    Layouts._Form.render w ctx form.Room form.CanAdminister Open (fun w ->
        UsersHelper.userFilterMenuTag w (fun w ->
            w.Lit t0
            w.Text(ctx.Asset "everyone.svg")
            w.Lit t1
            if form.CanAdminister then
                w.Lit t2
                w.Text typeChangePath
                w.Lit t3
            w.Lit t4
            if form.Users.Length > 20 then
                w.Lit t5
                UsersHelper.userFilterSearchTag w
            w.Lit t6
            for user in form.Users do
                w.Lit t7
                _User.render w ctx user form.CanAdminister
            w.Lit t8))
