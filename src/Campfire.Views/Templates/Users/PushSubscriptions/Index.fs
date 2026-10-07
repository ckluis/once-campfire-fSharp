// Port of rust/crates/views/templates/users/push_subscriptions/index.html (reference/app/views/users/push_subscriptions/index.html.erb)
/// `users/push_subscriptions/index.html.erb`: the signed-in user's push subscriptions.
module Campfire.Views.Templates.Users.PushSubscriptions.Index

open Campfire.Views
open Campfire.Views.Helpers
open Campfire.Views.Users

let private t0 = Utf8.lit "  <div class=\"flex-item-justify-start\">\n    "
let private t1 = Utf8.lit "\n  </div>\n"
let private t2 = Utf8.lit "<section class=\"panel panel--wide flex flex-column gap\">\n  <h1 class=\"txt-align-center txt-large margin-none\">Push Notification Subscriptions</h1>\n  <div class=\"pad-inline fill-shade border-radius\" id=\"push_subscriptions\">\n    <menu class=\"pad flex flex-column gap\">\n      "
let private t3 = Utf8.lit "    </menu>\n  </div>\n</section>\n"

/// `@page_title`.
let pageTitle: string option = Some "Push notification subscriptions"

/// `content_for :head` (empty).
let head: Out -> unit = ignore

/// `content_for :nav`.
let nav (w: Out) (ctx: ViewContext) : unit =
    w.Lit t0
    Application.linkBackToLastRoomVisited w ctx
    w.Lit t1

/// The page itself.
let content (w: Out) (ctx: ViewContext) (pushSubscriptions: PushSubscription list) : unit =
    w.Lit t2
    for pushSubscription in pushSubscriptions do
        _PushSubscription.render w ctx pushSubscription
    w.Lit t3

let render (w: Out) (ctx: ViewContext) (pushSubscriptions: PushSubscription list) : unit =
    Templates.Layouts.Application.render
        w
        ctx
        pageTitle
        None
        head
        (fun w -> nav w ctx)
        (fun w -> content w ctx pushSubscriptions)
        ignore
        ignore
