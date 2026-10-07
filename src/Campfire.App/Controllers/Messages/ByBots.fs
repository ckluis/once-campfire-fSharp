// Port of rust/crates/campfire/src/controllers/messages/by_bots.rs
//
// `Messages::ByBotsController` (reference/app/controllers/messages/by_bots_controller.rb): the
// bot API under `/rooms/:room_id/:bot_key/messages` (JSON by route default). Bodies are the raw
// request body (`RawRequestBody`), or a top-level multipart `attachment`.
namespace Campfire.App.Controllers

open System
open System.Text
open System.Threading.Tasks
open Campfire.App
open Campfire.Db
open Campfire.Kit
open Campfire.Routes
open Campfire.Ruby
open Campfire.Views

module ByBots =
    let private before: Before = Before.allowBotAccess Before.Default

    /// `set_room`: `Current.user.rooms.find_by(id: params[:room_id])`, else `head :not_found`.
    let private setRoom (c: Ctx) : Task<Result<Room, Error>> =
        act {
            let! (user: User) = Concerns.requireCurrentUser c
            let! room =
                match (match c.ParamStr "room_id" with null -> None | id -> Ruby.integerCast id) with
                | Some id -> c.App.Read(fun conn -> Room.findForUser conn user.Id id)
                | None -> Task.FromResult(Ok None)
            match room with
            | Some room -> return room
            | None -> return! halt (Concerns.head Status.NotFound)
        }

    /// `RawRequestBody#raw_request_body`: the whole body, as UTF-8.
    let rawRequestBody (c: Ctx) : string = Encoding.UTF8.GetString c.Request.RawPost.Span

    /// `String#blank?`: empty or only whitespace.
    let isBlank (value: string) : bool = value |> Seq.forall Char.IsWhiteSpace

    /// `head :unprocessable_content if params[:attachment].blank? && raw_request_body.blank?`
    let private ensureBodyOrAttachmentPresent (c: Ctx) : Result<unit, Error> =
        let attachmentBlank =
            match c.Params.Get "attachment" with
            | ValueSome param -> param.IsBlank
            | ValueNone -> true
        if attachmentBlank && isBlank (rawRequestBody c) then
            halt (Concerns.head Status.UnprocessableEntity)
        else
            Ok()

    /// `params[:attachment] ? params.permit(:attachment) : { body: raw_request_body }`
    let private messageParams (c: Ctx) : MessageParams =
        match c.Params.Get "attachment" with
        | ValueSome param when (match param with Param.Null -> false | _ -> true) ->
            let permitted = c.Params.Permit(Params.permitKeys [ "attachment" ])
            { MessagesController.MessageParamsNone with Attachment = MessagesController.attachmentAssignment permitted }
        | _ ->
            { MessagesController.MessageParamsNone with Body = Some(rawRequestBody c) }

    /// `X-Total-Count`, and a `Link` to the next page when there is one.
    let private setPaginationHeaders (c: Ctx) (room: Room) (messages: Message list) : Task<Result<unit, Error>> =
        act {
            let after =
                match c.Params.Get "after" with
                | ValueSome param -> param.IsPresent
                | ValueNone -> false
            let roomId = room.Id
            let first, last = List.tryHead messages, List.tryLast messages
            // Offloaded: the count reads every message in the room.
            let! count, nextPage =
                c.App.ReadOffloaded(fun conn ->
                    let count = Message.countInRoom conn roomId
                    let nextPage =
                        match first, last with
                        | Some _, Some last when after ->
                            if Message.existsAfter conn roomId last then Some("after", last.Id) else None
                        | Some first, Some _ -> if Message.existsBefore conn roomId first then Some("before", first.Id) else None
                        | _ -> None
                    count, nextPage)
            c.SetHeader("x-total-count", string count)
            match nextPage with
            | Some(key, id) ->
                let botKey =
                    match c.ParamStr "bot_key" with
                    | null -> ""
                    | key -> key
                let url = c.UrlFor $"{Routes.roomBotMessages roomId botKey}?{key}={id}"
                c.SetHeader("link", $"<{url}>; rel=\"next\"")
            | None -> ()
        }

    /// `render :show` (`messages/by_bots/show.json.jbuilder`).
    let private renderShow (c: Ctx) (message: Message) : Task<Result<Response, Error>> =
        act {
            let baseUrl = c.UrlFor ""
            let! (body: string) =
                MessagesController.present c (fun presenter ->
                    MessagesJson.byBotsShow (presenter.MessageJson(message, baseUrl)))
            return c.Render(Status.Ok, Format.Json, body)
        }

    let index (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c before
            let! (room: Room) = setRoom c
            let! (messages: Message list) = MessagesController.findPagedMessages c room
            do! setPaginationHeaders c room messages
            do! c.RespondTo [ Format.Json ] |> Result.map ignore
            let baseUrl = c.UrlFor ""
            let! (body: string) =
                MessagesController.present c (fun presenter ->
                    let jsons = messages |> List.map (fun m -> presenter.MessageJson(m, baseUrl))
                    MessagesJson.byBotsIndex jsons)
            return c.Render(Status.Ok, Format.Json, body)
        }

    let create (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c before
            let! (room: Room) = setRoom c
            do! ensureBodyOrAttachmentPresent c
            // MessagesController#create
            let attributes = messageParams c
            let! (message: Message) = MessagesController.createMessage c room attributes
            do! MessagesController.broadcastCreate c room message
            do! MessagesController.deliverWebhooksToBots c room message

            let location = c.UrlFor(Routes.message message.Id)
            return! c.HeadWithLocation(Status.Created, location)
        }

    let update (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c before
            let! (room: Room) = setRoom c
            let! (message: Message) = MessagesController.setMessage c room
            do! MessagesController.ensureCanAdminister c message
            // MessagesController#update
            let attributes = messageParams c
            let! (message: Message) = MessagesController.updateMessage c message attributes
            do! MessagesController.broadcastReplace c room message
            let! format = c.RespondTo [ Format.Html; Format.Json ]
            if format.Is "json" then
                return! renderShow c message
            else
                return! c.RedirectTo(c.UrlFor(Routes.roomMessage room.Id message.Id))
        }

    let destroy (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c before
            let! (room: Room) = setRoom c
            let! (message: Message) = MessagesController.setMessage c room
            do! MessagesController.ensureCanAdminister c message
            do! MessagesController.destroyMessage c room message
            return c.Head Status.NoContent
        }
