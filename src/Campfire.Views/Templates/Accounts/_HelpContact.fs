// Port of rust/crates/views/templates/accounts/_help_contact.html (reference/app/views/accounts/_help_contact.html.erb)
/// `accounts/_help_contact.html.erb`: how to reach the person to ask for help, on the sign-in and
/// sign-up pages.
module Campfire.Views.Templates.Accounts._HelpContact

open Campfire.Views
open Campfire.Views.Accounts
open Campfire.Views.Helpers

let private t0 = Utf8.lit "  <div class=\"txt-align-center margin-block-double full-width\">\n    "
let private t1 = Utf8.lit "\n      "
let private t2 = Utf8.lit "\n      <span>"
let private t3 = Utf8.lit "</span>\n"
let private t4 = Utf8.lit "\n    <div class=\"txt-align-center center margin-block txt-subtle\">Campfire&trade; version "
let private t5 = Utf8.lit "</div>\n  </div>\n"

let render (w: Out) (ctx: ViewContext) (helpContact: HelpContact option) : unit =
    match helpContact with
    | Some owner ->
        w.Lit t0
        Filters.linkTo
            w
            ("mailto:\"" + owner.Name + "\" <" + owner.EmailAddress + ">")
            (Tag.attrs().Class("btn center").Title("Email " + owner.Name))
            (fun w ->
                w.Lit t1
                Assets.imageTag w ctx "lifebuoy.svg" (Tag.attrs().AriaHidden())
                w.Lit t2
                w.Text owner.EmailAddress
                w.Lit t3)
        w.Lit t4
        Application.versionBadge w ctx
        w.Lit t5
    | None -> ()
