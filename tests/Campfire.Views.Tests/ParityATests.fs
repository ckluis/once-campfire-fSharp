// The cases of rust/crates/views/tests/parity_a.rs for the templates ported so far: DOM parity with golden
// renders from the reference app (`reference-tools/views/a/render.rb`, in rust/crates/views/tests/golden/a,
// read here and never written). Each case rebuilds, from facts.json, the view-model a controller would pass,
// renders it with the F# template, and compares normalized token streams.
module Campfire.Views.Tests.ParityATests

open System.Text.Json
open Xunit
open Campfire.Views
open Campfire.Views.Tests.Dom
open Campfire.Views.Tests.Facts

module Inputs = Campfire.Views.Differential.Inputs

let private text (e: JsonElement) : string = nonNull (e.GetString())

let private named (caseName: string) (userName: string) : Users.UserSummary =
    Inputs.userSummary (user caseName userName)

let private mentionUserOf (caseName: string) (userName: string) : Users.MentionUser =
    { User = named caseName userName
      AttachableSgid = nonNull ((user caseName userName).GetProperty("attachable_sgid").GetString()) }

/// `User.administrator.first`, shown by `accounts/_help_contact`.
let private helpContact (caseName: string) : Accounts.HelpContact option =
    let david = user caseName "David"
    Some { Name = text (david.GetProperty "name"); EmailAddress = text (david.GetProperty "email_address") }

/// All users in case `name`, `User.ordered` (by lowercased name).
let private orderedUsers (name: string) : Users.UserSummary list =
    (case name).GetProperty("users").EnumerateObject()
    |> Seq.map (fun p -> Inputs.userSummary p.Value)
    |> Seq.sortBy (fun user -> Helpers.Application.toLowercase user.Name)
    |> List.ofSeq

let private accountFact (name: string) (key: string) : JsonElement = (case name).GetProperty("account").GetProperty key

let private assertParity (name: string) (ext: string) (rendered: string) =
    let expected = normalizeHtml (golden name ext)
    let actual = normalizeHtml rendered
    match diff expected actual with
    | Some report -> failwith $"{name}: DOM differs from the reference\n{report}"
    | None -> ()

[<Fact>]
let ``pwa partials`` () =
    for ua in facts.Value.GetProperty("user_agents").EnumerateObject() |> Seq.map (fun p -> p.Name) do
        for kind in [ "browser_settings"; "system_settings"; "install_instructions" ] do
            let name = $"{kind}_{ua}"
            let ctx = context name Request.none
            let html =
                Render.text (fun w ->
                    match kind with
                    | "browser_settings" -> Templates.Pwa._BrowserSettings.render w ctx
                    | "system_settings" -> Templates.Pwa._SystemSettings.render w ctx
                    | _ -> Templates.Pwa._InstallInstructions.render w ctx)
            assertParity name "html" html

[<Fact>]
let ``users partials`` () =
    let name = "autocompletables_template"
    assertParity name "html" (Render.text (fun w -> Templates.Users.Autocompletables._Template.render w (context name Request.none)))

    let name = "mention"
    assertParity name "html" (Render.text (fun w -> Templates.Users._Mention.render w (context name Request.none) (mentionUser name "JZ")))

    let name = "ban_button_banned"
    let ctx = context name { Request.none with Partial = true }
    assertParity name "html" (Render.text (fun w -> Templates.Users._BanButton.render w ctx (named name "Spam Ham")))

    for (name, roomName, unread) in [ "shared_room_unread", "HQ", true; "shared_room", "All Talk", false ] do
        let room = facts.Value.GetProperty("rooms").GetProperty roomName
        let view: Users.SidebarRoom =
            { Id = room.GetProperty("id").GetInt64()
              ParamKey = nonNull (room.GetProperty("param_key").GetString())
              Name = roomName
              Unread = unread }
        assertParity name "html" (Render.text (fun w -> Templates.Users.Sidebars.Rooms._Shared.render w view))

    let name = "user_json"
    let jz = user name "JZ"
    let json =
        Campfire.RailsCompat.Json.encode (
            MessagesJson.userToValue
                { Id = jz.GetProperty("id").GetInt64()
                  Name = "JZ"
                  Role = text (jz.GetProperty "role")
                  AvatarUrl = text (facts.Value.GetProperty "base_url") + text (jz.GetProperty "avatar_path") }
        )
    Assert.Equal(golden name "json", json)

