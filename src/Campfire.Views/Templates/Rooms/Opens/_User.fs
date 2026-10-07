// Port of rust/crates/views/templates/rooms/opens/_user.html (reference/app/views/rooms/opens/_user.html.erb)
/// `rooms/opens/_user.html.erb`: a user's row in an open room's form.
module Campfire.Views.Templates.Rooms.Opens._User

open Campfire.Views
open Campfire.Views.Helpers
open Campfire.Views.Messages

let private t0 = Utf8.lit "<li class=\"flex align-center gap margin-none\" data-value=\""
let private t1 = Utf8.lit "\">\n  <figure class=\"avatar flex-item-no-shrink\" style=\"--avatar-size: 4ch;\">\n    <a title=\""
let private t2 = Utf8.lit "\" class=\"btn avatar\" data-turbo-frame=\"_top\" href=\""
let private t3 = Utf8.lit "\"><img aria-hidden=\"true\" loading=\"lazy\" src=\""
let private t4 = Utf8.lit "\" width=\"48\" height=\"48\" /></a>\n  </figure>\n\n  <div class=\"min-width\">\n    <div class=\"overflow-ellipsis fill-shade\"><strong>"
let private t5 = Utf8.lit "</strong></div>\n  </div>\n\n  <hr class=\"separator\" aria-hidden=\"true\">"
let private t6 = Utf8.lit "\n    <img class=\"colorize--black flex-item-no-shrink\" aria-hidden=\"true\" src=\""
let private t7 = Utf8.lit "\" width=\"20\" height=\"20\" />"
let private t8 = Utf8.lit "\n</li>"

let render (w: Out) (ctx: ViewContext) (user: UserView) (canAdminister: bool) : unit =
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
    if canAdminister then
        w.Lit t6
        w.Text(ctx.Asset "check.svg")
        w.Lit t7
    w.Lit t8
