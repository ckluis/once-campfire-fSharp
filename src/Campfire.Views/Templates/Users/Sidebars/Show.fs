// Port of rust/crates/views/templates/users/sidebars/show.html (reference/app/views/users/sidebars/show.html.erb)
/// `users/sidebars/show.html.erb`: the sidebar, in its own Turbo Frame (the layout is the page's `content`).
module Campfire.Views.Templates.Users.Sidebars.Show

open Campfire.Routes
open Campfire.Views
open Campfire.Views.Templates.Users.Sidebars.Rooms
open Campfire.Views.Helpers
open Campfire.Views.Users

let private t1 = Utf8.lit "\n  "
let private t2 = Utf8.lit "\n  "
let private t3 = Utf8.lit "\n\n  <div class=\"sidebar__container overflow-y overflow-hide-scrollbar\"\n      data-controller=\"badge-dot\"\n      data-badge-dot-unread-class=\"unread\"\n      data-action=\"rooms-list:unread@window->badge-dot#update rooms-list:read@window->badge-dot#update turbo:submit-start->turbo-frame#unpermanize\">\n    <turbo-frame id=\"direct_rooms_control\" target=\"_top\">\n      <div class=\"directs gap overflow-x overflow-hide-scrollbar\">\n        "
let private t4 = Utf8.lit "\n          <span class=\"avatar avatar--icon\">\n            "
let private t5 = Utf8.lit "\n          </span>\n\n          <span class=\"direct__author flex max-width min-width border-radius pad-inline-half\">\n            <span class=\"for-screen-reader\">New</span>\n            <span class=\"txt-small overflow-clip\">Ping</span>\n          </span>\n"
let private t6 = Utf8.lit "\n        <div id=\"direct_rooms\" contents data-controller=\"sorted-list\" data-action=\"rooms-list:unread@window->sorted-list#updateItem\">\n          "
let private t7 = Utf8.lit "        </div>\n\n        <div contents>\n          "
let private t8 = Utf8.lit "        </div>\n      </div>\n    </turbo-frame>\n\n    <div class=\"rooms position-relative flex flex-column gap\">\n      <div id=\"shared_rooms\" contents data-controller=\"sorted-list\">\n"
let private t9 = Utf8.lit "          "
let private t10 = Utf8.lit "      </div>\n\n"
let private t11 = Utf8.lit "        "
let private t12 = Utf8.lit "\n          "
let private t13 = Utf8.lit "\n"
let private t14 = Utf8.lit "    </div>\n\n    <button class=\"btn sidebar__toggle\" data-action=\"toggle-class#toggle\">\n      "
let private t15 = Utf8.lit "\n      <span class=\"for-screen-reader\">Open menu</span>\n    </button>\n  </div>\n\n  <div class=\"flex align-end sidebar__tools gap justify-end\">\n    "
let private t16 = Utf8.lit "\n      "
let private t17 = Utf8.lit "\n      <span class=\"for-screen-reader\">My Settings</span>\n"
let private t18 = Utf8.lit "\n    "
let private t19 = Utf8.lit "\n      "
let private t20 = Utf8.lit "\n      <span class=\"for-screen-reader\">Account Settings</span>\n"
let private t21 = Utf8.lit "  </div>\n"

/// `content_for :head` (empty).
let head: Out -> unit = ignore

/// The page itself.
let content (w: Out) (ctx: ViewContext) (sidebar: SidebarShow) : unit =
    UsersHelper.sidebarTurboFrameTag w None (fun w ->
        w.Lit t1
        Turbo.turboStreamFrom w sidebar.RoomsStream
        w.Lit t2
        Turbo.turboStreamFrom w sidebar.UserRoomsStream
        w.Lit t3
        Filters.linkTo w (Routes.newRoomsDirect ()) (Tag.attrs().Class("direct direct__new").Data("turbo_frame", "_self")) (fun w ->
            w.Lit t4
            Assets.imageTag w ctx "messages-add.svg" (Tag.attrs().Size(20).AriaHidden().Class("colorize--black"))
            w.Lit t5)
        w.Lit t6
        for membership in sidebar.DirectMemberships do
            UsersCached.cachedDirectRoom w ctx membership
        w.Lit t7
        for user in sidebar.DirectPlaceholderUsers do
            _DirectPlaceholder.render w ctx user
        w.Lit t8
        for room in sidebar.OtherMemberships do
            w.Lit t9
            _Shared.render w room
        w.Lit t10
        if sidebar.CanCreateRooms then
            w.Lit t11
            Filters.linkTo
                w
                (Routes.newRoomsOpen ())
                (Tag.attrs().Class("rooms__new-btn btn room align-center gap txt-reversed").Aria("label", "New Chat Room"))
                (fun w ->
                    w.Lit t12
                    Assets.imageTag w ctx "add.svg" (Tag.attrs().Size(20).AriaHidden().Style("view-transition-name: new-room"))
                    w.Lit t13)
        w.Lit t14
        Assets.imageTag w ctx "menu.svg" (Tag.attrs().Size(20).AriaHidden())
        w.Lit t15
        Filters.linkTo w (Routes.userProfile ()) (Tag.attrs().Class("btn avatar flex-item-no-shrink sidebar__tool")) (fun w ->
            w.Lit t16
            Assets.imageTag
                w
                ctx
                sidebar.CurrentUser.AvatarPath
                (Tag.attrs().Size(48).AriaHidden().Style(Tag.Numbered("view-transition-name: avatar-", sidebar.CurrentUser.Id)))
            w.Lit t17)
        w.Lit t18
        Filters.linkTo w (Routes.editAccount ()) (Tag.attrs().Class("btn align-center gap txt-reversed sidebar__tool")) (fun w ->
            w.Lit t19
            Assets.imageTag w ctx "settings.svg" (Tag.attrs().Size(20).AriaHidden().Style("view-transition-name: account-settings"))
            w.Lit t20)
        w.Lit t21)

let render (w: Out) (ctx: ViewContext) (sidebar: SidebarShow) : unit =
    Campfire.Views.Templates.Layouts.Application.render w ctx None None head ignore (fun w -> content w ctx sidebar) ignore ignore
