// Port of rust/crates/views/templates/rooms/directs/new.html (reference/app/views/rooms/directs/new.html.erb)
/// `rooms/directs/new.html.erb`: the form that starts a Ping.
module Campfire.Views.Templates.Rooms.Directs.New

open Campfire.Routes
open Campfire.Views

let private t0 = Utf8.lit "\n<turbo-frame id=\"direct_rooms_control\" target=\"_top\">\n  <div class=\"directs directs--new flex flex-column gap\">\n    <form class=\"flex gap flex-item-grow\" data-controller=\"form\" data-action=\"keydown.esc-&gt;form#cancel\" action=\""
let private t1 = Utf8.lit "\" accept-charset=\"UTF-8\" method=\"post\">\n      <a class=\"btn flex-item-no-shrink\" data-turbo-frame=\"user_sidebar\" data-form-target=\"cancel\" href=\""
let private t2 = Utf8.lit "\">\n        <img aria-hidden=\"true\" src=\""
let private t3 = Utf8.lit "\" />\n        <span class=\"for-screen-reader\">Cancel changes</span>\n</a>\n      <section class=\"autocomplete__container unpad input input--actor\">\n        <div class=\"autocomplete__input input flex flex-wrap position-relative flex-item-grow\"\n            data-controller=\"autocomplete\" data-autocomplete-url-value=\""
let private t4 = Utf8.lit "\">\n          <select name=\"user_ids[]\" data-autocomplete-target=\"select\" data-template-id=\"autocompletable-user\" multiple=\"true\" hidden required></select>\n\n          "
let private t5 = Utf8.lit "\n\n          <input autocomplete=\"off\" autocorrect=\"off\" data-1p-ignore=\"true\" class=\"autocomplete__input input flex flex-wrap position-relative\" data-autocomplete-target=\"input\" data-action=\"input-&gt;autocomplete#search keydown-&gt;autocomplete#didPressKey\" type=\"text\" name=\"rooms_direct[user_ids_input]\" id=\"rooms_direct_user_ids_input\" />\n        </div>\n      </section>\n\n      <button name=\"button\" type=\"submit\" class=\"btn btn--reversed flex-item-no-shrink\">\n        <img aria-hidden=\"true\" src=\""
let private t6 = Utf8.lit "\" />\n        <span class=\"for-screen-reader\">Start Ping</span>\n</button></form>\n    <span class=\"txt-small translucent pad-inline-half center\">Type names to ping someone…</span>\n  </div>\n</turbo-frame>"

/// `@page_title`.
let pageTitle: string option = None

/// `content_for :head` (empty).
let head: Out -> unit = ignore

/// The page itself.
let content (w: Out) (ctx: ViewContext) : unit =
    w.Lit t0
    w.Text(Routes.roomsDirects ())
    w.Lit t1
    w.Text(Routes.userSidebar ())
    w.Lit t2
    w.Text(ctx.Asset "arrow-left.svg")
    w.Lit t3
    w.Text(Routes.autocompletableUsers ())
    w.Lit t4
    Templates.Users.Autocompletables._Template.render w ctx
    w.Lit t5
    w.Text(ctx.Asset "check.svg")
    w.Lit t6

let render (w: Out) (ctx: ViewContext) : unit =
    Campfire.Views.Templates.Layouts.Application.render w ctx pageTitle None head ignore (fun w -> content w ctx) ignore ignore
