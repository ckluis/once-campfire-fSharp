// Port of rust/crates/views/templates/accounts/bots/_form.html (reference/app/views/accounts/bots/_form.html.erb)
/// `accounts/bots/_form.html.erb`: the fields of the new and edit bot forms, inside the page's `form_with` block (`form`).
module Campfire.Views.Templates.Accounts.Bots._Form

open Campfire.Views
open Campfire.Views.Accounts
open Campfire.Views.Helpers

let private t0 = Utf8.lit "<h1 class=\"for-screen-reader\">Chat Bot Setup</h1>\n<label class=\"align-center center avatar__form gap\" data-controller=\"upload-preview\">\n  <div class=\"btn input--file\">\n    "
let private t1 = Utf8.lit "\n    "
let private t2 = Utf8.lit "\n    <span class=\"for-screen-reader\">Upload bot avatar</span>\n  </div>\n\n  <div class=\"avatar input--file txt-xx-large\" style=\"--avatar-size: var(--btn-size);\">\n    "
let private t3 = Utf8.lit "\n  </div>\n</label>\n\n<div class=\"flex align-center gap\">\n  "
let private t4 = Utf8.lit "\n  <label class=\"flex align-center gap flex-item-grow txt-large input input--actor\">\n    "
let private t5 = Utf8.lit "\n    "
let private t6 = Utf8.lit "\n  </label>\n</div>\n\n<div class=\"flex align-center gap\">\n  "
let private t7 = Utf8.lit "\n  <label class=\"flex align-center gap flex-item-grow txt-large input input--actor\">\n    "
let private t8 = Utf8.lit "\n    "
let private t9 = Utf8.lit "\n  </label>\n</div>\n\n"
let private t10 = Utf8.lit "\n"

let render (w: Out) (ctx: ViewContext) (form: Forms.FormWith) (bot: BotForm) : unit =
    w.Lit t0
    Assets.imageTag w ctx "camera.svg" (Tag.attrs().AriaHidden().Size(20))
    w.Lit t1
    form.FileField(
        w,
        "avatar",
        Tag.attrs().Class("input").Accept("image/*").Data("upload_preview_target", "input").Data("action", "upload-preview#previewImage")
    )
    w.Lit t2
    Assets.imageTag
        w
        ctx
        (defaultArg bot.AvatarAttachmentUrl "default-bot-avatar.svg")
        (Tag.attrs().Alt("Bot avatar").Size(48).Data("upload_preview_target", "image"))
    w.Lit t3
    Translations.translationButton w ctx "bot_name"
    w.Lit t4
    form.TextField(
        w,
        "name",
        bot.Name,
        Tag.attrs().Class("input").Autocomplete("name").Placeholder("Name the bot").Autofocus().Required(true).Data("1p-ignore", true)
    )
    w.Lit t5
    Assets.imageTag w ctx "bot.svg" (Tag.attrs().AriaHidden().Size(24).Class("colorize--black"))
    w.Lit t6
    Translations.translationButton w ctx "webhook_url"
    w.Lit t7
    form.UrlField(w, "webhook_url", bot.WebhookUrl, Tag.attrs().Class("input").Placeholder("Webhook URL"))
    w.Lit t8
    Assets.imageTag w ctx "web.svg" (Tag.attrs().AriaHidden().Size(24).Class("colorize--black"))
    w.Lit t9
    UsersHelper.profileFormSubmitButton w ctx
    w.Lit t10
