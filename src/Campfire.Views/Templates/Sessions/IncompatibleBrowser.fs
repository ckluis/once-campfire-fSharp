// Port of rust/crates/views/templates/sessions/incompatible_browser.html (reference/app/views/sessions/incompatible_browser.html.erb)
/// `sessions/incompatible_browser.html.erb`, rendered by `AllowBrowser` for old browsers.
module Campfire.Views.Templates.Sessions.IncompatibleBrowser

open Campfire.Views
open Campfire.Views.Helpers

let private t0 = Utf8.lit "<div class=\"panel center\">\n  <header>\n    <h1 class=\"txt-x-large txt-tight-lines txt-align-center margin-none-block-start margin-block-end\">\n      Upgrade to a supported web browser\n    </h1>\n    <div class=\"flex align-start gap\">\n      "
let private t1 = Utf8.lit "\n      <p class=\"margin-none-block-start\">Campfire requires a modern web browser. Please use one of the browsers listed below and make sure auto-updates are enabled.</p>\n    </div>\n  </header>\n\n  <div class=\"browser-list flex align-center flex-wrap gap justify-center margin-block\">\n"
let private t2 = Utf8.lit "      <div class=\"browser flex flex-column\">\n        "
let private t3 = Utf8.lit "\n        <div class=\"flex flex-column align-center margin-block-start-half\">\n          <strong>"
let private t4 = Utf8.lit "</strong>\n          <span> "
let private t5 = Utf8.lit "+</span>\n        </div>\n      </div>\n"
let private t6 = Utf8.lit "  </div>\n</div>\n"

/// `@page_title`.
let pageTitle (ctx: ViewContext) : string option =
    Some(if ctx.Platform.AppleMessages then "Campfire" else "Unsupported browser")

/// `content_for :head` (empty).
let head: Out -> unit = ignore

/// The page itself.
let content (w: Out) (ctx: ViewContext) : unit =
    w.Lit t0
    Translations.translationButton w ctx "incompatible_browser_messsage"
    w.Lit t1
    for (browser, version) in Campfire.Views.Sessions.allowBrowserVersions do
        w.Lit t2
        Assets.imageTag w ctx ("browsers/" + browser + ".svg") (Tag.attrs().AriaHidden().Class("center"))
        w.Lit t3
        w.Text(Application.capitalize browser)
        w.Lit t4
        w.Text version
        w.Lit t5
    w.Lit t6

let render (w: Out) (ctx: ViewContext) : unit =
    Templates.Layouts.Application.render w ctx (pageTitle ctx) None head ignore (fun w -> content w ctx) ignore ignore
