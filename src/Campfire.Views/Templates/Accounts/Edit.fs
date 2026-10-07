// Port of rust/crates/views/templates/accounts/edit.html (reference/app/views/accounts/edit.html.erb)
/// `accounts/edit.html.erb`: the account settings page. A page module exposes its regions (`head`, `nav`, `content`,
/// `footer`) as functions of the writer, which `render` hands to the application layout.
module Campfire.Views.Templates.Accounts.Edit

open Campfire.Routes
open Campfire.Views
open Campfire.Views.Accounts
open Campfire.Views.Helpers

let private t0 = Utf8.lit "  <div class=\"flex-item-justify-start\">\n    "
let private t1 = Utf8.lit "\n  </div>\n\n"
let private t2 = Utf8.lit "    <div class=\"flex align-center gap flex-item-justify-end\">\n      "
let private t3 = Utf8.lit "\n        "
let private t4 = Utf8.lit "\n        <span class=\"for-screen-reader\">Set up chat bots</span>\n"
let private t5 = Utf8.lit "\n      "
let private t6 = Utf8.lit "\n        "
let private t7 = Utf8.lit "\n        <span class=\"for-screen-reader\">Custom styles</span>\n"
let private t8 = Utf8.lit "    </div>\n"
let private t9 = Utf8.lit "<section class=\"panel txt-align-center flex flex-column gap\" style=\"view-transition-name: account-settings\">\n"
let private t10 = Utf8.lit "    <div class=\"align-center center avatar__form gap\" data-controller=\"upload-preview\">\n"
let private t11 = Utf8.lit "      "
let private t12 = Utf8.lit "\n        <label class=\"btn input--file\">\n          "
let private t13 = Utf8.lit "\n          "
let private t14 = Utf8.lit "\n          <span class=\"for-screen-reader\">Upload logo</span>\n        </label>\n"
let private t15 = Utf8.lit "\n"
let private t16 = Utf8.lit "      "
let private t17 = Utf8.lit "\n        <label class=\"btn avatar input--file account-logo txt-xx-large\">\n          "
let private t18 = Utf8.lit "\n          "
let private t19 = Utf8.lit "\n          <span class=\"for-screen-reader\">Upload logo</span>\n        </label>\n"
let private t20 = Utf8.lit "\n"
let private t21 = Utf8.lit "        "
let private t22 = Utf8.lit "\n          "
let private t23 = Utf8.lit "\n          <span class=\"for-screen-reader\">Delete logo</span>\n"
let private t24 = Utf8.lit "    </div>\n\n"
let private t25 = Utf8.lit "    "
let private t26 = Utf8.lit "\n      <div class=\"flex align-center gap\">\n        "
let private t27 = Utf8.lit "\n\n        <label class=\"flex align-center gap flex-item-grow\">\n          "
let private t28 = Utf8.lit "\n        </label>\n\n        "
let private t29 = Utf8.lit "\n          "
let private t30 = Utf8.lit "\n          <span class=\"for-screen-reader\">Save changes</span>\n"
let private t31 = Utf8.lit "      </div>\n"
let private t32 = Utf8.lit "\n    <div class=\"margin-block-start pad-block pad-inline-double fill-shade border-radius\">\n"
let private t33 = Utf8.lit "      "
let private t34 = Utf8.lit "\n        <div class=\"flex-item-grow flex align-center gap txt-align-start\">\n          "
let private t35 = Utf8.lit " Must be admin to create new rooms\n        </div>\n"
let private t36 = Utf8.lit "          "
let private t37 = Utf8.lit "\n\n        <label class=\"switch\">\n          <input type=\"checkbox\"\n                class=\"switch__input\"\n                "
let private t38 = Utf8.lit "checked"
let private t39 = Utf8.lit "\n                data-action=\"change->form#submit\">\n          <span class=\"switch__btn round\"></span>\n          <span class=\"for-screen-reader\">\n            Must be admin to create new rooms\n          </span>\n        </label>\n\n"
let private t40 = Utf8.lit "    </div>\n"
let private t41 = Utf8.lit "    "
let private t42 = Utf8.lit "\n    <h1 class=\"flex-item-grow txt-x-large\">"
let private t43 = Utf8.lit "</h1>\n"
let private t44 = Utf8.lit "\n  <div class=\"margin-block pad-inline pad-block-start fill-shade border-radius\">\n    "
let private t45 = Utf8.lit "\n\n    <hr class=\"margin-block separator full-width\" style=\"--border-style: solid\">\n\n    <menu class=\"flex flex-column gap margin-none pad\">\n      <turbo-frame id=\"account_users\">\n        "
let private t46 = Utf8.lit "\n"
let private t47 = Utf8.lit "          <hr class=\"separator full-width\" style=\"--border-style: solid\">\n"
let private t48 = Utf8.lit "\n        "
let private t49 = Utf8.lit "        "
let private t50 = Utf8.lit "\n      </turbo-frame>\n    </menu>\n  </div>\n</section>\n"
let private t51 = Utf8.lit "  <div class=\"txt-align-center center margin-block-double txt-subtle\">Campfire&trade; version "
let private t52 = Utf8.lit "</div>\n"

/// `@page_title`.
let pageTitle: string option = Some "Account settings"

/// `content_for :head` (empty).
let head: Out -> unit = ignore

