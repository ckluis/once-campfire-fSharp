/// Reading the cases `bin/views-differential` generates (see `src/ops.rs` of the Rust tool for what each
/// operation's arguments are).
module Campfire.Views.Differential.Inputs

open System
open System.Text.Json
open Campfire.RailsCompat
open Campfire.Views
open Campfire.Views.Helpers
open Campfire.Views.Helpers.Tag
open Campfire.Views.Messages
open Campfire.Views.MessagesSupport
open Campfire.Views.Users

/// A property of an object, or an undefined element (which reads as null, false, 0 and empty).
let get (element: JsonElement) (name: string) : JsonElement =
    match element.ValueKind with
    | JsonValueKind.Object ->
        match element.TryGetProperty name with
        | true, value -> value
        | _ -> JsonElement()
    | _ -> JsonElement()

let at (element: JsonElement) (index: int) : JsonElement =
    if element.ValueKind = JsonValueKind.Array && index < element.GetArrayLength() then element[index] else JsonElement()

let str (element: JsonElement) : string =
    if element.ValueKind = JsonValueKind.String then nonNull (element.GetString()) else ""

let opt (element: JsonElement) : string option =
    if element.ValueKind = JsonValueKind.String then Some(nonNull (element.GetString())) else None

let int64Of (element: JsonElement) : int64 =
    if element.ValueKind = JsonValueKind.Number then element.GetInt64() else 0L

let intOf (element: JsonElement) : int = int (int64Of element)

let bool (element: JsonElement) : bool = element.ValueKind = JsonValueKind.True

let arr (element: JsonElement) : JsonElement list =
    if element.ValueKind = JsonValueKind.Array then [ for item in element.EnumerateArray() -> item ] else []

let timestamp (element: JsonElement) : Timestamp = (Timestamps.tryParse (str element)).Value

let platform (v: JsonElement) : Platform =
    let flag name = bool (get v name)
    { Ios = flag "ios"
      Android = flag "android"
      Mac = flag "mac"
      Windows = flag "windows"
      Chrome = flag "chrome"
      Firefox = flag "firefox"
      Safari = flag "safari"
      Edge = flag "edge"
      Mobile = flag "mobile"
      Desktop = flag "desktop"
      AppleMessages = flag "apple_messages"
      Browser = str (get v "browser")
      OperatingSystem = str (get v "operating_system") }

/// Every logical asset path resolves to a deterministic fake digest: `x.svg` is `/assets/x-d1g3st.svg`.
let asset (logical: string) : string =
    match logical.IndexOf '.' with
    | -1 -> $"/assets/{logical}-d1g3st"
    | at -> $"/assets/{logical.Substring(0, at)}-d1g3st{logical.Substring at}"

/// The shared strings, read once: they are kilobytes of JSON text that a case would otherwise decode
/// again each time it builds its context.
let private sharedStrings = System.Collections.Generic.Dictionary<string, string>()

/// The shared value of `key` unless the case's `ctx` has its own.
let private pick (ctx: JsonElement) (shared: JsonElement) (key: string) : string =
    match get ctx key with
    | v when v.ValueKind = JsonValueKind.String -> str v
    | _ ->
        match sharedStrings.TryGetValue key with
        | true, value -> value
        | _ ->
            let value = str (get shared key)
            sharedStrings[key] <- value
            value

let viewContext (ctx: JsonElement) (shared: JsonElement) : ViewContext =
    let user = get ctx "current_user"
    { CurrentUser =
        if user.ValueKind = JsonValueKind.Object then
            Some
                { Id = int64Of (get user "id")
                  Name = str (get user "name")
                  Administrator = bool (get user "administrator")
                  Bot = bool (get user "bot")
                  AvatarUrl = str (get user "avatar_url") }
        else
            None
      Account =
        { Name = str (get (get ctx "account") "name")
          LogoUrl = str (get (get ctx "account") "logo_url")
          HasLogo = bool (get (get ctx "account") "has_logo") }
      FlashNotice = opt (get ctx "flash_notice")
      FlashAlert = opt (get ctx "flash_alert")
      Platform = platform (get ctx "platform")
      VapidPublicKey = opt (get ctx "vapid_public_key")
      AssetPath = asset
      ImportmapTags = pick ctx shared "importmap_tags"
      StylesheetTags = pick ctx shared "stylesheet_tags"
      CustomStyles = opt (get ctx "custom_styles")
      CableUrl = str (get ctx "cable_url")
      BaseUrl = str (get ctx "base_url")
      RequestUrl = str (get ctx "request_url")
      Referrer = opt (get ctx "referrer")
      LastRoomVisitedId =
        (match get ctx "last_room_visited_id" with
         | v when v.ValueKind = JsonValueKind.Number -> Some(v.GetInt64())
         | _ -> None)
      AppVersion = str (get ctx "app_version") }

