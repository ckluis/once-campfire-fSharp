// Port of rust/crates/views/templates/messages/_actions.html (reference/app/views/messages/_actions.html.erb)
/// `messages/_actions.html.erb`: the options menu of a message (boosts, reply or download/share, copy link, edit).
module Campfire.Views.Templates.Messages._Actions

open Campfire.Views
open Campfire.Views.Messages

let private boostingId = Utf8.lit "boosting_message_"
let private newBoostId = Utf8.lit "new_boost_message_"
let private editId = Utf8.lit "edit_message_"

let private t0 = Utf8.lit "<div class=\"message__actions\" data-controller=\"soft-keyboard\">\n  <details class=\"position-relative\" data-controller=\"popup\" data-action=\"keydown.esc-&gt;popup#close toggle-&gt;popup#toggle click@document-&gt;popup#closeOnClickOutside\" data-popup-orientation-top-class=\"popup-orientation-top\">\n    <summary class=\"btn message__action-btn message__options-btn\">\n      <img class=\"colorize--black\" aria-hidden=\"true\" src=\""
let private t1 = Utf8.lit "\" width=\"20\" height=\"20\" />\n      <span class=\"for-screen-reader\">Message options</span>\n    </summary>\n\n    <div class=\"message__actions-menu border shadow\" data-popup-target=\"menu\">\n      <div class=\"quick-boosts\">"
let private t2 = Utf8.lit "\n          <form data-turbo-frame=\""
let private t3 = Utf8.lit "\" data-action=\"popup#close\" action=\""
let private t4 = Utf8.lit "\" accept-charset=\"UTF-8\" method=\"post\">\n            <input type=\"hidden\" name=\"boost[content]\" id=\"boost_content\" value=\""
let private t5 = Utf8.lit "\" />\n            <button name=\"button\" type=\"submit\" title=\""
let private t6 = Utf8.lit "\" class=\"btn message__action-btn\" data-emoji=\""
let private t7 = Utf8.lit "\">\n              <figure class=\"margin-none boost-character\">"
let private t8 = Utf8.lit "</figure>\n              <span class=\"for-screen-reader\">"
let private t9 = Utf8.lit "</span>\n</button></form>"
let private t10 = Utf8.lit "\n\n        <a class=\"btn message__action-btn message__boost-btn\" data-turbo-frame=\""
let private t11 = Utf8.lit "\" data-action=\"soft-keyboard#open popup#close\" href=\""
let private t12 = Utf8.lit "\">\n          <img class=\"colorize--black\" aria-hidden=\"true\" src=\""
let private t13 = Utf8.lit "\" width=\"20\" height=\"20\" />\n          <span class=\"for-screen-reader\">New boost</span>\n</a>      </div>\n\n      <div class=\"flex flex-wrap border-top margin-block-start-half pad-block-start-half message__actions-grid\">"
let private t14 = Utf8.lit "\n          <a class=\"btn message__action-btn center full-width hide-in-ios-pwa\" title=\"Download\" aria-label=\"Download\" href=\""
let private t15 = Utf8.lit "\">\n            <img class=\"colorize--black\" aria-hidden=\"true\" src=\""
let private t16 = Utf8.lit "\" width=\"20\" height=\"20\" />\n</a>\n          <button class=\"btn message__action-btn center full-width\" data-controller=\"web-share\" data-action=\"web-share#share\" data-web-share-files-value=\""
let private t17 = Utf8.lit "\" data-web-share-title-value=\""
let private t18 = Utf8.lit "\" title=\"Share\" aria-label=\"Share\">\n            <img class=\"colorize--black\" aria-hidden=\"true\" src=\""
let private t19 = Utf8.lit "\" width=\"20\" height=\"20\" />\n</button>"
let private t20 = Utf8.lit "\n          <button class=\"btn message__action-btn center full-width\" data-action=\"reply#reply\" title=\"Reply\" aria-label=\"Reply\">\n            <img class=\"colorize--black\" aria-hidden=\"true\" src=\""
let private t21 = Utf8.lit "\" width=\"20\" height=\"20\" />\n</button>"
let private t22 = Utf8.lit "\n\n        <button class=\"btn message__action-btn center full-width\" title=\"Copy link\" aria-label=\"Copy link\" data-controller=\"copy-to-clipboard\" data-action=\"copy-to-clipboard#copy\" data-copy-to-clipboard-success-class=\"btn--success\" data-copy-to-clipboard-url-value=\""
let private t23 = Utf8.lit "\">\n          <img class=\"colorize--black\" aria-hidden=\"true\" src=\""
let private t24 = Utf8.lit "\" width=\"20\" height=\"20\" />\n</button>\n        <a class=\"btn message__action-btn center full-width message__edit-btn\" data-turbo-frame=\""
let private t25 = Utf8.lit "\" title=\"Edit\" aria-label=\"Edit\" href=\""
let private t26 = Utf8.lit "\">\n          <img class=\"colorize--black\" aria-hidden=\"true\" src=\""
let private t27 = Utf8.lit "\" width=\"20\" height=\"20\" />\n</a>      </div>\n    </div>\n</details></div>"

let render (w: Out) (ctx: ViewContext) (message: MessageView) : unit =
    w.Lit t0
    w.Text(ctx.Asset "menu-dots-horizontal.svg")
    w.Lit t1
    for (character, title) in reactions do
        w.Lit t2
        w.Lit boostingId
        w.Text message.ClientMessageId
        w.Lit t3
        w.Text message.BoostsPath
        w.Lit t4
        w.Text character
        w.Lit t5
        w.Text title
        w.Lit t6
        w.Text character
        w.Lit t7
        w.Text character
        w.Lit t8
        w.Text title
        w.Lit t9
    w.Lit t10
    w.Lit newBoostId
    w.Text message.ClientMessageId
    w.Lit t11
    w.Text message.NewBoostPath
    w.Lit t12
    w.Text(ctx.Asset "boost.svg")
    w.Lit t13
    match message.Attachment with
    | Some attachment ->
        w.Lit t14
        w.Text attachment.DownloadPath
        w.Lit t15
        w.Text(ctx.Asset "download.svg")
        w.Lit t16
        w.Text attachment.BlobPath
        w.Lit t17
        w.Text attachment.Filename
        w.Lit t18
        w.Text(ctx.Asset "share.svg")
        w.Lit t19
    | None ->
        w.Lit t20
        w.Text(ctx.Asset "reply.svg")
        w.Lit t21
    w.Lit t22
    w.Text message.AtPath
    w.Lit t23
    w.Text(ctx.Asset "link.svg")
    w.Lit t24
    w.Lit editId
    w.Text message.ClientMessageId
    w.Lit t25
    w.Text message.EditPath
    w.Lit t26
    w.Text(ctx.Asset "pencil.svg")
    w.Lit t27
