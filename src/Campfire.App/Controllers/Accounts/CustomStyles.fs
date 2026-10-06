// Port of rust/crates/campfire/src/controllers/accounts/custom_styles.rs
//
// `Accounts::CustomStylesController` (reference/app/controllers/accounts/custom_styles_controller.rb).
namespace Campfire.App.Controllers

open System.Threading.Tasks
open Campfire.Views
open Campfire.Kit
open Campfire.App
open Campfire.App.Presenters
open Campfire.Db
open Campfire.Routes

module CustomStyles =
    module EditPage = Campfire.Views.Templates.Accounts.CustomStyles.Edit

    let private editSize = RenderSize()

    /// `before_action :ensure_can_administer, :set_account`
    let edit (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            do! Concerns.ensureCanAdminister c
            let! (account: Account) = AccountsController.currentAccount c
            do! c.RespondTo [ Format.Html ] |> Result.map ignore
            let customStyles = account.CustomStyles
            return!
                Page.framedPage
                    c
                    Status.Ok
                    editSize
                    (fun w ctx -> EditPage.render w ctx customStyles)
                    EditPage.head
                    (fun w ctx -> EditPage.content w ctx customStyles)
        }

    /// `@account.update!(params.require(:account).permit(:custom_styles))`
    let update (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            do! Concerns.ensureCanAdminister c
            let! (account: Account) = AccountsController.currentAccount c
            let! required = c.Params.Require "account"
            let parameters = required.Permit(Params.permitKeys [ "custom_styles" ])
            let customStyles =
                if parameters.ContainsKey "custom_styles" then Some(Accounts.paramToS parameters "custom_styles") else None
            let! (_: Account) = c.App.Write(fun tx -> Account.update tx account None customStyles None)
            return! c.RedirectToWith(c.UrlFor(Routes.editAccountCustomStyles ()), { Redirect.Default with Notice = "✓" })
        }
