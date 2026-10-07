// Port of rust/crates/views/templates/users/_ban_button.html (reference/app/views/users/_ban_button.html.erb)
/// `users/_ban_button.html.erb`: ban or lift the ban on a user.
module Campfire.Views.Templates.Users._BanButton

open Campfire.Routes
open Campfire.Views
open Campfire.Views.Helpers
open Campfire.Views.Users

let private t0 = Utf8.lit "  "
let private t1 = Utf8.lit "\n    "
let private t2 = Utf8.lit "\n    <span>Ban "
let private t3 = Utf8.lit "</span>\n"
let private t4 = Utf8.lit "  "
let private t5 = Utf8.lit "\n    "
let private t6 = Utf8.lit "\n    <span>Remove ban</span>\n"

let render (w: Out) (ctx: ViewContext) (user: UserSummary) : unit =
    if user.Active then
        w.Lit t0
        Filters.buttonTo
            w
            (Routes.userBan user.Id)
            (Tag.attrs()
                .Method("post")
                .Class("btn full-width")
                .Data("turbo_confirm", "Are you sure you want to ban this user? This will log them out, delete their messages, and block their IP addresses."))
            (fun w ->
                w.Lit t1
                Assets.imageTag w ctx "cancel.svg" (Tag.attrs().Aria("hidden", "true").Aria("label", "Ban " + user.Name))
                w.Lit t2
                w.Text user.Name
                w.Lit t3)
    else
        w.Lit t4
        Filters.buttonTo
            w
            (Routes.userBan user.Id)
            (Tag.attrs()
                .Method("delete")
                .Class("btn btn--negative full-width")
                .Data("turbo_confirm", "Are you sure you want to remove the ban on this user?"))
            (fun w ->
                w.Lit t5
                Assets.imageTag w ctx "cancel.svg" (Tag.attrs().Aria("hidden", "true").Aria("label", "Remove Ban " + user.Name))
                w.Lit t6)
