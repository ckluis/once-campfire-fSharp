// Port of rust/crates/views/templates/rooms/opens/edit.html (reference/app/views/rooms/opens/edit.html.erb)
/// `rooms/opens/edit.html.erb`.
module Campfire.Views.Templates.Rooms.Opens.Edit

open Campfire.Routes
open Campfire.Views
open Campfire.Views.Templates.Rooms
open Campfire.Views.Rooms



/// `@page_title`.
let pageTitle (form: OpenFormView) : string option = Some("Edit settings for " + form.Room.DisplayName)

/// `content_for :head` (empty).
let head: Out -> unit = Layouts._Edit.head

/// The page itself.
let content (w: Out) (ctx: ViewContext) (form: OpenFormView) : unit =
    let roomId = defaultArg form.Room.Id 0L
    Layouts._Edit.content w ctx form.CanAdminister roomId form.Room.DisplayName (fun w ->
        _Form.render w ctx form (Routes.editRoomsClosed roomId))

let render (w: Out) (ctx: ViewContext) (form: OpenFormView) : unit =
    let roomId = defaultArg form.Room.Id 0L
    Layouts._Edit.render w ctx (pageTitle form) form.CanAdminister roomId form.Room.DisplayName (fun w ->
        _Form.render w ctx form (Routes.editRoomsClosed roomId))
