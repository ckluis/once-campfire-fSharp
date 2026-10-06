// Port of rust/crates/views/templates/messages/edit.html (reference/app/views/messages/edit.html.erb)
/// `messages/edit.html.erb`: the edit frame, with the rich text editor (or just delete, for an attachment).
module Campfire.Views.Templates.Messages.Edit

open Campfire.Views
open Campfire.Views.Messages
open Campfire.Views.Rooms

let private editId = Utf8.lit "edit_message_"
let private deleteFormId = Utf8.lit "delete_form_message_"
let private formId = Utf8.lit "form_message_"
let private messageId = Utf8.lit "message_"

let private t0 = Utf8.lit "<turbo-frame id=\""
let private t1 = Utf8.lit "\">\n  <div class=\"message__body position-relative\" data-controller=\"scroll-into-view\">\n    <div class=\"message__body-content message__body-content--editing gap\">"
let private t2 = Utf8.lit "\n        "
let private t3 = Utf8.lit "\n\n        <div class=\"message__edit-btns flex align-center justify-space-between gap full-width pad-block-start-half\">\n          <button name=\"button\" type=\"submit\" class=\"btn btn--negative center margin-block-end\" form=\""
let private t4 = Utf8.lit "\" data-turbo-confirm=\"Are you sure you want to delete this message?\">\n            <img aria-hidden=\"true\" src=\""
let private t5 = Utf8.lit "\" />\n            <span class=\"for-screen-reader\">Delete message</span>\n</button>        </div>"
let private t6 = Utf8.lit "\n        <div class=\"composer--edit composer--rich-text\">\n          <form id=\""
let private t7 = Utf8.lit "\" data-controller=\"form\" data-action=\"lexxy:file-accept-&gt;form#preventAttachment keydown.esc-&gt;form#cancel keydown.ctrl+enter-&gt;form#submit:prevent keydown.meta+enter-&gt;form#submit:prevent\" action=\""
let private t8 = Utf8.lit "\" accept-charset=\"UTF-8\" method=\"post\"><input type=\"hidden\" name=\"_method\" value=\"patch\" />\n            <div class=\"full-width input input--actor min-width fill-white\">\n              <lexxy-editor rows=\"1\" class=\"input lexxy-content\" aria-multiline=\"true\" aria-label=\"Edit message\" autofocus=\"autofocus\" permitted-attachment-types=\"application/vnd.campfire.mention application/vnd.actiontext.opengraph-embed\" data-action=\"lexxy:change-&gt;typing-notifications#start keydown-&gt;composer#submitByKeyboard:capture\" data-direct-upload-url=\""
let private t9 = Utf8.lit "\" data-blob-url-template=\""
let private t10 = Utf8.lit "\" id=\"message_body\" input=\"message_body_trix_input_"
let private t11 = Utf8.lit "\" name=\"message[body]\" value=\""
let private t12 = Utf8.lit "\">\n                <lexxy-prompt trigger=\"@\" name=\"mention\" src=\""
let private t13 = Utf8.lit "\" remote-filtering=\"true\" empty-results=\"No matches\"></lexxy-prompt>\n</lexxy-editor>            </div>\n\n            <a data-form-target=\"cancel\" hidden=\"hidden\" href=\""
let private t14 = Utf8.lit "\">Close editor and discard changes</a>\n\n            <div class=\"message__edit-btns flex align-center justify-space-between gap full-width pad-block-start-half\">\n              <button name=\"button\" type=\"submit\" class=\"btn btn--reversed\">\n                <img aria-hidden=\"true\" src=\""
let private t15 = Utf8.lit "\" />\n                <span class=\"for-screen-reader\">Save changes</span>\n</button>\n              <button name=\"button\" type=\"submit\" class=\"btn btn--negative\" form=\""
let private t16 = Utf8.lit "\" data-turbo-confirm=\"Are you sure you want to delete this message?\">\n                <img aria-hidden=\"true\" src=\""
let private t17 = Utf8.lit "\" />\n                <span class=\"for-screen-reader\">Delete message</span>\n</button>            </div>\n</form>        </div>"
let private t18 = Utf8.lit "\n    </div>\n\n    <div class=\"message__actions flex flex-wrap\">\n      <a class=\"message__action-btn message__edit-close-btn txt-small btn btn--borderless\" href=\""
let private t19 = Utf8.lit "\">\n        <img class=\"colorize--black\" aria-hidden=\"true\" src=\""
let private t20 = Utf8.lit "\" />\n        <span class=\"for-screen-reader\">Close editor and discard changes</span>\n</a>    </div>\n\n    <form id=\""
let private t21 = Utf8.lit "\" data-turbo-frame=\""
let private t22 = Utf8.lit "\" action=\""
let private t23 = Utf8.lit "\" accept-charset=\"UTF-8\" method=\"post\"><input type=\"hidden\" name=\"_method\" value=\"delete\" />"
let private t24 = Utf8.lit "\n  </div>\n</turbo-frame>"

let render (w: Out) (ctx: ViewContext) (edit: EditView) : unit =
    let message = edit.Message
    w.Lit t0
    w.Lit editId
    w.Text message.ClientMessageId
    w.Lit t1
    match message.Attachment with
    | Some attachment ->
        w.Lit t2
        MessagesPresentation.attachmentPresentation w ctx attachment
        w.Lit t3
        w.Lit deleteFormId
        w.Text message.ClientMessageId
        w.Lit t4
        w.Text(ctx.Asset "trash.svg")
        w.Lit t5
    | None ->
        w.Lit t6
        w.Lit formId
        w.Text message.ClientMessageId
        w.Lit t7
        w.Text message.Path
        w.Lit t8
        w.Text(ctx.Url "/rails/active_storage/direct_uploads")
        w.Lit t9
        w.Text(ctx.Url "/rails/active_storage/blobs/redirect/:signed_id/:filename")
        w.Lit t10
        w.Lit messageId
        w.Text message.ClientMessageId
        w.Lit t11
        w.Text edit.EditableBodyHtml
        w.Lit t12
        w.Text(mentionPromptSrc message.RoomId)
        w.Lit t13
        w.Text message.Path
        w.Lit t14
        w.Text(ctx.Asset "check.svg")
        w.Lit t15
        w.Lit deleteFormId
        w.Text message.ClientMessageId
        w.Lit t16
        w.Text(ctx.Asset "trash.svg")
        w.Lit t17
    w.Lit t18
    w.Text message.Path
    w.Lit t19
    w.Text(ctx.Asset "remove.svg")
    w.Lit t20
    w.Lit deleteFormId
    w.Text message.ClientMessageId
    w.Lit t21
    w.Lit editId
    w.Text message.ClientMessageId
    w.Lit t22
    w.Text message.Path
    w.Lit t23
    w.Lit t24
