// Port of rust/crates/campfire/src/controllers/users/bans.rs
//
// `Users::BansController` (reference/app/controllers/users/bans_controller.rb).
namespace Campfire.App.Controllers

open System.Threading.Tasks
open Campfire.Kit
open Campfire.App
open Campfire.Db
open Campfire.Routes

module Bans =
    /// `redirect_to @user`
    let private redirectToUser (c: Ctx) (id: int64) : Task<Result<Response, Error>> =
        Task.FromResult(c.RedirectTo(c.UrlFor(Routes.user id)))

    /// `before_action :ensure_can_administer, :set_user`; `@user.ban`
    let create (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            do! Concerns.ensureCanAdminister c
            let! (user: User) = UsersController.findUser c "user_id"
            let! (_: User) = c.App.Write(fun tx -> User.ban tx user)
            return! redirectToUser c user.Id
        }

    /// `@user.unban`
    let destroy (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            do! Concerns.ensureCanAdminister c
            let! (user: User) = UsersController.findUser c "user_id"
            let! (_: User) = c.App.Write(fun tx -> User.unban tx user)
            return! redirectToUser c user.Id
        }
