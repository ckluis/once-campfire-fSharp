// Port of rust/crates/views/templates/messages/_unrenderable.html (reference/app/views/messages/_unrenderable.html.erb)
/// `messages/_unrenderable.html.erb`: what `message_tag` renders in place of a message whose content raised.
module Campfire.Views.Templates.Messages._Unrenderable

open Campfire.Views

let private t0 = Utf8.lit "<div class=\"message message--formatted message--failed center\">\n  <div class=\"message__body\">\n    <div class=\"message__body-content txt-align-center\">\n      Failed to load message content\n    </div>\n  </div>\n</div>"

let render (w: Out) : unit = w.Lit t0
