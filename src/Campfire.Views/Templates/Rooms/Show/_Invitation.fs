// Port of rust/crates/views/templates/rooms/show/_invitation.html (reference/app/views/rooms/show/_invitation.html.erb)
/// `rooms/show/_invitation.html.erb`: the welcome message with the join link, shown when the caller decided it applies.
module Campfire.Views.Templates.Rooms.Show._Invitation

open Campfire.Views
open Campfire.Views.Helpers

let private t0 = Utf8.lit "<div id=\"system_welcome\" class=\"message message--formatted txt-align-center center\">\n    <div class=\"message__body center\">\n      <div class=\"message__body-content position-relative\">\n        "
let private t1 = Utf8.lit "\n        <div class=\"flex align-center gap\">\n          <div class=\"system-welcome--translation\">\n            "
let private t2 = Utf8.lit "\n          </div>\n          <p>\n            <strong>Welcome to Campfire</strong><br>\n            To invite people to chat, share the join link below.\n          </p>\n        </div>\n        "
let private t3 = Utf8.lit "\n      </div>\n    </div>\n  </div>"

let render (w: Out) (ctx: ViewContext) (joinCode: string) : unit =
    w.Lit t0
    UsersHelper.accountLogoTag w ctx (Some "center margin-block-end txt-large")
    w.Lit t1
    Translations.translationButton w ctx "invite_message"
    w.Lit t2
    Templates.Accounts._Invite.render w ctx joinCode
    w.Lit t3
