// Port of rust/crates/views/templates/messages/_message.html (reference/app/views/messages/_message.html.erb)
/// `messages/_message.html.erb`, with `MessagesHelper#message_tag`: one message, or `_unrenderable` in its place when its
/// content raised. Its body is `cache [ message, "presentation-v3" ]` (`MessagesCached.message`).
module Campfire.Views.Templates.Messages._Message

open Campfire.Views
open Campfire.Views.Messages

let private messageId = Utf8.lit "message_"
let private editId = Utf8.lit "edit_message_"

let private t0 = Utf8.lit "\n  "
let private t1 = Utf8.lit "\n  <div id=\""
let private t2 = Utf8.lit "\" class=\"message "
let private t3 = Utf8.lit "message--emoji"
let private t4 = Utf8.lit "\" data-controller=\"reply\" data-user-id=\""
let private t5 = Utf8.lit "\" data-message-id=\""
let private t6 = Utf8.lit "\" data-message-timestamp=\""
let private t7 = Utf8.lit "\" data-message-updated-at=\""
let private t8 = Utf8.lit "\" data-sort-value=\""
let private t9 = Utf8.lit "\" data-messages-target=\"message\" data-search-results-target=\"message\" data-refresh-room-target=\"message\" data-reply-composer-outlet=\"#composer\">\n    <h2 class=\"message__day-separator\"><time datetime=\""
let private t10 = Utf8.lit "\" data-local-time-target=\"date\"></time></h2>\n\n    <figure class=\"avatar message__avatar\">\n      <a title=\""
let private t11 = Utf8.lit "\" class=\"btn avatar\" data-turbo-frame=\"_top\" href=\""
let private t12 = Utf8.lit "\"><img aria-hidden=\"true\" src=\""
let private t13 = Utf8.lit "\" width=\"48\" height=\"48\" /></a>\n    </figure>\n\n    <turbo-frame id=\""
let private t14 = Utf8.lit "\">\n      <div class=\"message__body\">\n        <div class=\"message__body-content\">\n          <div class=\"message__meta\">\n            <h3 class=\"message__heading\">\n              <span class=\"message__author\" title=\""
let private t15 = Utf8.lit "\">\n                <strong data-reply-target=\"author\">"
let private t16 = Utf8.lit "</strong>\n              </span>\n              <a target=\"_top\" class=\"message__permalink\" href=\""
let private t17 = Utf8.lit "\"><time class=\"message__timestamp\" datetime=\""
let private t18 = Utf8.lit "\" data-local-time-target=\"time\"></time></a>\n              <span class=\"message__room\">\n                <a target=\"_top\" data-reply-target=\"link\" href=\""
let private t19 = Utf8.lit "\">"
let private t20 = Utf8.lit "</a>\n              </span>\n            </h3>\n            "
let private t21 = Utf8.lit "\n          </div>\n          "
let private t22 = Utf8.lit "\n          "
let private t23 = Utf8.lit "\n        </div>\n      </div>\n    </turbo-frame>\n</div>"

let render (w: Out) (ctx: ViewContext) (message: MessageView) : unit =
    if message.IsUnrenderable then
        w.Lit t0
        _Unrenderable.render w
    else
        w.Lit t1
        w.Lit messageId
        w.Text message.ClientMessageId
        w.Lit t2
        if message.AllEmoji then
            w.Lit t3
        w.Lit t4
        w.Int message.Creator.Id
        w.Lit t5
        w.Int message.Id
        w.Lit t6
        w.Int message.CreatedAtEpoch
        w.Lit t7
        w.Int message.UpdatedAtEpoch
        w.Lit t8
        w.Int message.CreatedAtEpoch
        w.Lit t9
        w.Text message.CreatedAtIso
        w.Lit t10
        w.Text message.Creator.Title
        w.Lit t11
        w.Text message.Creator.Path
        w.Lit t12
        w.Text message.Creator.AvatarUrl
        w.Lit t13
        w.Lit editId
        w.Text message.ClientMessageId
        w.Lit t14
        w.Text message.Creator.Title
        w.Lit t15
        w.Text message.Creator.Name
        w.Lit t16
        w.Text message.AtPath
        w.Lit t17
        w.Text message.CreatedAtIso
        w.Lit t18
        w.Text message.AtPath
        w.Lit t19
        w.Text message.RoomName
        w.Lit t20
        _Actions.render w ctx message
        w.Lit t21
        _Presentation.render w ctx message
        w.Lit t22
        Boosts._Boosts.render w ctx message
        w.Lit t23
