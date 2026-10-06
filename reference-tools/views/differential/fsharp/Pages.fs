/// The F# counterpart of `src/pages.rs`: the hot-path templates (the room page, messages and their partials, the
/// sidebar, search, the room forms and the mentions prompt). A branch of `tryRun` here is the branch of the same
/// name there, and the arguments are described there.
module Campfire.Views.Differential.Pages

open System
open System.Text
open System.Text.Json
open Campfire.Views
open Campfire.Views.Messages
open Campfire.Views.Users
open Campfire.Views.Differential.Answer
open Campfire.Views.Differential.Inputs

let private known =
    set
        [ "messages/_message"; "messages/message_cached"; "messages/_actions"; "messages/_presentation"; "messages/_unrenderable"
          "messages/_template"; "messages/index"; "messages/show"; "messages/edit"; "messages/create_turbo_stream"
          "messages/destroy_turbo_stream"; "messages/room_not_found"; "messages/boosts/_boost"; "messages/boosts/_boosts"
          "messages/boosts/index"; "messages/boosts/new"; "rooms/show"; "rooms/involvements/show"; "rooms/refreshes/show"
          "rooms/opens/new"; "rooms/opens/edit"; "rooms/closeds/new"; "rooms/closeds/edit"; "rooms/directs/new"; "rooms/directs/edit"
          "rooms/layouts/_form"; "searches/index"; "autocompletable/users/index"; "autocompletable/users/_prompt_item"
          "users/sidebars/show" ]

/// `mode`: "mixed" renders every second message into the cache first and gives the template the cached fragment.
let private mixed (ctx: ViewContext) (items: Messages.MessageItem list) (args: JsonElement) : MessageItem list =
    if str (get args "mode") <> "mixed" then
        items
    else
        items
        |> List.mapi (fun n item ->
            match item with
            | MessageItem.View view when n % 2 = 1 ->
                MessageItem.Cached(view.ClientMessageId, view.RoomId, MessagesCached.message ctx view)
            | other -> other)

let private recordedAnswer (page: RecordedPage) : Answer =
    { Out = page.ToString()
      Text = Some(Encoding.UTF8.GetString page.Text)
      Fragments = Some [ for struct (offset, fragment) in page.Fragments -> offset, fragment.Length ] }

/// A page: rendered cold, then warm (the same bytes), plain and recorded (the same bytes), and in the Turbo-Frame
/// layout when `frame` is set.
let pageAnswer (args: JsonElement) (render: Out -> unit) (head: Out -> unit) (content: Out -> unit) : Answer =
    let frame = bool (get args "frame")
    let once () =
        let plain = Render.text render
        let recorded = if frame then Layouts.frame head content else Render.page 0 render
        plain, recorded
    let coldPlain, cold = once ()
    let plain, recorded = once ()
    if cold.ToString() <> recorded.ToString() || coldPlain <> plain then failwith "a warm render is not the cold one"
    if not frame && recorded.ToString() <> plain then failwith "a recorded page is not the plain render"
    recordedAnswer recorded

/// A template that is not a page: rendered cold and then warm.
let fragmentAnswer (render: Out -> unit) : Answer =
    let cold = Render.text render
    let warm = Render.text render
    if cold <> warm then failwith "a warm render is not the cold one"
    text warm

/// A list of messages as a recorded page, as a template that isn't a page is served.
let private recordedFragmentAnswer (render: Out -> unit) : Answer =
    let recorded = Render.page 0 render
    let plain = Render.text render
    if recorded.ToString() <> plain then failwith "a recorded page is not the plain render"
    { recordedAnswer recorded with Out = plain }

