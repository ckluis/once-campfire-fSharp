// Port of rust/crates/views/templates/pwa/_browser_settings.html (reference/app/views/pwa/_browser_settings.html.erb)
/// `pwa/_browser_settings.html.erb`: where to allow notifications in the browser.
module Campfire.Views.Templates.Pwa._BrowserSettings

open Campfire.Views
open Campfire.Views.Helpers

let private t0 = Utf8.lit "  <details class=\"notifications-help\" data-notifications-target=\"details\">\n    <summary class=\"btn\">\n      "
let private t1 = Utf8.lit "\n      <strong>Check your "
let private t2 = Utf8.lit " settings</strong>\n      "
let private t3 = Utf8.lit "\n    </summary>\n\n"
let private t4 = Utf8.lit "        <ol>\n          <li>Tap <em>"
let private t5 = Utf8.lit "</em> in the address bar.</li>\n          <li>Tap <em>Notification</em> to change to <em>Allowed</em>.</li>\n        </ol>\n"
let private t6 = Utf8.lit "        <h2 class=\"txt-normal txt-medium margin-block-start\">Turn on notifications for this website.</h2>\n        <ol>\n          <li>Click <em>"
let private t7 = Utf8.lit "</em> left of the address bar.</li>\n          <li>Under <em>Permissions for this site &gt; Notifications</em>, choose <em>Allow</em>.</li>\n        </ol>\n        <h2 class=\"txt-normal txt-medium margin-block-start\">Turn on notifications for "
let private t8 = Utf8.lit ".</h2>\n        <ol>\n"
let private t9 = Utf8.lit "            <li>Click <em>Start</em>, then <em>Settings</em>.</li>\n            <li>Go to <em>System &gt; Notification</em>.</li>\n            <li>Click <em>"
let private t10 = Utf8.lit "</em> <em>ON</em> for "
let private t11 = Utf8.lit ".</li>\n"
let private t12 = Utf8.lit "            <li>Click <em aria-label=\"the Apple menu\"></em> in the top left.</li>\n            <li>Click <em>System Settings…</em>.</li>\n            <li>Click <em>Notifications</em>.</li>\n            <li>Click <em>"
let private t13 = Utf8.lit "</em>.</li>\n            <li>Click <em>"
let private t14 = Utf8.lit "</em> to <em>Allow notifications</em>.</li>\n"
let private t15 = Utf8.lit "        </ol>\n"
let private t16 = Utf8.lit "        <h2 class=\"txt-normal txt-medium margin-block-start\">Turn on notifications for this website.</h2>\n        <ol>\n          <li>Click <em>"
let private t17 = Utf8.lit "</em> in the top left.</li>\n          <li>Click <em>Settings…</em>.</li>\n          <li>Click <em>Privacy & Security</em> in the sidebar.</li>\n          <li>Scroll down to <em>Permissions</em>.</li>\n          <li>Click <em>Settings</em> next to <em>Notifications</em>.</li>\n          <li>Select <em>Allow</em> next to <em>"
let private t18 = Utf8.lit "</em>.</li>\n        </ol>\n\n        <h2 class=\"txt-normal txt-medium margin-block-start\">Turn on notifications for "
let private t19 = Utf8.lit ".</h2>\n        <ol>\n"
let private t20 = Utf8.lit "            <li>Click <em>Start</em>, then <em>Settings</em>.</li>\n            <li>Go to <em>System &gt; Notification</em>.</li>\n            <li>Click <em>"
let private t21 = Utf8.lit "</em> <em>ON</em> for "
let private t22 = Utf8.lit ".</li>\n"
let private t23 = Utf8.lit "            <li>Click <em aria-label=\"the Apple menu\"></em> in the top left.</li>\n            <li>Click <em>System Settings…</em>.</li>\n            <li>Click <em>Notifications</em>.</li>\n            <li>Click <em>"
let private t24 = Utf8.lit "</em>.</li>\n            <li>Click <em>"
let private t25 = Utf8.lit "</em> to <em>Allow notifications</em>.</li>\n"
let private t26 = Utf8.lit "        </ol>\n"
let private t27 = Utf8.lit "        <h2 class=\"txt-normal txt-medium margin-block-start\">Turn on notifications for this website.</h2>\n        <ol>\n          <li>Click the <em>"
let private t28 = Utf8.lit "</em> icon in the address bar.</li>\n          <li>Click <em>Site Settings</em>.</li>\n          <li>Ensure notifications are <em>Allowed</em>.</li>\n        </ol>\n\n        <h2 class=\"txt-normal txt-medium margin-block-start\">Turn on notifications for "
let private t29 = Utf8.lit ".</h2>\n        <ol>\n"
let private t30 = Utf8.lit "            <li>Click <em>Start</em>, then <em>Settings</em>.</li>\n            <li>Go to <em>System &gt; Notification</em>.</li>\n            <li>Click <em>"
let private t31 = Utf8.lit "</em> <em>ON</em> for "
let private t32 = Utf8.lit ".</li>\n"
let private t33 = Utf8.lit "            <li>Click <em aria-label=\"the Apple menu\"></em> in the top left.</li>\n            <li>Click <em>System Settings…</em>.</li>\n            <li>Click <em>Notifications</em>.</li>\n            <li>Click <em>"
let private t34 = Utf8.lit "</em>.</li>\n            <li>Click <em>"
let private t35 = Utf8.lit "</em> to <em>Allow notifications</em>.</li>\n"
let private t36 = Utf8.lit "        </ol>\n"
let private t37 = Utf8.lit "        <ol>\n          <li>Tap the <em>"
let private t38 = Utf8.lit "</em> menu button.</li>\n          <li>Tap <em>Settings</em>.</li>\n          <li>Tap <em>Notifications</em>.</li>\n          <li>Tap <em>"
let private t39 = Utf8.lit "</em> to <em>Allow "
let private t40 = Utf8.lit " notifications</em>.</li>\n          <li>Tap <em>"
let private t41 = Utf8.lit "</em> next to <em>Web apps</em>.</li>\n          <li>Tap <em>"
let private t42 = Utf8.lit "</em> and select <em>Allow</em>.</li>\n        </ol>\n"
let private t43 = Utf8.lit "        <ol>\n          <li>Click <em>"
let private t44 = Utf8.lit "</em> in the top left.</li>\n          <li>Click <em>Settings…</em>.</li>\n          <li>Click the <em>Websites</em> tab.</li>\n          <li>Click <em>Notifications</em> in the sidebar.</li>\n          <li>Click <em>"
let private t45 = Utf8.lit "</em> in the list.</li>\n          <li>Select <em>Allow</em>.</li>\n        </ol>\n"
let private t46 = Utf8.lit "        <p>Ensure notifications are enabled for <em>"
let private t47 = Utf8.lit "</em> in your web browser settings.</p>\n"
let private t48 = Utf8.lit "  </details>\n"

