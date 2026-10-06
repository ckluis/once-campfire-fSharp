// Port of rust/crates/views/templates/users/sidebars/rooms/_direct.html (reference/app/views/users/sidebars/rooms/_direct.html.erb)
/// `users/sidebars/rooms/_direct.html.erb` on its own (broadcast when a direct room appears). The reference wraps it in
/// `cache membership do` and assigns `members` on lines Erubi drops, leaving the blank line and indentation below; the
/// cached rendering is `UsersCached.directRoom`.
module Campfire.Views.Templates.Users.Sidebars.Rooms._Direct

open Campfire.Views
open Campfire.Views.Helpers
open Campfire.Views.Users

let private t0 = Utf8.lit "\n  "
let private t1 = Utf8.lit "\n"
let private t2 = Utf8.lit "      <div class=\"avatar__group\">\n"
let private t3 = Utf8.lit "          <span class=\"avatar\">\n            "
let private t4 = Utf8.lit "\n          </span>\n"
let private t5 = Utf8.lit "      </div>\n"
let private t6 = Utf8.lit "      <span class=\"avatar\">\n        "
let private t7 = Utf8.lit "\n      </span>\n"
let private t8 = Utf8.lit "\n    <span class=\"direct__author flex align-center gap max-width min-width border-radius txt-small\">\n      <span class=\"txt-nowrap overflow-ellipsis\">\n        <span class=\"for-screen-reader\">Ping with</span>\n"
let private t9 = Utf8.lit "          "
let private t10 = Utf8.lit "\n"
let private t11 = Utf8.lit "          "
let private t12 = Utf8.lit "\n"
let private t13 = Utf8.lit "      </span>\n    </span>\n"

let render (w: Out) (ctx: ViewContext) (membership: SidebarDirect) : unit =
    w.Lit t0
    Filters.linkToRoom
        w
        membership.RoomId
        (Tag.attrs()
            .Class(membership.ClassNames)
            .Id(Turbo.domIdValue "rooms_direct" membership.RoomId (Some "list"))
            .Data("sorted_list_number", membership.UpdatedAtEpoch))
        (fun w ->
            w.Lit t1
            if membership.Members.Length > 1 then
                w.Lit t2
                for member' in List.truncate 4 membership.Members do
                    w.Lit t3
                    Assets.imageTag w ctx member'.AvatarPath (Tag.attrs().Size(20).AriaHidden())
                    w.Lit t4
                w.Lit t5
            else
                w.Lit t6
                Assets.imageTag w ctx (List.head membership.Members).AvatarPath (Tag.attrs().Size(48).AriaHidden())
                w.Lit t7
            w.Lit t8
            if membership.Members.Length > 1 then
                w.Lit t9
                w.Text membership.MemberInitials
                w.Lit t10
            else
                w.Lit t11
                w.Text (List.head membership.Members).FirstName
                w.Lit t12
            w.Lit t13)
