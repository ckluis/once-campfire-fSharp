// Port of rust/crates/views/templates/users/sidebars/rooms/_direct_placeholder.html (reference/app/views/users/sidebars/rooms/_direct_placeholder.html.erb)
/// `users/sidebars/rooms/_direct_placeholder.html.erb`: a button to start a ping with a user.
module Campfire.Views.Templates.Users.Sidebars.Rooms._DirectPlaceholder

open Campfire.Views
open Campfire.Views.Helpers
open Campfire.Views.Users

let private t0 = Utf8.lit "\n  <span class=\"avatar\">\n    "
let private t1 = Utf8.lit "\n  </span>\n\n  <span class=\"direct__author flex align-center gap max-width min-width border-radius txt-small\">\n    <span class=\"txt-nowrap overflow-ellipsis\">\n      <span class=\"for-screen-reader\">Start a ping with</span>\n      "
let private t2 = Utf8.lit "\n    </span>\n  </span>\n"

let render (w: Out) (ctx: ViewContext) (user: UserSummary) : unit =
    Filters.buttonTo w (Url.roomsDirectsWithUser user.Id) (Tag.attrs().Class("direct borderless fill-transparent unpad")) (fun w ->
        w.Lit t0
        Assets.imageTag w ctx user.AvatarPath (Tag.attrs().AriaHidden())
        w.Lit t1
        w.Text user.FirstName
        w.Lit t2)
