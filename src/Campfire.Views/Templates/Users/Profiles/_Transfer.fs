// Port of rust/crates/views/templates/users/profiles/_transfer.html (reference/app/views/users/profiles/_transfer.html.erb)
/// `users/profiles/_transfer.html.erb`: the link that signs a user in on another device.
module Campfire.Views.Templates.Users.Profiles._Transfer

open Campfire.Routes
open Campfire.Views
open Campfire.Views.Helpers
open Campfire.Views.Users

let private t0 = Utf8.lit "\n<fieldset>\n  <legend class=\"gap\">\n    "
let private t1 = Utf8.lit "\n    "
let private t2 = Utf8.lit "\n    "
let private t3 = Utf8.lit "\n  </legend>\n\n\n  <div class=\"flex flex-column gap\">\n"
let private t4 = Utf8.lit "      <div class=\"flex align-center gap justify-center\">\n        "
let private t5 = Utf8.lit "\n        <label for=\"session_transfer_url\">Share to get them back into their account</label>\n      </div>\n"
let private t6 = Utf8.lit "      <label for=\"session_transfer_url\" class=\"for-screen-reader\">Use this link to login automatically on another device</label>\n"
let private t7 = Utf8.lit "\n    <input type=\"text\" class=\"input\" value=\""
let private t8 = Utf8.lit "\" id=\"session_transfer_url\" readonly>\n\n    <div class=\"flex align-center center gap\">\n      "
let private t9 = Utf8.lit "\n        <span class=\"for-screen-reader\">Show auto-login QR code</span>\n        "
let private t10 = Utf8.lit "\n"
let private t11 = Utf8.lit "\n      "
let private t12 = Utf8.lit "\n        <span class=\"for-screen-reader\">Copy auto-login link</span>\n        "
let private t13 = Utf8.lit "\n"
let private t14 = Utf8.lit "\n      "
let private t15 = Utf8.lit "\n        <span class=\"for-screen-reader\">Share auto-login link</span>\n        "
let private t16 = Utf8.lit "\n"
let private t17 = Utf8.lit "    </div>\n  </div>\n</fieldset>\n"

let render (w: Out) (ctx: ViewContext) (user: UserSummary) (transferId: string) : unit =
    let url = ctx.Url(Routes.sessionTransfer transferId)
    w.Lit t0
    Assets.imageTag w ctx "laptop.svg" (Tag.attrs().AriaHidden().Size(36).Class("colorize--black"))
    w.Lit t1
    Assets.imageTag w ctx "transfer.svg" (Tag.attrs().AriaHidden().Size(36).Class("colorize--black"))
    w.Lit t2
    Assets.imageTag w ctx "mobile-phone.svg" (Tag.attrs().AriaHidden().Size(36).Class("colorize--black"))
    w.Lit t3
    if not (ctx.IsCurrentUser user.Id) then
        w.Lit t4
        Assets.imageTag w ctx "crown.svg" (Tag.attrs().Size(16).AriaHidden().Class("flex-item-no-shrink colorize--black"))
        w.Lit t5
    else
        w.Lit t6
    w.Lit t7
    w.Text url
    w.Lit t8
    Filters.linkToZoomQrCode w url (fun w ->
        w.Lit t9
        Assets.imageTag w ctx "qr-code.svg" (Tag.attrs().AriaHidden().Size(20).Class("colorize--black"))
        w.Lit t10)
    w.Lit t11
    Filters.buttonToCopyToClipboard w url (fun w ->
        w.Lit t12
        Assets.imageTag w ctx "copy-paste.svg" (Tag.attrs().AriaHidden().Size(20).Class("flex-item-no-shrink colorize--black"))
        w.Lit t13)
    w.Lit t14
    Filters.webShareSessionButton
        w
        url
        "Your sign-in link"
        "This is your own private sign-in URL, DO NOT SHARE IT. Use it to sign-in on another device or if you get locked out."
        (fun w ->
            w.Lit t15
            Assets.imageTag w ctx "share.svg" (Tag.attrs().AriaHidden().Size(20).Class("flex-item-no-shrink colorize--black"))
            w.Lit t16)
    w.Lit t17
