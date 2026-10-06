// Port of rust/crates/views/templates/messages/boosts/_boost.html (reference/app/views/messages/boosts/_boost.html.erb)
/// `messages/boosts/_boost.html.erb`, whose body is `cache boost` (`BoostsCached.boost`); the boosts controller
/// also broadcasts it on its own.
module Campfire.Views.Templates.Messages.Boosts._Boost

open Campfire.Views
open Campfire.Views.Messages

let private t0 = Utf8.lit "<div id=\""
let private t1 = Utf8.lit "\"\n      class=\"boost boost-item flex-inline postion--relative max-width align-center fill-white gap\"\n      data-controller=\"boost-delete\" data-boost-delete-perform-class=\"boost--deleting\" data-boost-delete-reveal-class=\"expanded\" data-boost-delete-booster-id-value=\""
let private t2 = Utf8.lit "\">\n    <figure class=\"avatar boost__avatar flex-item-no-shrink\">\n      <a title=\""
let private t3 = Utf8.lit "\" class=\"btn avatar\" data-turbo-frame=\"_top\" href=\""
let private t4 = Utf8.lit "\"><img aria-label=\""
let private t5 = Utf8.lit " boosted "
let private t6 = Utf8.lit "\" src=\""
let private t7 = Utf8.lit "\" width=\"48\" height=\"48\" /></a>\n    </figure>\n\n    <span role=\"button\" class=\""
let private t8 = Utf8.lit "txt-small txt-medium"
let private t9 = Utf8.lit "txt-small"
let private t10 = Utf8.lit "\" data-action=\"click-&gt;boost-delete#reveal keydown.enter-&gt;boost-delete#reveal:prevent\" data-boost-delete-target=\"content\">"
let private t11 = Utf8.lit "</span>\n\n    <form class=\"button_to\" method=\"post\" action=\""
let private t12 = Utf8.lit "\"><input type=\"hidden\" name=\"_method\" value=\"delete\" /><button data-action=\"boost-delete#perform\" data-boost-delete-target=\"button\" class=\"btn btn--negative flex-item-justify-end boost__delete\" type=\"submit\">\n      <img aria-hidden=\"true\" src=\""
let private t13 = Utf8.lit "\" width=\"20\" height=\"20\" />\n      <span class=\"for-screen-reader\">Delete this boost</span>\n</button></form>  </div>\n  <span id=\"delete_boost_accessible_label\" class=\"for-screen-reader\">Press enter to delete this boost</span>"

let render (w: Out) (ctx: ViewContext) (boost: BoostView) : unit =
    w.Lit t0
    w.Text boost.DomId
    w.Lit t1
    w.Int boost.Booster.Id
    w.Lit t2
    w.Text boost.Booster.Title
    w.Lit t3
    w.Text boost.Booster.Path
    w.Lit t4
    w.Text boost.Booster.Name
    w.Lit t5
    w.Text boost.Content
    w.Lit t6
    w.Text boost.Booster.AvatarUrl
    w.Lit t7
    if boost.AllEmoji then
        w.Lit t8
    else
        w.Lit t9
    w.Lit t10
    w.Text boost.Content
    w.Lit t11
    w.Text boost.Path
    w.Lit t12
    w.Text(ctx.Asset "minus.svg")
    w.Lit t13
