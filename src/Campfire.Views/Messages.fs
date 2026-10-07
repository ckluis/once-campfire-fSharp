// Port of rust/crates/views/src/messages.rs (the view-model; the templates, and the functions that
// render them into the fragment cache, are under Templates/Messages)
/// Views for `reference/app/views/messages`, plus `MessagesHelper`, `Messages::AttachmentPresentation`
/// and the boost partials: what the message views show of a user, a room, a message and a boost,
/// and the keys their cached fragments live under.
module Campfire.Views.Messages

open System
open Campfire.RailsCompat
open Campfire.Routes
open Campfire.Views.MessagesSupport

/// What the message views show of a user: `avatar_tag` and the author heading.
type UserView =
    { Id: int64
      Name: string
      /// `User#title`: name and bio joined by " – ".
      Title: string
      /// `fresh_user_avatar_path(user)`.
      AvatarUrl: string }

    member this.Path: string = Routes.user this.Id

type RoomKind =
    | Open
    | Closed
    | Direct

module RoomKind =
    /// `Rooms::Open.model_name.param_key`, the stem of `dom_id(room)`.
    let paramKey (kind: RoomKind) : string =
        match kind with
        | Open -> "rooms_open"
        | Closed -> "rooms_closed"
        | Direct -> "rooms_direct"

    let isDirect (kind: RoomKind) : bool = kind = Direct

/// `dom_id(room)` / `dom_id(room, prefix)`.
let roomDomId (kind: RoomKind) (id: int64) (prefix: string) : string =
    if prefix = "" then $"{RoomKind.paramKey kind}_{id}" else $"{prefix}_{RoomKind.paramKey kind}_{id}"

/// `dom_id(room, prefix)` written into a template: `prefix` is the template's literal with its trailing
/// underscore ("messages_"), or empty for none; no string is made for the id.
let writeRoomDomId (w: Out) (prefix: byte[]) (kind: RoomKind) (id: int64) : unit =
    w.Lit prefix
    w.Raw(RoomKind.paramKey kind)
    w.Byte(byte '_')
    w.Int id

/// A `/play <name>` message's `Sound`.
type SoundImage =
    { /// `image_path(image.asset_path)`.
      Src: string
      Width: uint32
      Height: uint32 }

type SoundView =
    { /// `asset_path(sound.asset_path)`, the digested mp3.
      Url: string
      Image: SoundImage option
      Text: string option }

type AttachmentPreview =
    /// `attachment.video?`: `url_for(attachment.preview(format: :webp, resize_to_limit: ...))`.
    | Video of posterUrl: string
    /// Otherwise previewable or variable: `polymorphic_url(attachment.representation(:thumb), only_path: true)`.
    | Image of thumbUrl: string
    /// Neither previewable nor variable: a download link.
    | File

/// The message's Active Storage attachment.
type AttachmentView =
    { /// `attachment.filename.to_s`.
      Filename: string
      /// `rails_blob_path(attachment)`.
      BlobPath: string
      /// `rails_blob_path(attachment, disposition: "attachment")`.
      DownloadPath: string
      Preview: AttachmentPreview
      /// `attachment.metadata[:width]`: an Integer for images, a Float for videos.
      Width: RubyNumber option
      Height: RubyNumber option }

/// `Message#content_type` with what each presentation needs.
type MessageContent =
    /// The presentation filters' output after `auto_link`, from the richtext crate.
    | Text of html: string
    | Sound of SoundView
    | Attachment of AttachmentView
    /// Rendering raised past `message_presentation`'s own rescue (or `plain_text_body` raised):
    /// `message_tag` rescues and renders `messages/_unrenderable` in place of the whole message.
    | Unrenderable

type BoostView =
    { Id: int64
      /// For the fragment cache key (`boost.cache_key_with_version`).
      UpdatedAt: Timestamp
      MessageId: int64
      Content: string
      /// `boost.content.all_emoji?`.
      AllEmoji: bool
      Booster: UserView }

    member this.DomId: string = $"boost_{this.Id}"
    member this.Path: string = Routes.messageBoost this.MessageId this.Id

