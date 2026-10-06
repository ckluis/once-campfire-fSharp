/// The F# counterpart of `src/rest.rs`: the remaining templates (the account, bot, custom styles, sign-in, join,
/// first-run, profile and push-subscription pages, their partials, the avatar SVG and the PWA manifest and
/// service worker). A branch of `tryRun` here is the branch of the same name there, and the arguments are
/// described there.
module Campfire.Views.Differential.Rest

open System.Text.Json
open Campfire.Views
open Campfire.Views.Users
open Campfire.Views.Differential.Answer
open Campfire.Views.Differential.Inputs

let private known =
    set
        [ "accounts/edit"; "accounts/users/_user"; "accounts/users/_next_page_container"; "accounts/users/index_turbo_stream"
          "accounts/bots/_bot"; "accounts/bots/_form"; "accounts/bots/index"; "accounts/bots/new"; "accounts/bots/edit"
          "accounts/custom_styles/edit"; "first_runs/show"; "sessions/new"; "sessions/incompatible_browser"; "sessions/transfers/show"
          "users/new"; "users/show"; "users/_ban_button"; "users/profiles/show"; "users/profiles/_membership"; "users/profiles/_transfer"
          "users/push_subscriptions/index"; "users/push_subscriptions/_push_subscription"; "users/avatars/show"; "pwa/manifest"
          "pwa/service_worker" ]

let private users (list: JsonElement) : UserSummary list = arr list |> List.map userSummary

let private botForm (v: JsonElement) : Accounts.BotForm =
    { Name = opt (get v "name")
      WebhookUrl = opt (get v "webhook_url")
      AvatarAttachmentUrl = opt (get v "avatar_attachment_url") }

let private bot (v: JsonElement) : Accounts.Bot =
    { User = userSummary (get v "user")
      BotKey = str (get v "bot_key")
      Rooms = arr (get v "rooms") |> List.map (fun room -> { Id = int64Of (get room "id"); Name = str (get room "name") }: Accounts.BotRoom) }

let private membership (v: JsonElement) : ProfileMembership =
    { RoomId = int64Of (get v "room_id")
      RoomParamKey = str (get v "room_param_key")
      RoomDisplayName = str (get v "room_display_name")
      Involvement = str (get v "involvement")
      Direct = bool (get v "direct") }

let private memberships (list: JsonElement) : ProfileMembership list = arr list |> List.map membership

let private pushSubscription (v: JsonElement) : PushSubscription =
    { Id = int64Of (get v "id")
      Endpoint = str (get v "endpoint")
      Browser = str (get v "browser")
      Version = str (get v "version")
      Platform = str (get v "platform") }

