// Port of rust/crates/views/templates/accounts/custom_styles/edit.html (reference/app/views/accounts/custom_styles/edit.html.erb)
/// `accounts/custom_styles/edit.html.erb`: the account's custom CSS.
module Campfire.Views.Templates.Accounts.CustomStyles.Edit

open Campfire.Routes
open Campfire.Views
open Campfire.Views.Helpers

let private t0 = Utf8.lit "  <div class=\"flex-item-justify-start\">\n    "
let private t1 = Utf8.lit "\n  </div>\n"
let private t2 = Utf8.lit "<section class=\"panel panel--wide txt-align-center flex flex-column position-relative\" style=\"view-transition-name: custom-styles\">\n"
let private t3 = Utf8.lit "  "
let private t4 = Utf8.lit "\n    <div class=\"panel__button\">\n      "
let private t5 = Utf8.lit "\n    </div>\n\n    <div class=\"pad-inline-double margin-inline\">\n      <h1 class=\"margin-none\">Custom CSS</h1>\n      <p class=\"flex flex-wrap align-center justify-center gap margin-none-block-start\" style=\"--column-gap: 0.5ch; --row-gap: 0\">\n        <span>Add custom CSS styles.</span>\n        "
let private t6 = Utf8.lit "\n        <span>Use Caution: you could break things.</span>\n      </p>\n    </div>\n\n    <label class=\"flex align-start gap flex-item-grow\">\n      "
let private t7 = Utf8.lit "\n    </label>\n\n    "
let private t8 = Utf8.lit "\n      "
let private t9 = Utf8.lit "\n      <span class=\"for-screen-reader\">Save changes</span>\n"
let private t10 = Utf8.lit "</section>\n"

/// `@page_title`.
let pageTitle: string option = Some "Custom styles"

/// `content_for :head` (empty).
let head: Out -> unit = ignore

/// `content_for :nav`.
let nav (w: Out) (ctx: ViewContext) : unit =
    w.Lit t0
    Application.linkBackTo w ctx (Routes.editAccount ())
    w.Lit t1

/// The page itself.
let content (w: Out) (ctx: ViewContext) (customStyles: string option) : unit =
    w.Lit t2
    let form =
        (Forms.formWith (ctx.Url(Routes.accountCustomStyles ())))
            .Model("account")
            .Method("patch")
            .Class("flex flex-column gap")
            .Data("controller", "form")
            .Data("action", "keydown.ctrl+enter->form#submit keydown.meta+enter->form#submit")
    w.Lit t3
    Filters.formWith w form (fun w ->
        w.Lit t4
        Translations.translationButton w ctx "custom_styles"
        w.Lit t5
        Assets.imageTag w ctx "alert.svg" (Tag.attrs().Class("flex-inline colorize--black").Size(16).AriaHidden())
        w.Lit t6
        form.TextArea(
            w,
            "custom_styles",
            customStyles,
            Tag.attrs()
                .Class("input input--code txt--small")
                .Placeholder("Add CSS styles…")
                .Autocomplete("off")
                .Attr("spellcheck", "false")
                .Attr("autocorrect", "off")
                .Attr("autocapitalize", "off")
                .Rows(16)
                .Required(false)
        )
        w.Lit t7
        Filters.button w (Tag.attrs().Class("btn btn--reversed center txt-large").Type("submit")) (fun w ->
            w.Lit t8
            Assets.imageTag w ctx "check.svg" (Tag.attrs().AriaHidden().Size(20))
            w.Lit t9))
    w.Lit t10

let render (w: Out) (ctx: ViewContext) (customStyles: string option) : unit =
    Templates.Layouts.Application.render w ctx pageTitle None head (fun w -> nav w ctx) (fun w -> content w ctx customStyles) ignore ignore
