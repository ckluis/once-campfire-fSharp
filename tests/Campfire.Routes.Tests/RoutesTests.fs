// The routes crate has no tests of its own in Rust: its helpers are exercised through the
// campfire crate, and vectors/campfire_routes.json is read there (controllers.rs). These tests
// stand in for them at this level. Each path helper is checked against its expected path, and
// against `bin/rails routes` in the vector, so a helper can't drift from `config/routes.rb`.
// Recognizing a request path (the second half of the vector) is the router's job and is covered
// where the router is ported (Campfire.App); here each recognition sample is checked to name a
// route that exists.
module Campfire.Routes.Tests.RoutesTests

open System
open System.Text.Json
open System.Text.RegularExpressions
open Xunit
open Campfire.Routes
open Campfire.Tests

// Case counts in vectors/campfire_routes.json and the number of `path!` helpers in the crate.
[<Literal>]
let RouteRows = 177

[<Literal>]
let Recognitions = 111

[<Literal>]
let PathHelpers = 71

let private vectors = lazy (Repo.vector "campfire_routes")

let private str (e: JsonElement) : string = nonNull (e.GetString())

/// (verb, path spec, endpoint) for each row of `bin/rails routes`.
let private routes =
    lazy
        (vectors.Value.RootElement.GetProperty("routes").EnumerateArray()
         |> Seq.map (fun r -> str (r.GetProperty "verb"), str (r.GetProperty "path"), str (r.GetProperty "endpoint"))
         |> Seq.toList)

/// A Rails path spec as a regex: `:name` is one segment, `(.:format)` an optional extension and
/// `*name` the rest of the path.
let private compile (spec: string) : Regex =
    let escaped = Regex.Escape spec
    let withFormat = escaped.Replace(@"\(\.:format\)", @"(?:\.[^/.?]+)?")
    let withGlobs = Regex.Replace(withFormat, @"\*\w+", ".+")
    Regex(@"^" + Regex.Replace(withGlobs, @":\w+", "[^/.?]+") + "$")

let private routePatterns = lazy (routes.Value |> List.map (fun (verb, spec, _) -> verb, compile spec))

let private inTable (path: string) =
    let withoutQuery = path.Split('?').[0]
    routePatterns.Value |> List.exists (fun (_, pattern) -> pattern.IsMatch withoutQuery)

/// Every path helper called with sample arguments, and the path Rust's format string gives them.
let private helpers: (string * string * string) list =
        [
          "root", Routes.root (), "/"
          "firstRun", Routes.firstRun (), "/first_run"
          "session", Routes.session (), "/session"
          "newSession", Routes.newSession (), "/session/new"
          "sessionTransfer", Routes.sessionTransfer 7, "/session/transfers/7"
          "account", Routes.account (), "/account"
          "editAccount", Routes.editAccount (), "/account/edit"
          "accountUsers", Routes.accountUsers (), "/account/users"
          "accountUser", Routes.accountUser 7, "/account/users/7"
          "editAccountUser", Routes.editAccountUser 7, "/account/users/7/edit"
          "accountBots", Routes.accountBots (), "/account/bots"
          "newAccountBot", Routes.newAccountBot (), "/account/bots/new"
          "accountBot", Routes.accountBot 7, "/account/bots/7"
          "editAccountBot", Routes.editAccountBot 7, "/account/bots/7/edit"
          "accountBotKey", Routes.accountBotKey 4, "/account/bots/4/key"
          "accountJoinCode", Routes.accountJoinCode (), "/account/join_code"
          "accountLogo", Routes.accountLogo (), "/account/logo"
          "accountCustomStyles", Routes.accountCustomStyles (), "/account/custom_styles"
          "editAccountCustomStyles", Routes.editAccountCustomStyles (), "/account/custom_styles/edit"
          "join", Routes.join "abc-def", "/join/abc-def"
          "qrCode", Routes.qrCode 7, "/qr_code/7"
          "user", Routes.user 7, "/users/7"
          "userAvatar", Routes.userAvatar 3, "/users/3/avatar"
          "userBan", Routes.userBan 3, "/users/3/ban"
          "userSidebar", Routes.userSidebar (), "/users/me/sidebar"
          "userProfile", Routes.userProfile (), "/users/me/profile"
          "editUserProfile", Routes.editUserProfile (), "/users/me/profile/edit"
          "userPushSubscriptions", Routes.userPushSubscriptions (), "/users/me/push_subscriptions"
          "userPushSubscription", Routes.userPushSubscription 7, "/users/me/push_subscriptions/7"
          "userPushSubscriptionTestNotifications", Routes.userPushSubscriptionTestNotifications 5, "/users/me/push_subscriptions/5/test_notifications"
          "autocompletableUsers", Routes.autocompletableUsers (), "/autocompletable/users"
          "rooms", Routes.rooms (), "/rooms"
          "newRoom", Routes.newRoom (), "/rooms/new"
          "room", Routes.room 7, "/rooms/7"
          "editRoom", Routes.editRoom 7, "/rooms/7/edit"
          "roomMessages", Routes.roomMessages 1, "/rooms/1/messages"
          "newRoomMessage", Routes.newRoomMessage 1, "/rooms/1/messages/new"
          "roomMessage", Routes.roomMessage 1 7, "/rooms/1/messages/7"
          "editRoomMessage", Routes.editRoomMessage 1 7, "/rooms/1/messages/7/edit"
          "roomBotMessages", Routes.roomBotMessages 1 "1-abc", "/rooms/1/1-abc/messages"
          "roomBotMessage", Routes.roomBotMessage 1 "1-abc" 7, "/rooms/1/1-abc/messages/7"
          "roomBotMessageBoosts", Routes.roomBotMessageBoosts 1 "1-abc" 2, "/rooms/1/1-abc/messages/2/boosts"
          "roomBotMessageBoost", Routes.roomBotMessageBoost 1 "1-abc" 2 7, "/rooms/1/1-abc/messages/2/boosts/7"
          "roomRefresh", Routes.roomRefresh 1, "/rooms/1/refresh"
          "roomSettings", Routes.roomSettings 1, "/rooms/1/settings"
          "roomInvolvement", Routes.roomInvolvement 1, "/rooms/1/involvement"
          "roomAtMessage", Routes.roomAtMessage 1 2, "/rooms/1/@2"
          "roomsOpens", Routes.roomsOpens (), "/rooms/opens"
          "newRoomsOpen", Routes.newRoomsOpen (), "/rooms/opens/new"
          "roomsOpen", Routes.roomsOpen 7, "/rooms/opens/7"
          "editRoomsOpen", Routes.editRoomsOpen 7, "/rooms/opens/7/edit"
          "roomsCloseds", Routes.roomsCloseds (), "/rooms/closeds"
          "newRoomsClosed", Routes.newRoomsClosed (), "/rooms/closeds/new"
          "roomsClosed", Routes.roomsClosed 7, "/rooms/closeds/7"
          "editRoomsClosed", Routes.editRoomsClosed 7, "/rooms/closeds/7/edit"
          "roomsDirects", Routes.roomsDirects (), "/rooms/directs"
          "newRoomsDirect", Routes.newRoomsDirect (), "/rooms/directs/new"
          "roomsDirect", Routes.roomsDirect 7, "/rooms/directs/7"
          "editRoomsDirect", Routes.editRoomsDirect 7, "/rooms/directs/7/edit"
          "messages", Routes.messages (), "/messages"
          "message", Routes.message 7, "/messages/7"
          "editMessage", Routes.editMessage 7, "/messages/7/edit"
          "messageBoosts", Routes.messageBoosts 2, "/messages/2/boosts"
          "newMessageBoost", Routes.newMessageBoost 2, "/messages/2/boosts/new"
          "messageBoost", Routes.messageBoost 2 7, "/messages/2/boosts/7"
          "searches", Routes.searches (), "/searches"
          "clearSearches", Routes.clearSearches (), "/searches/clear"
          "unfurlLink", Routes.unfurlLink (), "/unfurl_link"
          "webmanifest", Routes.webmanifest (), "/webmanifest"
          "serviceWorker", Routes.serviceWorker (), "/service-worker"
          "railsHealthCheck", Routes.railsHealthCheck (), "/up" ]

