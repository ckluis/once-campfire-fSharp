// Port of rust/crates/views/templates/messages/boosts/_boosts.html (reference/app/views/messages/boosts/_boosts.html.erb)
/// `messages/boosts/_boosts.html.erb`: a message's boosts and the "Add a boost" link.
module Campfire.Views.Templates.Messages.Boosts._Boosts

open Campfire.Views
open Campfire.Views.Messages

let private boostingId = Utf8.lit "boosting_message_"
let private boostsId = Utf8.lit "boosts_message_"
let private newBoostId = Utf8.lit "new_boost_message_"

let private t0 = Utf8.lit "<turbo-frame id=\""
let private t1 = Utf8.lit "\">\n  <div class=\"boosts flex flex-wrap align-center gap full-width\" style=\"--column-gap: 0.4ch; --row-gap: 0\"\n      data-controller=\"turbo-streaming\" data-action=\"turbo:submit-start->turbo-streaming#unsubscribe\">\n    <div class=\"flex-inline flex-wrap gap\" id=\""
let private t2 = Utf8.lit "\" data-turbo-streaming-target=\"container\">"
let private t3 = Utf8.lit "\n      "
let private t4 = Utf8.lit "\n    </div>\n\n    <turbo-frame id=\""
let private t5 = Utf8.lit "\">\n      <div class=\"flex-inline message__boost-inline\" data-controller=\"soft-keyboard\">\n        <a class=\"boost__action txt-small btn\" action=\"soft-keyboard#open\" href=\""
let private t6 = Utf8.lit "\">\n          <img aria-hidden=\"true\" src=\""
let private t7 = Utf8.lit "\" width=\"20\" height=\"20\" />\n          <span class=\"for-screen-reader\">Add a boost</span>\n</a>      </div>\n</turbo-frame>  </div>\n</turbo-frame>"

let render (w: Out) (ctx: ViewContext) (message: MessageView) : unit =
    w.Lit t0
    w.Lit boostingId
    w.Text message.ClientMessageId
    w.Lit t1
    w.Lit boostsId
    w.Text message.ClientMessageId
    w.Lit t2
    for boost in message.Boosts do
        w.Lit t3
        BoostsCached.cachedBoost w ctx boost
    w.Lit t4
    w.Lit newBoostId
    w.Text message.ClientMessageId
    w.Lit t5
    w.Text message.NewBoostPath
    w.Lit t6
    w.Text(ctx.Asset "boost.svg")
    w.Lit t7
