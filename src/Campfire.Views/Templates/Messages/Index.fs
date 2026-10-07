// Port of rust/crates/views/templates/messages/index.html (reference/app/views/messages/index.html.erb)
/// `messages/index.html.erb`: the page of messages the client fetches while scrolling (no layout).
module Campfire.Views.Templates.Messages.Index

open Campfire.Views
open Campfire.Views.Messages

let private t0 = Utf8.lit "\n"

let render (w: Out) (ctx: ViewContext) (messages: MessageItem list) : unit =
    for message in messages do
        w.Lit t0
        MessagesCached.cachedMessageItem w ctx message
