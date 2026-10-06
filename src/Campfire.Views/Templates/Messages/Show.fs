// Port of rust/crates/views/templates/messages/show.html (reference/app/views/messages/show.html.erb)
/// `messages/show.html.erb`: the message partial (no layout).
module Campfire.Views.Templates.Messages.Show

open Campfire.Views
open Campfire.Views.Messages



let render (w: Out) (ctx: ViewContext) (message: MessageView) : unit = MessagesCached.cachedMessage w ctx message
