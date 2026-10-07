// Port of rust/crates/views/templates/accounts/_invite.html (reference/app/views/accounts/_invite.html.erb)
/// `accounts/_invite.html.erb`: the join link with its QR code, copy and share buttons, on the
/// account page and the original room's invitation.
module Campfire.Views.Templates.Accounts._Invite

open Campfire.Routes
open Campfire.Views
open Campfire.Views.Helpers

let private t0 = Utf8.lit "<div class=\"flex flex-column align-center gap\">\n"
let private t1 = Utf8.lit "\n  <label class=\"flex flex-column gap full-width\" style=\"--row-gap: 0.5em\">\n    <strong id=\"invite_label\" class=\"invite-label\">Share to invite more people</strong>\n    <span class=\"flex align-center gap input input--actor fill-white\">\n      "
let private t2 = Utf8.lit "\n      <input type=\"text\" class=\"input\" id=\"invite_url\" value=\""
let private t3 = Utf8.lit "\" aria-labelledby=\"invite_label\" readonly>\n    </span>\n  </label>\n\n  <div class=\"flex align-center gap\">\n    "
let private t4 = Utf8.lit "\n      <span class=\"for-screen-reader\">Show join link QR code</span>\n      "
let private t5 = Utf8.lit "\n"
let private t6 = Utf8.lit "\n    "
let private t7 = Utf8.lit "\n      <span class=\"for-screen-reader\">Copy join link</span>\n      "
let private t8 = Utf8.lit "\n"
let private t9 = Utf8.lit "\n    "
let private t10 = Utf8.lit "\n      <span class=\"for-screen-reader\">Share join link</span>\n      "
let private t11 = Utf8.lit "\n"
let private t12 = Utf8.lit "\n"
let private t13 = Utf8.lit "      "
let private t14 = Utf8.lit "\n        "
let private t15 = Utf8.lit "\n        <span class=\"for-screen-reader\">Regenerate join link</span>\n"
let private t16 = Utf8.lit "  </div>\n</div>\n"

let render (w: Out) (ctx: ViewContext) (joinCode: string) : unit =
    w.Lit t0
    let url = ctx.Url(Routes.join joinCode)
    w.Lit t1
    Assets.imageTag w ctx "person-add.svg" (Tag.attrs().AriaHidden().Size(20).Class("colorize--black"))
    w.Lit t2
    w.Text url
    w.Lit t3
    Filters.linkToZoomQrCode w url (fun w ->
        w.Lit t4
        Assets.imageTag w ctx "qr-code.svg" (Tag.attrs().AriaHidden().Size(20).Class("colorize--black"))
        w.Lit t5)
    w.Lit t6
    Filters.buttonToCopyToClipboard w url (fun w ->
        w.Lit t7
        Assets.imageTag w ctx "copy-paste.svg" (Tag.attrs().AriaHidden().Size(20).Class("colorize--black"))
        w.Lit t8)
    w.Lit t9
    Filters.webShareSessionButton w url "Link to join Campfire" "Hit this link to join me in Campfire and start chatting." (fun w ->
        w.Lit t10
        Assets.imageTag w ctx "share.svg" (Tag.attrs().AriaHidden().Size(20).Class("colorize--black"))
        w.Lit t11)
    w.Lit t12
    if ctx.CanAdminister then
        w.Lit t13
        Filters.buttonTo w (Routes.accountJoinCode ()) (Tag.attrs().Class("btn btn--regenerate")) (fun w ->
            w.Lit t14
            Assets.imageTag w ctx "refresh.svg" (Tag.attrs().AriaHidden().Size(20).Class("colorize--black"))
            w.Lit t15)
    w.Lit t16
