// Port of rust/crates/views/templates/messages/boosts/new.html (reference/app/views/messages/boosts/new.html.erb)
/// `messages/boosts/new.html.erb`: the form that adds a boost; `user` is `Current.user`.
module Campfire.Views.Templates.Messages.Boosts.New

open Campfire.Views
open Campfire.Views.Messages

let private newBoostId = Utf8.lit "new_boost_message_"
let private boostingId = Utf8.lit "boosting_message_"
let private boostsId = Utf8.lit "boosts_message_"

let private t0 = Utf8.lit "<turbo-frame id=\""
let private t1 = Utf8.lit "\">\n  <div class=\"boost flex-inline postion--relative max-width fill-white\" style=\"--column-gap: var(--inline-space-half)\">\n    <form class=\"boost__form flex align-center gap expanded\" data-controller=\"form scroll-into-view\" data-turbo-frame=\""
let private t2 = Utf8.lit "\" data-action=\"keydown.esc-&gt;form#cancel\" action=\""
let private t3 = Utf8.lit "\" accept-charset=\"UTF-8\" method=\"post\">\n      <label class=\"boost__form-label flex gap\" style=\"--column-gap: 0.7ch;\" role=\"button\" tabindex=\"0\" aria-label=\"Add a boost\">\n        <figure class=\"avatar boost__avatar flex-item-no-shrink\">\n          <a title=\""
let private t4 = Utf8.lit "\" class=\"btn avatar\" data-turbo-frame=\"_top\" href=\""
let private t5 = Utf8.lit "\"><img aria-hidden=\"true\" src=\""
let private t6 = Utf8.lit "\" width=\"48\" height=\"48\" /></a>\n          <span class=\"for-screen-reader\">"
let private t7 = Utf8.lit "</span>\n        </figure>\n\n        <input autofocus=\"autofocus\" autocomplete=\"off\" autocorrect=\"off\" maxlength=\"16\" required=\"required\" pattern=\"\\S+.*\" data-boost-form-target=\"input\" class=\"input input--boost txt-small\" size=\"16\" type=\"text\" name=\"boost[content]\" />\n      </label>\n\n      <button name=\"button\" type=\"submit\" class=\"btn btn--reversed\">\n        <img aria-hidden=\"true\" src=\""
let private t8 = Utf8.lit "\" />\n        <span class=\"for-screen-reader\">Submit</span>\n</button>\n      <a data-turbo-frame=\""
let private t9 = Utf8.lit "\" data-form-target=\"cancel\" class=\"btn btn--negative\" href=\""
let private t10 = Utf8.lit "\">\n        <img aria-hidden=\"true\" src=\""
let private t11 = Utf8.lit "\" />\n        <span class=\"for-screen-reader\">Cancel</span>\n</a></form>  </div>\n</turbo-frame>"

let render (w: Out) (ctx: ViewContext) (message: MessageView) (user: UserView) : unit =
    w.Lit t0
    w.Lit newBoostId
    w.Text message.ClientMessageId
    w.Lit t1
    w.Lit boostingId
    w.Text message.ClientMessageId
    w.Lit t2
    w.Text message.BoostsPath
    w.Lit t3
    w.Text user.Title
    w.Lit t4
    w.Text user.Path
    w.Lit t5
    w.Text user.AvatarUrl
    w.Lit t6
    w.Text user.Name
    w.Lit t7
    w.Text(ctx.Asset "check.svg")
    w.Lit t8
    w.Lit boostsId
    w.Text message.ClientMessageId
    w.Lit t9
    w.Text message.BoostsPath
    w.Lit t10
    w.Text(ctx.Asset "minus.svg")
    w.Lit t11
