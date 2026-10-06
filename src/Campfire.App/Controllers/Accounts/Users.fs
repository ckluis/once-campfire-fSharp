// Port of rust/crates/campfire/src/controllers/accounts/users.rs
//
// `Accounts::UsersController` (reference/app/controllers/accounts/users_controller.rb): the
// people list's next pages, role changes and removal.
namespace Campfire.App.Controllers

open System.Threading.Tasks
open Campfire.Views
open Campfire.Kit
open Campfire.App
open Campfire.App.Presenters
open Campfire.Db
open Campfire.Routes
open Campfire.Ruby

module AccountUsers =
    /// `set_page_and_extract_portion_from User.active.ordered.without_bots, per_page: 500`,
    /// rendered as `index.turbo_stream.erb` (the only template, so other formats are 406).
    let index (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            do! c.RespondTo [ Format.TurboStream ] |> Result.map ignore
            let! (users: User list) = c.App.ReadOffloaded(fun conn -> User.activeOrderedWithoutBots conn)
            let page = Pagination.Page.Create(c.ParamStr "page", int64 users.Length, [ 500L ])
            let secrets = c.App.Secrets
            let users = page.Records users |> List.map (Common.userSummary secrets)
            let nextPage = if page.IsLast then None else Some(string page.NextParam)

            let! layout = Layout.load c
            let html =
                Layout.render layout c (fun ctx ->
                    Render.page 0 (fun w -> Campfire.Views.Templates.Accounts.Users.IndexTurboStream.render w ctx users nextPage))
            page.ApplyHeaders c
            return c.TurboStream(System.ReadOnlyMemory<byte> html.Text)
        }

    /// `User.active.find(params[:user_id] || params[:id])`
    let private setUser (c: Ctx) : Task<Result<User, Error>> =
        let id =
            match c.ParamStr "user_id" with
            | null -> c.ParamStr "id"
            | id -> id
        match (match id with null -> None | id -> Ruby.integerCast id) with
        | None -> Task.FromResult(Error NotFound)
        | Some id -> c.App.Read(fun conn -> User.findActive conn id)

    let private redirectToEditAccount (c: Ctx) : Task<Result<Response, Error>> =
        Task.FromResult(c.RedirectTo(c.UrlFor(Routes.editAccount ())))

    /// `@user.update(role: params.require(:user)[:role].presence_in(%w[ member administrator ]) || "member")`
    let update (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            do! Concerns.ensureCanAdminister c
            let! (user: User) = setUser c
            let! required = c.Params.Require "user"
            let role =
                match required.Get "role" with
                | ValueSome(Param.Str "administrator") -> Role.Administrator
                | _ -> Role.Member
            let! (_: User) = c.App.Write(fun tx -> User.update tx user { UserChanges.none with Role = Some role })
            return! redirectToEditAccount c
        }

    /// `@user.deactivate`
    let destroy (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            do! Concerns.ensureCanAdminister c
            let! (user: User) = setUser c
            let! (_: User) = c.App.Write(fun tx -> User.deactivate tx user)
            return! redirectToEditAccount c
        }
