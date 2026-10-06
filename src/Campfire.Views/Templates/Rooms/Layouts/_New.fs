// Port of rust/crates/views/templates/rooms/layouts/_new.html (reference/app/views/rooms/layouts/_new.html.erb)
/// `rooms/layouts/_new.html.erb`: the page layout for new rooms; a page fills `roomForm`.
module Campfire.Views.Templates.Rooms.Layouts._New

open Campfire.Views
open Campfire.Views.Helpers

let private t0 = Utf8.lit "\n  <div class=\"flex-item-justify-start\">\n    "
let private t1 = Utf8.lit "\n  </div>"
let private t2 = Utf8.lit "\n<section class=\"panel txt-align-center\" style=\"view-transition-name: new-room\">\n  "
let private t3 = Utf8.lit "\n</section>"

/// `content_for :head` (empty).
let head: Out -> unit = ignore

/// `content_for :nav`.
let nav (w: Out) (ctx: ViewContext) : unit =
    w.Lit t0
    Application.linkBackToLastRoomVisited w ctx
    w.Lit t1

/// The page itself, around the page's `roomForm`.
let content (w: Out) (roomForm: Out -> unit) : unit =
    w.Lit t2
    roomForm w
    w.Lit t3

let render (w: Out) (ctx: ViewContext) (pageTitle: string option) (roomForm: Out -> unit) : unit =
    Campfire.Views.Templates.Layouts.Application.render
        w
        ctx
        pageTitle
        None
        head
        (fun w -> nav w ctx)
        (fun w -> content w roomForm)
        ignore
        ignore
