// Port of rust/crates/campfire/src/controllers/first_runs.rs
//
// `FirstRunsController` (reference/app/controllers/first_runs_controller.rb): set up the account
// and its first administrator.
namespace Campfire.App.Controllers

open System
open System.Threading.Tasks
open Campfire.Views
open Campfire.Kit
open Campfire.App
open Campfire.App.Presenters
open Campfire.Db
open Campfire.Routes

module FirstRuns =
    module ShowPage = Campfire.Views.Templates.FirstRuns.Show

    let private showSize = RenderSize()

    /// `redirect_to root_url if Account.any?`
    let private preventRepeats (c: Ctx) : Task<Result<unit, Error>> =
        act {
            let! any = c.App.Read(fun conn -> Account.count conn > 0L)
            if any then
                let! response = c.RedirectTo(c.UrlFor(Routes.root ()))
                return! halt response
        }

    /// `allow_unauthenticated_access`, `before_action :prevent_repeats`
    let show (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c (Before.allowUnauthenticatedAccess Before.Default)
            do! preventRepeats c
            do! c.RespondTo [ Format.Html ] |> Result.map ignore
            return!
                Page.framedPage c Status.Ok showSize (fun w ctx -> ShowPage.render w ctx) ShowPage.head (fun w ctx -> ShowPage.content w ctx)
        }

    let create (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c (Before.allowUnauthenticatedAccess Before.Default)
            do! preventRepeats c

            // params.require(:user).permit(:name, :avatar, :email_address, :password)
            let! required = c.Params.Require "user"
            let user = required.Permit(Params.permitKeys [ "name"; "avatar"; "email_address"; "password" ])
            let name = Accounts.paramToS user "name"
            let emailAddress = defaultArg (Accounts.paramToS user "email_address") ""
            let password = defaultArg (Accounts.paramToS user "password") ""
            let avatar = Assignment.fromParams user "avatar"
            // users.name is NOT NULL: Rails raises ActiveRecord::NotNullViolation.
            let! name =
                match name with
                | Some name -> Ok name
                | None -> Error(Internal(exn "NOT NULL constraint failed: users.name"))
            let! avatar = Assignment.stage c.App avatar
            let! passwordDigest =
                task {
                    try
                        let! digest = PasswordDigest.hash password c.App.Db.Env.BcryptCost
                        return Ok digest
                    with e ->
                        return Error(Internal e)
                }

            let! result =
                c.App.Db.Write(fun tx ->
                    let administrator = FirstRun.create tx name emailAddress passwordDigest
                    let pending = AttachmentWrites.assign tx (Record.user administrator.Id) "avatar" avatar
                    administrator, pending)

            let root = c.UrlFor(Routes.root ())
            match result with
            | Ok(administrator, pending) ->
                AttachmentWrites.analyzeLater c.App pending
                let! (_: Session) = Concerns.startNewSessionFor c administrator
                return! c.RedirectTo root
            // rescue ActiveRecord::RecordNotUnique
            | Error error when DbError.isRecordNotUnique error -> return! c.RedirectTo root
            | Error error -> return! Error(Internal(Exception(DbError.display error)))
        }
