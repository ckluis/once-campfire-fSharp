// Port of rust/crates/campfire/src/controllers/accounts.rs
//
// `AccountsController` (reference/app/controllers/accounts_controller.rb): account settings. Its
// nested controllers (`Controllers/Accounts/`) are separate modules.
namespace Campfire.App.Controllers

open System.Threading.Tasks
open Campfire.Views
open Campfire.Kit
open Campfire.App
open Campfire.App.Presenters
open Campfire.Db
open Campfire.Routes

module AccountsController =
    module EditPage = Campfire.Views.Templates.Accounts.Edit

    let private editSize = RenderSize()

    /// `set_page_and_extract_portion_from users, per_page: 500`
    let private PerPage = [ 500L ]

    /// `Current.account` where the reference dereferences it (a nil account raises NoMethodError).
    let currentAccount (c: Ctx) : Task<Result<Account, Error>> =
        task {
            match! c.App.Read(fun conn -> Account.first conn) with
            | Error e -> return Error e
            | Ok(Some account) -> return Ok account
            | Ok None -> return Error(Internal(exn "no account"))
        }

    /// Everyone, administrators first; the page only decides whether a next-page loader follows.
    let edit (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            let! (account: Account) = currentAccount c
            do! c.RespondTo [ Format.Html ] |> Result.map ignore
            let canAdminister = Concerns.currentUser c |> Option.exists (fun user -> User.canAdminister user None false)
            let! (users: User list) = c.App.ReadOffloaded(fun conn -> Accounts.accountUsers conn canAdminister)
            let page = Pagination.Page.Create(c.ParamStr "page", int64 users.Length, PerPage)

            let secrets = c.App.Secrets
            let administrators, members =
                users |> List.map (Common.userSummary secrets) |> List.partition (fun user -> user.Administrator)
            let edit: Campfire.Views.Accounts.EditView =
                { AccountId = account.Id
                  JoinCode = account.JoinCode
                  RestrictRoomCreationToAdministrators =
                    AccountSettings.restrictRoomCreationToAdministrators (Account.settings account)
                  Administrators = administrators
                  Members = members
                  NextPage = if page.IsLast then None else Some(string page.NextParam) }
            return!
                Page.framedPage
                    c
                    Status.Ok
                    editSize
                    (fun w ctx -> EditPage.render w ctx edit)
                    EditPage.head
                    (fun w ctx -> EditPage.content w ctx edit)
        }

    /// `@account.update!(params.require(:account).permit(:name, :logo, settings: {}))`
    let update (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            do! Concerns.ensureCanAdminister c
            let! (account: Account) = currentAccount c

            let! required = c.Params.Require "account"
            let parameters = required.Permit [ Permit.Key "name"; Permit.Key "logo"; Permit.AnyHash "settings" ]
            let name = Accounts.paramToS parameters "name"
            let settings =
                match parameters.Get "settings" with
                | ValueSome(Param.Hash settings) ->
                    Some [ for struct (key, value) in settings.Iter -> key, defaultArg (Option.ofObj (value.ToS())) "" ]
                | _ -> None
            let! logo = Assignment.stage c.App (Assignment.fromParams parameters "logo")

            let! pending =
                c.App.Write(fun tx ->
                    Account.update tx account name None settings |> ignore
                    AttachmentWrites.assign tx (Record.account account.Id) "logo" logo)
            AttachmentWrites.analyzeLater c.App pending

            return! c.RedirectToWith(c.UrlFor(Routes.editAccount ()), { Redirect.Default with Notice = "✓" })
        }
