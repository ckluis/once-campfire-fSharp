// Port of rust/crates/views/templates/users/autocompletables/_template.html (reference/app/views/users/autocompletables/_template.html.erb)
/// `users/autocompletables/_template.html.erb`: the client-side template for a selected mention.
module Campfire.Views.Templates.Users.Autocompletables._Template

open Campfire.Views
open Campfire.Views.Helpers

let private t0 = Utf8.lit "<template id=\"autocompletable-user\">\n  <div class=\"autocomplete__pill max-width\" data-value=\"\" tabindex=\"0\">\n    <img class=\"avatar flex-item-no-shrink\" data-content=\"avatar\" src=\"\" />\n    <span class=\"autocomplete-field__selected-value-text overflow-ellipsis flex-item-grow\" data-content=\"label\"></span>\n\n    <button type=\"button\" data-action=\"autocomplete#remove:prevent\" data-value=\"\" tabindex=\"-1\" class=\"btn btn--plain txt-small translucent flex-item-no-shrink\">\n      "
let private t1 = Utf8.lit "\n      <span class=\"for-screen-reader\">Remove <span data-content=\"screenReaderLabel\"></span></span>\n    </button>\n  </div>\n</template>\n"

let render (w: Out) (ctx: ViewContext) : unit =
    w.Lit t0
    Assets.imageTag w ctx "remove-circle.svg" (Tag.attrs().AriaHidden().Class("colorize--black"))
    w.Lit t1
