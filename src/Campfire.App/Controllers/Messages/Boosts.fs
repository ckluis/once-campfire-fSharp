// Port of rust/crates/campfire/src/controllers/messages/boosts.rs
//
// `Messages::BoostsController` (reference/app/controllers/messages/boosts_controller.rb). Its
// `show`/`edit`/`update` routes have no action or template (`action_not_found`).
namespace Campfire.App.Controllers

open System.Threading.Tasks
open Campfire.App
open Campfire.App.Presenters
open Campfire.Db
open Campfire.Kit
open Campfire.Routes
open Campfire.Ruby
open Campfire.Views

module BoostsController =
    module IndexPage = Campfire.Views.Templates.Messages.Boosts.Index
    module NewPage = Campfire.Views.Templates.Messages.Boosts.New

    /// `Current.user.reachable_messages.find(params[:message_id])`
    let private setMessage (c: Ctx) : Task<Result<Message, Error>> =
        act {
            let! (user: User) = Concerns.requireCurrentUser c
            match (match c.ParamStr "message_id" with null -> None | id -> Ruby.integerCast id) with
            | None -> return! Error NotFound
            | Some id -> return! c.App.Read(fun conn -> Message.findReachable conn user.Id id)
        }

    /// `@message.boosts.find_by!(id: params[:id], booster: Current.user)`
    let setBoost (c: Ctx) (message: Message) : Task<Result<Boost, Error>> =
        act {
            let! (user: User) = Concerns.requireCurrentUser c
            match (match c.ParamStr "id" with null -> None | id -> Ruby.integerCast id) with
            | None -> return! Error NotFound
            | Some id ->
                let messageId = message.Id
                return! c.App.Read(fun conn -> Boost.findByMessageAndBooster conn messageId id user.Id)
        }

    /// `@message.boosts.create!(content:)`, boosted by `Current.user`.
    let createBoost (c: Ctx) (message: Message) (content: string option) : Task<Result<Boost, Error>> =
        act {
            let! (user: User) = Concerns.requireCurrentUser c
            let messageId = message.Id
            // A nil content violates the column's NOT NULL (ActiveRecord::NotNullViolation, a 500).
            let! content =
                match content with
                | Some content -> Ok content
                | None -> Error(Internal(exn "NOT NULL constraint failed: boosts.content"))
            return! c.App.Write(fun tx -> Boost.create tx messageId user.Id content)
        }

    /// `@boost.destroy!` then `broadcast_remove`.
    let destroyBoost (c: Ctx) (message: Message) (boost: Boost) : Task<Result<unit, Error>> =
        act {
            do! c.App.Write(fun tx -> Boost.destroy tx boost)
            let roomId = message.RoomId
            let! (room: Room) = c.App.Read(fun conn -> Room.find conn roomId)
            c.App.Broadcasts.BoostRemove(room, boost)
        }

    /// `broadcast_create`: `messages/boosts/_boost` appended to the message's boosts.
    let broadcastCreate (c: Ctx) (message: Message) (boost: Boost) : Task<Result<unit, Error>> =
        let app = c.App
        let baseUrl = Page.rendererBaseUrl c
        app.Read(fun conn ->
            let presenter = Presenter(conn, app, None)
            let view = presenter.Boost boost
            let account = Account.first conn
            let html = Page.renderDetachedAt app account baseUrl (fun ctx -> BoostsCached.boost ctx view)
            let room = Room.find conn message.RoomId
            let partials = { Rendered.empty with Boost = Some html }
            app.Broadcasts.BoostCreate(room, message, boost, Rendered.partials partials))

    let index (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            let! (message: Message) = setMessage c
            do! c.RespondTo [ Format.Html ] |> Result.map ignore
            let! view = MessagesController.present c (fun presenter -> presenter.Message message)
            return! Page.content c Status.Ok (fun w ctx -> IndexPage.render w ctx view)
        }

    let ``new`` (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            let! (message: Message) = setMessage c
            do! c.RespondTo [ Format.Html ] |> Result.map ignore
            let! (current: User) = Concerns.requireCurrentUser c
            let user = PresenterSupport.userView c.App.Secrets current
            let! view = MessagesController.present c (fun presenter -> presenter.Message message)
            return! Page.content c Status.Ok (fun w ctx -> NewPage.render w ctx view user)
        }

    let create (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            let! (message: Message) = setMessage c
            // params.require(:boost).permit(:content)
            let! (boost: Param) = c.Params.Require "boost"
            let content =
                match (boost.Permit(Params.permitKeys [ "content" ])).Get "content" with
                | ValueSome(Param.Str content) -> Some content
                | _ -> None
            let! (boost: Boost) = createBoost c message content
            do! broadcastCreate c message boost
            return! c.RedirectTo(c.UrlFor(Routes.messageBoosts message.Id))
        }

    let destroy (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            let! (message: Message) = setMessage c
            let! (boost: Boost) = setBoost c message
            do! destroyBoost c message boost
            // No destroy template: `head :no_content`.
            return c.Head Status.NoContent
        }
