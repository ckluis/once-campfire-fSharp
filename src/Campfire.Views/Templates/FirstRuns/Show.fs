// Port of rust/crates/views/templates/first_runs/show.html (reference/app/views/first_runs/show.html.erb)
/// `first_runs/show.html.erb`: account setup, shown until the first user exists.
module Campfire.Views.Templates.FirstRuns.Show

open Campfire.Routes
open Campfire.Views
open Campfire.Views.Helpers

let private t0 = Utf8.lit "\n  <section class=\"nametag u-relative\">\n    <div class=\"flex justify-center align-center pad-block\">\n      "
let private t1 = Utf8.lit "\n    </div>\n\n    <div class=\"nametag__inner flex flex-column gap\">\n      <fieldset class=\"flex flex-column center-block\">\n        <legend class=\"txt-large txt-align-center\"><strong>"
let private t2 = Utf8.lit "</strong></legend>\n\n        <label class=\"align-center center avatar__form gap\" data-controller=\"upload-preview\">\n          <div class=\"btn input--file\">\n            "
let private t3 = Utf8.lit "\n            "
let private t4 = Utf8.lit "\n            <span class=\"for-screen-reader\">Add your avatar</span>\n          </div>\n\n          <div class=\"btn avatar input--file txt-xx-large\">\n            "
let private t5 = Utf8.lit "\n            <span class=\"for-screen-reader\">Avatar</span>\n          </div>\n        </label>\n      </fieldset>\n\n      <div class=\"flex align-center gap\">\n        "
let private t6 = Utf8.lit "\n        <label class=\"flex align-center gap flex-item-grow txt-large input input--actor\">\n          "
let private t7 = Utf8.lit "\n          "
let private t8 = Utf8.lit "\n        </label>\n      </div>\n\n      <div class=\"flex align-center gap\">\n        "
let private t9 = Utf8.lit "\n        <label class=\"flex align-center gap flex-item-grow txt-large input input--actor\">\n          "
let private t10 = Utf8.lit "\n          "
let private t11 = Utf8.lit "\n        </label>\n      </div>\n\n      <div class=\"flex align-center gap\">\n        "
let private t12 = Utf8.lit "\n        <label class=\"flex align-center gap flex-item-grow txt-large input input--actor\">\n          "
let private t13 = Utf8.lit "\n          "
let private t14 = Utf8.lit "\n        </label>\n      </div>\n\n      "
let private t15 = Utf8.lit "\n        "
let private t16 = Utf8.lit "\n        <span class=\"for-screen-reader\">Save</span>\n"
let private t17 = Utf8.lit "    </div>\n  </section>\n"

/// `@page_title`.
let pageTitle: string option = Some "Set up Campfire"

/// `@body_class`.
let bodyClass: string option = Some "signup"

/// `content_for :head` (empty).
let head: Out -> unit = ignore

/// The page itself.
let content (w: Out) (ctx: ViewContext) : unit =
    let form = (Forms.formWith (Routes.firstRun ())).Model("user").Class("center max-width")
    Filters.formWith w form (fun w ->
        w.Lit t0
        Assets.imageTag w ctx "lanyard.svg" (Tag.attrs().Class("nametag__lanyard").AriaHidden())
        w.Lit t1
        w.Text(defaultArg pageTitle "")
        w.Lit t2
        Assets.imageTag w ctx "camera.svg" (Tag.attrs().AriaHidden())
        w.Lit t3
        form.FileField(
            w,
            "avatar",
            Tag.attrs().Class("input").Accept("image/*").Data("upload_preview_target", "input").Data("action", "upload-preview#previewImage")
        )
        w.Lit t4
        Assets.imageTag w ctx "default-avatar.svg" (Tag.attrs().AriaHidden().Data("upload_preview_target", "image").Alt("Add your avatar"))
        w.Lit t5
        Translations.translationButton w ctx "user_name"
        w.Lit t6
        form.TextField(
            w,
            "name",
            None,
            Tag.attrs().Class("input").Autocomplete("name").Placeholder("Name").Autofocus().Required(true).Data("1p-ignore", true)
        )
        w.Lit t7
        Assets.imageTag w ctx "person.svg" (Tag.attrs().AriaHidden().Size(24).Class("colorize--black"))
        w.Lit t8
        Translations.translationButton w ctx "email_address"
        w.Lit t9
        form.EmailField(
            w,
            "email_address",
            None,
            Tag.attrs().Class("input").Autocomplete("username").Placeholder("Email address").Required(true)
        )
        w.Lit t10
        Assets.imageTag w ctx "email.svg" (Tag.attrs().AriaHidden().Size(24).Class("colorize--black"))
        w.Lit t11
        Translations.translationButton w ctx "password"
        w.Lit t12
        form.PasswordField(
            w,
            "password",
            Tag.attrs().Class("input").Autocomplete("new-password").Placeholder("Password").Required(true).Maxlength(72)
        )
        w.Lit t13
        Assets.imageTag w ctx "password.svg" (Tag.attrs().AriaHidden().Size(24).Class("colorize--black"))
        w.Lit t14
        Filters.button w (Tag.attrs().Class("btn btn--reversed center txt-large").Type("submit")) (fun w ->
            w.Lit t15
            Assets.imageTag w ctx "arrow-right.svg" (Tag.attrs().AriaHidden())
            w.Lit t16)
        w.Lit t17)

let render (w: Out) (ctx: ViewContext) : unit =
    Templates.Layouts.Application.render w ctx pageTitle bodyClass head ignore (fun w -> content w ctx) ignore ignore