/// `[[name, kind, value], ...]`: kind is "text", "safe", "bool", "int" or "none".
let attrs (list: JsonElement) : Attrs =
    let attrs = Tag.attrs ()
    for item in arr list do
        let name = str (at item 0)
        let value = at item 2
        match str (at item 1) with
        | "text" -> attrs.Attr(name, str value) |> ignore
        | "safe" -> attrs.Attr(name, Safe(str value)) |> ignore
        | "bool" -> attrs.Attr(name, bool value) |> ignore
        | "int" -> attrs.Attr(name, int64Of value) |> ignore
        | "none" -> attrs.Set(name, ValueNone)
        | other -> failwith $"attribute kind {other}"
    attrs

let userSummary (v: JsonElement) : UserSummary =
    { Id = int64Of (get v "id")
      Name = str (get v "name")
      Bio = opt (get v "bio")
      EmailAddress = opt (get v "email_address")
      Role =
        (match str (get v "role") with
         | "administrator" -> Administrator
         | "bot" -> Bot
         | _ -> Member)
      Status =
        (match str (get v "status") with
         | "deactivated" -> Deactivated
         | "banned" -> Banned
         | _ -> Active)
      AvatarPath = str (get v "avatar_path") }

let helpContact (v: JsonElement) : Accounts.HelpContact option =
    if v.ValueKind = JsonValueKind.Object then
        Some { Name = str (get v "name"); EmailAddress = str (get v "email_address") }
    else
        None

// The views crate's serde formats of the message view-models.

let userView (v: JsonElement) : UserView =
    { Id = int64Of (get v "id")
      Name = str (get v "name")
      Title = str (get v "title")
      AvatarUrl = str (get v "avatar_url") }

let rubyNumber (v: JsonElement) : RubyNumber option =
    match v.ValueKind with
    | JsonValueKind.Number ->
        match v.TryGetInt64() with
        | true, n -> Some(Int n)
        | _ -> Some(Float(v.GetDouble()))
    | _ -> None

let messageContent (v: JsonElement) : MessageContent =
    match str (get v "type") with
    | "text" -> Text(str (get v "html"))
    | "sound" ->
        let image = get v "image"
        Sound
            { Url = str (get v "url")
              Image =
                (if image.ValueKind = JsonValueKind.Object then
                     Some
                         { Src = str (get image "src")
                           Width = uint32 (int64Of (get image "width"))
                           Height = uint32 (int64Of (get image "height")) }
                 else
                     None)
              Text = opt (get v "text") }
    | "attachment" ->
        let preview = get v "preview"
        Attachment
            { Filename = str (get v "filename")
              BlobPath = str (get v "blob_path")
              DownloadPath = str (get v "download_path")
              Preview =
                (match str (get preview "type") with
                 | "video" -> Video(str (get preview "poster_url"))
                 | "image" -> Image(str (get preview "thumb_url"))
                 | _ -> File)
              Width = rubyNumber (get v "width")
              Height = rubyNumber (get v "height") }
    | _ -> Unrenderable

let boostView (v: JsonElement) : BoostView =
    { Id = int64Of (get v "id")
      UpdatedAt =
        (match get v "updated_at" with
         | t when t.ValueKind = JsonValueKind.String -> timestamp t
         | _ -> Timestamps.unixEpoch)
      MessageId = int64Of (get v "message_id")
      Content = str (get v "content")
      AllEmoji = bool (get v "all_emoji")
      Booster = userView (get v "booster") }

let messageView (v: JsonElement) : MessageView =
    { Id = int64Of (get v "id")
      ClientMessageId = str (get v "client_message_id")
      RoomId = int64Of (get v "room_id")
      RoomName = str (get v "room_name")
      Creator = userView (get v "creator")
      CreatedAt = timestamp (get v "created_at")
      UpdatedAt = timestamp (get v "updated_at")
      AllEmoji = bool (get v "all_emoji")
      Content = messageContent (get v "content")
      Boosts = arr (get v "boosts") |> List.map boostView }

// The Jbuilder inputs.

let userJson (v: JsonElement) : MessagesJson.UserJson =
    { Id = int64Of (get v "id")
      Name = str (get v "name")
      Role = str (get v "role")
      AvatarUrl = str (get v "avatar_url") }

let messageJson (v: JsonElement) : MessagesJson.MessageJson =
    { Id = int64Of (get v "id")
      CreatedAt = str (get v "created_at")
      Body = { PlainText = str (get (get v "body") "plain_text"); Html = str (get (get v "body") "html") }
      Creator = userJson (get v "creator")
      Room = { Id = int64Of (get (get v "room") "id") }
      Url = str (get v "url") }

let boostJson (v: JsonElement) : MessagesJson.BoostJson =
    { Id = int64Of (get v "id")
      Content = str (get v "content")
      CreatedAt = str (get v "created_at")
      Booster = userJson (get v "booster")
      Message = { Id = int64Of (get (get v "message") "id"); Url = str (get (get v "message") "url") } }