[<Fact>]
let ``the vector has every row of bin/rails routes`` () =
    Assert.Equal(RouteRows, vectors.Value.RootElement.GetProperty("routes").GetArrayLength())
    Assert.Equal(RouteRows, routes.Value.Length)

[<Fact>]
let ``path helpers build the paths of config/routes.rb`` () =
    Assert.Equal(PathHelpers, helpers.Length)
    Assert.Equal<string list>([], helpers |> List.distinct |> List.filter (fun (_, ours, expected) -> ours <> expected) |> List.map (fun (name, ours, _) -> name + " is " + ours))
    Assert.Equal(PathHelpers, (helpers |> List.map (fun (n, _, _) -> n) |> List.distinct).Length)

[<Fact>]
let ``every path helper is a route in the reference table`` () =
    let missing = helpers |> List.filter (fun (_, ours, _) -> not (inTable ours)) |> List.map (fun (name, ours, _) -> $"{name}: {ours}")
    Assert.True(missing.IsEmpty, "not in bin/rails routes:\n" + String.Join("\n", missing))
    // The matcher is not vacuous.
    Assert.False(inTable "/nope")
    Assert.False(inTable "/rooms/1/bogus")

[<Fact>]
let ``direct helpers build cache-busting urls`` () =
    Assert.Equal("/users/tok/avatar?v=20260101120000", Routes.freshUserAvatar "tok" 20260101120000L)
    Assert.Equal("/account/logo", Routes.freshAccountLogo None None)
    Assert.Equal("/account/logo?v=20260101120000", Routes.freshAccountLogo (Some "20260101120000") None)
    Assert.Equal("/account/logo?size=small", Routes.freshAccountLogo None (Some "small"))
    Assert.Equal("/account/logo?size=small&v=20260101120000", Routes.freshAccountLogo (Some "20260101120000") (Some "small"))
    for path in [ Routes.freshUserAvatar "tok" 1; Routes.freshAccountLogo (Some "1") (Some "small") ] do
        Assert.True(inTable path, path)

[<Fact>]
let ``recognition samples name routes in the table`` () =
    let samples = vectors.Value.RootElement.GetProperty("recognitions").EnumerateArray() |> Seq.toList
    Assert.Equal(Recognitions, samples.Length)
    let endpoints = routes.Value |> List.map (fun (_, _, endpoint) -> endpoint) |> Set.ofList
    let verbs = routes.Value |> List.map (fun (verb, _, _) -> verb) |> Set.ofList |> Set.add "HEAD"
    let mutable recognized = 0
    for sample in samples do
        let verb = str (sample.GetProperty "verb")
        Assert.Contains(verb, verbs)
        let endpoint = sample.GetProperty "endpoint"
        if endpoint.ValueKind = JsonValueKind.String then
            recognized <- recognized + 1
            let path = str (sample.GetProperty "path")
            Assert.True(endpoints.Contains(str endpoint), $"{verb} {path} names {str endpoint}")
    Assert.True(recognized > 0)
