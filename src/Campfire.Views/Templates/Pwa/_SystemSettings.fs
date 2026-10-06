// Port of rust/crates/views/templates/pwa/_system_settings.html (reference/app/views/pwa/_system_settings.html.erb)
/// `pwa/_system_settings.html.erb`: where to allow notifications in the system settings.
module Campfire.Views.Templates.Pwa._SystemSettings

open Campfire.Views
open Campfire.Views.Helpers

let private t0 = Utf8.lit "<details class=\"notifications-help hide-in-browser\" data-notifications-target=\"details\">\n  <summary class=\"btn\">\n    "
let private t1 = Utf8.lit "\n    <strong>Check your "
let private t2 = Utf8.lit " settings</strong>\n    "
let private t3 = Utf8.lit "\n  </summary>\n\n"
let private t4 = Utf8.lit "      <ol>\n        <li>Tap the <em>"
let private t5 = Utf8.lit "</em> menu button.</li>\n        <li>Tap <em>Settings</em>.</li>\n        <li>Tap <em>Notifications</em>.</li>\n        <li>Tap <em>"
let private t6 = Utf8.lit "</em> to <em>Allow "
let private t7 = Utf8.lit " notifications</em>.</li>\n      </ol>\n"
let private t8 = Utf8.lit "      <ol>\n        <li>Click <em>Start</em>, then <em>Settings</em>.</li>\n        <li>Go to <em>System &gt; Notification</em>.</li>\n        <li>Click <em>"
let private t9 = Utf8.lit "</em> <em>ON</em> for Campfire.</li>\n      </ol>\n"
let private t10 = Utf8.lit "      <ol>\n"
let private t11 = Utf8.lit "          <li>Click <em>Start</em>, then <em>Settings</em>.</li>\n          <li>Go to <em>System &gt; Notification</em>.</li>\n          <li>Click <em>"
let private t12 = Utf8.lit "</em> <em>ON</em> for Campfire.</li>\n"
let private t13 = Utf8.lit "          <li>Click <em aria-label=\"the Apple menu\"></em> in the top left.</li>\n          <li>Click <em>System Settings…</em>.</li>\n          <li>Click <em>Notifications</em>.</li>\n          <li>Click <em>Campfire</em>.</li>\n          <li>Click <em>"
let private t14 = Utf8.lit "</em> to <em>Allow notifications</em>.</li>\n"
let private t15 = Utf8.lit "      </ol>\n"
let private t16 = Utf8.lit "      <ol>\n        <li>Click <em aria-label=\"the Apple menu\"></em> in the top left.</li>\n        <li>Click <em>System Settings…</em>.</li>\n        <li>Click <em>Notifications</em>.</li>\n        <li>Click <em>Campfire</em>.</li>\n        <li>Click <em>"
let private t17 = Utf8.lit "</em> to <em>Allow notifications</em>.</li>\n      </ol>\n"
let private t18 = Utf8.lit "      <ol>\n        <li>Open the <em>"
let private t19 = Utf8.lit "</em> Settings app.</li>\n        <li>Scroll to and tap <em>Campfire</em>.</li>\n        <li>Tap <em>Notifications</em>.</li>\n        <li>Tap <em>"
let private t20 = Utf8.lit "</em> to <em>Allow Notifications</em>.</li>\n      </ol>\n"
let private t21 = Utf8.lit "      <ol>\n        <li>Open the <em>"
let private t22 = Utf8.lit "</em> Settings app.</li>\n        <li>Tap <em>Notifications</em>.</li>\n        <li>Tap <em>App notifications</em>.</li>\n        <li>Scroll to <em>Campfire</em>.</li>\n        <li>Tap <em>"
let private t23 = Utf8.lit "</em> to <em>Allow Notifications</em>.</li>\n      </ol>\n"
let private t24 = Utf8.lit "      <p>Ensure notifications are allowed for "
let private t25 = Utf8.lit " in your system settings.</p>\n"
let private t26 = Utf8.lit "</details>\n"

let render (w: Out) (ctx: ViewContext) : unit =
    w.Lit t0
    Assets.imageTag w ctx "external/gear.svg" (Tag.attrs().AriaHidden().Size(20))
    w.Lit t1
    w.Text ctx.Platform.OperatingSystem
    w.Lit t2
    Assets.imageTag w ctx "disclosure.svg" (Tag.attrs().AriaHidden().Size(10).Class("disclosure"))
    w.Lit t3
    if ctx.Platform.Firefox && ctx.Platform.Android then
        w.Lit t4
        Assets.imageTag w ctx "menu-dots-vertical.svg" (Tag.attrs().Alt("More options").Size(16))
        w.Lit t5
        Assets.imageTag w ctx "external/switch.svg" (Tag.attrs().Alt("the toggle button").Size(22))
        w.Lit t6
        w.Text(Application.capitalize ctx.Platform.Browser)
        w.Lit t7
    elif ctx.Platform.Edge && ctx.Platform.Desktop then
        w.Lit t8
        Assets.imageTag w ctx "external/switch.svg" (Tag.attrs().Alt("the toggle button").Size(22))
        w.Lit t9
    elif (ctx.Platform.Firefox || ctx.Platform.Chrome) && ctx.Platform.Desktop then
        w.Lit t10
        if ctx.Platform.Windows then
            w.Lit t11
            Assets.imageTag w ctx "external/switch.svg" (Tag.attrs().Alt("the toggle button").Size(22))
            w.Lit t12
        else
            w.Lit t13
            Assets.imageTag w ctx "external/switch.svg" (Tag.attrs().Alt("the allow notifications switch").Size(22))
            w.Lit t14
        w.Lit t15
    elif ctx.Platform.Safari && ctx.Platform.Desktop then
        w.Lit t16
        Assets.imageTag w ctx "external/switch.svg" (Tag.attrs().Alt("the allow notifications switch").Size(22))
        w.Lit t17
    elif (ctx.Platform.Safari || ctx.Platform.Chrome) && ctx.Platform.Ios then
        w.Lit t18
        Assets.imageTag w ctx "external/gear.svg" (Tag.attrs().AriaHidden().Size(20))
        w.Lit t19
        Assets.imageTag w ctx "external/switch.svg" (Tag.attrs().Alt("the allow notifications switch button").Size(22))
        w.Lit t20
    elif ctx.Platform.Chrome && ctx.Platform.Android then
        w.Lit t21
        Assets.imageTag w ctx "external/gear.svg" (Tag.attrs().AriaHidden().Size(20))
        w.Lit t22
        Assets.imageTag w ctx "external/switch.svg" (Tag.attrs().Alt("the switch").Size(22))
        w.Lit t23
    else
        w.Lit t24
        w.Text(Application.capitalize ctx.Platform.Browser)
        w.Lit t25
    w.Lit t26
