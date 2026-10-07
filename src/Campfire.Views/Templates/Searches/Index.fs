// Port of rust/crates/views/templates/searches/index.html (reference/app/views/searches/index.html.erb)
/// `searches/index.html.erb`, with `SearchesHelper#search_results_tag`: the search page.
module Campfire.Views.Templates.Searches.Index

open Campfire.Routes
open Campfire.Views
open Campfire.Views.Searches

let private t0 = Utf8.lit "\n    <div class=\"searches__query flex align-center gap pad-block-start-half\">\n      <div class=\"btn btn--reversed btn--faux align-center gap txt-nowrap\">\n        <span class=\"overflow-ellipsis\">“"
let private t1 = Utf8.lit "”</span>\n        <span class=\"flex-item-no-shrink\">"
let private t2 = Utf8.lit "</span>\n</div>    </div>"
let private t3 = Utf8.lit "\n\n  <div class=\"searches__recents align-center gap pad-block-half overflow-y overflow-hide-scrollbar\">\n    "
let private t4 = Utf8.lit "\n  </div>"
let private t5 = Utf8.lit "\n  <div class=\"rooms position-relative flex flex-column gap overflow-y overflow-hide-scrollbar\">\n    "
let private t6 = Utf8.lit "\n  </div>"
let private t7 = Utf8.lit "\n<div id=\"message-area\" class=\"message-area\">\n  <div class=\"message-area--empty min-width center\">\n    <figure class=\"center pad\">\n      <img aria-hidden=\"true\" class=\"colorize--black translucent\" src=\""
let private t8 = Utf8.lit "\" />\n    </figure>\n  </div>\n\n  <div id=\"search-results\" class=\"messages searches__results\" data-controller=\"search-results\" data-search-results-target=\"messages\" data-search-results-me-class=\"message--me\" data-search-results-threaded-class=\"message--threaded\" data-search-results-mentioned-class=\"message--mentioned\" data-search-results-formatted-class=\"message--formatted\">"
let private t9 = Utf8.lit "\n    "
let private t10 = Utf8.lit "\n  </div></div>"
let private t11 = Utf8.lit "\n  <div class=\"composer flex align-end gap\">\n    <a class=\"btn flex-item-no-shrink margin-block-end\" style=\"view-transition-name: input-switcher; --btn-border-radius: 0.5em\" href=\""
let private t12 = Utf8.lit "\">\n      <img aria-hidden=\"true\" src=\""
let private t13 = Utf8.lit "\" />\n      <span class=\"for-screen-reader\">Exit search </span>\n</a>\n    <form class=\"margin-block flex-item-grow contain flex align-center gap\" data-controller=\"form\" data-action=\"keydown.esc-&gt;form#cancel\" action=\""
let private t14 = Utf8.lit "\" accept-charset=\"UTF-8\" method=\"post\">\n      <div class=\"composer__input flex align-center flex-item-grow gap full-width input input--actor min-width\">\n        <img aria-hidden=\"true\" class=\"composer__input-hint colorize--black\" style=\"view-transition-name: input-btn;\" src=\""
let private t15 = Utf8.lit "\" width=\"20\" height=\"20\" />\n\n        <input"
let private t16 = Utf8.lit " value=\""
let private t17 = Utf8.lit "\""
let private t18 = Utf8.lit " class=\"searches__input input flex-item-grow\" role=\"searchbox\" aria-label=\"search\" autofocus=\"autofocus\" required=\"required\" type=\"text\" name=\"q\" id=\"q\" />\n\n        <a data-form-target=\"cancel\" role=\"button\" class=\"searches__reset\" href=\""
let private t19 = Utf8.lit "\">\n          <img aria-hidden=\"true\" class=\"colorize--black\" src=\""
let private t20 = Utf8.lit "\" width=\"14\" height=\"14\" />\n          <span class=\"for-screen-reader\">Clear search field</span>\n</a>\n        <button name=\"button\" type=\"submit\" class=\"btn btn--reversed flex-item-no-shrink txt-small\" style=\"--btn-border-radius: 0.5em\">\n          <img aria-hidden=\"true\" src=\""
let private t21 = Utf8.lit "\" />\n          <span class=\"for-screen-reader\">Search</span>\n</button>      </div>\n</form>  </div>"

/// `@page_title`.
let pageTitle: string option = Some "Search"

/// `@body_class`.
let bodyClass: string option = Some "sidebar searches"

/// `content_for :head` (empty).
let head: Out -> unit = ignore

/// `content_for :nav`.
let nav (w: Out) (ctx: ViewContext) (index: IndexView) : unit =
    match index.Query with
    | Some query ->
        w.Lit t0
        w.Text query
        w.Lit t1
        w.Int(int64 index.Messages.Length)
        w.Lit t2
    | None -> ()
    w.Lit t3
    _Recents.render w ctx index
    w.Lit t4

/// `content_for :sidebar`.
let sidebar (w: Out) (ctx: ViewContext) (index: IndexView) : unit =
    w.Lit t5
    _Recents.render w ctx index
    w.Lit t6

/// The page itself.
let content (w: Out) (ctx: ViewContext) (index: IndexView) : unit =
    w.Lit t7
    w.Text(ctx.Asset "search.svg")
    w.Lit t8
    for message in index.Messages do
        w.Lit t9
        MessagesCached.cachedMessageItem w ctx message
    w.Lit t10

/// `content_for :footer`.
let footer (w: Out) (ctx: ViewContext) (index: IndexView) : unit =
    w.Lit t11
    w.Text(Routes.room index.ReturnToRoomId)
    w.Lit t12
    w.Text(ctx.Asset "arrow-left.svg")
    w.Lit t13
    w.Text(Routes.searches ())
    w.Lit t14
    w.Text(ctx.Asset "search.svg")
    w.Lit t15
    match index.Q with
    | Some q ->
        w.Lit t16
        w.Text q
        w.Lit t17
    | None -> ()
    w.Lit t18
    w.Text(Routes.searches ())
    w.Lit t19
    w.Text(ctx.Asset "remove.svg")
    w.Lit t20
    w.Text(ctx.Asset "arrow-up.svg")
    w.Lit t21

let render (w: Out) (ctx: ViewContext) (index: IndexView) : unit =
    Campfire.Views.Templates.Layouts.Application.render
        w
        ctx
        pageTitle
        bodyClass
        head
        (fun w -> nav w ctx index)
        (fun w -> content w ctx index)
        (fun w -> footer w ctx index)
        (fun w -> sidebar w ctx index)
