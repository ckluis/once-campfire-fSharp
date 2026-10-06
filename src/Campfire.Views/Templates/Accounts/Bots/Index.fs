// Port of rust/crates/views/templates/accounts/bots/index.html (reference/app/views/accounts/bots/index.html.erb)
/// `accounts/bots/index.html.erb`: the account's bots.
module Campfire.Views.Templates.Accounts.Bots.Index

open Campfire.Routes
open Campfire.Views
open Campfire.Views.Accounts
open Campfire.Views.Helpers

let private t0 = Utf8.lit "  <div class=\"flex-item-justify-start\">\n    "
let private t1 = Utf8.lit "\n  </div>\n"
let private t2 = Utf8.lit "<section class=\"panel panel--wide txt-align-center flex flex-column position-relative\" style=\"view-transition-name: chat-bots\">\n  <div class=\"flex align-center gap\">\n    <div class=\"panel__button\">\n      "
let private t3 = Utf8.lit "\n    </div>\n    <div class=\"pad-inline-double center\">\n      <h1 class=\"margin-none\">Chat bots</h1>\n      <p class=\"margin-none-block-start\">With Chat bots, other sites and services can post updates directly to Campfire.</p>\n\n      "
let private t4 = Utf8.lit "\n        "
let private t5 = Utf8.lit "\n        "
let private t6 = Utf8.lit "\n"
let private t7 = Utf8.lit "    </div>\n  </div>\n\n  <div class=\"pad-inline pad-block-start \">\n    <menu class=\"flex flex-column gap margin-none pad\">\n      "
let private t8 = Utf8.lit "    </menu>\n  </div>\n</section>\n"

/// `@page_title`.
let pageTitle: string option = Some "Chat bots"

/// `content_for :head` (empty).
let head: Out -> unit = ignore

/// `content_for :nav`.
let nav (w: Out) (ctx: ViewContext) : unit =
    w.Lit t0
    Application.linkBackTo w ctx (Routes.editAccount ())
    w.Lit t1

/// The page itself.
let content (w: Out) (ctx: ViewContext) (bots: Bot list) : unit =
    w.Lit t2
    Translations.translationButton w ctx "chat_bots"
    w.Lit t3
    Filters.linkTo w (Routes.newAccountBot ()) (Tag.attrs().Class("btn btn--reversed txt-large").Aria("label", "Add a chat bot")) (fun w ->
        w.Lit t4
        Assets.imageTag w ctx "bot.svg" (Tag.attrs().AriaHidden().Size(20))
        w.Lit t5
        Assets.imageTag w ctx "add.svg" (Tag.attrs().AriaHidden().Size(20))
        w.Lit t6)
    w.Lit t7
    for bot in bots do
        _Bot.render w ctx bot
    w.Lit t8

let render (w: Out) (ctx: ViewContext) (bots: Bot list) : unit =
    Templates.Layouts.Application.render w ctx pageTitle None head (fun w -> nav w ctx) (fun w -> content w ctx bots) ignore ignore
