// Port of rust/crates/views/templates/pwa/_install_instructions.html (reference/app/views/pwa/_install_instructions.html.erb)
/// `pwa/_install_instructions.html.erb`: how to install Campfire as a web app on the platform.
module Campfire.Views.Templates.Pwa._InstallInstructions

open Campfire.Views
open Campfire.Views.Helpers

let private t0 = Utf8.lit "  <details class=\"notifications-help pwa__instructions hide-in-pwa\" data-controller=\"pwa-install\" data-pwa-install-prompting-class=\"pwa--can-install\" data-notifications-target=\"details\">\n    <summary class=\"btn\">\n      "
let private t1 = Utf8.lit "\n      <strong>Install Campfire as a web app.</strong>\n      "
let private t2 = Utf8.lit "\n    </summary>\n\n"
let private t3 = Utf8.lit "        <ol>\n          <li>Click <em>"
let private t4 = Utf8.lit "</em>in the address bar.</li>\n          <li>Click <em>Install</em>.</li>\n        </ol>\n"
let private t5 = Utf8.lit "        <ol>\n          <li>Tap the <em>"
let private t6 = Utf8.lit "</em> menu button.</li>\n          <li>Tap <em>Install app</em> in the menu.</li>\n        </ol>\n"
let private t7 = Utf8.lit "        <ol>\n          <li>Tap the <em>"
let private t8 = Utf8.lit "</em> menu button.</li>\n          <li>Tap <em>Install</em> in the menu.</li>\n        </ol>\n"
let private t9 = Utf8.lit "        <ol>\n          <li>Click <em>File</em> in the top left.</li>\n          <li>Click <em>Add to Dock…</em>.</li>\n        </ol>\n"
let private t10 = Utf8.lit "        <p>To receive push notifications in "
let private t11 = Utf8.lit " for "
let private t12 = Utf8.lit ", you must install Campfire as a web app.</p>\n        <ol>\n          <li>Tap <em>"
let private t13 = Utf8.lit "</em></li>\n          <li>Tap <em>Add to Home Screen</em>.</li>\n        </ol>\n"
let private t14 = Utf8.lit "        <p>Some platforms require you to install Campfire as a web app to receive push notifications.</p>\n"
let private t15 = Utf8.lit "\n    <div class=\"margin-block-start txt-align-center pwa__installer\">\n      <hr class=\"separator margin-block\">\n      <button class=\"btn btn--reversed center\" data-action=\"pwa-install#promptInstall\">\n        "
let private t16 = Utf8.lit "\n        Install now\n      </button>\n    </div>\n  </details>\n"

let render (w: Out) (ctx: ViewContext) : unit =
    if not (ctx.Platform.Chrome || (ctx.Platform.Firefox && not ctx.Platform.Android)) then
        w.Lit t0
        Assets.imageTag w ctx "external/install.svg" (Tag.attrs().AriaHidden().Size(20))
        w.Lit t1
        Assets.imageTag w ctx "disclosure.svg" (Tag.attrs().AriaHidden().Size(10).Class("disclosure"))
        w.Lit t2
        if ctx.Platform.Edge then
            w.Lit t3
            Assets.imageTag w ctx "install-edge.svg" (Tag.attrs().Alt("the app available - install Campfire chat button").Size(16))
            w.Lit t4
        elif ctx.Platform.Chrome && ctx.Platform.Android then
            w.Lit t5
            Assets.imageTag w ctx "menu-dots-vertical.svg" (Tag.attrs().Alt("More options").Size(16))
            w.Lit t6
        elif ctx.Platform.Firefox && ctx.Platform.Android then
            w.Lit t7
            Assets.imageTag w ctx "menu-dots-vertical.svg" (Tag.attrs().Alt("More options").Size(16))
            w.Lit t8
        elif ctx.Platform.Safari && ctx.Platform.Desktop then
            w.Lit t9
        elif (ctx.Platform.Safari || ctx.Platform.Chrome) && ctx.Platform.Ios then
            w.Lit t10
            w.Text(Application.capitalize ctx.Platform.Browser)
            w.Lit t11
            w.Text ctx.Platform.OperatingSystem
            w.Lit t12
            Assets.imageTag w ctx "external/share.svg" (Tag.attrs().Alt("the share button").Size(20))
            w.Lit t13
        else
            w.Lit t14
        w.Lit t15
        Assets.imageTag w ctx "external/install.svg" (Tag.attrs().AriaHidden())
        w.Lit t16