[<Fact>]
let ``welcome show`` () =
    // The first page to use the layout: its parity covers the layout and the lightbox.
    let name = "welcome"
    let ctx = context name Request.none
    let html = Render.text (fun w -> Templates.Welcome.Show.render w ctx ctx.CurrentUser.Value.Name)
    assertParity name "html" html

[<Fact>]
let ``application layout wrapper matches extended pages`` () =
    // A page rendered through the wrapper with its parts equals the page calling the layout directly.
    let name = "welcome"
    let ctx = context name Request.none
    let userName = ctx.CurrentUser.Value.Name
    let direct = Render.text (fun w -> Templates.Welcome.Show.render w ctx userName)
    let wrapped =
        Render.text (fun w ->
            Layouts.Application.render
                w
                ctx
                { Layouts.Application.create (Render.html (fun w -> Templates.Welcome.Show.content w ctx userName)) with
                    PageTitle = Templates.Welcome.Show.pageTitle
                    BodyClass = Templates.Welcome.Show.bodyClass
                    Sidebar = Render.html Templates.Welcome.Show.sidebar })
    Assert.Equal(direct, wrapped)
    assertParity name "html" wrapped

    // The same for a page with a nav (users/show), as parity_a.rs's own case.
    let name = "users_show_self"
    let ctx = context name Request.none
    let transferId = text ((user name "David").GetProperty "transfer_id")
    let user = named name "David"
    let direct = Render.text (fun w -> Templates.Users.Show.render w ctx user transferId)
    let wrapped =
        Render.text (fun w ->
            Layouts.Application.render
                w
                ctx
                { Layouts.Application.create (Render.html (fun w -> Templates.Users.Show.content w ctx user transferId)) with
                    PageTitle = Templates.Users.Show.pageTitle user
                    Nav = Render.html (fun w -> Templates.Users.Show.nav w ctx user) })
    Assert.Equal(direct, wrapped)
    assertParity name "html" wrapped

[<Fact>]
let ``a flash notice and a flash alert render in the layout`` () =
    let name = "welcome"
    let notice = Render.text (fun w -> Templates.Welcome.Show.render w (context name { Request.none with FlashNotice = Some "Saved <ok>" }) "X")
    Assert.Contains("<span class=\"for-screen-reader\" role=\"alert\" aria-atomic=\"true\">Saved &lt;ok&gt;</span>", notice)
    Assert.DoesNotContain("--flash-background", notice)
    let alert = Render.text (fun w -> Templates.Welcome.Show.render w (context name { Request.none with FlashAlert = Some "No" }) "X")
    Assert.Contains("style=\"--flash-background: var(--color-negative)\"", alert)
    Assert.Contains("alert-", alert)

// The cases of parity_a.rs for the hot-path templates (unit 4.2): the sidebar and the mentions prompt.

let private sidebar (name: string) : Users.SidebarShow =
    let sidebar = (data name).GetProperty "sidebar"
    let me = userByEmail name (nonNull ((case name).GetProperty("as").GetString()))
    let meName = nonNull (me.GetProperty("name").GetString())
    let strings (e: JsonElement) = [ for n in e.EnumerateArray() -> text n ]
    { CurrentUser = Campfire.Views.Differential.Inputs.userSummary me
      RoomsStream = text (facts.Value.GetProperty("signed_streams").GetProperty "rooms")
      UserRoomsStream = text (facts.Value.GetProperty("signed_streams").GetProperty("user_rooms").GetProperty meName)
      DirectMemberships =
        [ for d in sidebar.GetProperty("direct").EnumerateArray() ->
              let roomId = d.GetProperty("room_id").GetInt64()
              Users.SidebarDirectItem.View
                  { RoomId = roomId
                    Unread = d.GetProperty("unread").GetBoolean()
                    UpdatedAtEpoch = text (d.GetProperty "updated_at_epoch")
                    Members = strings (d.GetProperty "member_names") |> List.map (named name)
                    MembershipId = roomId
                    MembershipUpdatedAt = Campfire.RailsCompat.Timestamps.unixEpoch } ]
      DirectPlaceholderUsers = strings (sidebar.GetProperty "placeholders") |> List.map (named name)
      OtherMemberships =
        [ for r in sidebar.GetProperty("shared").EnumerateArray() ->
              { Id = r.GetProperty("room_id").GetInt64()
                ParamKey = text (r.GetProperty "param_key")
                Name = text (r.GetProperty "name")
                Unread = r.GetProperty("unread").GetBoolean() } ]
      CanCreateRooms = sidebar.GetProperty("can_create_rooms").GetBoolean() }

