// Port of rust/crates/views/templates/accounts/bots/new.html (reference/app/views/accounts/bots/new.html.erb)
/// `accounts/bots/new.html.erb`.
module Campfire.Views.Templates.Accounts.Bots.New

open Campfire.Routes
open Campfire.Views
open Campfire.Views.Accounts
open Campfire.Views.Helpers

let private t0 = Utf8.lit "  <div class=\"flex-item-justify-start\">\n    "
let private t1 = Utf8.lit "\n  </div>\n"
let private t2 = Utf8.lit "<section class=\"panel\">\n"
let private t3 = Utf8.lit "  "
let private t4 = Utf8.lit "\n    "
let private t5 = Utf8.lit "\n"
let private t6 = Utf8.lit "</section>\n"

/// `@page_title`.
let pageTitle: string option = Some "New chat bot"

/// `content_for :head` (empty).
let head: Out -> unit = ignore

/// `content_for :nav`.
let nav (w: Out) (ctx: ViewContext) : unit =
    w.Lit t0
    Application.linkBackTo w ctx (Routes.accountBots ())
    w.Lit t1

/// The page itself.
let content (w: Out) (ctx: ViewContext) (bot: BotForm) : unit =
    w.Lit t2
    let form = (Forms.formWith (Routes.accountBots ())).Model("user").Class("flex flex-column gap")
    w.Lit t3
    Filters.formWith w form (fun w ->
        w.Lit t4
        _Form.render w ctx form bot
        w.Lit t5)
    w.Lit t6

let render (w: Out) (ctx: ViewContext) (bot: BotForm) : unit =
    Templates.Layouts.Application.render w ctx pageTitle None head (fun w -> nav w ctx) (fun w -> content w ctx bot) ignore ignore
