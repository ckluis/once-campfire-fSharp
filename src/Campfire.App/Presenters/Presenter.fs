// Port of rust/crates/campfire/src/controllers/presenters.rs, all but what `Common.fs` has (the avatar
// paths, `user_summary`, `epoch_string`, `storage_error`).
//
// Maps database rows into the view models `Campfire.Views` renders: what the Rails views read off the
// records (`message.creator`, `room_display_name`, `message_presentation`, the Jbuilder partials)
// computed up front.
namespace Campfire.App.Presenters

open System
open System.Collections.Generic
open System.Text.RegularExpressions
open Campfire.App
open Campfire.Db
open Campfire.RailsCompat
open Campfire.RichText
open Campfire.Routes
open Campfire.Storage
open Campfire.Views
open Campfire.Views.Messages
open Campfire.Views.MessagesJson
open Campfire.Views.Rooms

module PresenterSupport =
    /// `Message::THUMBNAIL_MAX_WIDTH` / `THUMBNAIL_MAX_HEIGHT`.
    [<Literal>]
    let ThumbnailMaxWidth = 1200L

    [<Literal>]
    let ThumbnailMaxHeight = 800L

    /// `String#all_emoji?` (reference/lib/rails_ext/string.rb):
    /// `\A(\p{Emoji_Presentation}|\p{Extended_Pictographic}|️)+\z`.
    let allEmoji (text: string) : bool =
        let ranges = EmojiRanges.ranges
        let isEmoji (codePoint: int) =
            let mutable lo = 0
            let mutable hi = ranges.Length / 2 - 1
            let mutable found = false
            while not found && lo <= hi do
                let mid = (lo + hi) / 2
                if ranges[mid * 2 + 1] < codePoint then lo <- mid + 1
                elif ranges[mid * 2] > codePoint then hi <- mid - 1
                else found <- true
            found
        text.Length > 0 && (text.EnumerateRunes() |> Seq.forall (fun rune -> isEmoji rune.Value))

    let roomKind (roomType: RoomType) : RoomKind =
        match roomType with
        | RoomType.Open -> RoomKind.Open
        | RoomType.Closed -> RoomKind.Closed
        | RoomType.Direct -> RoomKind.Direct

    let userView (secrets: Secrets) (user: User) : UserView =
        { Id = user.Id
          Name = user.Name
          Title = User.title user
          AvatarUrl = Common.avatarPath secrets user }

    /// The digest in Jbuilder's `json.cache!` keys: the templates are fixed while the process runs.
    let jbuilderDigest: string =
        FragmentCache.digest [| "users/_user.json"; "messages/_message.json"; "messages/boosts/_boost.json" |]

    /// Jbuilder's `json.cache!` key: `jbuilder/views/<template>:<digest>/<record key>`. The JSON carries
    /// absolute URLs built from the request's `base_url`, which comes from its Host header, so the key does
    /// too: Rails' key doesn't, and one request with a forged Host fed its URLs to every bot.
    /// `record` is the table, id and `updated_at` of its `cache_key_with_version`.
    let jbuilderKey (key: KeyBuf) (template: string) (table: string) (id: int64) (updatedAt: DateTimeOffset) (baseUrl: string) : unit =
        key.Append "jbuilder/views/"
        key.Append template
        key.Append ':'
        key.Append jbuilderDigest
        key.Append '/'
        FragmentCache.pushCacheKeyWithVersion key table id updatedAt
        key.Append '/'
        key.Append baseUrl

    /// `users/_user.json.jbuilder`.
    let userJson (secrets: Secrets) (baseUrl: string) (user: User) : UserJson =
        { Id = user.Id
          Name = user.Name
          Role = Role.name user.Role
          AvatarUrl = baseUrl + Common.avatarPath secrets user }

    /// `users/_user.json.jbuilder` (`json.cache! user`).
    let cachedUserJson (secrets: Secrets) (baseUrl: string) (user: User) : UserJson =
        let key (key: KeyBuf) =
            jbuilderKey key "users/_user" "users" user.Id (user.UpdatedAt.ToDateTimeOffset()) baseUrl
        match FragmentCache.tryFetchValue key userCacheSize (fun () -> (Ok(userJson secrets baseUrl user): Result<UserJson, unit>)) with
        | Ok value -> value
        | Error() -> failwith "unreachable"

    /// `attachment.metadata[:width]`: an Integer for images, a Float for videos.
    let dimension (blob: Campfire.Storage.Blob) (name: string) : MessagesSupport.RubyNumber option =
        match blob.Metadata.TryGet name with
        | Some(Value.Int value) -> Some(MessagesSupport.RubyNumber.Int value)
        | Some(Value.Float value) -> Some(MessagesSupport.RubyNumber.Float value)
        | _ -> None

