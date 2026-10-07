// Port of rust/crates/views/templates/accounts/users/_user.html (reference/app/views/accounts/users/_user.html.erb)
/// `accounts/users/_user.html.erb`: a person on the account page, with the admin's role, remove and settings buttons.
module Campfire.Views.Templates.Accounts.Users._User

open Campfire.Routes
open Campfire.Views
open Campfire.Views.Helpers
open Campfire.Views.Users

let private t0 = Utf8.lit "<li class=\"flex align-center gap margin-none "
let private t1 = Utf8.lit "banned"
let private t2 = Utf8.lit "\">\n  <figure class=\"avatar flex-item-no-shrink\" style=\"--avatar-size: 3.75ch;\">\n    "
let private t3 = Utf8.lit "\n  </figure>\n\n  <div class=\"min-width\">\n    <div class=\"overflow-ellipsis fill-shade\"><strong>"
let private t4 = Utf8.lit "</strong></div>\n  </div>\n\n  <hr class=\"separator\" aria-hidden=\"true\">\n\n"
let private t5 = Utf8.lit "      "
let private t6 = Utf8.lit "\n        <label class=\"btn txt-small flex-item-no-shrink\" for=\""
let private t7 = Utf8.lit "\">\n          <span class=\"for-screen-reader\">Role: "
let private t8 = Utf8.lit "Administrator"
let private t9 = Utf8.lit "Member"
let private t10 = Utf8.lit "</span>\n          "
let private t11 = Utf8.lit "\n          "
let private t12 = Utf8.lit "\n        </label>\n"
let private t13 = Utf8.lit "\n"
let private t14 = Utf8.lit "      "
let private t15 = Utf8.lit "\n        "
let private t16 = Utf8.lit "\n        <span class=\"for-screen-reader\">Delete "
let private t17 = Utf8.lit "</span>\n"
let private t18 = Utf8.lit "    "
let private t19 = Utf8.lit "\n      "
let private t20 = Utf8.lit "\n      <span class=\"for-screen-reader\">My settings</span>\n"
let private t21 = Utf8.lit "</li>\n"

let render (w: Out) (ctx: ViewContext) (user: UserSummary) : unit =
    w.Lit t0
    if user.Banned then
        w.Lit t1
    w.Lit t2
    UsersHelper.avatarTag w ctx user.Avatar (Tag.attrs().Loading("lazy"))
    w.Lit t3
    w.Text user.Name
    w.Lit t4
    if ctx.CanAdminister && user.Active then
        if not user.Bot then
            let form = (Forms.formWith (Routes.accountUser user.Id)).Model("user").Data("controller", "form").Method("patch")
            w.Lit t5
            Filters.formWith w form (fun w ->
                w.Lit t6
                Turbo.writeDomId w "user" user.Id (Some "role")
                w.Lit t7
                if user.Administrator then
                    w.Lit t8
                else
                    w.Lit t9
                w.Lit t10
                Assets.imageTag w ctx "crown.svg" (Tag.attrs().Size(20).AriaHidden())
                w.Lit t11
                form.CheckBox(
                    w,
                    "role",
                    Tag.attrs().Data("action", "form#submit").Hidden().Id(Turbo.domIdValue "user" user.Id (Some "role")).Disabled(ctx.IsCurrentUser user.Id),
                    "administrator",
                    "member",
                    Role.asStr user.Role
                )
                w.Lit t12)
        w.Lit t13
        if not (ctx.IsCurrentUser user.Id) then
            w.Lit t14
            Filters.buttonTo
                w
                (Routes.accountUser user.Id)
                (Tag.attrs()
                    .Method("delete")
                    .Class("btn txt-small flex-item-no-shrink btn--negative")
                    .Data("turbo_confirm", "Are you sure you want to permanently remove this person from the account? This can’t be undone."))
                (fun w ->
                    w.Lit t15
                    Assets.imageTag w ctx "minus.svg" (Tag.attrs().Size(20).AriaHidden())
                    w.Lit t16
                    w.Text user.Name
                    w.Lit t17)
    if ctx.IsCurrentUser user.Id then
        w.Lit t18
        Filters.linkTo w (Routes.userProfile ()) (Tag.attrs().Class("btn txt-small flex-item-no-shrink").Target("_top")) (fun w ->
            w.Lit t19
            Assets.imageTag w ctx "pencil.svg" (Tag.attrs().Size(20).AriaHidden())
            w.Lit t20)
    w.Lit t21
