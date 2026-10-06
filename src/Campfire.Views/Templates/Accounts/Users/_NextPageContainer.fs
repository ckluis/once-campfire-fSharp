// Port of rust/crates/views/templates/accounts/users/_next_page_container.html (reference/app/views/accounts/users/_next_page_container.html.erb)
/// `accounts/users/_next_page_container.html.erb`: the lazy frame that loads the next page of users.
module Campfire.Views.Templates.Accounts.Users._NextPageContainer

open Campfire.Views
open Campfire.Views.Helpers

let private t0 = Utf8.lit "\n  <div class=\"spinner center\"></div>\n"

let render (w: Out) (page: string) : unit =
    Filters.turboFrameTag
        w
        "next_page_container"
        (Tag.attrs().Loading("lazy").Attr("src", "/account/users.turbo_stream?page=" + Url.cgiEscape page).Class("flex center"))
        (fun w -> w.Lit t0)
