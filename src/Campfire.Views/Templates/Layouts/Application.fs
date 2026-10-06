// Port of rust/crates/views/templates/layouts/application.html (reference/app/views/layouts/application.html.erb)
/// The application layout. Askama pages `{% extends %}` it; here a page calls `render` with its
/// `@page_title` and `@body_class` (`layouts::Page`) and the regions it fills (`content_for`'s
/// `head`, `nav`, `footer` and `sidebar`, and the page itself as `content`), each a function of the
/// writer, `ignore` for a region it leaves empty.
module Campfire.Views.Templates.Layouts.Application

open Campfire.Views
open Campfire.Views.Helpers

let private t0 = Utf8.lit "<!DOCTYPE html>\n<html>\n  <head>\n    "
let private t1 = Utf8.lit "\n\n    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1, user-scalable=no, interactive-widget=resizes-content\">\n    <meta name=\"view-transition\" content=\"same-origin\">\n    <meta name=\"color-scheme\" content=\"light dark\">\n    <meta name=\"theme-color\" content=\"#ffffff\" media=\"(prefers-color-scheme: light)\">\n    <meta name=\"theme-color\" content=\"#000000\" media=\"(prefers-color-scheme: dark)\">\n    <meta name=\"apple-mobile-web-app-capable\" content=\"yes\">\n    "
let private t2 = Utf8.lit "\n    "
let private t3 = Utf8.lit "\n\n    "
let private t4 = Utf8.lit "\n    "
let private t5 = Utf8.lit "\n\n    "
let private t6 = Utf8.lit "\n    "
let private t7 = Utf8.lit "\n    "
let private t8 = Utf8.lit "\n\n    "
let private t9 = Utf8.lit "\n    "
let private t10 = Utf8.lit "\n\n    "
let private t11 = Utf8.lit "\n\n"
let private t12 = Utf8.lit "  </head>\n\n  <body class=\""
let private t13 = Utf8.lit "\" data-controller=\"local-time lightbox\">\n    <a href=\"#main-content\" class=\"skip-navigation btn\">Skip to main content</a>\n\n    <nav id=\"nav\">\n"
let private t14 = Utf8.lit "    </nav>\n\n"
let private t15 = Utf8.lit "      <div class=\"flash\" data-controller=\"element-removal\" data-action=\"animationend->element-removal#remove\">\n        <div class=\"flash__inner shadow\" style=\""
let private t16 = Utf8.lit "--flash-background: var(--color-negative)"
let private t17 = Utf8.lit "\">\n"
let private t18 = Utf8.lit "            "
let private t19 = Utf8.lit "</span>\n"
let private t20 = Utf8.lit "            "
let private t21 = Utf8.lit "</span>\n"
let private t22 = Utf8.lit "        </div>\n        <span class=\"for-screen-reader\" role=\"alert\" aria-atomic=\"true\">"
let private t23 = Utf8.lit "</span>\n      </div>\n"
let private t24 = Utf8.lit "\n    <main id=\"main-content\">\n"
let private t25 = Utf8.lit "\n      <footer id=\"footer\">\n"
let private t26 = Utf8.lit "      </footer>\n    </main>\n\n    <aside id=\"sidebar\" data-controller=\"toggle-class\" data-toggle-class-toggle-class=\"open\">\n"
let private t27 = Utf8.lit "    </aside>\n\n    "
let private t28 = Utf8.lit "\n\n    <a href=\"https://once.com\" id=\"app-logo\" target=\"_blank\" aria-label=\"Once software from 37signals home page\">\n      "
let private t29 = Utf8.lit "\n    </a>\n  </body>\n</html>\n"

let render
    (w: Out)
    (ctx: ViewContext)
    (pageTitle: string option)
    (bodyClass: string option)
    (head: Out -> unit)
    (nav: Out -> unit)
    (content: Out -> unit)
    (footer: Out -> unit)
    (sidebar: Out -> unit)
    : unit =
    w.Lit t0
    Application.pageTitleTag w pageTitle
    w.Lit t1
    Application.currentUserMetaTags w ctx
    w.Lit t2
    Application.scriptAwareActionCableMetaTag w ctx
    w.Lit t3
    Tag.builderTag w "meta" (Tag.attrs().Name("vapid-public-key").AttrOpt("content", ctx.VapidPublicKey))
    w.Lit t4
    Tag.builderTag w "meta" (Tag.attrs().Name("turbo-prefetch").Attr("content", "true"))
    w.Lit t5
    Tag.builderTag w "link" (Tag.attrs().Attr("rel", "manifest").Attr("href", "/webmanifest.json"))
    w.Lit t6
    Tag.builderTag w "link" (Tag.attrs().Attr("rel", "icon").Attr("href", ctx.Account.LogoUrl).Type("image/png"))
    w.Lit t7
    Tag.builderTag w "link" (Tag.attrs().Attr("rel", "apple-touch-icon").Attr("href", ctx.Account.LogoUrl))
    w.Lit t8
    w.Raw ctx.StylesheetTags
    w.Lit t9
    Application.customStylesTag w ctx
    w.Lit t10
    w.Raw ctx.ImportmapTags
    w.Lit t11
    head w
    w.Lit t12
    Application.writeBodyClasses w ctx bodyClass
    w.Lit t13
    nav w
    w.Lit t14
    let notice =
        match ctx.FlashNotice with
        | Some notice -> Some notice
        | None -> ctx.FlashAlert
    match notice with
    | Some notice ->
        w.Lit t15
        if ctx.FlashAlert.IsSome then w.Lit t16
        w.Lit t17
        if ctx.FlashAlert.IsSome then
            w.Lit t18
            Assets.imageTag w ctx "alert.svg" (Tag.attrs().Aria("hidden", true).Size(24).Class("colorize--white"))
            w.Lit t19
        else
            w.Lit t20
            Assets.imageTag w ctx "check.svg" (Tag.attrs().Aria("hidden", true).Size(24).Class("colorize--white"))
            w.Lit t21
        w.Lit t22
        w.Text notice
        w.Lit t23
    | None -> ()
    w.Lit t24
    content w
    w.Lit t25
    footer w
    w.Lit t26
    sidebar w
    w.Lit t27
    _Lightbox.render w ctx
    w.Lit t28
    Assets.imageTag w ctx "campfire-icon.png" (Tag.attrs().Alt("Campfire logo").Attr("width", 256).Attr("height", 216))
    w.Lit t29
