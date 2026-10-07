// Port of rust/crates/views/templates/sessions/new.html (reference/app/views/sessions/new.html.erb)
/// `sessions/new.html.erb`: sign in.
module Campfire.Views.Templates.Sessions.New

open Campfire.Routes
open Campfire.Views
open Campfire.Views.Accounts
open Campfire.Views.Helpers

let private t0 = Utf8.lit "<section class=\"txt-align-center\">\n  <div class=\"panel "
let private t1 = Utf8.lit "shake"
let private t2 = Utf8.lit "\">\n    "
let private t3 = Utf8.lit "\n\n"
let private t4 = Utf8.lit "    "
let private t5 = Utf8.lit "\n      <fieldset class=\"flex flex-column gap center-block upad\">\n        <legend class=\"txt-large txt-align-center\"><strong>"
let private t6 = Utf8.lit "</strong></legend>\n\n        <div class=\"flex align-center gap\">\n          "
let private t7 = Utf8.lit "\n          <label class=\"flex align-center gap input input--actor txt-large\">\n            "
let private t8 = Utf8.lit "\n            "
let private t9 = Utf8.lit "\n          </label>\n        </div>\n\n        <div class=\"flex align-center gap\">\n          "
let private t10 = Utf8.lit "\n          <label class=\"flex align-center gap input input--actor txt-large\">\n            "
let private t11 = Utf8.lit "\n            "
let private t12 = Utf8.lit "\n          </label>\n        </div>\n\n        "
let private t13 = Utf8.lit "\n          "
let private t14 = Utf8.lit "\n          <span class=\"for-screen-reader\">Go</span>\n"
let private t15 = Utf8.lit "      </fieldset>\n"
let private t16 = Utf8.lit "  </div>\n\n  "
let private t17 = Utf8.lit "\n</section>\n"

/// `@page_title`.
let pageTitle: string option = Some "Sign in"

/// `content_for :head`.
let head (w: Out) : unit = Turbo.turboPageRequiresReloadTag w

/// The page itself. `emailAddress` is `params[:email_address]`; `helpContact` is `User.administrator.first`.
let content (w: Out) (ctx: ViewContext) (emailAddress: string option) (helpContact: HelpContact option) : unit =
    w.Lit t0
    if ctx.FlashAlert.IsSome then
        w.Lit t1
    w.Lit t2
    UsersHelper.accountLogoTag w ctx (Some "center margin-block-end txt-xx-large")
    w.Lit t3
    let form = (Forms.formWith (ctx.Url(Routes.session ()))).Class("flex flex-column gap")
    w.Lit t4
    Filters.formWith w form (fun w ->
        w.Lit t5
        w.Text ctx.Account.Name
        w.Lit t6
        Translations.translationButton w ctx "email_address"
        w.Lit t7
        form.EmailField(
            w,
            "email_address",
            None,
            Tag.attrs()
                .Required(true)
                .Class("input")
                .Autofocus()
                .Autocomplete("username")
                .Placeholder("Enter your email address")
                .AttrOpt("value", emailAddress)
        )
        w.Lit t8
        Assets.imageTag w ctx "email.svg" (Tag.attrs().AriaHidden().Size(24).Class("colorize--black"))
        w.Lit t9
        Translations.translationButton w ctx "password"
        w.Lit t10
        form.PasswordField(
            w,
            "password",
            Tag.attrs().Required(true).Class("input").Autocomplete("current-password").Placeholder("Enter your password").Maxlength(72)
        )
        w.Lit t11
        Assets.imageTag w ctx "password.svg" (Tag.attrs().AriaHidden().Size(24).Class("colorize--black"))
        w.Lit t12
        Filters.button w (Tag.attrs().Class("btn btn--reversed center txt-large").Type("submit").Name("log_in")) (fun w ->
            w.Lit t13
            Assets.imageTag w ctx "arrow-right.svg" (Tag.attrs().AriaHidden())
            w.Lit t14)
        w.Lit t15)
    w.Lit t16
    Campfire.Views.Templates.Accounts._HelpContact.render w ctx helpContact
    w.Lit t17

let render (w: Out) (ctx: ViewContext) (emailAddress: string option) (helpContact: HelpContact option) : unit =
    Templates.Layouts.Application.render w ctx pageTitle None head ignore (fun w -> content w ctx emailAddress helpContact) ignore ignore
