// Port of rust/crates/views/templates/rooms/closeds/new.html (reference/app/views/rooms/closeds/new.html.erb)
/// `rooms/closeds/new.html.erb`.
module Campfire.Views.Templates.Rooms.Closeds.New

open Campfire.Routes
open Campfire.Views
open Campfire.Views.Templates.Rooms
open Campfire.Views.Rooms



/// `@page_title`.
let pageTitle: string option = Some "New chat room"

/// `content_for :head` (empty).
let head: Out -> unit = Layouts._New.head

/// The page itself.
let content (w: Out) (ctx: ViewContext) (form: ClosedFormView) : unit =
    Layouts._New.content w (fun w -> _Form.render w ctx form (Routes.newRoomsOpen ()))

let render (w: Out) (ctx: ViewContext) (form: ClosedFormView) : unit =
    Layouts._New.render w ctx pageTitle (fun w -> _Form.render w ctx form (Routes.newRoomsOpen ()))
