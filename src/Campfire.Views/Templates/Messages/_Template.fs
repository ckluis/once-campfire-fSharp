// Port of rust/crates/views/templates/messages/_template.html (reference/app/views/messages/_template.html.erb)
/// `messages/_template.html.erb`: the client-side message template the composer clones for a message being sent.
module Campfire.Views.Templates.Messages._Template

open Campfire.Views
open Campfire.Views.Messages

let private t0 = Utf8.lit "<script type=\"text/template\" data-messages-target=\"template\">\n  <div class=\"message message--me $messageClasses$\"\n      id=\"message_$clientMessageId$\"\n      data-format-message-target=\"message\"\n      data-user-id=\""
let private t1 = Utf8.lit "\"\n      data-message-timestamp=\"$messageTimestamp$\"\n      data-messages-target=\"message\">\n    <div class=\"message__day-separator\"><time class=\"message__timestamp\" datetime=\"$messageDatetime$\" data-local-time-target=\"date\"></time></div>\n\n    <figure class=\"avatar message__avatar\">\n      <a title=\""
let private t2 = Utf8.lit "\" class=\"btn avatar\" data-turbo-frame=\"_top\" href=\""
let private t3 = Utf8.lit "\"><img aria-hidden=\"true\" src=\""
let private t4 = Utf8.lit "\" width=\"48\" height=\"48\" /></a>\n    </figure>\n\n    <div class=\"message__body\">\n      <div class=\"message__body-content\">\n        <div class=\"message__meta\">\n          <h3 class=\"message__heading\">\n            <span class=\"message__author\"><strong>"
let private t5 = Utf8.lit "</strong></span>\n            <span class=\"message__permalink\"><time class=\"message__timestamp\" datetime=\"$messageDatetime$\" data-local-time-target=\"time\"></time></span>\n          </h3>\n          <div class=\"message__actions\">\n            <div class=\"position-relative\">\n              <span class=\"btn message__action-btn message__options-btn\">\n                <img class=\"colorize--black\" aria-hidden=\"true\" src=\""
let private t6 = Utf8.lit "\" />\n                <span class=\"for-screen-reader\">Message options</span>\n              </span>\n            </div class=\"position-relative\">\n          </div>\n        </div>\n        $body$\n      </div>\n    </div>\n  </div>\n</script>"

/// `user` is `Current.user`.
let render (w: Out) (ctx: ViewContext) (user: UserView) : unit =
    w.Lit t0
    w.Int user.Id
    w.Lit t1
    w.Text user.Title
    w.Lit t2
    w.Text user.Path
    w.Lit t3
    w.Text user.AvatarUrl
    w.Lit t4
    w.Text user.Name
    w.Lit t5
    w.Text(ctx.Asset "menu-dots-horizontal.svg")
    w.Lit t6