let private run (op: string) (args: JsonElement) (ctx: ViewContext) : Answer =
    match op with
    | "accounts/edit" ->
        let edit = get args "edit"
        let edit: Accounts.EditView =
            { AccountId = int64Of (get edit "account_id")
              JoinCode = str (get edit "join_code")
              RestrictRoomCreationToAdministrators = bool (get edit "restrict_room_creation_to_administrators")
              Administrators = users (get edit "administrators")
              Members = users (get edit "members")
              NextPage = opt (get edit "next_page") }
        Pages.pageAnswer
            args
            (fun w -> Templates.Accounts.Edit.render w ctx edit)
            Templates.Accounts.Edit.head
            (fun w -> Templates.Accounts.Edit.content w ctx edit)
    | "accounts/users/_user" ->
        let user = userSummary (get args "user")
        Pages.fragmentAnswer (fun w -> Templates.Accounts.Users._User.render w ctx user)
    | "accounts/users/_next_page_container" ->
        Pages.fragmentAnswer (fun w -> Templates.Accounts.Users._NextPageContainer.render w (str (get args "page")))
    | "accounts/users/index_turbo_stream" ->
        let users = users (get args "users")
        Pages.fragmentAnswer (fun w -> Templates.Accounts.Users.IndexTurboStream.render w ctx users (opt (get args "next_page")))
    | "accounts/bots/_bot" ->
        let bot = bot (get args "bot")
        Pages.fragmentAnswer (fun w -> Templates.Accounts.Bots._Bot.render w ctx bot)
    | "accounts/bots/_form" ->
        let form = get args "form"
        let mutable builder = Helpers.Forms.formWith (str (get form "url"))
        opt (get form "model") |> Option.iter (fun m -> builder <- builder.Model m)
        opt (get form "class") |> Option.iter (fun c -> builder <- builder.Class c)
        let bot = botForm (get args "bot")
        Pages.fragmentAnswer (fun w -> Templates.Accounts.Bots._Form.render w ctx builder bot)
    | "accounts/bots/index" ->
        let bots = arr (get args "bots") |> List.map bot
        Pages.pageAnswer
            args
            (fun w -> Templates.Accounts.Bots.Index.render w ctx bots)
            Templates.Accounts.Bots.Index.head
            (fun w -> Templates.Accounts.Bots.Index.content w ctx bots)
    | "accounts/bots/new" ->
        let bot = botForm (get args "bot")
        Pages.pageAnswer
            args
            (fun w -> Templates.Accounts.Bots.New.render w ctx bot)
            Templates.Accounts.Bots.New.head
            (fun w -> Templates.Accounts.Bots.New.content w ctx bot)
    | "accounts/bots/edit" ->
        let botId = int64Of (get args "bot_id")
        let bot = botForm (get args "bot")
        Pages.pageAnswer
            args
            (fun w -> Templates.Accounts.Bots.Edit.render w ctx botId bot)
            Templates.Accounts.Bots.Edit.head
            (fun w -> Templates.Accounts.Bots.Edit.content w ctx botId bot)
    | "accounts/custom_styles/edit" ->
        let styles = opt (get args "custom_styles")
        Pages.pageAnswer
            args
            (fun w -> Templates.Accounts.CustomStyles.Edit.render w ctx styles)
            Templates.Accounts.CustomStyles.Edit.head
            (fun w -> Templates.Accounts.CustomStyles.Edit.content w ctx styles)
    | "first_runs/show" ->
        Pages.pageAnswer
            args
            (fun w -> Templates.FirstRuns.Show.render w ctx)
            Templates.FirstRuns.Show.head
            (fun w -> Templates.FirstRuns.Show.content w ctx)
    | "sessions/new" ->
        let email = opt (get args "email_address")
        let contact = helpContact (get args "help_contact")
        Pages.pageAnswer
            args
            (fun w -> Templates.Sessions.New.render w ctx email contact)
            Templates.Sessions.New.head
            (fun w -> Templates.Sessions.New.content w ctx email contact)
    | "sessions/incompatible_browser" ->
        Pages.pageAnswer
            args
            (fun w -> Templates.Sessions.IncompatibleBrowser.render w ctx)
            Templates.Sessions.IncompatibleBrowser.head
            (fun w -> Templates.Sessions.IncompatibleBrowser.content w ctx)
    | "sessions/transfers/show" ->
        let action = str (get args "action")
        Pages.pageAnswer
            args
            (fun w -> Templates.Sessions.Transfers.Show.render w ctx action)
            Templates.Sessions.Transfers.Show.head
            (fun w -> Templates.Sessions.Transfers.Show.content w action)
    | "users/new" ->
        let joinCode = str (get args "join_code")
        let contact = helpContact (get args "help_contact")
        Pages.pageAnswer
            args
            (fun w -> Templates.Users.New.render w ctx joinCode contact)
            Templates.Users.New.head
            (fun w -> Templates.Users.New.content w ctx joinCode contact)
    | "users/show" ->
        let user = userSummary (get args "user")
        let transferId = str (get args "transfer_id")
        Pages.pageAnswer
            args
            (fun w -> Templates.Users.Show.render w ctx user transferId)
            Templates.Users.Show.head
            (fun w -> Templates.Users.Show.content w ctx user transferId)
    | "users/_ban_button" ->
        let user = userSummary (get args "user")
        Pages.fragmentAnswer (fun w -> Templates.Users._BanButton.render w ctx user)
    | "users/profiles/show" ->
        let profile = get args "profile"
        let profile: ProfileShow =
            { User = userSummary (get profile "user")
              AvatarAttached = bool (get profile "avatar_attached")
              TransferId = str (get profile "transfer_id")
              SharedMemberships = memberships (get profile "shared_memberships")
              DirectMemberships = memberships (get profile "direct_memberships") }
        Pages.pageAnswer
            args
            (fun w -> Templates.Users.Profiles.Show.render w ctx profile)
            Templates.Users.Profiles.Show.head
            (fun w -> Templates.Users.Profiles.Show.content w ctx profile)
    | "users/profiles/_membership" ->
        let membership = membership (get args "membership")
        Pages.fragmentAnswer (fun w -> Templates.Users.Profiles._Membership.render w ctx membership)
    | "users/profiles/_transfer" ->
        let user = userSummary (get args "user")
        let transferId = str (get args "transfer_id")
        Pages.fragmentAnswer (fun w -> Templates.Users.Profiles._Transfer.render w ctx user transferId)
    | "users/push_subscriptions/index" ->
        let subscriptions = arr (get args "push_subscriptions") |> List.map pushSubscription
        Pages.pageAnswer
            args
            (fun w -> Templates.Users.PushSubscriptions.Index.render w ctx subscriptions)
            Templates.Users.PushSubscriptions.Index.head
            (fun w -> Templates.Users.PushSubscriptions.Index.content w ctx subscriptions)
    | "users/push_subscriptions/_push_subscription" ->
        let subscription = pushSubscription (get args "push_subscription")
        Pages.fragmentAnswer (fun w -> Templates.Users.PushSubscriptions._PushSubscription.render w ctx subscription)
    | "users/avatars/show" ->
        let userId = int64Of (get args "user_id")
        let initials = str (get args "initials")
        Pages.fragmentAnswer (fun w -> Templates.Users.Avatars.Show.render w userId initials)
    | "pwa/manifest" ->
        Pages.fragmentAnswer (fun w ->
            Templates.Pwa.Manifest.render
                w
                (opt (get args "account_name"))
                (str (get args "logo_path_small"))
                (str (get args "logo_path"))
                (str (get args "base_url"))
                asset)
    | "pwa/service_worker" -> text Templates.Pwa.ServiceWorker.js
    | _ -> failwith $"unknown rest op {op}"

let tryRun (op: string) (args: JsonElement) (ctx: JsonElement) (shared: JsonElement) : Answer option =
    if known.Contains op then
        let view = viewContext ctx shared
        let cache = FragmentCache FragmentCacheLimits.DefaultMaxBytes
        Some(FragmentCache.withCache cache (fun () -> run op args view))
    else
        None
