// Port of rust/crates/views/templates/accounts/bots/edit.html (reference/app/views/accounts/bots/edit.html.erb)
/// `accounts/bots/edit.html.erb`: a bot's form, with the buttons that delete it and change its key.
module Campfire.Views.Templates.Accounts.Bots.Edit

open Campfire.Routes
open Campfire.Views
open Campfire.Views.Accounts
open Campfire.Views.Helpers

let private t0 = Utf8.lit "  <div class=\"flex-item-justify-start\">\n    "
let private t1 = Utf8.lit "\n  </div>\n"
let private t2 = Utf8.lit "<section class=\"panel\" style=\"view-transition-name: chat-bot-"
let private t3 = Utf8.lit "\">\n"
let private t4 = Utf8.lit "  "
let private t5 = Utf8.lit "\n    "
let private t6 = Utf8.lit "\n"
let private t7 = Utf8.lit "\n  <hr class=\"separator full-width margin-block-double\">\n\n  <div class=\"flex align-center gap justify-space-between\">\n    "
let private t8 = Utf8.lit "\n      "
let private t9 = Utf8.lit "\n      "
let private t10 = Utf8.lit "\n"
let private t11 = Utf8.lit "\n    "
let private t12 = Utf8.lit "\n      "
let private t13 = Utf8.lit "\n      "
let private t14 = Utf8.lit "\n"
let private t15 = Utf8.lit "  </div>\n</section>\n"

/// `@page_title`.
let pageTitle: string option = Some "Edit bot"

/// `content_for :head` (empty).
let head: Out -> unit = ignore

/// `content_for :nav`.
let nav (w: Out) (ctx: ViewContext) : unit =
    w.Lit t0
    Application.linkBackTo w ctx (Routes.accountBots ())
    w.Lit t1

/// The page itself.
let content (w: Out) (ctx: ViewContext) (botId: int64) (bot: BotForm) : unit =
    w.Lit t2
    w.Int botId
    w.Lit t3
    let form = (Forms.formWith (Routes.accountBot botId)).Model("user").Method("patch").Class("flex flex-column gap")
    w.Lit t4
    Filters.formWith w form (fun w ->
        w.Lit t5
        _Form.render w ctx form bot
        w.Lit t6)
    w.Lit t7
    Filters.buttonTo
        w
        (Routes.accountBot botId)
        (Tag.attrs()
            .Method("delete")
            .Class("btn txt--small btn--negative")
            .Aria("label", "Delete this chat bot")
            .Data("turbo_confirm", "Are you sure you want to permanently remove this bot from the account? This can’t be undone."))
        (fun w ->
            w.Lit t8
            Assets.imageTag w ctx "trash.svg" (Tag.attrs().AriaHidden().Size(20))
            w.Lit t9
            Assets.imageTag w ctx "bot.svg" (Tag.attrs().AriaHidden().Size(20))
            w.Lit t10)
    w.Lit t11
    Filters.buttonTo
        w
        (Routes.accountBotKey botId)
        (Tag.attrs()
            .Method("put")
            .Class("btn full-width txt--small btn--negative")
            .Aria("label", "Generate a new key")
            .Data("turbo_confirm", "Are you sure you want to change the bot key? All usage of this bot must be updated."))
        (fun w ->
            w.Lit t12
            Assets.imageTag w ctx "refresh.svg" (Tag.attrs().AriaHidden().Size(20))
            w.Lit t13
            Assets.imageTag w ctx "key.svg" (Tag.attrs().AriaHidden().Size(20))
            w.Lit t14)
    w.Lit t15

let render (w: Out) (ctx: ViewContext) (botId: int64) (bot: BotForm) : unit =
    Templates.Layouts.Application.render w ctx pageTitle None head (fun w -> nav w ctx) (fun w -> content w ctx botId bot) ignore ignore
