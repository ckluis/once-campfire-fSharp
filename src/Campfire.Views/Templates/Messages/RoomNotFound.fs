// Port of rust/crates/views/templates/messages/room_not_found.html (reference/app/views/messages/room_not_found.html.erb)
/// `messages/room_not_found.html.erb`: the composer's frame when the room was deleted.
module Campfire.Views.Templates.Messages.RoomNotFound

open Campfire.Views

let private t0 = Utf8.lit "<turbo-frame id=\"composer-frame\">\n  <span class=\"composer__input input input--actor shake margin-block-end txt-negative txt-align-center\" style=\"--input-border-color: var(--color-negative)\">\n      <span>This room was deleted.</span>\n  </span>\n</turbo-frame>"

let render (w: Out) : unit = w.Lit t0
