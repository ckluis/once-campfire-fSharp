// Port of rust/crates/views/templates/users/profiles/_membership.html (reference/app/views/users/profiles/_membership.html.erb)
/// `users/profiles/_membership.html.erb`: a room on the profile, with the button that changes its notifications.
module Campfire.Views.Templates.Users.Profiles._Membership

open Campfire.Routes
open Campfire.Views
open Campfire.Views.Helpers
open Campfire.Views.Users

let private t0 = Utf8.lit "<li class=\"flex align-center gap margin-none min-width membership-item\">\n  "
let private t1 = Utf8.lit "\n    <strong>"
let private t2 = Utf8.lit "</strong>\n"
let private t3 = Utf8.lit "\n  <hr class=\"separator\" aria-hidden=\"true\">\n\n  <span class=\"txt-small\">\n    "
let private t4 = Utf8.lit "\n      "
let private t5 = Utf8.lit "\n"
let private t6 = Utf8.lit "  </span>\n</li>\n"

let render (w: Out) (ctx: ViewContext) (membership: ProfileMembership) : unit =
    w.Lit t0
    Filters.linkTo
        w
        (Routes.room membership.RoomId)
        (Tag.attrs().Class("overflow-ellipsis fill-shade txt-primary txt-undecorated"))
        (fun w ->
            w.Lit t1
            w.Text membership.RoomDisplayName
            w.Lit t2)
    w.Lit t3
    Filters.turboFrameTagValue w (Turbo.domIdValue membership.RoomParamKey membership.RoomId (Some "involvement")) (Tag.attrs()) (fun w ->
        w.Lit t4
        RoomsHelper.buttonToChangeInvolvement w ctx membership.InvolvementRoom membership.Involvement
        w.Lit t5)
    w.Lit t6
