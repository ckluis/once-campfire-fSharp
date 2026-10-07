// Port of rust/crates/campfire/src/controllers/users/profiles.rs
//
// `Users::ProfilesController` (reference/app/controllers/users/profiles_controller.rb): the
// signed-in user's own profile.
namespace Campfire.App.Controllers

open System.Threading.Tasks
open Campfire.Views
open Campfire.Views.Users
open Campfire.Kit
open Campfire.App
open Campfire.App.Presenters
open Campfire.Db
open Campfire.Routes

module Profiles =
    module ShowPage = Campfire.Views.Templates.Users.Profiles.Show

    let private showSize = RenderSize()

    /// `set_user` (`Current.user`); memberships partitioned into direct and shared rooms.
    let show (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            do! c.RespondTo [ Format.Html ] |> Result.map ignore
            let! (user: User) = Concerns.requireCurrentUser c
            let secrets = c.App.Secrets
            let transferId = Accounts.transferId secrets user.Id (c.Now())
            let! avatarAttached, (directMemberships, sharedMemberships) =
                c.App.Read(fun conn ->
                    let attached = (Attachments.attachedBlob conn "User" user.Id "avatar").IsSome
                    attached, Accounts.profileMemberships conn user)
            let profile: ProfileShow =
                { User = Common.userSummary secrets user
                  AvatarAttached = avatarAttached
                  TransferId = transferId
                  SharedMemberships = sharedMemberships
                  DirectMemberships = directMemberships }
            return!
                Page.framedPage
                    c
                    Status.Ok
                    showSize
                    (fun w ctx -> ShowPage.render w ctx profile)
                    ShowPage.head
                    (fun w ctx -> ShowPage.content w ctx profile)
        }

    /// `@user.update user_params`, then `redirect_to user_profile_url, notice: update_notice`.
    let update (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            let! (user: User) = Concerns.requireCurrentUser c

            // params.require(:user).permit(:name, :avatar, :email_address, :password, :bio).compact
            let! required = c.Params.Require "user"
            let parameters = required.Permit(Params.permitKeys [ "name"; "avatar"; "email_address"; "password"; "bio" ])
            let present (key: string) : string option = Accounts.stringAttribute parameters key |> Option.flatten
            // `password=` ignores a blank password.
            let! passwordDigest = Concerns.passwordDigest c (present "password" |> Option.filter (fun password -> password <> ""))
            let changes =
                { UserChanges.none with
                    Name = present "name"
                    EmailAddress = present "email_address" |> Option.map Some
                    PasswordDigest = passwordDigest
                    Bio = present "bio" |> Option.map Some }
            let avatar =
                match Assignment.fromParams parameters "avatar" with
                // `.compact` drops a nil avatar before it's assigned.
                | Delete when (match parameters.Get "avatar" with ValueNone | ValueSome Param.Null -> true | _ -> false) -> Unchanged
                | assignment -> assignment
            // `params[:user][:avatar] ? ... : "✓"`: any non-nil value counts.
            let notice =
                match c.Params.Get "user" with
                | ValueSome userParam ->
                    match userParam.Get "avatar" with
                    | ValueSome Param.Null
                    | ValueNone -> "✓"
                    | ValueSome _ -> "It may take up to 30 minutes to change everywhere."
                | ValueNone -> "✓"

            let! avatar = Assignment.stage c.App avatar
            let! pending =
                c.App.Write(fun tx ->
                    User.update tx user changes |> ignore
                    AttachmentWrites.assign tx (Record.user user.Id) "avatar" avatar)
            AttachmentWrites.analyzeLater c.App pending

            return! c.RedirectToWith(c.UrlFor(Routes.userProfile ()), { Redirect.Default with Notice = notice })
        }
