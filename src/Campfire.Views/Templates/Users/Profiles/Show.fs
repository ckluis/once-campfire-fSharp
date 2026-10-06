// Port of rust/crates/views/templates/users/profiles/show.html (reference/app/views/users/profiles/show.html.erb)
/// `users/profiles/show.html.erb`: the signed-in user's own profile.
module Campfire.Views.Templates.Users.Profiles.Show

open Campfire.Routes
open Campfire.Views
open Campfire.Views.Helpers
open Campfire.Views.Users

let private t0 = Utf8.lit "  <div class=\"flex-item-justify-start\">\n    "
let private t1 = Utf8.lit "\n  </div>\n\n  <div class=\"flex-item-justify-end\">\n"
let private t2 = Utf8.lit "    "
let private t3 = Utf8.lit "\n      "
let private t4 = Utf8.lit "\n\n      <button class=\"btn\" data-action=\"sessions#logout:prevent\">\n        "
let private t5 = Utf8.lit "\n        <span class=\"for-screen-reader\">Log out</span>\n      </button>\n"
let private t6 = Utf8.lit "  </div>\n"
let private t7 = Utf8.lit "<section class=\"panel flex flex-column gap\" style=\"view-transition-name: avatar-"
let private t8 = Utf8.lit "\">\n  "
let private t9 = Utf8.lit "\n\n  <div class=\"align-center center avatar__form gap\" data-controller=\"upload-preview\">\n"
let private t10 = Utf8.lit "    "
let private t11 = Utf8.lit "\n      <label class=\"btn input--file\">\n        "
let private t12 = Utf8.lit "\n        "
let private t13 = Utf8.lit "\n        <span class=\"for-screen-reader\">Upload avatar</span>\n      </label>\n"
let private t14 = Utf8.lit "\n"
let private t15 = Utf8.lit "    "
let private t16 = Utf8.lit "\n      <label class=\"btn avatar input--file txt-xx-large\">\n        "
let private t17 = Utf8.lit "\n        "
let private t18 = Utf8.lit "\n        <span class=\"for-screen-reader\">Avatar</span>\n      </label>\n"
let private t19 = Utf8.lit "\n"
let private t20 = Utf8.lit "      "
let private t21 = Utf8.lit "\n        "
let private t22 = Utf8.lit "\n        <span class=\"for-screen-reader\">Delete avatar</span>\n"
let private t23 = Utf8.lit "  </div>\n\n"
let private t24 = Utf8.lit "  "
let private t25 = Utf8.lit "\n    <div class=\"flex flex-column gap\">\n      <div class=\"flex align-center gap\">\n        "
let private t26 = Utf8.lit "\n\n        <label class=\"flex align-center gap flex-item-grow input input--actor\">\n          "
let private t27 = Utf8.lit "\n          "
let private t28 = Utf8.lit "\n        </label>\n      </div>\n\n      <div class=\"flex align-center gap\">\n        "
let private t29 = Utf8.lit "\n\n        <label class=\"flex align-center gap flex-item-grow input input--actor\">\n          "
let private t30 = Utf8.lit "\n          "
let private t31 = Utf8.lit "\n        </label>\n      </div>\n\n      <div class=\"flex align-center gap\">\n        "
let private t32 = Utf8.lit "\n\n        <label class=\"flex align-center gap flex-item-grow input input--actor\">\n          "
let private t33 = Utf8.lit "\n          "
let private t34 = Utf8.lit "\n        </label>\n      </div>\n\n      <div class=\"flex align-start gap\">\n        "
let private t35 = Utf8.lit "\n\n        <label class=\"flex align--center gap flex-item--grow input input--actor\">\n          "
let private t36 = Utf8.lit "\n          "
let private t37 = Utf8.lit "\n        </label>\n      </div>\n\n      "
let private t38 = Utf8.lit "\n    </div>\n"
let private t39 = Utf8.lit "\n  <div class=\"margin-block pad-inline pad-block fill-shade border-radius\">\n    <menu class=\"flex flex-column gap margin-none pad\">\n      "
let private t40 = Utf8.lit "\n"
let private t41 = Utf8.lit "        <hr class=\"separator full-width\" style=\"--border-style: solid\">\n"
let private t42 = Utf8.lit "\n      "
let private t43 = Utf8.lit "    </menu>\n  </div>\n\n  "
let private t44 = Utf8.lit "\n</section>\n"

/// `profile_form_with(@user, **params)`.
let private profileForm () : Forms.FormWith =
    (Forms.formWith (Routes.userProfile ())).Model("user").Method("patch").Data("controller", "form")

/// `@page_title`.
let pageTitle (profile: ProfileShow) : string option = Some profile.User.Name

/// `content_for :head` (empty).
let head: Out -> unit = ignore

