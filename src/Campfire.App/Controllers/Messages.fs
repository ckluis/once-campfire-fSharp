// Port of rust/crates/campfire/src/controllers/messages.rs
//
// `MessagesController` (reference/app/controllers/messages_controller.rb), including the multipart
// attachment upload the composer's `FileUploader` posts here, plus what `Messages::ByBotsController`
// reuses: creating a message (with `process_attachment`), delivering webhooks and the broadcasts.
namespace Campfire.App.Controllers

open System
open System.Threading.Tasks
open Campfire.App
open Campfire.App.Channels
open Campfire.Db
open Campfire.Kit
open Campfire.RichText
open Campfire.Routes
open Campfire.Ruby
open Campfire.Storage
open Campfire.Views
open Campfire.App.Presenters

/// What `create_with_attachment!`/`update!` receive.
type MessageParams =
    { Body: string option
      /// `attachment=`: `None` when the key wasn't given.
      Attachment: Assignment<Upload> option
      ClientMessageId: string option }

module MessagesController =
    module IndexPage = Campfire.Views.Templates.Messages.Index
    module ShowPage = Campfire.Views.Templates.Messages.Show
    module EditPage = Campfire.Views.Templates.Messages.Edit
    module CreateStream = Campfire.Views.Templates.Messages.CreateTurboStream
    module DestroyStream = Campfire.Views.Templates.Messages.DestroyTurboStream
    module RoomNotFoundPage = Campfire.Views.Templates.Messages.RoomNotFound
    module PresentationPartial = Campfire.Views.Templates.Messages._Presentation

    let private indexSize = RenderSize()

    /// Stands in for the digest `ETagWithTemplateDigest` adds for `messages/index` (only the ETag's
    /// shape has to match the reference).
    [<Literal>]
    let private TemplateDigestIndex = "messages/index"

    let MessageParamsNone: MessageParams =
        { Body = None
          Attachment = None
          ClientMessageId = None }

    /// Runs `f` with a presenter on a reader connection.
    let present (c: Ctx) (f: Presenter -> 'T) : Task<Result<'T, Error>> =
        let app = c.App
        let requestHost = Some c.Request.Host
        app.Read(fun conn ->
            let presenter = Presenter(conn, app, requestHost)
            // The Jbuilder partials (`json.cache!`) read the fragment cache on this thread.
            FragmentCache.withCache app.FragmentCache (fun () -> f presenter))

    // --- Before-actions and params ---------------------------------------------------------------

    /// `@room.messages.find(params[:id])`
    let setMessage (c: Ctx) (room: Room) : Task<Result<Message, Error>> =
        match (match c.ParamStr "id" with null -> None | id -> Ruby.integerCast id) with
        | None -> Task.FromResult(Error NotFound)
        | Some id ->
            let roomId = room.Id
            c.App.Read(fun conn -> Message.findInRoom conn roomId id)

    /// `head :forbidden unless Current.user.can_administer?(@message)`
    let ensureCanAdminister (c: Ctx) (message: Message) : Result<unit, Error> =
        match Concerns.requireCurrentUser c with
        | Error e -> Error e
        | Ok user ->
            if User.canAdminister user (Some message.CreatorId) false then
                Ok()
            else
                halt (Concerns.head Status.Forbidden)

    /// What assigning the permitted `attachment` does: an upload replaces the attachment, nil or ""
    /// removes it (`Attached::Changes::DeleteOne`), anything else raises.
    let attachmentAssignment (permitted: ParamMap) : Assignment<Upload> option =
        match Assignment.fromParams permitted "attachment" with
        | Unchanged -> None
        | assignment -> Some assignment

    /// A permitted string attribute as `Param#as_str` reads it.
    let private text (permitted: ParamMap) (key: string) : string option =
        match permitted.Get key with
        | ValueSome(Param.Str value) -> Some value
        | _ -> None

    /// `params.require(:message).permit(:body, :attachment, :client_message_id)`
    let messageParams (c: Ctx) : Result<MessageParams, Error> =
        c.Params.Require "message"
        |> Result.map (fun message ->
            let permitted = message.Permit(Params.permitKeys [ "body"; "attachment"; "client_message_id" ])
            { Body = text permitted "body"
              Attachment = attachmentAssignment permitted
              ClientMessageId = text permitted "client_message_id" })

    /// `@room.messages.find(params[:before])` and friends (`find_paged_messages`).
    let findPagedMessages (c: Ctx) (room: Room) : Task<Result<Message list, Error>> =
        let present (key: string) : int64 option option =
            match c.Params.Get key with
            | ValueSome param when param.IsPresent -> Some(match param.AsStr with null -> None | value -> Ruby.integerCast value)
            | _ -> None
        let before, after = present "before", present "after"
        let roomId = room.Id
        let findInRoom (id: int64 option) (conn: Conn) =
            match id with
            | Some id -> Message.findInRoom conn roomId id
            | None -> Err.fail (RecordNotFound "Message")
        c.App.Read(fun conn ->
            match before, after with
            | Some before, _ -> Message.pageBefore conn roomId (findInRoom before conn)
            | None, Some after -> Message.pageAfter conn roomId (findInRoom after conn)
            | None, None -> Message.lastPage conn roomId)

    // --- Creating, updating, destroying -----------------------------------------------------------

    /// Assigning something that isn't an upload, a signed blob id, nil or "".
    let private invalidAttachment () : Error = Internal(exn "Could not find or build blob: expected attachable")

    /// Inserts a staged blob's row, keeping its file once the transaction commits.
    let saveStaged (tx: Tx) (staged: Staged) : Campfire.Storage.Blob =
        let blob = Common.raiseStorage (staged.Insert(tx.Conn.Raw, (tx.Now()).ToDateTimeOffset()))
        ActiveStorage.keepAfterCommit tx staged
        blob

    /// Assigning a String to a rich text attribute stores the canonicalized content
    /// (`ActionText::Content.new(body, canonicalize: true).to_html`).
    let canonicalBody (conn: Conn) (app: AppState) (body: string) (requestHost: string option) : string =
        let resolver = DbResolver(conn, app.Secrets, app.Clock.Now())
        let ctx = resolver.RenderContext requestHost
        match Content.load body ctx with
        | Ok content -> Content.toHtml content
        | Error _ -> body

    /// `canonicalBody` on a reader, ahead of the write that stores it.
    let canonicalizeBody (app: AppState) (body: string) (requestHost: string option) : Task<Result<string, Error>> =
        app.Read(fun conn -> canonicalBody conn app body requestHost)

    /// `Blob#touch_attachments`: each attached record is touched (a message also touches its room).
    let private touchAttachmentRecords (tx: Tx) (blobId: int64) : unit =
        for (recordType, recordId) in Common.raiseStorage (Campfire.Storage.Blob.attachmentRecords tx.Conn.Raw blobId) do
            if recordType = "Message" then Message.touch tx (Message.find tx.Conn recordId) |> ignore

    /// `blob.analyze`: its `after_update` touches the attached records. The file is analyzed off the
    /// writer.
    let private analyzeAttachment (app: AppState) (blob: Campfire.Storage.Blob) : Task<Result<Campfire.Storage.Blob, Error>> =
        act {
            let! metadata = ActiveStorage.analyzedMetadata app blob
            return!
                app.Write(fun tx ->
                    let blob = Common.raiseStorage (Campfire.Storage.Blob.updateMetadata tx.Conn.Raw metadata blob)
                    touchAttachmentRecords tx blob.Id
                    blob)
        }

    /// `Message#process_attachment`: analyze the blob now (its `after_update` touches the message),
    /// then generate the video preview or the `:thumb` representation.
    let processAttachment (app: AppState) (blob: Campfire.Storage.Blob) : Task<Result<unit, Error>> =
        act {
            let! blob = analyzeAttachment app blob
            if Campfire.Storage.Blob.isVideo blob then
                // attachment.preview(format: :webp).processed
                let! (_: Campfire.Storage.Blob) = ActiveStorage.processedPreview app blob (Variation.formatOnly "webp")
                return ()
            elif Campfire.Storage.Blob.isRepresentable blob then
                // attachment.representation(:thumb).processed
                let thumb = Variation.resizeToLimit 1200L 800L None
                let! (_: Campfire.Storage.Blob) = ActiveStorage.processedRepresentation app blob thumb
                return ()
            else
                return ()
        }

    let private stageUpload (app: AppState) (upload: Upload) : Task<Result<Staged option, Error>> =
        task {
            match! Upload.stage app upload with
            | Ok staged -> return Ok(Some staged)
            | Error e -> return Error e
        }

    /// `@room.messages.create_with_attachment!(attributes)`: the message (with its uploaded blob, in
    /// one transaction), then `process_attachment`. The upload's file is copied into storage and the
    /// body canonicalized before the transaction, so the writer only inserts rows.
    let createMessage (c: Ctx) (room: Room) (attributes: MessageParams) : Task<Result<Message, Error>> =
        act {
            let! (user: User) = Concerns.requireCurrentUser c
            let app = c.App
            let roomId = room.Id
            let! (attachment: Staged option) =
                match attributes.Attachment with
                | Some(Create upload) -> stageUpload app upload
                | Some Invalid -> Task.FromResult(Error(invalidAttachment ()))
                | _ -> Task.FromResult(Ok None)
            let! (body: string option) =
                match attributes.Body with
                | Some body ->
                    task {
                        match! canonicalizeBody app body (Some c.Request.Host) with
                        | Ok body -> return Ok(Some body)
                        | Error e -> return Error e
                    }
                | None -> Task.FromResult(Ok None)
            let! (message: Message), (blob: Campfire.Storage.Blob option) =
                app.Write(fun tx ->
                    let blob = attachment |> Option.map (saveStaged tx)
                    let message =
                        Message.create
                            tx
                            { RoomId = roomId
                              CreatorId = user.Id
                              ClientMessageId = attributes.ClientMessageId
                              Body = body
                              AttachmentBlobId = blob |> Option.map (fun blob -> blob.Id) }
                    message, blob)
            // Without an attachment, `message` is the row as stored: nothing after the commit writes to it,
            // and Rails answers with the same record (`create!(attributes).tap(&:process_attachment)`,
            // reference/app/models/message/attachment.rb). Analyzing an attachment touches the message, so
            // then it's read back.
            match blob with
            | None -> return message
            | Some blob ->
                let id = message.Id
                do! processAttachment app blob
                return! app.Read(fun conn -> Message.find conn id)
        }

    /// `@message.update!(message_params)`. A new attachment replaces the old one (whose blob is purged
    /// later) without `process_attachment`: the blob is only analyzed, by `ActiveStorage::AnalyzeJob`
    /// after commit (verified against the reference with a bot's `PUT` and `attachment`).
    let updateMessage (c: Ctx) (message: Message) (attributes: MessageParams) : Task<Result<Message, Error>> =
        act {
            let app = c.App
            // None: no change; Some None: remove; Some(Some staged): replace.
            let! (attachment: Staged option option) =
                match attributes.Attachment with
                | Some Invalid -> Task.FromResult(Error(invalidAttachment ()))
                | Some(Create upload) ->
                    task {
                        match! stageUpload app upload with
                        | Ok staged -> return Ok(Some staged)
                        | Error e -> return Error e
                    }
                | Some _ -> Task.FromResult(Ok(Some None))
                | None -> Task.FromResult(Ok None)
            let! (body: string option) =
                match attributes.Body with
                | Some body ->
                    task {
                        match! canonicalizeBody app body (Some c.Request.Host) with
                        | Ok body -> return Ok(Some body)
                        | Error e -> return Error e
                    }
                | None -> Task.FromResult(Ok None)
            let! (id: int64), (blob: Campfire.Storage.Blob option) =
                app.Write(fun tx ->
                    let mutable message = message
                    match body with
                    | Some body -> message <- Message.updateBody tx message body
                    | None -> ()
                    let attachmentGiven = attachment.IsSome
                    let blob = attachment |> Option.flatten |> Option.map (saveStaged tx)
                    if attachmentGiven then
                        message <- Message.replaceAttachment tx message (blob |> Option.map (fun blob -> blob.Id))
                    message.Id, blob)
            match blob |> Option.filter (fun blob -> not (Campfire.Storage.Blob.isAnalyzed blob)) with
            | Some blob ->
                app.Jobs.PerformLater(
                    "ActiveStorage::AnalyzeJob",
                    fun () ->
                        task {
                            match! analyzeAttachment app blob with
                            | Ok _ -> return Ok()
                            | Error e -> return Error(sprintf "%A" e)
                        }
                )
            | None -> ()
            return! app.Read(fun conn -> Message.find conn id)
        }

    /// `@message.destroy` then `@message.broadcast_remove`.
    let destroyMessage (c: Ctx) (room: Room) (message: Message) : Task<Result<unit, Error>> =
        act {
            do! c.App.Write(fun tx -> Message.destroy tx message)
            c.App.Broadcasts.MessageRemove(room, message)
        }

    // --- Broadcasts and webhooks -------------------------------------------------------------------

    /// `@message.broadcast_create`: the message partial appended to the room, then the unread pings.
    let broadcastCreate (c: Ctx) (room: Room) (message: Message) : Task<Result<unit, Error>> =
        let app = c.App
        let baseUrl = Page.rendererBaseUrl c
        app.Read(fun conn ->
            let presenter = Presenter(conn, app, None)
            let view = presenter.Message message
            let account = Account.first conn
            let html = Page.renderDetachedAt app account baseUrl (fun ctx -> MessagesCached.message ctx view)
            let partials = { Rendered.empty with Message = Some html }
            app.Broadcasts.MessageCreate(conn, room, message, Rendered.partials partials))

    /// `broadcast_replace_to @room, :messages, target: [ @message, :presentation ], partial:
    /// "messages/presentation", attributes: { maintain_scroll: true }`
    let broadcastReplace (c: Ctx) (room: Room) (message: Message) : Task<Result<unit, Error>> =
        let app = c.App
        let baseUrl = Page.rendererBaseUrl c
        app.Read(fun conn ->
            let presenter = Presenter(conn, app, None)
            let view = presenter.Message message
            let account = Account.first conn
            let html =
                Page.renderDetachedAt app account baseUrl (fun ctx -> Render.text (fun w -> PresentationPartial.render w ctx view))
            let partials = { Rendered.empty with MessagePresentation = Some html }
            app.Broadcasts.MessageReplace(room, message, Rendered.partials partials))

    /// `deliver_webhooks_to_bots`: every active bot in a direct room, else every mentioned active
    /// bot, except the message's creator.
    let deliverWebhooksToBots (c: Ctx) (room: Room) (message: Message) : Task<Result<unit, Error>> =
        act {
            let app = c.App
            let! (bots: User list) =
                app.Read(fun conn ->
                    let candidates =
                        if Room.isDirect room then Room.activeBots conn room else Message.mentionees conn app.Db.Env.RichText message
                    candidates
                    |> List.filter (fun (user: User) -> user.Role = Role.Bot && user.Status = Status.Active && user.Id <> message.CreatorId))
            if not bots.IsEmpty then
                // bot.deliver_webhook_later(@message)
                let messageId = message.Id
                do! app.Write(fun tx -> bots |> List.iter (fun bot -> User.deliverWebhookLater tx bot messageId))
        }

    // --- Rendering ---------------------------------------------------------------------------------

    /// `render action: :room_not_found` (inside the layout).
    let private renderRoomNotFound (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! c.RespondTo [ Format.Html ] |> Result.map ignore
            return! Page.contentInApplicationLayout c Status.Ok (fun w _ -> RoomNotFoundPage.render w)
        }

    // --- Actions -----------------------------------------------------------------------------------

    /// `index` (`layout false`): the page before/after a message, or the last page; 204 when empty.
    let index (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            let! (_: Membership), (room: Room) = Concerns.setRoom c
            let! (messages: Message list) = findPagedMessages c room
            if messages.IsEmpty then
                return c.Head Status.NoContent
            else
                // fresh_when @messages: the records' cache keys, their latest updated_at, and the template.
                let etag =
                    messages
                    |> List.map (fun m -> FragmentCache.cacheKeyWithVersion "messages" m.Id (m.UpdatedAt.ToDateTimeOffset()))
                    |> String.concat "/"
                let freshness =
                    { Freshness.Default with
                        Etag = etag
                        LastModified = ValueSome((messages |> List.map (fun m -> m.UpdatedAt) |> List.max).ToDateTimeOffset())
                        Template = TemplateDigestIndex }
                match c.FreshWhen freshness with
                | ValueSome notModified -> return notModified
                | ValueNone ->
                    do! c.RespondTo [ Format.Html ] |> Result.map ignore
                    let! views = present c (fun presenter -> presenter.Messages messages)
                    return!
                        Page.bare c Status.Ok Format.Html (fun ctx ->
                            Render.page 0 (fun w -> IndexPage.render w ctx views))
        }

    /// `create`: `set_room` runs inside the action, and a room that's gone renders `room_not_found`.
    let create (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            let! room =
                task {
                    match! Concerns.setRoom c with
                    | Ok(_, room) -> return Ok(Some room)
                    | Error NotFound -> return Ok None
                    | Error e -> return Error e
                }
            match room with
            | None -> return! renderRoomNotFound c
            | Some room ->
                let! attributes = messageParams c
                let! (message: Message) = createMessage c room attributes
                do! broadcastCreate c room message
                do! deliverWebhooksToBots c room message

                // The message partial comes out of the fragment cache `broadcastCreate` just filled
                // (`cache [ message, "presentation-v3" ]`), so it's the request-less rendering: no CSRF
                // tokens in its forms.
                do! c.RespondTo [ Format.TurboStream ] |> Result.map ignore
                let kind = PresenterSupport.roomKind room.RoomType
                let app = c.App
                let baseUrl = c.UrlFor ""
                let! html =
                    app.Read(fun conn ->
                        FragmentCache.withCache app.FragmentCache (fun () ->
                            let presenter = Presenter(conn, app, None)
                            let item = presenter.MessageItem message
                            let account = Account.first conn
                            Page.renderDetachedAt app account baseUrl (fun ctx ->
                                Render.plain (fun w -> CreateStream.render w ctx item kind))))
                return c.Render(Status.Ok, Format.TurboStream, ReadOnlyMemory<byte> html)
        }

    let show (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            let! (_: Membership), (room: Room) = Concerns.setRoom c
            let! (message: Message) = setMessage c room
            do! c.RespondTo [ Format.Html ] |> Result.map ignore
            let! view = present c (fun presenter -> presenter.Message message)
            return! Page.contentInApplicationLayout c Status.Ok (fun w ctx -> ShowPage.render w ctx view)
        }

    let edit (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            let! (_: Membership), (room: Room) = Concerns.setRoom c
            let! (message: Message) = setMessage c room
            do! ensureCanAdminister c message
            do! c.RespondTo [ Format.Html ] |> Result.map ignore
            let! edit =
                present c (fun presenter ->
                    let editableBodyHtml = presenter.EditableBody message
                    ({ EditableBodyHtml = editableBodyHtml
                       Message = presenter.Message message }: Campfire.Views.Messages.EditView))
            return! Page.contentInApplicationLayout c Status.Ok (fun w ctx -> EditPage.render w ctx edit)
        }

    let update (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            let! (_: Membership), (room: Room) = Concerns.setRoom c
            let! (message: Message) = setMessage c room
            do! ensureCanAdminister c message
            let! attributes = messageParams c
            let! (message: Message) = updateMessage c message attributes
            do! broadcastReplace c room message

            // respond_to html: redirect; json: `render :show`, which has no JSON template here.
            let! format = c.RespondTo [ Format.Html; Format.Json ]
            if format.Is "json" then
                return! Error(Internal(exn "Missing template messages/show"))
            else
                return! c.RedirectTo(c.UrlFor(Routes.roomMessage room.Id message.Id))
        }

    let destroy (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            let! (_: Membership), (room: Room) = Concerns.setRoom c
            let! (message: Message) = setMessage c room
            do! ensureCanAdminister c message
            do! destroyMessage c room message

            do! c.RespondTo [ Format.TurboStream ] |> Result.map ignore
            let! view = present c (fun presenter -> presenter.Message message)
            return!
                Page.bare c Status.Ok Format.TurboStream (fun _ ->
                    Render.page 0 (fun w -> DestroyStream.render w view))
        }
