// Port of rust/crates/campfire/src/controllers/messages/boosts/by_bots.rs
//
// `Messages::Boosts::ByBotsController` (reference/app/controllers/messages/boosts/by_bots_controller.rb):
// bots boost with the raw request body as the content.
namespace Campfire.App.Controllers

open System.Threading.Tasks
open Campfire.App
open Campfire.Db
open Campfire.Kit
open Campfire.Ruby
open Campfire.Views

module BoostsByBots =
    let private before: Before = Before.allowBotAccess Before.Default

    /// The room among `Current.user.rooms`, then its message; `head :not_found` without one.
    let private setMessage (c: Ctx) : Task<Result<Message, Error>> =
        act {
            let! (user: User) = Concerns.requireCurrentUser c
            let roomId = match c.ParamStr "room_id" with null -> None | id -> Ruby.integerCast id
            let messageId = match c.ParamStr "message_id" with null -> None | id -> Ruby.integerCast id
            let! message =
                c.App.Read(fun conn ->
                    match roomId |> Option.bind (fun id -> Room.findForUser conn user.Id id) with
                    | None -> None
                    | Some room ->
                        match messageId with
                        | Some id -> Message.findById conn id |> Option.filter (fun message -> message.RoomId = room.Id)
                        | None -> None)
            match message with
            | Some message -> return message
            | None -> return! halt (Concerns.head Status.NotFound)
        }

    let create (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c before
            let! (message: Message) = setMessage c
            // ensure_content_present
            let content = ByBots.rawRequestBody c
            if ByBots.isBlank content then return! halt (Concerns.head Status.UnprocessableEntity)
            let! (boost: Boost) = BoostsController.createBoost c message (Some content)
            do! BoostsController.broadcastCreate c message boost

            // render :show, status: :created
            do! c.RespondTo [ Format.Json ] |> Result.map ignore
            let baseUrl = c.UrlFor ""
            let! (body: string) =
                MessagesController.present c (fun presenter ->
                    MessagesJson.boostsByBotsShow (presenter.BoostJson(boost, message, baseUrl)))
            return c.Render(Status.Created, Format.Json, body)
        }

    let destroy (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c before
            let! (message: Message) = setMessage c
            // set_boost, with `rescue ActiveRecord::RecordNotFound` -> head :not_found
            let! boost =
                task {
                    match! BoostsController.setBoost c message with
                    | Ok boost -> return Ok(Some boost)
                    | Error NotFound -> return Ok None
                    | Error e -> return Error e
                }
            match boost with
            | None -> return Concerns.head Status.NotFound
            | Some boost ->
                do! BoostsController.destroyBoost c message boost
                return c.Head Status.NoContent
        }
