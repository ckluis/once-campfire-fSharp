// Port of rust/crates/views/templates/rooms/show.html (reference/app/views/rooms/show.html.erb)
/// `rooms/show.html.erb`, with `MessagesHelper#message_area_tag` and `#messages_tag`: the room page. A page module
/// exposes its regions (`head`, `nav`, `sidebar`, `content`, `footer`) as functions of the writer, which `render` hands
/// to the application layout and `Layouts.frame` (with `head` and `content`) to the Turbo-Frame layout.
///
/// The module is `ShowPage`, not `Show`: `rooms/show/` holds the partials (`Templates/Rooms/Show/_Nav.fs`...), and a
/// module and a namespace of one name can't both be in an assembly.
module Campfire.Views.Templates.Rooms.ShowPage

open Campfire.Routes
open Campfire.Views
open Campfire.Views.Helpers
open Campfire.Views.Messages
open Campfire.Views.Rooms

let private messagesPrefix = Utf8.lit "messages_"

let private t0 = Utf8.lit "\n  <meta name=\"turbo-cache-control\" content=\"no-preview\">\n  <meta name=\"current-room-id\" content=\""
let private t1 = Utf8.lit "\">"
let private t2 = Utf8.lit "\n<div id=\"message-area\" class=\"message-area\" contents=\"true\" data-controller=\"messages presence drop-target\" data-action=\"turbo:before-stream-render@document-&gt;messages#beforeStreamRender keydown.up@document-&gt;messages#editMyLastMessage dragenter-&gt;drop-target#dragenter dragover-&gt;drop-target#dragover drop-&gt;drop-target#drop visibilitychange@document-&gt;presence#visibilityChanged\" data-messages-first-of-day-class=\"message--first-of-day\" data-messages-formatted-class=\"message--formatted\" data-messages-me-class=\"message--me\" data-messages-mentioned-class=\"message--mentioned\" data-messages-threaded-class=\"message--threaded\" data-messages-page-url-value=\""
let private t3 = Utf8.lit "\">"
let private t4 = Utf8.lit "\n  "
let private t5 = Utf8.lit "\n\n  <div id=\""
let private t6 = Utf8.lit "\" class=\"messages\" data-controller=\"maintain-scroll refresh-room\" data-action=\"turbo:before-stream-render@document-&gt;maintain-scroll#beforeStreamRender visibilitychange@document-&gt;refresh-room#visibilityChanged online@window-&gt;refresh-room#online\" data-messages-target=\"messages\" data-refresh-room-loaded-at-value=\""
let private t7 = Utf8.lit "\" data-refresh-room-url-value=\""
let private t8 = Utf8.lit "\">"
let private t9 = Utf8.lit "\n    "
let private t10 = Utf8.lit "\n    "
let private t11 = Utf8.lit "\n  </div>\n\n  <turbo-cable-stream-source channel=\"RoomMessagesChannel\" signed-stream-name=\""
let private t12 = Utf8.lit "\"></turbo-cable-stream-source>\n  <button class=\"message-area__return-to-latest btn\" data-action=\"messages#returnToLatest\" data-messages-target=\"latest\" hidden=\"hidden\"><img aria-hidden=\"true\" src=\""
let private t13 = Utf8.lit "\" width=\"20\" height=\"20\" /><span class=\"for-screen-reader\">Jump to newest message</span></button>\n</div>"

/// `@page_title`.
let pageTitle (show: ShowView) : string option = Some show.Room.DisplayName

/// `@body_class`.
let bodyClass: string option = Some "sidebar"

/// `content_for :head`.
let head (w: Out) (show: ShowView) : unit =
    w.Lit t0
    w.Int show.Room.Id
    w.Lit t1

/// `content_for :nav`.
let nav (w: Out) (ctx: ViewContext) (show: ShowView) : unit = Show._Nav.render w ctx show.Room

/// `content_for :sidebar`.
let sidebar (w: Out) : unit =
    UsersHelper.sidebarTurboFrameTag w (Some(Routes.userSidebar ())) ignore

/// The page itself.
let content (w: Out) (ctx: ViewContext) (show: ShowView) : unit =
    w.Lit t2
    w.Text(ctx.Url(Routes.roomMessages show.Room.Id))
    w.Lit t3
    w.Lit t4
    Campfire.Views.Templates.Messages._Template.render w ctx show.User
    w.Lit t5
    writeRoomDomId w messagesPrefix show.Room.Kind show.Room.Id
    w.Lit t6
    w.Int(MessagesSupport.epochMs show.UpdatedAt)
    w.Lit t7
    w.Text(ctx.Url(Routes.roomRefresh show.Room.Id))
    w.Lit t8
    if show.Invitation then
        w.Lit t9
        Show._Invitation.render w ctx show.JoinCode
    for message in show.Messages do
        w.Lit t10
        MessagesCached.cachedMessageItem w ctx message
    w.Lit t11
    w.Text show.MessagesStreamName
    w.Lit t12
    w.Text(ctx.Asset "arrow-down.svg")
    w.Lit t13

/// `content_for :footer`.
let footer (w: Out) (ctx: ViewContext) (show: ShowView) : unit = Show._Composer.render w ctx show.Room

let render (w: Out) (ctx: ViewContext) (show: ShowView) : unit =
    Campfire.Views.Templates.Layouts.Application.render
        w
        ctx
        (pageTitle show)
        bodyClass
        (fun w -> head w show)
        (fun w -> nav w ctx show)
        (fun w -> content w ctx show)
        (fun w -> footer w ctx show)
        sidebar
