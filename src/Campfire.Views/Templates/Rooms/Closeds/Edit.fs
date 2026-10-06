// Port of rust/crates/views/templates/rooms/closeds/edit.html (reference/app/views/rooms/closeds/edit.html.erb)
/// `rooms/closeds/edit.html.erb`.
module Campfire.Views.Templates.Rooms.Closeds.Edit

open Campfire.Routes
open Campfire.Views
open Campfire.Views.Templates.Rooms
open Campfire.Views.Rooms



/// `@page_title`.
let pageTitle (form: ClosedFormView) : string option = Some("Edit settings for " + form.Room.DisplayName)

/// `content_for :head` (empty).
let head: Out -> unit = Layouts._Edit.head

/// The page itself.
let content (w: Out) (ctx: ViewContext) (form: ClosedFormView) : unit =
    let roomId = defaultArg form.Room.Id 0L
    Layouts._Edit.content w ctx form.CanAdminister roomId form.Room.DisplayName (fun w ->
        _Form.render w ctx form (Routes.editRoomsOpen roomId))

let render (w: Out) (ctx: ViewContext) (form: ClosedFormView) : unit =
    let roomId = defaultArg form.Room.Id 0L
    Layouts._Edit.render w ctx (pageTitle form) form.CanAdminister roomId form.Room.DisplayName (fun w ->
        _Form.render w ctx form (Routes.editRoomsOpen roomId))
