// Port of rust/crates/views/templates/messages/_presentation.html (reference/app/views/messages/_presentation.html.erb)
/// `messages/_presentation.html.erb`, which `MessagesController#update` also broadcasts.
module Campfire.Views.Templates.Messages._Presentation

open Campfire.Views
open Campfire.Views.Messages

let private presentationId = Utf8.lit "presentation_message_"

let private t0 = Utf8.lit "<div id=\""
let private t1 = Utf8.lit "\" dir=\"auto\" data-reply-target=\"body\" data-messages-target=\"body\">\n  "
let private t2 = Utf8.lit "\n</div>"

let render (w: Out) (ctx: ViewContext) (message: MessageView) : unit =
    w.Lit t0
    w.Lit presentationId
    w.Text message.ClientMessageId
    w.Lit t1
    MessagesPresentation.messagePresentation w ctx message
    w.Lit t2
