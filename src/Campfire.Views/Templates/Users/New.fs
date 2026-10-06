// Port of rust/crates/views/templates/users/new.html (reference/app/views/users/new.html.erb)
/// `users/new.html.erb` (the join page).
module Campfire.Views.Templates.Users.New

open Campfire.Routes
open Campfire.Views
open Campfire.Views.Accounts
open Campfire.Views.Helpers

let private t0 = Utf8.lit "  <div class=\"flex-item-justify-end\">\n    "
let private t1 = Utf8.lit "\n      "
let private t2 = Utf8.lit "\n      <span class=\"for-screen-reader\">Sign in</span>\n"
let private t3 = Utf8.lit "  </div>\n"
let private t4 = Utf8.lit "\n  <section class=\"nametag u-relative\">\n    <div class=\"flex justify-center align-center pad-block\">\n      "
let private t5 = Utf8.lit "\n    </div>\n\n    <div class=\"nametag__inner flex flex-column gap\">\n      <fieldset class=\"flex flex-column center-block\">\n        <legend class=\"txt-align-center flex gap\">\n          "
let private t6 = Utf8.lit "\n          <strong class=\"txt-large\">"
let private t7 = Utf8.lit "</strong>\n        </legend>\n\n        <label class=\"align-center center avatar__form gap\" data-controller=\"upload-preview\">\n          <div class=\"btn input--file\">\n            "
let private t8 = Utf8.lit "\n            "
let private t9 = Utf8.lit "\n            <span class=\"for-screen-reader\">Upload avatar</span>\n          </div>\n\n          <div class=\"btn avatar input--file txt-xx-large\">\n            "
let private t10 = Utf8.lit "\n            <span class=\"for-screen-reader\">Avatar</span>\n          </div>\n        </label>\n      </fieldset>\n\n      <div class=\"flex align-center gap\">\n        "
let private t11 = Utf8.lit "\n        <label class=\"flex align-center gap flex-item-grow txt-large input input--actor\">\n          "
let private t12 = Utf8.lit "\n          "
let private t13 = Utf8.lit "\n        </label>\n      </div>\n\n      <div class=\"flex align-center gap\">\n        "
let private t14 = Utf8.lit "\n        <label class=\"flex align-center gap flex-item-grow txt-large input input--actor\">\n          "
let private t15 = Utf8.lit "\n          "
let private t16 = Utf8.lit "\n        </label>\n      </div>\n\n      <div class=\"flex align-center gap\">\n        "
let private t17 = Utf8.lit "\n        <label class=\"flex align-center gap flex-item-grow txt-large input input--actor\">\n          "
let private t18 = Utf8.lit "\n          "
let private t19 = Utf8.lit "\n        </label>\n      </div>\n\n      "
let private t20 = Utf8.lit "\n        "
let private t21 = Utf8.lit "\n        <span class=\"for-screen-reader\">Save</span>\n"
let private t22 = Utf8.lit "    </div>\n  </section>\n"
let private t23 = Utf8.lit "\n"
let private t24 = Utf8.lit "\n"

/// `@page_title`.
let pageTitle: string option = Some "Sign up"

/// `@body_class`.
let bodyClass: string option = Some "signup"

/// `content_for :head` (empty).
let head: Out -> unit = ignore

/// `content_for :nav`.
let nav (w: Out) (ctx: ViewContext) : unit =
    w.Lit t0
    Filters.linkTo w (Routes.newSession ()) (Tag.attrs().Class("btn flex-item-justify-end")) (fun w ->
        w.Lit t1
        Assets.imageTag w ctx "login-keys.svg" (Tag.attrs().AriaHidden())
        w.Lit t2)
    w.Lit t3

/// The page itself.
let content (w: Out) (ctx: ViewContext) (joinCode: string) (helpContact: HelpContact option) : unit =
    let form = (Forms.formWith (Routes.join joinCode)).Model("user").Class("center")
    Filters.formWith w form (fun w ->
        w.Lit t4
        Assets.imageTag w ctx "lanyard.svg" (Tag.attrs().Class("nametag__lanyard").AriaHidden())
        w.Lit t5
        UsersHelper.accountLogoTag w ctx None
        w.Lit t6
        w.Text ctx.Account.Name
        w.Lit t7
        Assets.imageTag w ctx "camera.svg" (Tag.attrs().AriaHidden())
        w.Lit t8
        form.FileField(
            w,
            "avatar",
            Tag.attrs().Class("input").Accept("image/*").Data("upload_preview_target", "input").Data("action", "upload-preview#previewImage")
        )
        w.Lit t9
        Assets.imageTag w ctx "default-avatar.svg" (Tag.attrs().AriaHidden().Data("upload_preview_target", "image"))
        w.Lit t10
        Translations.translationButton w ctx "user_name"
        w.Lit t11
        form.TextField(
            w,
            "name",
            None,
            Tag.attrs().Class("input").Autocomplete("name").Placeholder("Name").Autofocus().Required(true).Data("1p-ignore", true)
        )
        w.Lit t12
        Assets.imageTag w ctx "person.svg" (Tag.attrs().AriaHidden().Size(24).Class("colorize--black"))
        w.Lit t13
        Translations.translationButton w ctx "email_address"
        w.Lit t14
        form.EmailField(
            w,
            "email_address",
            None,
            Tag.attrs().Class("input").Autocomplete("username").Placeholder("Email address").Required(true)
        )
        w.Lit t15
        Assets.imageTag w ctx "email.svg" (Tag.attrs().AriaHidden().Size(24).Class("colorize--black"))
        w.Lit t16
        Translations.translationButton w ctx "password"
        w.Lit t17
        form.PasswordField(
            w,
            "password",
            Tag.attrs().Class("input").Autocomplete("new-password").Placeholder("Password").Required(true).Maxlength(72)
        )
        w.Lit t18
        Assets.imageTag w ctx "password.svg" (Tag.attrs().AriaHidden().Size(24).Class("colorize--black"))
        w.Lit t19
        Filters.button w (Tag.attrs().Class("btn btn--reversed center txt-large").Type("submit")) (fun w ->
            w.Lit t20
            Assets.imageTag w ctx "check.svg" (Tag.attrs().AriaHidden())
            w.Lit t21)
        w.Lit t22)
    w.Lit t23
    Campfire.Views.Templates.Accounts._HelpContact.render w ctx helpContact
    w.Lit t24

let render (w: Out) (ctx: ViewContext) (joinCode: string) (helpContact: HelpContact option) : unit =
    Templates.Layouts.Application.render w ctx pageTitle bodyClass head (fun w -> nav w ctx) (fun w -> content w ctx joinCode helpContact) ignore ignore