let private run (op: string) (args: JsonElement) (ctx: ViewContext) : Answer =
    let message () = messageView (get args "message")
    match op with
    | "messages/_message" ->
        let message = message ()
        fragmentAnswer (fun w -> Templates.Messages._Message.render w ctx message)
    | "messages/message_cached" ->
        let first, second = messageView (get args "message"), messageView (get args "second")
        let a = MessagesCached.message ctx first
        let b = MessagesCached.message ctx second
        if not (obj.ReferenceEquals(a, b)) then failwith "the second render did not reuse the cached fragment"
        let c = Render.text (fun w -> MessagesCached.cachedMessage w ctx second)
        let d = Render.text (fun w -> MessagesCached.cachedMessageItem w ctx (MessageItem.View second))
        if c <> d then failwith "cached_message and cached_message_item differ"
        { Out = a.ToString(); Text = Some c; Fragments = None }
    | "messages/_actions" ->
        let message = message ()
        fragmentAnswer (fun w -> Templates.Messages._Actions.render w ctx message)
    | "messages/_presentation" ->
        let message = message ()
        fragmentAnswer (fun w -> Templates.Messages._Presentation.render w ctx message)
    | "messages/_unrenderable" -> fragmentAnswer Templates.Messages._Unrenderable.render
    | "messages/_template" ->
        let user = userView (get args "user")
        fragmentAnswer (fun w -> Templates.Messages._Template.render w ctx user)
    | "messages/index" ->
        let messages = arr (get args "messages") |> List.map messageItem |> fun items -> mixed ctx items args
        recordedFragmentAnswer (fun w -> Templates.Messages.Index.render w ctx messages)
    | "messages/show" ->
        let message = message ()
        fragmentAnswer (fun w -> Templates.Messages.Show.render w ctx message)
    | "messages/edit" ->
        let edit = editView (get args "edit")
        fragmentAnswer (fun w -> Templates.Messages.Edit.render w ctx edit)
    | "messages/create_turbo_stream" ->
        let message = message ()
        let kind = roomKind (get args "room_kind")
        let item =
            if bool (get args "fragment") then
                MessageItem.Cached(message.ClientMessageId, message.RoomId, MessagesCached.message ctx message)
            else
                MessageItem.View message
        fragmentAnswer (fun w -> Templates.Messages.CreateTurboStream.render w ctx item kind)
    | "messages/destroy_turbo_stream" ->
        let message = message ()
        fragmentAnswer (fun w -> Templates.Messages.DestroyTurboStream.render w message)
    | "messages/room_not_found" -> fragmentAnswer Templates.Messages.RoomNotFound.render
    | "messages/boosts/_boost" ->
        let boost = boostView (get args "boost")
        let first = BoostsCached.boost ctx boost
        let second = BoostsCached.boost ctx boost
        if not (obj.ReferenceEquals(first, second)) then failwith "the second render did not reuse the cached fragment"
        let partial = Render.text (fun w -> Templates.Messages.Boosts._Boost.render w ctx boost)
        if partial <> first.ToString() then failwith "the cached boost is not the partial"
        { Out = partial; Text = Some(Render.text (fun w -> BoostsCached.cachedBoost w ctx boost)); Fragments = None }
    | "messages/boosts/_boosts" ->
        let message = message ()
        fragmentAnswer (fun w -> Templates.Messages.Boosts._Boosts.render w ctx message)
    | "messages/boosts/index" ->
        let message = message ()
        fragmentAnswer (fun w -> Templates.Messages.Boosts.Index.render w ctx message)
    | "messages/boosts/new" ->
        let message = message ()
        let user = userView (get args "user")
        fragmentAnswer (fun w -> Templates.Messages.Boosts.New.render w ctx message user)
    | "rooms/show" ->
        let show = showView (get args "show")
        let show = { show with Messages = mixed ctx show.Messages args }
        pageAnswer
            args
            (fun w -> Templates.Rooms.ShowPage.render w ctx show)
            (fun w -> Templates.Rooms.ShowPage.head w show)
            (fun w -> Templates.Rooms.ShowPage.content w ctx show)
    | "rooms/involvements/show" ->
        let involvement = involvementView (get args "involvement")
        fragmentAnswer (fun w -> Templates.Rooms.Involvements.Show.render w ctx involvement)
    | "rooms/refreshes/show" ->
        let refresh = refreshView (get args "refresh")
        let refresh =
            { refresh with
                NewMessages = mixed ctx refresh.NewMessages args
                UpdatedMessages = mixed ctx refresh.UpdatedMessages args }
        recordedFragmentAnswer (fun w -> Templates.Rooms.Refreshes.Show.render w ctx refresh)
    | "rooms/opens/new" ->
        let form = openFormView (get args "form")
        pageAnswer
            args
            (fun w -> Templates.Rooms.Opens.New.render w ctx form)
            Templates.Rooms.Opens.New.head
            (fun w -> Templates.Rooms.Opens.New.content w ctx form)
    | "rooms/opens/edit" ->
        let form = openFormView (get args "form")
        pageAnswer
            args
            (fun w -> Templates.Rooms.Opens.Edit.render w ctx form)
            Templates.Rooms.Opens.Edit.head
            (fun w -> Templates.Rooms.Opens.Edit.content w ctx form)
    | "rooms/closeds/new" ->
        let form = closedFormView (get args "form")
        pageAnswer
            args
            (fun w -> Templates.Rooms.Closeds.New.render w ctx form)
            Templates.Rooms.Closeds.New.head
            (fun w -> Templates.Rooms.Closeds.New.content w ctx form)
    | "rooms/closeds/edit" ->
        let form = closedFormView (get args "form")
        pageAnswer
            args
            (fun w -> Templates.Rooms.Closeds.Edit.render w ctx form)
            Templates.Rooms.Closeds.Edit.head
            (fun w -> Templates.Rooms.Closeds.Edit.content w ctx form)
    | "rooms/directs/new" ->
        pageAnswer
            args
            (fun w -> Templates.Rooms.Directs.New.render w ctx)
            Templates.Rooms.Directs.New.head
            (fun w -> Templates.Rooms.Directs.New.content w ctx)
    | "rooms/directs/edit" ->
        let edit = directEditView (get args "edit")
        pageAnswer
            args
            (fun w -> Templates.Rooms.Directs.Edit.render w ctx edit)
            Templates.Rooms.Directs.Edit.head
            (fun w -> Templates.Rooms.Directs.Edit.content w ctx edit)
    | "rooms/layouts/_form" ->
        let room = formRoom (get args "room")
        let kind = roomKind (get args "kind")
        let content = str (get args "content")
        fragmentAnswer (fun w ->
            Templates.Rooms.Layouts._Form.render w ctx room (bool (get args "can_administer")) kind (fun w -> w.Raw content))
    | "searches/index" ->
        let index = searchesIndexView (get args "index")
        let index = { index with Messages = mixed ctx index.Messages args }
        pageAnswer
            args
            (fun w -> Templates.Searches.Index.render w ctx index)
            Templates.Searches.Index.head
            (fun w -> Templates.Searches.Index.content w ctx index)
    | "autocompletable/users/index" ->
        let users = arr (get args "users") |> List.map mentionUser
        fragmentAnswer (fun w -> Templates.Autocompletable.Users.Index.render w ctx users)
    | "autocompletable/users/_prompt_item" ->
        let user = mentionUser (get args "user")
        fragmentAnswer (fun w -> Templates.Autocompletable.Users._PromptItem.render w ctx user)
    | "users/sidebars/show" ->
        let mixedDirect = str (get args "mode") = "mixed"
        let sidebar: SidebarShow =
            { CurrentUser = userSummary (get args "current_user")
              RoomsStream = str (get args "rooms_stream")
              UserRoomsStream = str (get args "user_rooms_stream")
              DirectMemberships =
                arr (get args "direct")
                |> List.mapi (fun n v ->
                    let membership = sidebarDirect v
                    if mixedDirect && n % 2 = 1 then
                        SidebarDirectItem.Cached(UsersCached.directRoom ctx membership)
                    else
                        SidebarDirectItem.View membership)
              DirectPlaceholderUsers = arr (get args "placeholders") |> List.map userSummary
              OtherMemberships =
                arr (get args "shared")
                |> List.map (fun r ->
                    { Id = int64Of (get r "id")
                      ParamKey = str (get r "param_key")
                      Name = str (get r "name")
                      Unread = bool (get r "unread") })
              CanCreateRooms = bool (get args "can_create_rooms") }
        pageAnswer
            args
            (fun w -> Templates.Users.Sidebars.Show.render w ctx sidebar)
            Templates.Users.Sidebars.Show.head
            (fun w -> Templates.Users.Sidebars.Show.content w ctx sidebar)
    | _ -> failwith $"unknown pages op {op}"

let tryRun (op: string) (args: JsonElement) (ctx: JsonElement) (shared: JsonElement) : Answer option =
    if known.Contains op then
        let view = viewContext ctx shared
        let cache = FragmentCache FragmentCacheLimits.DefaultMaxBytes
        Some(FragmentCache.withCache cache (fun () -> run op args view))
    else
        None
