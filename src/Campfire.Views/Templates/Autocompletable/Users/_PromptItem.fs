// Port of rust/crates/views/templates/autocompletable/users/_prompt_item.html (reference/app/views/autocompletable/users/_prompt_item.html.erb)
/// `autocompletable/users/_prompt_item.html.erb`: one `<lexxy-prompt-item>` of the mentions prompt.
module Campfire.Views.Templates.Autocompletable.Users._PromptItem

open Campfire.Views
open Campfire.Views.Helpers
open Campfire.Views.Users

let private t0 = Utf8.lit "<lexxy-prompt-item search=\""
let private t1 = Utf8.lit "\" sgid=\""
let private t2 = Utf8.lit "\">\n  <template type=\"menu\">\n    <span class=\"autocomplete__item flex align-center gap unpad\">\n      "
let private t3 = Utf8.lit "\n      <span class=\"autocompletable__name\">"
let private t4 = Utf8.lit "</span>\n    </span>\n  </template>\n  <template type=\"editor\">\n    "
let private t5 = Utf8.lit "\n  </template>\n</lexxy-prompt-item>\n"

let render (w: Out) (ctx: ViewContext) (user: MentionUser) : unit =
    w.Lit t0
    w.Text user.User.Name
    w.Lit t1
    w.Text user.AttachableSgid
    w.Lit t2
    UsersHelper.avatarTag w ctx user.User.Avatar (Tag.attrs())
    w.Lit t3
    w.Text user.User.Name
    w.Lit t4
    Campfire.Views.Templates.Users._Mention.render w ctx user
    w.Lit t5
