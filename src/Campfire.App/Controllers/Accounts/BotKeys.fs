// Port of rust/crates/campfire/src/controllers/accounts/bots/keys.rs
//
// `Accounts::Bots::KeysController` (reference/app/controllers/accounts/bots/keys_controller.rb).
namespace Campfire.App.Controllers

open System.Threading.Tasks
open Campfire.Kit
open Campfire.App
open Campfire.Db
open Campfire.Routes

module BotKeys =
    /// `User.active_bots.find(params[:bot_id]).reset_bot_key`
    let update (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            do! Concerns.ensureCanAdminister c
            let! bot = Bots.findActiveBot c "bot_id"
            let! (_: User) = c.App.Write(fun tx -> User.resetBotKey tx bot)
            return! c.RedirectTo(c.UrlFor(Routes.accountBots ()))
        }