let render (w: Out) (ctx: ViewContext) : unit =
    if not ((ctx.Platform.Safari || ctx.Platform.Chrome) && ctx.Platform.Ios) then
        w.Lit t0
        Assets.imageTag w ctx "external/web.svg" (Tag.attrs().AriaHidden().Size(20))
        w.Lit t1
        w.Text(Application.capitalize ctx.Platform.Browser)
        w.Lit t2
        Assets.imageTag w ctx "disclosure.svg" (Tag.attrs().AriaHidden().Size(10).Class("disclosure"))
        w.Lit t3
        if ctx.Platform.Firefox && ctx.Platform.Android then
            w.Lit t4
            Assets.imageTag w ctx "lock.svg" (Tag.attrs().Alt("the View site information button").Size(20))
            w.Lit t5
        elif ctx.Platform.Edge && ctx.Platform.Desktop then
            w.Lit t6
            Assets.imageTag w ctx "lock.svg" (Tag.attrs().Alt("the View site information button").Size(20))
            w.Lit t7
            w.Text(Application.capitalize ctx.Platform.Browser)
            w.Lit t8
            if ctx.Platform.Windows then
                w.Lit t9
                Assets.imageTag w ctx "external/switch.svg" (Tag.attrs().Alt("the switch").Size(22))
                w.Lit t10
                w.Text(Application.capitalize ctx.Platform.Browser)
                w.Lit t11
            else
                w.Lit t12
                w.Text(Application.capitalize ctx.Platform.Browser)
                w.Lit t13
                Assets.imageTag w ctx "external/switch.svg" (Tag.attrs().Alt("the switch").Size(22))
                w.Lit t14
            w.Lit t15
        elif ctx.Platform.Firefox && ctx.Platform.Desktop then
            w.Lit t16
            w.Text(Application.capitalize ctx.Platform.Browser)
            w.Lit t17
            w.Text(ctx.Url("/"))
            w.Lit t18
            w.Text(Application.capitalize ctx.Platform.Browser)
            w.Lit t19
            if ctx.Platform.Windows then
                w.Lit t20
                Assets.imageTag w ctx "external/switch.svg" (Tag.attrs().Alt("the toggle button").Size(22))
                w.Lit t21
                w.Text(Application.capitalize ctx.Platform.Browser)
                w.Lit t22
            else
                w.Lit t23
                w.Text(Application.capitalize ctx.Platform.Browser)
                w.Lit t24
                Assets.imageTag w ctx "external/switch.svg" (Tag.attrs().Alt("the switch").Size(22))
                w.Lit t25
            w.Lit t26
        elif ctx.Platform.Chrome && ctx.Platform.Desktop then
            w.Lit t27
            Assets.imageTag w ctx "external/sliders.svg" (Tag.attrs().Alt("View site information").Size(20))
            w.Lit t28
            w.Text(Application.capitalize ctx.Platform.Browser)
            w.Lit t29
            if ctx.Platform.Windows then
                w.Lit t30
                Assets.imageTag w ctx "external/switch.svg" (Tag.attrs().Alt("the switch").Size(22))
                w.Lit t31
                w.Text(Application.capitalize ctx.Platform.Browser)
                w.Lit t32
            else
                w.Lit t33
                w.Text(Application.capitalize ctx.Platform.Browser)
                w.Lit t34
                Assets.imageTag w ctx "external/switch.svg" (Tag.attrs().Alt("the switch").Size(22))
                w.Lit t35
            w.Lit t36
        elif ctx.Platform.Chrome && ctx.Platform.Android then
            w.Lit t37
            Assets.imageTag w ctx "menu-dots-vertical.svg" (Tag.attrs().Alt("More options").Size(16))
            w.Lit t38
            Assets.imageTag w ctx "external/switch.svg" (Tag.attrs().Alt("the switch").Size(22))
            w.Lit t39
            w.Text(Application.capitalize ctx.Platform.Browser)
            w.Lit t40
            Assets.imageTag w ctx "external/switch.svg" (Tag.attrs().Alt("the switch").Size(22))
            w.Lit t41
            Assets.imageTag w ctx "notification-bell-alert.svg" (Tag.attrs().Alt("the notification bell").Size(16))
            w.Lit t42
        elif ctx.Platform.Safari && ctx.Platform.Desktop then
            w.Lit t43
            w.Text(Application.capitalize ctx.Platform.Browser)
            w.Lit t44
            w.Text(ctx.Url("/"))
            w.Lit t45
        else
            w.Lit t46
            w.Text(ctx.Url("/"))
            w.Lit t47
        w.Lit t48
