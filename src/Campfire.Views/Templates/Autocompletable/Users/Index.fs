// Port of rust/crates/views/templates/autocompletable/users/index.html (reference/app/views/autocompletable/users/index.html.erb)
/// `autocompletable/users/index.html.erb`: `<lexxy-prompt-item>`s for the mentions prompt, rendered without a layout.
module Campfire.Views.Templates.Autocompletable.Users.Index

open Campfire.Views
open Campfire.Views.Users

let private t0 = Utf8.lit "\n"

let render (w: Out) (ctx: ViewContext) (users: MentionUser list) : unit =
    for user in users do
        _PromptItem.render w ctx user
    w.Lit t0
