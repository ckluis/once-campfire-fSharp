// Port of rust/crates/views/src/messages/json.rs
/// The Jbuilder views: `messages/_message.json`, `messages/by_bots/{index,show}.json`,
/// `messages/boosts/_boost.json` and `messages/boosts/by_bots/show.json`. Field order is the
/// JSON key order Jbuilder emits. Rust serializes the structs with serde; here each has a `toValue`
/// building the `Value` `Json.encode` writes.
module Campfire.Views.MessagesJson

open System.Text
open Campfire.RailsCompat

/// `users/_user.json.jbuilder`: `json.(user, :id, :name, :role)` and `avatar_url`.
type UserJson =
    { Id: int64
      Name: string
      /// "member", "administrator" or "bot".
      Role: string
      /// `fresh_user_avatar_url(user)`: a full URL.
      AvatarUrl: string }

type MessageBodyJson =
    { /// `message.plain_text_body`.
      PlainText: string
      /// `message.body.to_s`: the rich text rendered with its layout.
      Html: string }

type IdJson = { Id: int64 }

/// `messages/_message.json.jbuilder`.
type MessageJson =
    { Id: int64
      /// `message.created_at.utc`, formatted by `MessagesSupport.jsonTime`.
      CreatedAt: string
      Body: MessageBodyJson
      Creator: UserJson
      Room: IdJson
      /// `room_message_url(message.room, message)`.
      Url: string }

type BoostMessageJson =
    { Id: int64
      /// `room_message_url(boost.message.room, boost.message)`.
      Url: string }

/// `messages/boosts/_boost.json.jbuilder`.
type BoostJson =
    { Id: int64
      Content: string
      /// `boost.created_at.utc`, formatted by `MessagesSupport.jsonTime`.
      CreatedAt: string
      Booster: UserJson
      Message: BoostMessageJson }

let userToValue (user: UserJson) : Value =
    Value.Object
        [ "id", Value.Int user.Id
          "name", Value.String user.Name
          "role", Value.String user.Role
          "avatar_url", Value.String user.AvatarUrl ]

let messageToValue (message: MessageJson) : Value =
    Value.Object
        [ "id", Value.Int message.Id
          "created_at", Value.String message.CreatedAt
          "body",
          Value.Object [ "plain_text", Value.String message.Body.PlainText; "html", Value.String message.Body.Html ]
          "creator", userToValue message.Creator
          "room", Value.Object [ "id", Value.Int message.Room.Id ]
          "url", Value.String message.Url ]

let boostToValue (boost: BoostJson) : Value =
    Value.Object
        [ "id", Value.Int boost.Id
          "content", Value.String boost.Content
          "created_at", Value.String boost.CreatedAt
          "booster", userToValue boost.Booster
          "message", Value.Object [ "id", Value.Int boost.Message.Id; "url", Value.String boost.Message.Url ] ]

/// `messages/by_bots/index.json.jbuilder`: `json.array! @messages, partial: "messages/message"`.
let byBotsIndex (messages: MessageJson list) : string = Json.encode (Value.Array(messages |> List.map messageToValue))

/// `messages/by_bots/show.json.jbuilder`.
let byBotsShow (message: MessageJson) : string = Json.encode (messageToValue message)

/// `messages/boosts/by_bots/show.json.jbuilder`.
let boostsByBotsShow (boost: BoostJson) : string = Json.encode (boostToValue boost)

/// The length of a value's JSON: the payload size of a Jbuilder value in the fragment cache
/// (`serialized_size`).
let serializedSize (value: Value) : int = Encoding.UTF8.GetByteCount(Json.generate value)

// A Jbuilder fragment's payload is its JSON.
let userCacheSize (user: UserJson) : int = serializedSize (userToValue user)
let messageCacheSize (message: MessageJson) : int = serializedSize (messageToValue message)
let boostCacheSize (boost: BoostJson) : int = serializedSize (boostToValue boost)
