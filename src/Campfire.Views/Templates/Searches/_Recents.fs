// Port of rust/crates/views/templates/searches/_recents.html
/// The recent-searches links repeated in `searches/index.html.erb`'s nav and sidebar.
module Campfire.Views.Templates.Searches._Recents

open Campfire.Routes
open Campfire.Views
open Campfire.Views.Searches

let private t0 = Utf8.lit "\n      <a class=\"align-center gap room btn txt-nowrap\" href=\""
let private t1 = Utf8.lit "\">\n        <span class=\"overflow-ellipsis\">“"
let private t2 = Utf8.lit "”</span>\n</a>"
let private t3 = Utf8.lit "\n      <form class=\"button_to\" method=\"post\" action=\""
let private t4 = Utf8.lit "\"><input type=\"hidden\" name=\"_method\" value=\"delete\" /><button class=\"btn searches__btn\" data-turbo-confirm=\"Are you sure you want to clear your recent searches?\" type=\"submit\">\n        <img aria-hidden=\"true\" src=\""
let private t5 = Utf8.lit "\" />\n        <span class=\"for-screen-reader\">Clear recent searches</span>\n</button></form>"

let render (w: Out) (ctx: ViewContext) (index: IndexView) : unit =
    for search in index.RecentSearches do
        w.Lit t0
        w.Text(searchPath search)
        w.Lit t1
        w.Text search
        w.Lit t2
    if not index.RecentSearches.IsEmpty then
        w.Lit t3
        w.Text(ctx.Url(Routes.clearSearches ()))
        w.Lit t4
        w.Text(ctx.Asset "broom.svg")
        w.Lit t5
