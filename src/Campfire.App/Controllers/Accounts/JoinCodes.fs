// Port of rust/crates/campfire/src/controllers/accounts/join_codes.rs
//
// `Accounts::JoinCodesController` (reference/app/controllers/accounts/join_codes_controller.rb).
namespace Campfire.App.Controllers

open System.Threading.Tasks
open Campfire.Kit
open Campfire.App
open Campfire.Db
open Campfire.Routes

module JoinCodes =
    /// `Current.account.reset_join_code`
    let create (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            do! Concerns.ensureCanAdminister c
            let! (account: Account) = AccountsController.currentAccount c
            let! (_: Account) = c.App.Write(fun tx -> Account.resetJoinCode tx account)
            return! c.RedirectTo(c.UrlFor(Routes.editAccount ()))
        }