/// `content_for :nav`.
let nav (w: Out) (ctx: ViewContext) : unit =
    w.Lit t0
    Application.linkBack w ctx
    w.Lit t1
    let form = (Forms.formWith (Routes.session ())).Method("delete").Data("controller", "sessions")
    w.Lit t2
    Filters.formWith w form (fun w ->
        w.Lit t3
        Forms.hiddenFieldTag w "push_subscription_endpoint" None (Tag.attrs().Data("sessions_target", "pushSubscriptionEndpoint"))
        w.Lit t4
        Assets.imageTag w ctx "logout.svg" (Tag.attrs().AriaHidden())
        w.Lit t5)
    w.Lit t6

/// The page itself.
let content (w: Out) (ctx: ViewContext) (profile: ProfileShow) : unit =
    let user = profile.User
    let avatarFile () =
        Tag.attrs()
            .Id("file")
            .Class("input")
            .Accept("image/*")
            .Data("upload_preview_target", "input")
            .Data("action", "upload-preview#previewImage change->form#submit")
    w.Lit t7
    w.Int user.Id
    w.Lit t8
    Campfire.Views.Templates.Pwa._InstallInstructions.render w ctx
    w.Lit t9
    let form = (profileForm ()).Class("txt-medium")
    w.Lit t10
    Filters.formWith w form (fun w ->
        w.Lit t11
        Assets.imageTag w ctx "camera.svg" (Tag.attrs().AriaHidden().Size(20))
        w.Lit t12
        form.FileField(w, "avatar", avatarFile ())
        w.Lit t13)
    w.Lit t14
    let form = profileForm ()
    w.Lit t15
    Filters.formWith w form (fun w ->
        w.Lit t16
        Assets.imageTag w ctx user.AvatarPath (Tag.attrs().AriaHidden().Size(300).Data("upload_preview_target", "image"))
        w.Lit t17
        form.FileField(w, "avatar", avatarFile ())
        w.Lit t18)
    w.Lit t19
    if profile.AvatarAttached then
        w.Lit t20
        Filters.buttonTo w (Routes.userAvatar user.Id) (Tag.attrs().Method("delete").Class("btn btn--negative txt-small avatar__delete-btn")) (fun w ->
            w.Lit t21
            Assets.imageTag w ctx "minus.svg" (Tag.attrs().AriaHidden().Size(20))
            w.Lit t22)
    w.Lit t23
    let form = profileForm ()
    w.Lit t24
    Filters.formWith w form (fun w ->
        w.Lit t25
        Translations.translationButton w ctx "user_name"
        w.Lit t26
        form.TextField(
            w,
            "name",
            Some user.Name,
            Tag.attrs()
                .Class("input txt-large ")
                .Autocomplete("name")
                .Placeholder("Enter your name")
                .Autofocus()
                .Required(true)
                .Data("1p-ignore", true)
        )
        w.Lit t27
        Assets.imageTag w ctx "person.svg" (Tag.attrs().AriaHidden().Size(24).Class("colorize--black"))
        w.Lit t28
        Translations.translationButton w ctx "email_address"
        w.Lit t29
        form.EmailField(
            w,
            "email_address",
            None,
            Tag.attrs()
                .Class("input txt-large")
                .AttrOpt("value", user.EmailAddress)
                .Autocomplete("username")
                .Placeholder("Enter your email address")
                .Required(false)
        )
        w.Lit t30
        Assets.imageTag w ctx "email.svg" (Tag.attrs().AriaHidden().Size(24).Class("colorize--black"))
        w.Lit t31
        Translations.translationButton w ctx "update_password"
        w.Lit t32
        form.PasswordField(
            w,
            "password",
            Tag.attrs().Class("input txt-large").Autocomplete("new-password").Placeholder("Change password").Required(false).Maxlength(72)
        )
        w.Lit t33
        Assets.imageTag w ctx "password.svg" (Tag.attrs().AriaHidden().Size(24).Class("colorize--black"))
        w.Lit t34
        Translations.translationButton w ctx "bio"
        w.Lit t35
        form.TextArea(
            w,
            "bio",
            user.Bio,
            Tag.attrs().Class("input txt-large").Placeholder("A few words about yourself…").Maxlength(200).Rows(3).Required(false)
        )
        w.Lit t36
        Assets.imageTag w ctx "bio.svg" (Tag.attrs().AriaHidden().Size(24).Class("colorize--black"))
        w.Lit t37
        UsersHelper.profileFormSubmitButton w ctx
        w.Lit t38)
    w.Lit t39
    for membership in profile.SharedMemberships do
        _Membership.render w ctx membership
    w.Lit t40
    if not profile.DirectMemberships.IsEmpty && not profile.SharedMemberships.IsEmpty then
        w.Lit t41
    w.Lit t42
    for membership in profile.DirectMemberships do
        _Membership.render w ctx membership
    w.Lit t43
    _Transfer.render w ctx user profile.TransferId
    w.Lit t44

let render (w: Out) (ctx: ViewContext) (profile: ProfileShow) : unit =
    Templates.Layouts.Application.render
        w
        ctx
        (pageTitle profile)
        None
        head
        (fun w -> nav w ctx)
        (fun w -> content w ctx profile)
        ignore
        ignore