[<Fact>]
let ``users sidebars show`` () =
    for name in [ "sidebar_david"; "sidebar_kevin" ] do
        let ctx = context name Request.none
        let page = sidebar name
        assertParity name "html" (Render.text (fun w -> Templates.Users.Sidebars.Show.render w ctx page))
    let name = "sidebar_frame"
    let ctx = context name Request.none
    let page = sidebar name
    let framed =
        Layouts.frame Templates.Users.Sidebars.Show.head (fun w -> Templates.Users.Sidebars.Show.content w ctx page)
    assertParity name "html" (framed.ToString())

[<Fact>]
let ``autocompletable users`` () =
    let name = "autocompletable_users"
    let ctx = context name Request.none
    let users =
        [ for n in (data name).GetProperty("autocompletable").EnumerateArray() -> mentionUserOf name (nonNull (n.GetString())) ]
    assertParity name "html" (Render.text (fun w -> Templates.Autocompletable.Users.Index.render w ctx users))

    let name = "autocompletable_users_json"
    let users =
        [ for n in (data name).GetProperty("autocompletable").EnumerateArray() -> mentionUserOf name (nonNull (n.GetString())) ]
    let json = Autocompletable.usersIndexJson users (nonNull (facts.Value.GetProperty("base_url").GetString()))
    Assert.Equal(golden name "json", json)

// The cases of parity_a.rs for the remaining templates (unit 4.3): sessions, accounts, bots, custom styles, first run, join,
// a user's page, the profile, push subscriptions, the avatar SVG, the PWA manifest and the turbo stream of users.

[<Fact>]
let ``sessions new`` () =
    for (name, email, alert) in
        [ "sessions_new", None, None
          "sessions_new_email", Some "x@y.com", None
          "sessions_new_rejected", Some "david@37signals.com", Some "Too many requests or unauthorized."
          "sessions_new_with_logo", None, None ] do
        let ctx = context name { Request.none with FlashAlert = alert }
        assertParity name "html" (Render.text (fun w -> Templates.Sessions.New.render w ctx email (helpContact name)))

[<Fact>]
let ``sessions incompatible browser`` () =
    for name in [ "incompatible_browser"; "incompatible_browser_apple_messages" ] do
        let ctx = context name Request.none
        assertParity name "html" (Render.text (fun w -> Templates.Sessions.IncompatibleBrowser.render w ctx))

[<Fact>]
let ``sessions transfer`` () =
    let name = "sessions_transfer"
    let ctx = context name Request.none
    let action = text ((case name).GetProperty "path")
    assertParity name "html" (Render.text (fun w -> Templates.Sessions.Transfers.Show.render w ctx action))

[<Fact>]
let ``accounts edit`` () =
    for (name, notice) in
        [ "account_edit_admin", None
          "account_edit_member", None
          "account_edit_notice", Some "✓"
          "account_edit_paginated", None
          "account_edit_with_logo", None
          "account_edit_with_logo_member", None ] do
        let ctx = context name { Request.none with FlashNotice = notice }
        // AccountsController#account_users
        let visible =
            orderedUsers name
            |> List.filter (fun user -> not user.Bot)
            |> List.filter (fun user -> if ctx.CanAdminister then user.Active || user.Banned else user.Active)
        let administrators, members = visible |> List.partition (fun user -> user.Administrator)
        let edit: Accounts.EditView =
            { AccountId = (accountFact name "id").GetInt64()
              JoinCode = text (accountFact name "join_code")
              RestrictRoomCreationToAdministrators = (accountFact name "restrict_room_creation_to_administrators").GetBoolean()
              Administrators = administrators
              Members = members
              NextPage = if name = "account_edit_paginated" then Some "2" else None }
        assertParity name "html" (Render.text (fun w -> Templates.Accounts.Edit.render w ctx edit))

