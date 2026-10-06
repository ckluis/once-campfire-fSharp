// Port of rust/crates/views/templates/rooms/show/_composer.html (reference/app/views/rooms/show/_composer.html.erb)
/// `rooms/show/_composer.html.erb` (`content_for :footer`), with `RoomsHelper#composer_form_tag`.
module Campfire.Views.Templates.Rooms.Show._Composer

open Campfire.Routes
open Campfire.Views
open Campfire.Views.Rooms

let private t0 = Utf8.lit "<div class=\"composer flex align-end gap position-relative\"\n      data-controller=\"typing-notifications\" data-typing-notifications-active-class=\"typing-indicator--active\">\n    <a class=\"btn flex-item-no-shrink margin-block-end composer__context-btn\" style=\"view-transition-name: input-switcher\" href=\""
let private t1 = Utf8.lit "\">\n      <img aria-hidden=\"true\" src=\""
let private t2 = Utf8.lit "\" width=\"20\" height=\"20\" />\n      <span class=\"for-screen-reader\">Search</span>\n</a>\n    <turbo-frame id=\"composer-frame\">\n      <form id=\"composer\" class=\"margin-block flex-item-grow contain\" data-controller=\"composer drop-target\" data-action=\"dragenter-&gt;drop-target#dragenter dragover-&gt;drop-target#dragover drop-&gt;drop-target#drop drop-target:drop@window-&gt;composer#dropFiles lexxy:file-accept-&gt;composer#preventAttachment refresh-room:online@window-&gt;composer#online typing-notifications#stop paste-&gt;composer#pasteFiles turbo:submit-end-&gt;composer#submitEnd refresh-room:offline@window-&gt;composer#offline\" data-composer-messages-outlet=\"#message-area\" data-composer-toolbar-class=\"composer--rich-text\" data-composer-room-id-value=\""
let private t3 = Utf8.lit "\" action=\""
let private t4 = Utf8.lit "\" accept-charset=\"UTF-8\" method=\"post\">\n        <fieldset data-composer-target=\"fields\" contents>\n          <div class=\"flex flex-column\">\n            <div class=\"composer__filelist flex flex--align-center gap flex-wrap\" data-composer-target=\"fileList\"></div>\n\n            <div class=\"flex composer__input input input--actor fill-white min-width\" style=\"--input-border-radius: 1.3rem\">\n              <div class=\"flex align-end gap full-width\">\n                <img aria-hidden=\"true\" class=\"composer__input-hint colorize--black\" style=\"view-transition-name: input-btn;\" src=\""
let private t5 = Utf8.lit "\" width=\"22\" height=\"22\" />\n\n                <div class=\"flex flex-column flex-item-grow min-width gap\">\n                  <lexxy-editor rows=\"1\" class=\"input lexxy-content\" style=\"order: -1\" aria-multiline=\"true\" aria-label=\"Write a message\" permitted-attachment-types=\"application/vnd.campfire.mention application/vnd.actiontext.opengraph-embed\" data-controller=\"unfurl\" data-action=\"lexxy:change-&gt;typing-notifications#start keydown-&gt;composer#submitByKeyboard:capture lexxy:change-&gt;composer#saveDraft lexxy:insert-link-&gt;unfurl#unfurl\" data-composer-target=\"text\" data-direct-upload-url=\""
let private t6 = Utf8.lit "\" data-blob-url-template=\""
let private t7 = Utf8.lit "\" id=\"message_body\" input=\"message_body_trix_input_message\" name=\"message[body]\">\n                    <lexxy-prompt trigger=\"@\" name=\"mention\" src=\""
let private t8 = Utf8.lit "\" remote-filtering=\"true\" empty-results=\"No matches\"></lexxy-prompt>\n</lexxy-editor>                </div>\n\n                <label class=\"btn btn--borderless txt-small flex-item-no-shrink composer__attachment-btn input--file\">\n                  <img class=\"colorize--black\" aria-hidden=\"true\" src=\""
let private t9 = Utf8.lit "\" width=\"22\" height=\"22\" />\n                  <input type=\"file\" data-action=\"composer#filePicked\" multiple />\n                  <span class=\"for-screen-reader\">Attach a file</span>\n                </label>\n\n                <button class=\"btn btn--borderless txt-small flex-item-no-shrink composer__rich-text-btn\" type=\"button\" data-action=\"composer#toggleToolbar\">\n                  <img class=\"colorize--black\" aria-hidden=\"true\" src=\""
let private t10 = Utf8.lit "\" width=\"20\" height=\"20\" />\n                  <span class=\"for-screen-reader\">Rich text</span>\n                </button>\n\n                <button name=\"send\" type=\"submit\" data-action=\"composer#submit\" class=\"btn btn--reversed flex-item-no-shrink txt-small\">\n                  <img aria-hidden=\"true\" src=\""
let private t11 = Utf8.lit "\" width=\"20\" height=\"20\" />\n                  <span class=\"for-screen-reader\">Send Message</span>\n</button>              </div>\n            </div>\n          </div>\n        </fieldset>\n\n        <div class=\"typing-indicator gap txt-small align-center flex-inline\" data-typing-notifications-target=\"indicator\">\n          <div class=\"typing-indicator__author spinner\" data-typing-notifications-target=\"author\"></div>\n        </div>\n\n        <input data-composer-target=\"clientid\" type=\"hidden\" name=\"message[client_message_id]\" id=\"message_client_message_id\" />\n</form>    </turbo-frame>\n  </div>"

let render (w: Out) (ctx: ViewContext) (room: RoomView) : unit =
    w.Lit t0
    w.Text(Routes.searches ())
    w.Lit t1
    w.Text(ctx.Asset "search.svg")
    w.Lit t2
    w.Int room.Id
    w.Lit t3
    w.Text(Routes.roomMessages room.Id)
    w.Lit t4
    w.Text(ctx.Asset "messages-outlined.svg")
    w.Lit t5
    w.Text(ctx.Url "/rails/active_storage/direct_uploads")
    w.Lit t6
    w.Text(ctx.Url "/rails/active_storage/blobs/redirect/:signed_id/:filename")
    w.Lit t7
    w.Text(mentionPromptSrc room.Id)
    w.Lit t8
    w.Text(ctx.Asset "attachment.svg")
    w.Lit t9
    w.Text(ctx.Asset "text-options.svg")
    w.Lit t10
    w.Text(ctx.Asset "arrow-up.svg")
    w.Lit t11
