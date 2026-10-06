// Port of rust/crates/views/templates/users/push_subscriptions/_push_subscription.html (reference/app/views/users/push_subscriptions/_push_subscription.html.erb)
/// `users/push_subscriptions/_push_subscription.html.erb`: a device's push subscription.
module Campfire.Views.Templates.Users.PushSubscriptions._PushSubscription

open Campfire.Routes
open Campfire.Views
open Campfire.Views.Helpers
open Campfire.Views.Users

let private t0 = Utf8.lit "<li class=\"flex flex-column margin-none membership-item\">\n  <span class=\"overflow-ellipsis txt-primary txt-undecorated\">\n    <strong>"
let private t1 = Utf8.lit " "
let private t2 = Utf8.lit " on "
let private t3 = Utf8.lit "</strong><br>\n  </span>\n\n  <span class=\"flex align-start gap txt-small\">\n    <span>"
let private t4 = Utf8.lit "</span>\n\n    <span class=\"flex align-center gap\">\n      "
let private t5 = Utf8.lit "\n        "
let private t6 = Utf8.lit "\n        <span class=\"for-screen-reader\">Send test notification</span>\n"
let private t7 = Utf8.lit "\n      "
let private t8 = Utf8.lit "\n        "
let private t9 = Utf8.lit "\n        <span class=\"for-screen-reader\">Delete subscription</span>\n"
let private t10 = Utf8.lit "    </span>\n  </span>\n</li>\n"

let render (w: Out) (ctx: ViewContext) (pushSubscription: PushSubscription) : unit =
    w.Lit t0
    w.Text pushSubscription.Browser
    w.Lit t1
    w.Text pushSubscription.Version
    w.Lit t2
    w.Text pushSubscription.Platform
    w.Lit t3
    w.Text pushSubscription.Endpoint
    w.Lit t4
    Filters.buttonTo
        w
        (Routes.userPushSubscriptionTestNotifications pushSubscription.Id)
        (Tag.attrs().Class("btn btn--reversed"))
        (fun w ->
            w.Lit t5
            Assets.imageTag w ctx "notification-bell-everything.svg" (Tag.attrs().AriaHidden().Size(20))
            w.Lit t6)
    w.Lit t7
    Filters.buttonTo w (Routes.userPushSubscription pushSubscription.Id) (Tag.attrs().Method("delete").Class("btn btn--negative")) (fun w ->
        w.Lit t8
        Assets.imageTag w ctx "minus.svg" (Tag.attrs().AriaHidden().Size(20))
        w.Lit t9)
    w.Lit t10