/// `bot.rooms.without_directs.ordered` for each active bot of case `name`.
let private bots (name: string) : Accounts.Bot list =
    let facts = facts.Value
    [ for user in orderedUsers name |> List.filter (fun user -> user.Bot && user.Active) do
          let botKey = text ((Facts.user name user.Name).GetProperty "bot_key")
          let rooms =
              [ for m in facts.GetProperty("memberships").EnumerateArray() do
                    if m.GetProperty("user_id").GetInt64() = user.Id then
                        for room in facts.GetProperty("rooms").EnumerateObject() do
                            if room.Value.GetProperty("id").GetInt64() = m.GetProperty("room_id").GetInt64()
                               && text (room.Value.GetProperty "type") <> "Rooms::Direct" then
                                { Id = room.Value.GetProperty("id").GetInt64(); Name = text (room.Value.GetProperty "name") }: Accounts.BotRoom ]
              |> List.sortBy (fun room -> Helpers.Application.toLowercase room.Name)
          { User = user; BotKey = botKey; Rooms = rooms }: Accounts.Bot ]

[<Fact>]
let ``accounts bots`` () =
    let name = "bots_index"
    let ctx = context name Request.none
    let list = bots name
    assertParity name "html" (Render.text (fun w -> Templates.Accounts.Bots.Index.render w ctx list))

    let name = "bots_new"
    let ctx = context name Request.none
    assertParity name "html" (Render.text (fun w -> Templates.Accounts.Bots.New.render w ctx Accounts.BotForm.empty))

    for (name, withAvatar) in [ "bots_edit", false; "bots_edit_with_avatar", true ] do
        let ctx = context name Request.none
        let bender = user name "Bender Bot"
        let form: Accounts.BotForm =
            { Name = Inputs.opt (bender.GetProperty "name")
              WebhookUrl = Inputs.opt (bender.GetProperty "webhook_url")
              AvatarAttachmentUrl = if withAvatar then Inputs.opt ((case name).GetProperty "avatar_url") else None }
        let botId = bender.GetProperty("id").GetInt64()
        assertParity name "html" (Render.text (fun w -> Templates.Accounts.Bots.Edit.render w ctx botId form))

[<Fact>]
let ``accounts custom styles`` () =
    for name in [ "custom_styles_edit"; "custom_styles_layout" ] do
        let ctx = context name Request.none
        let styles = Inputs.opt (accountFact name "custom_styles")
        assertParity name "html" (Render.text (fun w -> Templates.Accounts.CustomStyles.Edit.render w ctx styles))

[<Fact>]
let ``first runs show`` () =
    let name = "first_run"
    let ctx = context name Request.none
    assertParity name "html" (Render.text (fun w -> Templates.FirstRuns.Show.render w ctx))

[<Fact>]
let ``users new`` () =
    let name = "users_new"
    let ctx = context name Request.none
    let joinCode = text (accountFact name "join_code")
    assertParity name "html" (Render.text (fun w -> Templates.Users.New.render w ctx joinCode (helpContact name)))

[<Fact>]
let ``users show`` () =
    for (name, shown) in
        [ "users_show_self", "David"
          "users_show_member_as_admin", "JZ"
          "users_show_member_as_member", "JZ"
          "users_show_bot", "Bender Bot"
          "users_show_deactivated", "Ex Employee"
          "users_show_banned", "Spam Ham" ] do
        let ctx = context name Request.none
        let transferId = text ((user name shown).GetProperty "transfer_id")
        assertParity name "html" (Render.text (fun w -> Templates.Users.Show.render w ctx (named name shown) transferId))

let private profileMemberships (list: JsonElement) : Users.ProfileMembership list =
    [ for m in list.EnumerateArray() ->
          { RoomId = m.GetProperty("room_id").GetInt64()
            RoomParamKey = text (m.GetProperty "param_key")
            RoomDisplayName = text (m.GetProperty "display_name")
            Involvement = text (m.GetProperty "involvement")
            Direct = m.GetProperty("direct").GetBoolean() } ]

