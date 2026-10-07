// Port of rust/crates/campfire/src/controllers/users.rs
//
// `UsersController` (reference/app/controllers/users_controller.rb): joining with the account's
// join code, and a user's page. (The module is `UsersController`: `Campfire.Views.Users` is a module
// too.) Its nested controllers (`Controllers/Users/`) are separate modules.
namespace Campfire.App.Controllers

open System
open System.Threading.Tasks
open Campfire.Views
open Campfire.Kit
open Campfire.App
open Campfire.App.Presenters
open Campfire.Db
open Campfire.Routes
open Campfire.Ruby

module UsersController =
    module NewPage = Campfire.Views.Templates.Users.New
    module ShowPage = Campfire.Views.Templates.Users.Show

    let private newSize = RenderSize()
    let private showSize = RenderSize()

    /// `User.find(params[key])`: 404 when there's no such user.
    let findUser (c: Ctx) (key: string) : Task<Result<User, Error>> =
        task {
            match (match c.ParamStr key with null -> None | id -> Ruby.integerCast id) with
            | None -> return Error NotFound
            | Some id ->
                match! c.App.Read(fun conn -> User.findById conn id) with
                | Error e -> return Error e
                | Ok(Some user) -> return Ok user
                | Ok None -> return Error NotFound
        }

    /// `head :not_found if Current.account.join_code != params[:join_code]`
    let private verifyJoinCode (c: Ctx) : Task<Result<Account, Error>> =
        act {
            // `Current.account.join_code` on nil raises NoMethodError.
            let! account = c.App.Read(fun conn -> Account.first conn)
            match account with
            | None -> return! Error(Internal(exn "undefined method 'join_code' for nil"))
            | Some account ->
                if c.ParamStr "join_code" <> account.JoinCode then return! halt (c.Head Status.NotFound)
                return account
        }

    /// `params.require(:user).permit(:name, :avatar, :email_address, :password)`
    let private userParams (c: Ctx) : Result<Campfire.Kit.ParamMap, Error> =
        c.Params.Require "user"
        |> Result.map (fun user -> user.Permit(Params.permitKeys [ "name"; "avatar"; "email_address"; "password" ]))

    /// `require_unauthenticated_access only: %i[ new create ]`, `before_action :verify_join_code`
    let ``new`` (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c (Before.requireUnauthenticatedAccess Before.Default)
            let! account = verifyJoinCode c
            do! c.RespondTo [ Format.Html ] |> Result.map ignore
            let! helpContact = c.App.Read(fun conn -> Accounts.helpContact conn)
            let joinCode = account.JoinCode
            return!
                Page.framedPage
                    c
                    Status.Ok
                    newSize
                    (fun w ctx -> NewPage.render w ctx joinCode helpContact)
                    NewPage.head
                    (fun w ctx -> NewPage.content w ctx joinCode helpContact)
        }

    let create (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c (Before.requireUnauthenticatedAccess Before.Default)
            let! (_: Account) = verifyJoinCode c
            let! parameters = userParams c
            let emailAddress = Accounts.paramToS parameters "email_address"
            // users.name is NOT NULL: a missing name fails the insert, as in Rails.
            let! name =
                match Accounts.paramToS parameters "name" with
                | Some name -> Ok name
                | None -> Error(Internal(exn "NOT NULL constraint failed: users.name"))
            let! passwordDigest =
                Concerns.passwordDigest c (Accounts.paramToS parameters "password" |> Option.filter (fun password -> password <> ""))
            let attributes =
                { NewUser.create name with
                    EmailAddress = emailAddress
                    PasswordDigest = passwordDigest }
            let! avatar = Assignment.stage c.App (Assignment.fromParams parameters "avatar")

            // `User.create!(user_params)`
            let! result =
                c.App.Db.Write(fun tx ->
                    let user = User.create tx attributes
                    let pending = AttachmentWrites.assign tx (Record.user user.Id) "avatar" avatar
                    user, pending)
            match result with
            | Ok(user, pending) ->
                AttachmentWrites.analyzeLater c.App pending
                let! (_: Session) = Concerns.startNewSessionFor c user
                return! c.RedirectTo(c.UrlFor(Routes.root ()))
            // rescue ActiveRecord::RecordNotUnique: `redirect_to new_session_url(email_address: user_params[:email_address])`
            | Error error when DbError.isRecordNotUnique error ->
                let location =
                    match emailAddress with
                    | Some email -> $"{c.UrlFor(Routes.newSession ())}?email_address={Ruby.cgiEscape email}"
                    | None -> c.UrlFor(Routes.newSession ())
                return! c.RedirectTo location
            | Error error -> return! Error(Internal(Exception(DbError.display error)))
        }

    /// `before_action :set_user, only: :show`
    let show (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            let! (user: User) = findUser c "id"
            do! c.RespondTo [ Format.Html ] |> Result.map ignore
            let secrets = c.App.Secrets
            let transferId = Accounts.transferId secrets user.Id (c.Now())
            let user = Common.userSummary secrets user
            return!
                Page.framedPage
                    c
                    Status.Ok
                    showSize
                    (fun w ctx -> ShowPage.render w ctx user transferId)
                    ShowPage.head
                    (fun w ctx -> ShowPage.content w ctx user transferId)
        }
