// Port of rust/crates/views/templates/users/_mention.html (reference/app/views/users/_mention.html.erb)
/// `users/_mention.html.erb`: the mention attachment's HTML. A span rather than a div: mentions render
/// inline inside the <p> paragraphs the editor produces, and block elements would make the browser
/// split the paragraph when parsing.
module Campfire.Views.Templates.Users._Mention

open Campfire.Views
open Campfire.Views.Helpers
open Campfire.Views.Users

let private t0 = Utf8.lit "<span class=\"mention\" sgid=\""
let private t1 = Utf8.lit "\">"
let private t2 = Utf8.lit " "
let private t3 = Utf8.lit "</span>\n"

let render (w: Out) (ctx: ViewContext) (user: MentionUser) : unit =
    w.Lit t0
    w.Text user.AttachableSgid
    w.Lit t1
    UsersHelper.avatarTag w ctx user.User.Avatar (Tag.attrs())
    w.Lit t2
    w.Text user.User.Name
    w.Lit t3