[<Fact>]
let ``users profiles show`` () =
    for ua in
        [ "chrome_mac"; "chrome_windows"; "safari_mac"; "safari_ios"; "chrome_android"; "firefox_mac"; "firefox_android"; "edge_windows"
          "kevin"; "with_avatar" ] do
        let name = $"profile_{ua}"
        let ctx = context name Request.none
        let me = userByEmail name (text ((case name).GetProperty "as"))
        let profile: Users.ProfileShow =
            { User = Inputs.userSummary me
              AvatarAttached = me.GetProperty("avatar_attached").GetBoolean()
              TransferId = text (me.GetProperty "transfer_id")
              SharedMemberships = profileMemberships ((data name).GetProperty("profile").GetProperty "shared")
              DirectMemberships = profileMemberships ((data name).GetProperty("profile").GetProperty "direct") }
        assertParity name "html" (Render.text (fun w -> Templates.Users.Profiles.Show.render w ctx profile))

[<Fact>]
let ``users push subscriptions`` () =
    let name = "push_subscriptions"
    let ctx = context name Request.none
    let subscriptions: Users.PushSubscription list =
        [ for ps in (data name).GetProperty("push_subscriptions").EnumerateArray() ->
              { Id = ps.GetProperty("id").GetInt64()
                Endpoint = text (ps.GetProperty "endpoint")
                Browser = text (ps.GetProperty "browser")
                Version = text (ps.GetProperty "version")
                Platform = text (ps.GetProperty "platform") } ]
    assertParity name "html" (Render.text (fun w -> Templates.Users.PushSubscriptions.Index.render w ctx subscriptions))

[<Fact>]
let ``users avatars show`` () =
    for (name, shown) in [ "avatar_david", "David"; "avatar_three_initials", "Anna Bea Cole" ] do
        let user = user name shown
        let svg =
            Render.text (fun w ->
                Templates.Users.Avatars.Show.render w (user.GetProperty("id").GetInt64()) (text (user.GetProperty "initials")))
        assertParity name "svg" svg
        Assert.Equal(golden name "svg", svg)

[<Fact>]
let ``pwa manifest and service worker`` () =
    let name = "manifest"
    let assets = facts.Value.GetProperty "assets"
    let assetPath (logical: string) = text (assets.GetProperty logical)
    let json =
        Render.text (fun w ->
            Templates.Pwa.Manifest.render
                w
                (Inputs.opt (accountFact name "name"))
                (text (accountFact name "logo_path_small"))
                (text (accountFact name "logo_path"))
                (text (facts.Value.GetProperty "base_url"))
                assetPath)
    // Rails HTML-escapes the values into the JSON (README, Known differences)
    Assert.Equal((golden name "json").Replace("&amp;", "&"), json)
    Assert.Equal(golden "service_worker" "js", Templates.Pwa.ServiceWorker.js)
    Assert.Equal(golden "service_worker" "js", System.Text.Encoding.UTF8.GetString Templates.Pwa.ServiceWorker.bytes)

[<Fact>]
let ``pwa manifest is valid json whatever the account is called`` () =
    let assetPath (logical: string) = $"/assets/{logical}"
    let name = "Back\\slash \"quoted\" <b>&amp;</b>"
    let json =
        Render.text (fun w ->
            Templates.Pwa.Manifest.render w (Some name) "/account/logo?size=small&v=1" "/account/logo?v=1" "http://campfire.test" assetPath)
    use manifest = JsonDocument.Parse json
    Assert.Equal(name, text (manifest.RootElement.GetProperty "name"))
    let icons = manifest.RootElement.GetProperty "icons"
    Assert.Equal("/account/logo?size=small&v=1", text (icons[0].GetProperty "src"))

[<Fact>]
let ``accounts users index turbo stream`` () =
    let name = "account_users_page_2"
    let ctx = context name Request.none
    let users = [ for n in (case name).GetProperty("page_users").EnumerateArray() -> named name (text n) ]
    assertParity name "turbo_stream.html" (Render.text (fun w -> Templates.Accounts.Users.IndexTurboStream.render w ctx users None))