/// `content_for :nav`.
let nav (w: Out) (ctx: ViewContext) : unit =
    w.Lit t0
    Application.linkBackToLastRoomVisited w ctx
    w.Lit t1
    if ctx.CanAdminister then
        w.Lit t2
        Filters.linkTo w (Routes.accountBots ()) (Tag.attrs().Class("btn").Style("view-transition-name: chat-bots")) (fun w ->
            w.Lit t3
            Assets.imageTag w ctx "bot.svg" (Tag.attrs().AriaHidden().Size(20))
            w.Lit t4)
        w.Lit t5
        Filters.linkTo w (Routes.editAccountCustomStyles ()) (Tag.attrs().Class("btn").Style("view-transition-name: custom-styles")) (fun w ->
            w.Lit t6
            Assets.imageTag w ctx "art.svg" (Tag.attrs().Size(20).AriaHidden())
            w.Lit t7)
        w.Lit t8

/// The page itself.
let content (w: Out) (ctx: ViewContext) (edit: EditView) : unit =
    let uploadAttrs () =
        Tag.attrs().Class("input").Accept("image/*").Data("action", "upload-preview#previewImage change->form#submit")
    w.Lit t9
    if ctx.CanAdminister then
        w.Lit t10
        let form = (Forms.formWith edit.AccountAction).Model("account").Method("patch").Class("txt--medium").Data("controller", "form")
        w.Lit t11
        Filters.formWith w form (fun w ->
            w.Lit t12
            Assets.imageTag w ctx "camera.svg" (Tag.attrs().AriaHidden().Size(20))
            w.Lit t13
            form.FileField(w, "logo", uploadAttrs ())
            w.Lit t14)
        w.Lit t15
        let form = (Forms.formWith edit.AccountAction).Model("account").Method("patch").Data("controller", "form")
        w.Lit t16
        Filters.formWith w form (fun w ->
            w.Lit t17
            Assets.imageTag w ctx ctx.Account.LogoUrl (Tag.attrs().Role("presentation").Size(48).Data("upload_preview_target", "image"))
            w.Lit t18
            form.FileField(w, "logo", uploadAttrs ())
            w.Lit t19)
        w.Lit t20
        if ctx.Account.HasLogo then
            w.Lit t21
            Filters.buttonTo w ctx.Account.LogoUrl (Tag.attrs().Method("delete").Class("btn btn--negative txt-small avatar__delete-btn")) (fun w ->
                w.Lit t22
                Assets.imageTag w ctx "minus.svg" (Tag.attrs().AriaHidden().Size(20))
                w.Lit t23)
        w.Lit t24
        let form =
            (Forms.formWith edit.AccountAction).Model("account").Method("patch").Data("controller", "form").Class("flex flex-column gap")
        w.Lit t25
        Filters.formWith w form (fun w ->
            w.Lit t26
            Translations.translationButton w ctx "account_name"
            w.Lit t27
            form.TextField(
                w,
                "name",
                Some ctx.Account.Name,
                Tag.attrs().Class("input txt-large").Autocomplete("off").Placeholder("Name this account").Autofocus().Data("action", "keydown.enter->form#submit")
            )
            w.Lit t28
            Filters.button w (Tag.attrs().Class("btn btn--reversed center").Type("submit")) (fun w ->
                w.Lit t29
                Assets.imageTag w ctx "check.svg" (Tag.attrs().AriaHidden().Size(20))
                w.Lit t30)
            w.Lit t31)
        w.Lit t32
        let form =
            (Forms.formWith edit.AccountAction).Model("account").Method("put").Data("controller", "form").Class("flex align-center gap center")
        w.Lit t33
        Filters.formWith w form (fun w ->
            w.Lit t34
            Assets.imageTag w ctx "crown.svg" (Tag.attrs().Class("colorize--black").AriaHidden().Size(18))
            w.Lit t35
            let settingsForm = form.FieldsFor "settings"
            w.Lit t36
            settingsForm.HiddenField(
                w,
                "restrict_room_creation_to_administrators",
                None,
                Tag.attrs().Value(if edit.RestrictRoomCreationToAdministrators then "false" else "true")
            )
            w.Lit t37
            if edit.RestrictRoomCreationToAdministrators then
                w.Lit t38
            w.Lit t39)
        w.Lit t40
    else
        w.Lit t41
        UsersHelper.accountLogoTag w ctx (Some "txt-xx-large center")
        w.Lit t42
        w.Text ctx.Account.Name
        w.Lit t43
    w.Lit t44
    _Invite.render w ctx edit.JoinCode
    w.Lit t45
    for user in edit.Administrators do
        Users._User.render w ctx user
    w.Lit t46
    if not edit.Administrators.IsEmpty && not edit.Members.IsEmpty then
        w.Lit t47
    w.Lit t48
    for user in edit.Members do
        Users._User.render w ctx user
    w.Lit t49
    match edit.NextPage with
    | Some page -> Users._NextPageContainer.render w page
    | None -> ()
    w.Lit t50

/// `content_for :footer`.
let footer (w: Out) (ctx: ViewContext) : unit =
    w.Lit t51
    Application.versionBadge w ctx
    w.Lit t52

let render (w: Out) (ctx: ViewContext) (edit: EditView) : unit =
    Templates.Layouts.Application.render w ctx pageTitle None head (fun w -> nav w ctx) (fun w -> content w ctx edit) (fun w -> footer w ctx) ignore
