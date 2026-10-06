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

[<Fact>]
let ``sidebar shared rooms`` () =
    for (name, roomName, unread) in [ "shared_room_unread", "HQ", true; "shared_room", "All Talk", false ] do
        let room = facts.Value.GetProperty("rooms").GetProperty roomName
        let view: Users.SidebarRoom =
            { Id = room.GetProperty("id").GetInt64()
              ParamKey = nonNull (room.GetProperty("param_key").GetString())
              Name = roomName
              Unread = unread }
        assertParity name "html" (Render.text (fun w -> Templates.Users.Sidebars.Rooms._Shared.render w view))

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

let private named (caseName: string) (userName: string) : Users.UserSummary =
    Campfire.Views.Differential.Inputs.userSummary (user caseName userName)

let private mentionUserOf (caseName: string) (userName: string) : Users.MentionUser =
    { User = named caseName userName
      AttachableSgid = nonNull ((user caseName userName).GetProperty("attachable_sgid").GetString()) }

let private sidebar (name: string) : Users.SidebarShow =
    let sidebar = (data name).GetProperty "sidebar"
    let me = userByEmail name (nonNull ((case name).GetProperty("as").GetString()))
    let meName = nonNull (me.GetProperty("name").GetString())
    let text (e: JsonElement) = nonNull (e.GetString())
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
