// Port of rust/crates/campfire/src/controllers/accounts/bots.rs
//
// `Accounts::BotsController` (reference/app/controllers/accounts/bots_controller.rb).
namespace Campfire.App.Controllers

open System.Threading.Tasks
open Campfire.Views
open Campfire.Kit
open Campfire.App
open Campfire.App.Presenters
open Campfire.Db
open Campfire.Routes
open Campfire.Ruby

module Bots =
    module IndexPage = Campfire.Views.Templates.Accounts.Bots.Index
    module NewPage = Campfire.Views.Templates.Accounts.Bots.New
    module EditPage = Campfire.Views.Templates.Accounts.Bots.Edit

    let private indexSize = RenderSize()
    let private newSize = RenderSize()
    let private editSize = RenderSize()

    /// ApplicationController's chain, then `ensure_can_administer`.
    let private before (c: Ctx) : Task<Result<unit, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            do! Concerns.ensureCanAdminister c
        }

    let findActiveBot (c: Ctx) (key: string) : Task<Result<User, Error>> =
        match (match c.ParamStr key with null -> None | id -> Ruby.integerCast id) with
        | None -> Task.FromResult(Error NotFound)
        | Some id -> c.App.Read(fun conn -> User.findActiveBot conn id)

    /// `User.active_bots.find(params[:id])`
    let private setBot (c: Ctx) : Task<Result<User, Error>> = findActiveBot c "id"

    /// `params.require(:user).permit(:name, :avatar, :webhook_url)`
    let private botParams (c: Ctx) : Result<Campfire.Kit.ParamMap, Error> =
        c.Params.Require "user" |> Result.map (fun user -> user.Permit(Params.permitKeys [ "name"; "avatar"; "webhook_url" ]))

    let private redirectToBots (c: Ctx) : Task<Result<Response, Error>> =
        Task.FromResult(c.RedirectTo(c.UrlFor(Routes.accountBots ())))

    /// `@bots = User.active_bots.ordered`
    let index (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! before c
            do! c.RespondTo [ Format.Html ] |> Result.map ignore
            let secrets = c.App.Secrets
            let! bots = c.App.ReadOffloaded(fun conn -> User.activeBotsOrdered conn |> List.map (Accounts.bot conn secrets))
            return!
                Page.framedPage
                    c
                    Status.Ok
                    indexSize
                    (fun w ctx -> IndexPage.render w ctx bots)
                    IndexPage.head
                    (fun w ctx -> IndexPage.content w ctx bots)
        }

    let ``new`` (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! before c
            do! c.RespondTo [ Format.Html ] |> Result.map ignore
            let bot = Campfire.Views.Accounts.BotForm.empty
            return!
                Page.framedPage
                    c
                    Status.Ok
                    newSize
                    (fun w ctx -> NewPage.render w ctx bot)
                    NewPage.head
                    (fun w ctx -> NewPage.content w ctx bot)
        }

    /// `User.create_bot! bot_params`
    let create (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! before c
            let! parameters = botParams c
            // users.name is NOT NULL.
            let! name =
                match Accounts.paramToS parameters "name" with
                | Some name -> Ok name
                | None -> Error(Internal(exn "NOT NULL constraint failed: users.name"))
            // `create_webhook!(url: webhook_url) if webhook_url`: any non-nil value, "" included.
            let webhookUrl = Accounts.paramToS parameters "webhook_url"
            let! avatar = Assignment.stage c.App (Assignment.fromParams parameters "avatar")
            let! pending =
                c.App.Write(fun tx ->
                    let bot = User.createBot tx name webhookUrl
                    AttachmentWrites.assign tx (Record.user bot.Id) "avatar" avatar)
            AttachmentWrites.analyzeLater c.App pending
            return! redirectToBots c
        }

    let edit (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! before c
            let! (bot: User) = setBot c
            do! c.RespondTo [ Format.Html ] |> Result.map ignore
            let storage = c.App.Storage
            let baseUrl = c.UrlFor ""
            let botId = bot.Id
            let! form = c.App.Read(fun conn -> Accounts.botForm conn storage baseUrl bot)
            return!
                Page.framedPage
                    c
                    Status.Ok
                    editSize
                    (fun w ctx -> EditPage.render w ctx botId form)
                    EditPage.head
                    (fun w ctx -> EditPage.content w ctx botId form)
        }

    /// `@bot.update_bot! bot_params`: the webhook first, then the bot, in one transaction.
    let update (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! before c
            let! (bot: User) = setBot c
            let! parameters = botParams c
            let changes = { UserChanges.none with Name = Accounts.paramToS parameters "name" }
            let webhookUrl = Accounts.paramToS parameters "webhook_url"
            let! avatar = Assignment.stage c.App (Assignment.fromParams parameters "avatar")
            let! pending =
                c.App.Write(fun tx ->
                    User.updateBot tx bot changes webhookUrl |> ignore
                    AttachmentWrites.assign tx (Record.user bot.Id) "avatar" avatar)
            AttachmentWrites.analyzeLater c.App pending
            return! redirectToBots c
        }

    /// `@bot.deactivate`
    let destroy (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! before c
            let! (bot: User) = setBot c
            let! (_: User) = c.App.Write(fun tx -> User.deactivate tx bot)
            return! redirectToBots c
        }
