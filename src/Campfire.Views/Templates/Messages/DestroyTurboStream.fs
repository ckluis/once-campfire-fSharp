// Port of rust/crates/views/templates/messages/destroy.turbo_stream.html (reference/app/views/messages/destroy.turbo_stream.erb)
/// `messages/destroy.turbo_stream.erb`, also what `Message#broadcast_remove` sends.
module Campfire.Views.Templates.Messages.DestroyTurboStream

open Campfire.Views
open Campfire.Views.Messages

let private messageId = Utf8.lit "message_"

let private t0 = Utf8.lit "<turbo-stream action=\"remove\" target=\""
let private t1 = Utf8.lit "\"></turbo-stream>"

let render (w: Out) (message: MessageView) : unit =
    w.Lit t0
    w.Lit messageId
    w.Text message.ClientMessageId
    w.Lit t1
