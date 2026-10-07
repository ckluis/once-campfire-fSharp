// Port of rust/crates/routes/src/lib.rs
/// Path helpers mirroring `reference/config/routes.rb`. Names match the Rails `*_path` helpers
/// with the `_path` suffix dropped. Shared by controllers (redirects) and views (links).
///
/// Rust writes each helper with a `path!` macro that expands to a function at compile time; the
/// expansion is written out here, in the same order. Nothing is generated at build time. An
/// argument is anything with a `ToString`, as Rust's `impl Display`.
module Campfire.Routes.Routes

let root () : string = "/"
let firstRun () : string = "/first_run"

let session () : string = "/session"
let newSession () : string = "/session/new"
let sessionTransfer (id: 'a) : string = $"/session/transfers/{id}"

let account () : string = "/account"
let editAccount () : string = "/account/edit"
let accountUsers () : string = "/account/users"
let accountUser (id: 'a) : string = $"/account/users/{id}"
let editAccountUser (id: 'a) : string = $"/account/users/{id}/edit"
let accountBots () : string = "/account/bots"
let newAccountBot () : string = "/account/bots/new"
let accountBot (id: 'a) : string = $"/account/bots/{id}"
let editAccountBot (id: 'a) : string = $"/account/bots/{id}/edit"
let accountBotKey (botId: 'a) : string = $"/account/bots/{botId}/key"
let accountJoinCode () : string = "/account/join_code"
let accountLogo () : string = "/account/logo"
let accountCustomStyles () : string = "/account/custom_styles"
let editAccountCustomStyles () : string = "/account/custom_styles/edit"

let join (joinCode: 'a) : string = $"/join/{joinCode}"
let qrCode (id: 'a) : string = $"/qr_code/{id}"

let user (id: 'a) : string = $"/users/{id}"
let userAvatar (userId: 'a) : string = $"/users/{userId}/avatar"
let userBan (userId: 'a) : string = $"/users/{userId}/ban"
let userSidebar () : string = "/users/me/sidebar"
let userProfile () : string = "/users/me/profile"
let editUserProfile () : string = "/users/me/profile/edit"
let userPushSubscriptions () : string = "/users/me/push_subscriptions"
let userPushSubscription (id: 'a) : string = $"/users/me/push_subscriptions/{id}"
let userPushSubscriptionTestNotifications (pushSubscriptionId: 'a) : string = $"/users/me/push_subscriptions/{pushSubscriptionId}/test_notifications"

let autocompletableUsers () : string = "/autocompletable/users"

let rooms () : string = "/rooms"
let newRoom () : string = "/rooms/new"
let room (id: 'a) : string = $"/rooms/{id}"
let editRoom (id: 'a) : string = $"/rooms/{id}/edit"
let roomMessages (roomId: 'a) : string = $"/rooms/{roomId}/messages"
let newRoomMessage (roomId: 'a) : string = $"/rooms/{roomId}/messages/new"
let roomMessage (roomId: 'a) (id: 'b) : string = $"/rooms/{roomId}/messages/{id}"
let editRoomMessage (roomId: 'a) (id: 'b) : string = $"/rooms/{roomId}/messages/{id}/edit"
let roomBotMessages (roomId: 'a) (botKey: 'b) : string = $"/rooms/{roomId}/{botKey}/messages"
let roomBotMessage (roomId: 'a) (botKey: 'b) (id: 'c) : string = $"/rooms/{roomId}/{botKey}/messages/{id}"
let roomBotMessageBoosts (roomId: 'a) (botKey: 'b) (messageId: 'c) : string = $"/rooms/{roomId}/{botKey}/messages/{messageId}/boosts"
let roomBotMessageBoost (roomId: 'a) (botKey: 'b) (messageId: 'c) (id: 'd) : string = $"/rooms/{roomId}/{botKey}/messages/{messageId}/boosts/{id}"
let roomRefresh (roomId: 'a) : string = $"/rooms/{roomId}/refresh"
let roomSettings (roomId: 'a) : string = $"/rooms/{roomId}/settings"
let roomInvolvement (roomId: 'a) : string = $"/rooms/{roomId}/involvement"
let roomAtMessage (roomId: 'a) (messageId: 'b) : string = $"/rooms/{roomId}/@{messageId}"

let roomsOpens () : string = "/rooms/opens"
let newRoomsOpen () : string = "/rooms/opens/new"
let roomsOpen (id: 'a) : string = $"/rooms/opens/{id}"
let editRoomsOpen (id: 'a) : string = $"/rooms/opens/{id}/edit"
let roomsCloseds () : string = "/rooms/closeds"
let newRoomsClosed () : string = "/rooms/closeds/new"
let roomsClosed (id: 'a) : string = $"/rooms/closeds/{id}"
let editRoomsClosed (id: 'a) : string = $"/rooms/closeds/{id}/edit"
let roomsDirects () : string = "/rooms/directs"
let newRoomsDirect () : string = "/rooms/directs/new"
let roomsDirect (id: 'a) : string = $"/rooms/directs/{id}"
let editRoomsDirect (id: 'a) : string = $"/rooms/directs/{id}/edit"

let messages () : string = "/messages"
let message (id: 'a) : string = $"/messages/{id}"
let editMessage (id: 'a) : string = $"/messages/{id}/edit"
let messageBoosts (messageId: 'a) : string = $"/messages/{messageId}/boosts"
let newMessageBoost (messageId: 'a) : string = $"/messages/{messageId}/boosts/new"
let messageBoost (messageId: 'a) (id: 'b) : string = $"/messages/{messageId}/boosts/{id}"

let searches () : string = "/searches"
let clearSearches () : string = "/searches/clear"
let unfurlLink () : string = "/unfurl_link"
let webmanifest () : string = "/webmanifest"
let serviceWorker () : string = "/service-worker"
let railsHealthCheck () : string = "/up"



/// `direct :fresh_user_avatar` — cache-busting avatar URL keyed by the signed avatar token.
let freshUserAvatar (avatarToken: 'a) (updatedAtNumber: 'b) : string = $"/users/{avatarToken}/avatar?v={updatedAtNumber}"

/// `direct :fresh_account_logo` — `v` is the account's `updated_at.to_fs(:number)`, when present.
let freshAccountLogo (v: string option) (size: string option) : string =
    let query =
        [ match size with
          | Some size -> $"size={size}"
          | None -> ()
          match v with
          | Some v -> $"v={v}"
          | None -> () ]
    if query.IsEmpty then accountLogo () else accountLogo () + "?" + String.concat "&" query
