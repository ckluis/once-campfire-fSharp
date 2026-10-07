// Port of rust/crates/views/src/lib.rs
namespace Campfire.Views

/// `Current.user`'s facts the layout and helpers read.
type CurrentUser =
    { Id: int64
      Name: string
      Administrator: bool
      Bot: bool
      /// `fresh_user_avatar_path(Current.user)`.
      AvatarUrl: string }

type AccountSummary =
    { Name: string
      /// `fresh_account_logo_path` (no size).
      LogoUrl: string
      /// `Current.account.logo.attached?` (adds the `account-has-logo` body class).
      HasLogo: bool }

/// `ApplicationPlatform` facts derived from the user agent.
type Platform =
    { Ios: bool
      Android: bool
      Mac: bool
      Windows: bool
      Chrome: bool
      Firefox: bool
      Safari: bool
      Edge: bool
      Mobile: bool
      Desktop: bool
      /// `ApplicationPlatform#apple_messages?`.
      AppleMessages: bool
      /// `user_agent.browser` from the useragent gem ("Chrome", "Safari", "Firefox", "Edge", ...).
      Browser: string
      /// `ApplicationPlatform#operating_system` ("macOS", "Windows", "iPhone", ...).
      OperatingSystem: string }

module Platform =
    /// `Platform::default()`: no flags, empty names.
    let none: Platform =
        { Ios = false
          Android = false
          Mac = false
          Windows = false
          Chrome = false
          Firefox = false
          Safari = false
          Edge = false
          Mobile = false
          Desktop = false
          AppleMessages = false
          Browser = ""
          OperatingSystem = "" }

/// Per-request state every page needs: what `ApplicationController`, the layout and the helpers
/// read from `Current`, `request`, `flash` and the session. Every template renders with one.
[<NoEquality; NoComparison>]
type ViewContext =
    { CurrentUser: CurrentUser option
      Account: AccountSummary
      FlashNotice: string option
      FlashAlert: string option
      Platform: Platform
      /// `Rails.configuration.x.vapid.public_key`; `None` omits the meta tag's content attribute.
      VapidPublicKey: string option
      /// Resolves a logical asset path ("campfire-icon.png") to its digested URL.
      AssetPath: string -> string
      /// The `<script type="importmap">` + modulepreload tags (`javascript_importmap_tags`).
      ImportmapTags: string
      /// `<link rel="stylesheet">` tags for `stylesheet_link_tag :all, "data-turbo-track": "reload"`.
      StylesheetTags: string
      /// The account's custom CSS, if any (`custom_styles_tag`).
      CustomStyles: string option
      /// `script_aware_action_cable_meta_tag` content: script_name + "/cable".
      CableUrl: string
      /// `request.base_url` ("http://campfire.test"), for the `*_url` helpers.
      BaseUrl: string
      /// `request.url`, compared against the referrer by `link_back`.
      RequestUrl: string
      /// `request.referrer`.
      Referrer: string option
      /// Id of `last_room_visited` (`TrackedRoomVisit`): the `last_room` cookie's room if the user
      /// is a member, else `Current.user.rooms.original`. `None` links back to the root.
      LastRoomVisitedId: int64 option
      /// `Rails.application.config.app_version` (APP_VERSION, GIT_REVISION or "0").
      AppVersion: string }

    member this.Asset(logicalPath: string) : string = this.AssetPath logicalPath

    /// `root_url`, `session_url`, `join_url(...)`: base URL + path.
    member this.Url(path: string) : string = this.BaseUrl + path

    member this.CanAdminister: bool =
        match this.CurrentUser with
        | Some user -> user.Administrator
        | None -> false

    member this.CurrentUserId: int64 option = this.CurrentUser |> Option.map (fun user -> user.Id)

    /// `Current.user == user`.
    member this.IsCurrentUser(userId: int64) : bool =
        match this.CurrentUser with
        | Some user -> user.Id = userId
        | None -> false
