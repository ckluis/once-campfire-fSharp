// Port of rust/crates/views/templates/rooms/directs/edit.html (reference/app/views/rooms/directs/edit.html.erb)
/// `rooms/directs/edit.html.erb`: a Ping's members, and the button that deletes it.
module Campfire.Views.Templates.Rooms.Directs.Edit

open Campfire.Routes
open Campfire.Views
open Campfire.Views.Helpers
open Campfire.Views.Rooms

let private t0 = Utf8.lit "\n  <div class=\"flex-item-justify-start\">\n    "
let private t1 = Utf8.lit "\n  </div>"
let private t2 = Utf8.lit "\n<div class=\"panel txt-align-center\">\n  <section class=\"directs--edit margin-block-end\">"
let private t3 = Utf8.lit "\n      <div class=\"member flex flex-column gap fill-shade pad border-radius\">\n        <figure class=\"avatar center\" style=\"--avatar-border-radius: 10ch; --avatar-size: 10ch;\" >\n          <a title=\""
let private t4 = Utf8.lit "\" class=\"btn avatar\" data-turbo-frame=\"_top\" href=\""
let private t5 = Utf8.lit "\"><img aria-hidden=\"true\" loading=\"lazy\" src=\""
let private t6 = Utf8.lit "\" width=\"48\" height=\"48\" /></a>\n        </figure>\n\n        <strong>"
let private t7 = Utf8.lit "</strong>\n      </div>"
let private t8 = Utf8.lit "\n  </section>\n\n  <form class=\"button_to\" method=\"post\" action=\""
let private t9 = Utf8.lit "\"><input type=\"hidden\" name=\"_method\" value=\"delete\" /><button class=\"btn btn--negative center\" aria-label=\"Delete Ping\" data-turbo-confirm=\"Are you sure you want to delete this ping and all messages in it? This can’t be undone.\" type=\"submit\">\n    <img aria-hidden=\"true\" src=\""
let private t10 = Utf8.lit "\" />\n    Ping\n</button></form></div>"

/// `@page_title`.
let pageTitle (edit: DirectEditView) : string option = Some("Edit settings for " + edit.DisplayName)

/// `content_for :head` (empty).
let head: Out -> unit = ignore

/// `content_for :nav`.
let nav (w: Out) (ctx: ViewContext) : unit =
    w.Lit t0
    Application.linkBackToLastRoomVisited w ctx
    w.Lit t1

/// The page itself.
let content (w: Out) (ctx: ViewContext) (edit: DirectEditView) : unit =
    w.Lit t2
    for user in edit.Users do
        w.Lit t3
        w.Text user.Title
        w.Lit t4
        w.Text user.Path
        w.Lit t5
        w.Text user.AvatarUrl
        w.Lit t6
        w.Text user.Name
        w.Lit t7
    w.Lit t8
    w.Text(ctx.Url(Routes.roomsDirect edit.RoomId))
    w.Lit t9
    w.Text(ctx.Asset "trash.svg")
    w.Lit t10

let render (w: Out) (ctx: ViewContext) (edit: DirectEditView) : unit =
    Campfire.Views.Templates.Layouts.Application.render
        w
        ctx
        (pageTitle edit)
        None
        head
        (fun w -> nav w ctx)
        (fun w -> content w ctx edit)
        ignore
        ignore
