// Port of rust/crates/views/templates/users/show.html (reference/app/views/users/show.html.erb)
/// `users/show.html.erb`: a person's page.
module Campfire.Views.Templates.Users.Show

open Campfire.Routes
open Campfire.Views
open Campfire.Views.Helpers
open Campfire.Views.Users

let private t0 = Utf8.lit "  <div class=\"flex-item-justify-start\">\n    "
let private t1 = Utf8.lit "\n  </div>\n\n  <div class=\"flex align-center gap flex-item-justify-end\">\n"
let private t2 = Utf8.lit "      "
let private t3 = Utf8.lit "\n        "
let private t4 = Utf8.lit "\n        <span class=\"for-screen-reader\">Edit my profile</span>\n"
let private t5 = Utf8.lit "  </div>\n"
let private t6 = Utf8.lit "<section class=\"panel txt-align-center\">\n  <div class=\"flex flex-column gap "
let private t7 = Utf8.lit "banned"
let private t8 = Utf8.lit "\">\n    <div class=\"avatar txt-xx-large center\" style=\"background: white\">\n      "
let private t9 = Utf8.lit "\n    </div>\n\n"
let private t10 = Utf8.lit "      <div class=\"pad-double--inline push--inline push--block-start\">\n"
let private t11 = Utf8.lit "          "
let private t12 = Utf8.lit "\n"
let private t13 = Utf8.lit "          <div>"
let private t14 = Utf8.lit " is no longer on this account</div>\n"
let private t15 = Utf8.lit "      </div>\n"
let private t16 = Utf8.lit "        <div class=\"flex flex-column gap\" style=\"--row-gap: calc(var(--block-space) / 3)\">\n          <h1 class=\"txt-x-large txt-tight-lines margin-none\">"
let private t17 = Utf8.lit "</h1>\n"
let private t18 = Utf8.lit "            <div>"
let private t19 = Utf8.lit "</div>\n"
let private t20 = Utf8.lit "          <div>"
let private t21 = Utf8.lit "</div>\n        </div>\n\n"
let private t22 = Utf8.lit "          <div class=\"pad-inline-double margin-inline margin-block-start\">\n            "
let private t23 = Utf8.lit "\n              "
let private t24 = Utf8.lit "\n"
let private t25 = Utf8.lit "          </div>\n\n"
let private t26 = Utf8.lit "            <hr class=\"margin-block-start borderless\">\n\n            "
let private t27 = Utf8.lit "\n"
let private t28 = Utf8.lit "\n"
let private t29 = Utf8.lit "          <div class=\"margin-block-start\">\n            "
let private t30 = Utf8.lit "\n          </div>\n"
let private t31 = Utf8.lit "        <div>\n          <h1 class=\"txt-x-large margin-none\">"
let private t32 = Utf8.lit "</h1>\n          <div>"
let private t33 = Utf8.lit " is no longer on this account</div>\n        </div>\n"
let private t34 = Utf8.lit "  </div>\n</section>\n"

/// `@page_title`.
let pageTitle (user: UserSummary) : string option = Some user.Name

/// `content_for :head` (empty).
let head: Out -> unit = ignore

/// `content_for :nav`.
let nav (w: Out) (ctx: ViewContext) (user: UserSummary) : unit =
    w.Lit t0
    Application.linkBack w ctx
    w.Lit t1
    if ctx.IsCurrentUser user.Id then
        w.Lit t2
        Filters.linkTo w (Routes.userProfile ()) (Tag.attrs().Class("btn")) (fun w ->
            w.Lit t3
            Assets.imageTag w ctx "pencil.svg" (Tag.attrs().AriaHidden())
            w.Lit t4)
    w.Lit t5

/// The page itself. `transferId` is `user.transfer_id`, for `users/profiles/_transfer` (shown to administrators).
let content (w: Out) (ctx: ViewContext) (user: UserSummary) (transferId: string) : unit =
    w.Lit t6
    if user.Banned then
        w.Lit t7
    w.Lit t8
    Assets.imageTag w ctx user.AvatarPath (Tag.attrs().Alt("Profile avatar").Class("avatar"))
    w.Lit t9
    if user.Bot then
        w.Lit t10
        if user.Active then
            w.Lit t11
            UsersHelper.buttonToDirectRoomWith w ctx user.Id
            w.Lit t12
        else
            w.Lit t13
            w.Text user.Name
            w.Lit t14
        w.Lit t15
    else if not user.Deactivated then
        w.Lit t16
        w.Text user.Name
        w.Lit t17
        if ctx.CanAdminister then
            w.Lit t18
            Links.mailTo w (defaultArg user.EmailAddress "")
            w.Lit t19
        w.Lit t20
        w.Text(defaultArg user.Bio "")
        w.Lit t21
        if user.Active then
            w.Lit t22
            Filters.buttonTo w (Url.roomsDirectsWithUser user.Id) (Tag.attrs().Class("btn btn--reversed full-width txt-large")) (fun w ->
                w.Lit t23
                Assets.imageTag w ctx "messages.svg" (Tag.attrs().Aria("hidden", "true").Aria("label", "Ping " + user.Name))
                w.Lit t24)
            w.Lit t25
            if ctx.CanAdminister then
                w.Lit t26
                Profiles._Transfer.render w ctx user transferId
                w.Lit t27
        w.Lit t28
        if ctx.CanAdminister && not (ctx.IsCurrentUser user.Id) then
            w.Lit t29
            _BanButton.render w ctx user
            w.Lit t30
    else
        w.Lit t31
        w.Text user.Name
        w.Lit t32
        w.Text user.Name
        w.Lit t33
    w.Lit t34

let render (w: Out) (ctx: ViewContext) (user: UserSummary) (transferId: string) : unit =
    Templates.Layouts.Application.render
        w
        ctx
        (pageTitle user)
        None
        head
        (fun w -> nav w ctx user)
        (fun w -> content w ctx user transferId)
        ignore
        ignore
