// Port of rust/crates/views/templates/accounts/bots/_bot.html (reference/app/views/accounts/bots/_bot.html.erb)
/// `accounts/bots/_bot.html.erb`: a bot, with the curl commands that post to each of its rooms.
module Campfire.Views.Templates.Accounts.Bots._Bot

open Campfire.Routes
open Campfire.Views
open Campfire.Views.Accounts
open Campfire.Views.Helpers

let private t0 = Utf8.lit "<li class=\"flex flex-column gap flush fill-shade border-radius pad-block pad-inline-double\">\n  <div class=\"flex align-center gap\">\n    <figure class=\"avatar flex-item--no-shrink\" style=\"--avatar-size: 2.65em;\">\n      "
let private t1 = Utf8.lit "\n    </figure>\n\n    <div class=\"min-width\">\n      <div class=\"overflow-ellipsis txt-large\"><strong>"
let private t2 = Utf8.lit "</strong></div>\n    </div>\n\n    "
let private t3 = Utf8.lit "\n      "
let private t4 = Utf8.lit "\n      <span class=\"for-screen-reader\">Edit "
let private t5 = Utf8.lit "</span>\n"
let private t6 = Utf8.lit "  </div>\n\n"
let private t7 = Utf8.lit "    <fieldset class=\"gap max-width pad border border-radius\">\n      <legend class=\"min-width txt-align-start pad-inline\">\n        <strong class=\"overflow-ellipsis\">"
let private t8 = Utf8.lit "</strong>\n      </legend>\n\n"
let private t9 = Utf8.lit "      <div class=\"flex align-center gap\">\n        "
let private t10 = Utf8.lit "\n\n        <div class=\"flex-item-grow\">\n          <input type=\"text\" class=\"input full-width fill-white\" value=\""
let private t11 = Utf8.lit "\" aria-label=\"curl command for posting messages\" readonly>\n        </div>\n\n        <div class=\"txt-small\">\n          "
let private t12 = Utf8.lit "\n            "
let private t13 = Utf8.lit "\n            <span class=\"for-screen-reader\">Copy message command</span>\n"
let private t14 = Utf8.lit "        </div>\n      </div>\n\n"
let private t15 = Utf8.lit "      <div class=\"flex align-center gap\">\n        "
let private t16 = Utf8.lit "\n\n        <div class=\"flex-item-grow\">\n          <input type=\"text\" class=\"input full-width fill-white\" value=\""
let private t17 = Utf8.lit "\" aria-label=\"curl command for posting attachments\" readonly>\n        </div>\n\n        <div class=\"txt-small\">\n          "
let private t18 = Utf8.lit "\n            "
let private t19 = Utf8.lit "\n            <span class=\"for-screen-reader\">Copy attachment command</span>\n"
let private t20 = Utf8.lit "        </div>\n      </div>\n    </fieldset>\n"
let private t21 = Utf8.lit "</li>\n"

let render (w: Out) (ctx: ViewContext) (bot: Bot) : unit =
    w.Lit t0
    UsersHelper.avatarTag w ctx bot.User.Avatar (Tag.attrs().Loading("lazy"))
    w.Lit t1
    w.Text bot.User.Name
    w.Lit t2
    Filters.linkTo
        w
        (Routes.editAccountBot bot.User.Id)
        (Tag.attrs().Class("btn flex-item-justify-end").Style("view-transition-name: chat-bot-" + string bot.User.Id))
        (fun w ->
            w.Lit t3
            Assets.imageTag w ctx "pencil.svg" (Tag.attrs().AriaHidden().Size(20))
            w.Lit t4
            w.Text bot.User.Name
            w.Lit t5)
    w.Lit t6
    for room in bot.Rooms do
        w.Lit t7
        w.Text room.Name
        w.Lit t8
        let curlTextLine = UsersHelper.curlTextLine (ctx.Url(Routes.roomBotMessages room.Id bot.BotKey))
        w.Lit t9
        Assets.imageTag w ctx "messages-outlined.svg" (Tag.attrs().AriaHidden().Size(24).Class("colorize--black"))
        w.Lit t10
        w.Text curlTextLine
        w.Lit t11
        Filters.buttonToCopyToClipboard w curlTextLine (fun w ->
            w.Lit t12
            Assets.imageTag w ctx "copy-paste.svg" (Tag.attrs().AriaHidden().Size(20))
            w.Lit t13)
        w.Lit t14
        let curlUploadLine = UsersHelper.curlUploadLine (ctx.Url(Routes.roomBotMessages room.Id bot.BotKey))
        w.Lit t15
        Assets.imageTag w ctx "attachment.svg" (Tag.attrs().AriaHidden().Size(24).Class("colorize--black"))
        w.Lit t16
        w.Text curlUploadLine
        w.Lit t17
        Filters.buttonToCopyToClipboard w curlUploadLine (fun w ->
            w.Lit t18
            Assets.imageTag w ctx "copy-paste.svg" (Tag.attrs().AriaHidden().Size(20))
            w.Lit t19)
        w.Lit t20
    w.Lit t21