/// A message as `messages/_message` renders it.
type MessageView =
    { Id: int64
      /// `Message#to_key`, so every `dom_id(message)` uses it.
      ClientMessageId: string
      RoomId: int64
      /// `room_display_name(message.room, for_user: nil)`; see `Rooms.roomDisplayName`.
      RoomName: string
      Creator: UserView
      CreatedAt: Timestamp
      UpdatedAt: Timestamp
      /// `message.plain_text_body.all_emoji?` (`reference/lib/rails_ext/string.rb`).
      AllEmoji: bool
      Content: MessageContent
      /// `message.boosts.ordered`.
      Boosts: BoostView list }

    /// `dom_id(message)` / `dom_id(message, prefix)`.
    member this.DomId(prefix: string) : string =
        if prefix = "" then $"message_{this.ClientMessageId}" else $"{prefix}_message_{this.ClientMessageId}"

    member this.IsUnrenderable: bool =
        match this.Content with
        | Unrenderable -> true
        | _ -> false

    member this.Attachment: AttachmentView option =
        match this.Content with
        | Attachment attachment -> Some attachment
        | _ -> None

    member this.CreatedAtIso: string = iso8601 this.CreatedAt
    member this.CreatedAtEpoch: int64 = epochMs this.CreatedAt
    member this.UpdatedAtEpoch: int64 = epochMs this.UpdatedAt
    member this.AtPath: string = Routes.roomAtMessage this.RoomId this.Id
    member this.Path: string = Routes.roomMessage this.RoomId this.Id
    member this.EditPath: string = Routes.editRoomMessage this.RoomId this.Id
    member this.BoostsPath: string = Routes.messageBoosts this.Id
    member this.NewBoostPath: string = Routes.newMessageBoost this.Id

/// A message on its way into `messages/_message`: the fragment itself when the cache already
/// holds this message version, else the view to render it from. `cache [ message,
/// "presentation-v3" ]` wraps the whole partial, so on a hit Rails evaluates none of it (no rich
/// text, attachment, avatar or boosts); `cachedMessageFragment` lets the presenter look first
/// and build a `MessageView` only on a miss.
type MessageItem =
    | Cached of clientMessageId: string * roomId: int64 * html: Fragment
    | View of MessageView

    /// `dom_id(message)` / `dom_id(message, prefix)`.
    member this.DomId(prefix: string) : string =
        match this with
        | Cached(clientMessageId, _, _) ->
            if prefix = "" then $"message_{clientMessageId}" else $"{prefix}_message_{clientMessageId}"
        | View message -> message.DomId prefix

    member this.RoomId: int64 =
        match this with
        | Cached(_, roomId, _) -> roomId
        | View message -> message.RoomId

    member this.ClientMessageId: string =
        match this with
        | Cached(clientMessageId, _, _) -> clientMessageId
        | View message -> message.ClientMessageId

/// `EmojiHelper::REACTIONS`.
let reactions: (string * string)[] =
    [| "👍", "Thumbs up"
       "👏", "Clapping"
       "👋", "Waving hand"
       "💪", "Muscle"
       "❤️", "Red heart"
       "😂", "Face with tears of joy"
       "🎉", "Party popper"
       "🔥", "Fire" |]

/// What `messages/edit` needs besides the message.
type EditView =
    { Message: MessageView
      /// The editor's `value`: `editable_body(message)` as HTML, from the richtext crate.
      EditableBodyHtml: string }

/// The template digest in `messages/_message`'s fragment keys: the partial and what it renders.
let messageDigest: string =
    FragmentCache.digest
        [| "messages/_message"
           "messages/_actions"
           "messages/_presentation"
           "messages/_unrenderable"
           "messages/boosts/_boosts"
           "messages/boosts/_boost" |]

let boostDigest: string = FragmentCache.digest [| "messages/boosts/_boost" |]

/// `views/messages/_message:<digest>/messages/<id>-<version>/presentation-v3`.
let messageFragmentKey (key: KeyBuf) (id: int64) (updatedAt: Timestamp) : unit =
    FragmentCache.pushRecordFragmentKey key "messages/_message" messageDigest "messages" id updatedAt
    key.Append "/presentation-v3"

/// `messages/boosts/_boost`'s key: `cache boost`.
let boostFragmentKey (key: KeyBuf) (id: int64) (updatedAt: Timestamp) : unit =
    FragmentCache.pushRecordFragmentKey key "messages/boosts/_boost" boostDigest "boosts" id updatedAt

/// `messages/_message`'s fragment for this message version, if the current store holds it. The
/// key needs only the message's id and `updated_at`.
let cachedMessageFragment (id: int64) (updatedAt: Timestamp) : Fragment =
    FragmentCache.read (fun key -> messageFragmentKey key id updatedAt)
