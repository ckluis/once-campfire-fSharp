// Port of rust/crates/views/templates/rooms/involvements/_bell.html (reference/app/views/rooms/involvements/_bell.html.erb)
/// `rooms/involvements/_bell.html.erb`: the notifications bell of a room's nav, and the dialog for blocked notifications.
module Campfire.Views.Templates.Rooms.Involvements._Bell

open Campfire.Routes
open Campfire.Views
open Campfire.Views.Messages
open Campfire.Views.Rooms

let private involvementPrefix = Utf8.lit "involvement_"

let private t0 = Utf8.lit "<span>\n  <span class=\"button_to_change_notifying\"\n      data-controller=\"notifications\" data-notifications-subscriptions-url-value=\""
let private t1 = Utf8.lit "\" data-notifications-attention-class=\"btn--pulsing\">\n    <turbo-frame data-controller=\"turbo-frame\" data-action=\"notifications:ready@window-&gt;turbo-frame#load\" data-turbo-frame-url-param=\""
let private t2 = Utf8.lit "\" id=\""
let private t3 = Utf8.lit "\">\n      <button class=\"btn\" data-action=\"click->notifications#attemptToSubscribe\" data-notifications-target=\"bell\">\n        <img aria-hidden=\"true\" src=\""
let private t4 = Utf8.lit "\" width=\"20\" height=\"20\" />\n        <img aria-hidden=\"true\" hidden=\"hidden\" src=\""
let private t5 = Utf8.lit "\" width=\"20\" height=\"20\" />\n        <span class=\"for-screen-reader\">Notification settings for this "
let private t6 = Utf8.lit "</span>\n      </button>\n</turbo-frame>\n    <dialog data-notifications-target=\"notAllowedNotice\" class=\"dialog pad center center-block border-radius border shadow\" style=\"--inline-space: var(--block-space)\">\n      <div class=\"flex flex-column txt-align-center\">\n        <span class=\"btn btn--faux center txt-x-large\">\n          <img aria-hidden=\"true\" src=\""
let private t7 = Utf8.lit "\" width=\"48\" height=\"48\" />\n          <span class=\"for-screen-reader\">Notifications alert</span>\n        </span>\n\n        <section>\n          <h1 class=\"txt-large margin-none\">Notifications aren’t allowed</h1>\n          <div class=\"txt-align-start margin-block-start\">\n            "
let private t8 = Utf8.lit "\n            "
let private t9 = Utf8.lit "\n            "
let private t10 = Utf8.lit "\n          </div>\n        </section>\n\n        <form method=\"dialog\" class=\"flex align-center gap center\">\n          <button class=\"btn dialog__close\" autofocus=\"true\">\n            <span class=\"for-screen-reader\">Close</span>\n            <img aria-hidden=\"true\" src=\""
let private t11 = Utf8.lit "\" width=\"20\" height=\"20\" />\n          </button>\n        </form>\n      </div>\n    </dialog>\n  </span>\n</span>"

let render (w: Out) (ctx: ViewContext) (room: RoomView) : unit =
    w.Lit t0
    w.Text(Routes.userPushSubscriptions ())
    w.Lit t1
    w.Text(Routes.roomInvolvement room.Id)
    w.Lit t2
    writeRoomDomId w involvementPrefix room.Kind room.Id
    w.Lit t3
    w.Text(ctx.Asset "notification-bell-loading.svg")
    w.Lit t4
    w.Text(ctx.Asset "notification-bell-alert.svg")
    w.Lit t5
    w.Text room.Noun
    w.Lit t6
    w.Text(ctx.Asset "notification-bell-alert.svg")
    w.Lit t7
    Templates.Pwa._BrowserSettings.render w ctx
    w.Lit t8
    Templates.Pwa._SystemSettings.render w ctx
    w.Lit t9
    Templates.Pwa._InstallInstructions.render w ctx
    w.Lit t10
    w.Text(ctx.Asset "remove.svg")
    w.Lit t11
