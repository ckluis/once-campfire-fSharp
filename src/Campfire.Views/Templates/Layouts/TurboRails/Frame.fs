// Port of rust/crates/views/templates/layouts/turbo_rails/frame.html (turbo-rails' app/views/layouts/turbo_rails/frame.html.erb)
/// turbo-rails' layout for Turbo-Frame requests, used instead of the application layout whenever a
/// request carries a `Turbo-Frame` header.
module Campfire.Views.Templates.Layouts.TurboRails.Frame

open Campfire.Views

let private t0 = Utf8.lit "<html>\n  <head>\n    "
let private t1 = Utf8.lit "\n  </head>\n  <body>\n    "
let private t2 = Utf8.lit "\n  </body>\n</html>\n"

/// `head` is the page's `:head` content; `content` is the page itself, with the fragments it
/// recorded (`Out.Page`).
let render (w: Out) (head: Out -> unit) (content: Out -> unit) : unit =
    w.Lit t0
    head w
    w.Lit t1
    content w
    w.Lit t2
