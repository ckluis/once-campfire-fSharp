// Port of rust/crates/views/templates/messages/boosts/index.html (reference/app/views/messages/boosts/index.html.erb)
/// `messages/boosts/index.html.erb`.
module Campfire.Views.Templates.Messages.Boosts.Index

open Campfire.Views
open Campfire.Views.Messages



let render (w: Out) (ctx: ViewContext) (message: MessageView) : unit = _Boosts.render w ctx message
