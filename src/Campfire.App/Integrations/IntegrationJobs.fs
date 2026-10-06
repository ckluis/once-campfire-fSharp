// Port of rust/crates/campfire/src/integrations/jobs.rs
//
// The jobs integrations perform for the app's job runner: `Room::PushMessageJob`
// (reference/app/jobs/room/push_message_job.rb) and `Bot::WebhookJob` (reference/app/jobs/bot/webhook_job.rb),
// including what `Webhook#deliver` does with a reply (create the bot's message, process an attachment,
// `broadcast_create`).
namespace Campfire.App.Integrations

open System
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Campfire.App
open Campfire.App.Controllers
open Campfire.App.Presenters
open Campfire.Db
open Campfire.Routes
open Campfire.Views

module IntegrationJobs =
    let private failure (error: DbError) : string = DbError.display error

    let private kitFailure (error: Campfire.Kit.Error) : string = sprintf "%A" error

    /// `Room::PushMessageJob#perform(room, message)`: `Room::MessagePusher.new(room:, message:).push`, unless Web
    /// Push is off.
    let private pushMessage (app: AppState) (event: Event) : Task<Result<unit, string>> =
        task {
            match event, app.WebPush with
            | Event.PushMessage(_, messageId), Some pool ->
                // Offloaded: it encrypts a notification for every subscriber.
                match!
                    app.Db.ReadOffloaded(fun conn ->
                        let message = Message.find conn messageId
                        WebPushPool.pushMessage pool conn app.Db.Env.RichText message (app.Db.Env.Now()) |> ignore)
                with
                | Ok() -> return Ok()
                | Error error -> return Error(failure error)
            | _ -> return Ok()
        }

    /// config/initializers/web_push.rb (`config.x.web_push_pool`): the pool, whose invalid subscription handler
    /// destroys the subscription (`Push::Subscription.find_by(id:)&.destroy`). `None`, and Web Push is off, when the
    /// VAPID keys are missing or invalid.
    let webPushPool (config: AppConfig) (db: Database) (logger: ILogger) : WebPushPool option =
        match VapidConfig.FromConfig config with
        | Error Missing ->
            logger.LogWarning("Web Push is off: {Reason}", VapidError.message Missing)
            None
        | Error error ->
            logger.LogError("Web Push is off: {Reason}", VapidError.message error)
            None
        | Ok vapid ->
            let invalidSubscriptionHandler (id: int64) : Result<unit, string> =
                match db.WriteBlocking(fun tx -> PushSubscription.destroy tx (PushSubscription.find tx.Conn id)) with
                | Ok()
                | Error(RecordNotFound _) -> Ok()
                | Error error -> Error(failure error)
            Some(WebPushPool(Network.system (), vapid, invalidSubscriptionHandler, logger))

    /// `room.messages.create!(body: text, creator: user)`: the text is assigned to the rich text body, which stores
    /// it canonicalized (as `MessagesController` does; there's no request host in a job).
    let private createTextReply (app: AppState) (room: Room) (bot: User) (text: string) : Task<Result<Message, string>> =
        task {
            match! MessagesController.canonicalizeBody app text None with
            | Error error -> return Error(kitFailure error)
            | Ok body ->
                let newMessage: NewMessage =
                    { RoomId = room.Id
                      CreatorId = bot.Id
                      ClientMessageId = None
                      Body = Some body
                      AttachmentBlobId = None }
                match! app.Db.Write(fun tx -> Message.create tx newMessage) with
                | Ok message -> return Ok message
                | Error error -> return Error(failure error)
        }

    /// `ActiveStorage::Blob.create_and_upload!` (its own save), then
    /// `room.messages.create_with_attachment!(attachment:, creator: user)`, which processes the attachment.
    let private createAttachmentReply (app: AppState) (room: Room) (bot: User) (attachment: WebhookAttachment) : Task<Result<Message, string>> =
        task {
            let! staged = Task.Run(fun () -> WebhookClient.stageBlob app.Storage attachment)
            match staged with
            | Error error -> return Error(sprintf "%A" error)
            | Ok staged ->
                match! app.Db.Write(fun tx -> MessagesController.saveStaged tx staged) with
                | Error error -> return Error(failure error)
                | Ok blob ->
                    let newMessage: NewMessage =
                        { RoomId = room.Id
                          CreatorId = bot.Id
                          ClientMessageId = None
                          Body = None
                          AttachmentBlobId = Some blob.Id }
                    match! app.Db.Write(fun tx -> Message.create tx newMessage) with
                    | Error error -> return Error(failure error)
                    | Ok message ->
                        match! MessagesController.processAttachment app blob with
                        | Error error -> return Error(kitFailure error)
                        | Ok() ->
                            match! app.Db.Read(fun conn -> Message.find conn message.Id) with
                            | Ok message -> return Ok message
                            | Error error -> return Error(failure error)
        }

    /// `message.broadcast_create`, rendered without a request (`ApplicationController.renderer`).
    let private broadcastCreate (app: AppState) (room: Room) (message: Message) : Task<Result<unit, string>> =
        task {
            let! result =
                app.Db.Read(fun conn ->
                    let presenter = Presenter(conn, app, None)
                    let view = presenter.Message message
                    let account = Account.first conn
                    let html = Page.renderDetached app account (fun ctx -> MessagesCached.message ctx view)
                    let partials = { Rendered.empty with Message = Some html }
                    app.Broadcasts.MessageCreate(conn, room, message, Rendered.partials partials))
            return result |> Result.mapError failure
        }

    /// `Bot::WebhookJob#perform(bot, message)`: `bot.deliver_webhook(message)`, i.e. `webhook.deliver(message)`, then
    /// the reply.
    let private deliverWebhook (app: AppState) (event: Event) : Task<Result<unit, string>> =
        task {
            match event with
            | Event.DeliverWebhook(botId, messageId) ->
                let db = app.Db
                match!
                    db.Read(fun conn ->
                        let bot = User.find conn botId
                        let message = Message.find conn messageId
                        let room = Room.find conn message.RoomId
                        match Webhook.findByUser conn botId with
                        | None -> None
                        | Some webhook ->
                            let payload =
                                Webhook.payload
                                    conn
                                    db.Env.RichText
                                    webhook
                                    message
                                    (Routes.roomBotMessages room.Id (User.botKey bot))
                                    (Routes.roomAtMessage room.Id message.Id)
                            Some(bot, room, webhook.Url, payload))
                with
                | Error error -> return Error(failure error)
                | Ok None -> return Error "undefined method 'deliver' for nil (the bot has no webhook)"
                | Ok(Some(bot, room, url, payload)) ->
                    match! WebhookClient.deliver (Network.system ()) (defaultArg url "") payload with
                    | Error error -> return Error(WebhookError.message error)
                    | Ok delivery ->
                        let! message =
                            task {
                                match delivery.Reply with
                                | ReplyNone -> return Ok None
                                | ReplyText text ->
                                    let! created = createTextReply app room bot text
                                    return created |> Result.map Some
                                | ReplyAttachment attachment ->
                                    let! created = createAttachmentReply app room bot attachment
                                    return created |> Result.map Some
                            }
                        match message with
                        | Error error -> return Error error
                        | Ok None -> return Ok()
                        | Ok(Some message) -> return! broadcastCreate app room message
            | _ -> return Ok()
        }

    /// Registers the handlers for `Event.PushMessage` and `Event.DeliverWebhook`.
    let registerJobs (registry: Registry<AppState>) : unit =
        registry.Handle(JobKind.PushMessage, pushMessage)
        registry.Handle(JobKind.DeliverWebhook, deliverWebhook)
