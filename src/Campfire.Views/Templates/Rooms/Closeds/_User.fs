// Port of rust/crates/views/templates/rooms/closeds/_user.html (reference/app/views/rooms/closeds/_user.html.erb)
/// `rooms/closeds/_user.html.erb`: a user's row in a closed room's form, `selected` when they have access.
module Campfire.Views.Templates.Rooms.Closeds._User

open Campfire.Views
open Campfire.Views.Helpers
open Campfire.Views.Messages
open Campfire.Views.Rooms

let private t0 = Utf8.lit "<li class=\"flex align-center gap margin-none\" data-value=\""
let private t1 = Utf8.lit "\">\n  <figure class=\"avatar flex-item-no-shrink\" style=\"--avatar-size: 4ch;\">\n    <a title=\""
let private t2 = Utf8.lit "\" class=\"btn avatar\" data-turbo-frame=\"_top\" href=\""
let private t3 = Utf8.lit "\"><img aria-hidden=\"true\" loading=\"lazy\" src=\""
let private t4 = Utf8.lit "\" width=\"48\" height=\"48\" /></a>\n  </figure>\n\n  <div class=\"min-width\">\n    <div class=\"overflow-ellipsis fill-shade\"><strong>"
let private t5 = Utf8.lit "</strong></div>\n  </div>\n\n  <hr class=\"separator\" aria-hidden=\"true\">"
let private t6 = Utf8.lit "\n      <input type=\"hidden\" name=\"user_ids[]\" value=\""
let private t7 = Utf8.lit "\" />\n      <img class=\"colorize--black flex-item-no-shrink\" aria-hidden=\"true\" src=\""
let private t8 = Utf8.lit "\" width=\"20\" height=\"20\" />"
let private t9 = Utf8.lit "\n      <label class=\"switch flex-item-no-shrink\">\n        <input type=\"checkbox\" name=\"user_ids[]\" value=\""
let private t10 = Utf8.lit "\" class=\"switch__input\""
let private t11 = Utf8.lit " checked=\"checked\""
let private t12 = Utf8.lit " />\n        <span class=\"switch__btn round\"></span>\n        <span class=\"for-screen-reader\">Give "
let private t13 = Utf8.lit " access to this room</span>\n      </label>"
let private t14 = Utf8.lit "\n</li>"

let render (w: Out) (ctx: ViewContext) (user: UserView) (form: ClosedFormView) (selected: bool) : unit =
    w.Lit t0
    w.Text(Application.toLowercase user.Name)
    w.Lit t1
    w.Text user.Title
    w.Lit t2
    w.Text user.Path
    w.Lit t3
    w.Text user.AvatarUrl
    w.Lit t4
    w.Text user.Name
    w.Lit t5
    if form.CanAdminister then
        if user.Id = form.CurrentUserId && form.Room.Id.IsNone then
            w.Lit t6
            w.Int user.Id
            w.Lit t7
            w.Text(ctx.Asset "check.svg")
            w.Lit t8
        else
            w.Lit t9
            w.Int user.Id
            w.Lit t10
            if selected then
                w.Lit t11
            w.Lit t12
            w.Text user.Name
            w.Lit t13
    w.Lit t14