open PresenterSupport

/// Everything a page of messages needs, with the rows it looks up along the way remembered
/// (Rails preloads them with `with_creator`, `with_boosts` and friends). One per read: it holds the
/// reader's `Conn`, so it can't outlive it.
[<Sealed; NoComparison; NoEquality>]
type Presenter(conn: Conn, app: AppState, requestHost: string option) =
    let secrets = app.Secrets
    let storage = app.Storage
    let richText = app.Db.Env.RichText
    let now = app.Clock.Now()
    let users = Dictionary<int64, User>()
    let roomNames = Dictionary<int64, Room * string>()

    member _.Conn = conn
    member _.Secrets = secrets

    member _.Resolver: DbResolver = DbResolver(conn, secrets, now)

    member _.User(id: int64) : User =
        match users.TryGetValue id with
        | true, user -> user
        | _ ->
            let user = User.find conn id
            users[id] <- user
            user

    member this.UserView(id: int64) : UserView = userView secrets (this.User id)

    /// `room_display_name(room, for_user:)`.
    member _.RoomDisplayName(room: Room, forUser: User option) : string =
        let names =
            if Room.isDirect room then
                Room.users conn room
                |> List.filter (fun user ->
                    match forUser with
                    | Some forUser -> forUser.Id <> user.Id
                    | None -> true)
                |> List.map (fun user -> user.Name)
            else
                []
        Campfire.Views.Rooms.roomDisplayName room.Name (Room.isDirect room) names (forUser |> Option.map (fun user -> user.Name))

    member this.RoomView(room: Room, forUser: User) : RoomView =
        { Id = room.Id
          Kind = roomKind room.RoomType
          Name = room.Name
          DisplayName = this.RoomDisplayName(room, Some forUser) }

    /// `message.room` with `room_display_name(message.room, for_user: nil)`.
    member private this.RoomAndName(roomId: int64) : Room * string =
        match roomNames.TryGetValue roomId with
        | true, entry -> entry
        | _ ->
            let room = Room.find conn roomId
            let name = this.RoomDisplayName(room, None)
            roomNames[roomId] <- (room, name)
            room, name

    member _.PlainTextBody(message: Message) : string = Message.plainTextBody conn richText message

    /// `render @messages`: each message's cached fragment when the current store has its version
    /// (`cache [ message, "presentation-v3" ]` wraps the whole partial, so Rails evaluates none of
    /// it on a hit), else its view.
    member this.Messages(messages: Message list) : MessageItem list = messages |> List.map this.MessageItem

    /// `render message`, as `Messages` does it.
    member this.MessageItem(message: Message) : MessageItem =
        match cachedMessageFragment message.Id (message.UpdatedAt.ToDateTimeOffset()) with
        | null -> MessageItem.View(this.Message message)
        | html -> MessageItem.Cached(message.ClientMessageId, message.RoomId, html)

    /// A message as `messages/_message` shows it.
    member this.Message(message: Message) : MessageView =
        let _, roomName = this.RoomAndName message.RoomId
        try
            this.RenderableMessage(message, roomName)
        with DbException(RecordNotFound _) ->
            // `message_tag` rescues whatever its block raises, e.g. `avatar_tag message.creator`
            // for a creator that's gone (nil), and renders `messages/_unrenderable` instead.
            { Id = message.Id
              ClientMessageId = message.ClientMessageId
              RoomId = message.RoomId
              RoomName = roomName
              Creator =
                { Id = message.CreatorId
                  Name = ""
                  Title = ""
                  AvatarUrl = "" }
              CreatedAt = message.CreatedAt.ToDateTimeOffset()
              UpdatedAt = message.UpdatedAt.ToDateTimeOffset()
              AllEmoji = false
              Content = MessageContent.Unrenderable
              Boosts = [] }

    member private this.RenderableMessage(message: Message, roomName: string) : MessageView =
        let plainText = this.PlainTextBody message
        { Id = message.Id
          ClientMessageId = message.ClientMessageId
          RoomId = message.RoomId
          RoomName = roomName
          Creator = this.UserView message.CreatorId
          CreatedAt = message.CreatedAt.ToDateTimeOffset()
          UpdatedAt = message.UpdatedAt.ToDateTimeOffset()
          AllEmoji = allEmoji plainText
          Content = this.Content(message, plainText)
          Boosts = this.Boosts message }

    /// `message.boosts.ordered`.
    member this.Boosts(message: Message) : BoostView list =
        Boost.forMessageOrdered conn message.Id |> List.map this.Boost

    member this.Boost(boost: Boost) : BoostView =
        { Id = boost.Id
          UpdatedAt = boost.UpdatedAt.ToDateTimeOffset()
          MessageId = boost.MessageId
          Content = boost.Content
          AllEmoji = allEmoji boost.Content
          Booster = this.UserView boost.BoosterId }

    /// `message.content_type`, with what `message_presentation` shows for it.
    member private this.Content(message: Message, plainText: string) : MessageContent =
        let body = defaultArg (Message.bodyHtml conn message) ""
        let ctx = this.Resolver.RenderContext requestHost
        // `message_tag` evaluates `message.plain_text_body` first; where that raises, it rescues
        // and renders `messages/_unrenderable`, unless logging the exception raises again (a
        // message that isn't UTF-8): then the page fails (verified against the reference).
        match ActionText.toPlainText body ctx with
        | Error(RenderError.Unrenderable error) ->
            Err.fail (DbError.other $"message_tag's rescue raised logging {error}")
        | Error _ -> MessageContent.Unrenderable
        | Ok _ ->
            match this.Attachment message with
            | Some attachment -> MessageContent.Attachment attachment
            | None ->
                match Message.soundIn plainText with
                | Some sound ->
                    MessageContent.Sound
                        { Url = Campfire.Assets.Assets.assetPath (Sound.assetPath sound)
                          Image =
                            sound.Image
                            |> Option.map (fun image ->
                                { Src = Campfire.Assets.Assets.imagePath (SoundImage.assetPath image)
                                  Width = uint32 image.Width
                                  Height = uint32 image.Height })
                          Text = sound.Text }
                | None ->
                    match ActionText.presentMessage body ctx with
                    | Presentation.Html html -> MessageContent.Text html
                    | Presentation.Unrenderable -> MessageContent.Unrenderable

    /// `message.attachment` as `Messages::AttachmentPresentation` needs it.
    member private this.Attachment(message: Message) : AttachmentView option =
        match Attachments.attachedBlob conn "Message" message.Id "attachment" with
        | None -> None
        | Some blob ->
            let verifier = storage.Verifier
            let preview =
                if Campfire.Storage.Blob.isPreviewable blob || Campfire.Storage.Blob.isVariable blob then
                    if Campfire.Storage.Blob.isVideo blob then
                        // `attachment.preview(format: :webp, resize_to_limit: [...])`
                        let poster =
                            Variation.create
                                [ "format", Marshal.Value.Symbol "webp"
                                  "resize_to_limit", Marshal.Value.Array [ Marshal.Value.Int ThumbnailMaxWidth; Marshal.Value.Int ThumbnailMaxHeight ] ]
                        AttachmentPreview.Video(Paths.representationRedirectPath verifier blob poster)
                    else
                        AttachmentPreview.Image(this.ThumbPath blob)
                else
                    AttachmentPreview.File
            let (Filename filename) = blob.Filename
            Some
                { Filename = filename
                  BlobPath = Paths.blobRedirectPath verifier blob None
                  DownloadPath = Paths.blobRedirectPath verifier blob (Some "attachment")
                  Preview = preview
                  Width = dimension blob "width"
                  Height = dimension blob "height" }

    /// `polymorphic_url(attachment.representation(:thumb), only_path: true)`.
    member private _.ThumbPath(blob: Campfire.Storage.Blob) : string =
        let thumb = Variation.resizeToLimit ThumbnailMaxWidth ThumbnailMaxHeight None
        let variation =
            if Campfire.Storage.Blob.isPreviewable blob then thumb else Common.raiseStorage (Storage.variationFor blob thumb)
        Paths.representationRedirectPath storage.Verifier blob variation

    /// `message.body.to_s`: the stored rich text rendered inside its layout.
    member this.BodyHtml(message: Message) : string =
        match Message.bodyHtml conn message with
        | None -> ""
        | Some body ->
            let ctx = this.Resolver.RenderContext requestHost
            match Content.load body ctx |> Result.bind (fun content -> Content.toRenderedHtmlWithLayout content ctx) with
            | Ok html -> html
            | Error _ -> ""

    /// `editable_body(message)` as the editor's `value`.
    member this.EditableBody(message: Message) : string =
        let body = defaultArg (Message.bodyHtml conn message) ""
        let ctx = this.Resolver.RenderContext requestHost
        // An `Error` is where the edit page raises in Rails (a missing attachment, say).
        match ActionText.editableValue body ctx with
        | Ok value -> defaultArg value ""
        | Error error -> Err.fail (DbError.other $"editable_body raised: {error.Message}")

    /// `messages/_message.json.jbuilder` (`json.cache! message`).
    member this.MessageJson(message: Message, baseUrl: string) : MessageJson =
        let key (key: KeyBuf) =
            jbuilderKey key "messages/_message" "messages" message.Id (message.UpdatedAt.ToDateTimeOffset()) baseUrl
        match FragmentCache.tryFetchValue key messageCacheSize (fun () -> (Ok(this.RenderMessageJson(message, baseUrl)): Result<MessageJson, unit>)) with
        | Ok value -> value
        | Error() -> failwith "unreachable"

    member private this.RenderMessageJson(message: Message, baseUrl: string) : MessageJson =
        { Id = message.Id
          CreatedAt = MessagesSupport.jsonTime (message.CreatedAt.ToDateTimeOffset())
          Body =
            { PlainText = this.PlainTextBody message
              Html = this.BodyHtml message }
          Creator = cachedUserJson secrets baseUrl (this.User message.CreatorId)
          Room = { Id = message.RoomId }
          Url = baseUrl + Routes.roomMessage message.RoomId message.Id }

    /// `messages/boosts/_boost.json.jbuilder` (`json.cache! boost`).
    member this.BoostJson(boost: Boost, message: Message, baseUrl: string) : BoostJson =
        let key (key: KeyBuf) =
            jbuilderKey key "messages/boosts/_boost" "boosts" boost.Id (boost.UpdatedAt.ToDateTimeOffset()) baseUrl
        match FragmentCache.tryFetchValue key boostCacheSize (fun () -> (Ok(this.RenderBoostJson(boost, message, baseUrl)): Result<BoostJson, unit>)) with
        | Ok value -> value
        | Error() -> failwith "unreachable"

    member private this.RenderBoostJson(boost: Boost, message: Message, baseUrl: string) : BoostJson =
        { Id = boost.Id
          Content = boost.Content
          CreatedAt = MessagesSupport.jsonTime (boost.CreatedAt.ToDateTimeOffset())
          Booster = cachedUserJson secrets baseUrl (this.User boost.BoosterId)
          Message =
            { Id = boost.MessageId
              Url = baseUrl + Routes.roomMessage message.RoomId message.Id } }

    /// `users/sidebars/rooms/_shared` locals.
    member _.SidebarRoom(room: Room) : Campfire.Views.Users.SidebarRoom =
        { Id = room.Id
          ParamKey = RoomKind.paramKey (roomKind room.RoomType)
          Name = defaultArg room.Name ""
          Unread = false }

    /// `users/sidebars/rooms/_direct` locals for `membership`.
    member this.SidebarDirect(membership: Membership) : Campfire.Views.Users.SidebarDirect =
        let room = Room.find conn membership.RoomId
        let members =
            match Room.users conn room |> List.filter (fun user -> user.Id <> membership.UserId) with
            | [] -> [ this.User membership.UserId ]
            | others -> others
        { RoomId = room.Id
          Unread = Membership.unread membership
          UpdatedAtEpoch = Common.epochString room.UpdatedAt
          Members = members |> List.map (Common.userSummary secrets)
          MembershipId = membership.Id
          MembershipUpdatedAt = membership.UpdatedAt.ToDateTimeOffset() }

    member _.UserSummary(user: User) : Campfire.Views.Users.UserSummary = Common.userSummary secrets user
