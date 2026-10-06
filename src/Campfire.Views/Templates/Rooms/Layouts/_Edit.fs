// Port of rust/crates/views/templates/rooms/layouts/_edit.html (reference/app/views/rooms/layouts/_edit.html.erb)
/// `rooms/layouts/_edit.html.erb`: the page layout for editing a room; a page fills `roomForm`. The title is the page's
/// `@page_title`; `canAdminister`, `roomId` and `displayName` are what the open and closed forms share.
module Campfire.Views.Templates.Rooms.Layouts._Edit

open Campfire.Views
open Campfire.Views.Helpers
open Campfire.Views.Rooms

let private t0 = Utf8.lit "\n  <div class=\"flex-item-justify-start\">\n    "
let private t1 = Utf8.lit "\n  </div>"
let private t2 = Utf8.lit "\n<section class=\"panel txt-align-center\" style=\"view-transition-name: edit-room-"
let private t3 = Utf8.lit "\">\n  "
let private t4 = Utf8.lit "\n</section>"
let private t5 = Utf8.lit "\n  <section class=\"panel txt-align-center\">\n    "
let private t6 = Utf8.lit "\n  </section>"

/// `content_for :head` (empty).
let head: Out -> unit = ignore

/// `content_for :nav`.
let nav (w: Out) (ctx: ViewContext) : unit =
    w.Lit t0
    Application.linkBackToLastRoomVisited w ctx
    w.Lit t1

/// The page itself, around the page's `roomForm`.
let content (w: Out) (ctx: ViewContext) (canAdminister: bool) (roomId: int64) (displayName: string) (roomForm: Out -> unit) : unit =
    w.Lit t2
    w.Int roomId
    w.Lit t3
    roomForm w
    w.Lit t4
    if canAdminister then
        w.Lit t5
        buttonToDeleteRoom w ctx roomId displayName
        w.Lit t6

let render
    (w: Out)
    (ctx: ViewContext)
    (pageTitle: string option)
    (canAdminister: bool)
    (roomId: int64)
    (displayName: string)
    (roomForm: Out -> unit)
    : unit =
    Campfire.Views.Templates.Layouts.Application.render
        w
        ctx
        pageTitle
        None
        head
        (fun w -> nav w ctx)
        (fun w -> content w ctx canAdminister roomId displayName roomForm)
        ignore
        ignore
