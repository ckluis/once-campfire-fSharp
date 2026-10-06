// Port of rust/crates/views/templates/welcome/show.html (reference/app/views/welcome/show.html.erb)
/// `welcome/show.html.erb`: shown to users who aren't in any room yet. Ported with the layouts, as the
/// first page that uses them, so the layout can be compared with the reference's.
module Campfire.Views.Templates.Welcome.Show

open Campfire.Routes
open Campfire.Views
open Campfire.Views.Helpers

let private t0 = Utf8.lit "<div id=\"message-area\" class=\"message-area\">\n  <div class=\"message-area--empty min-width center\">\n    <figure class=\"center pad\">\n      "
let private t1 = Utf8.lit "\n      <span class=\"for-screen-reader\">"
let private t2 = Utf8.lit "</span>\n    </figure>\n  </div>\n</div>\n"

/// `@page_title`.
let pageTitle: string option = Some "No rooms yet"

/// `@body_class`.
let bodyClass: string option = Some "sidebar"

/// `content_for :sidebar`.
let sidebar (w: Out) : unit =
    UsersHelper.sidebarTurboFrameTag w (Some(Routes.userSidebar ())) ignore

/// The page itself.
let content (w: Out) (ctx: ViewContext) (currentUserName: string) : unit =
    w.Lit t0
    Assets.imageTag w ctx "messages-empty.svg" (Tag.attrs().AriaHidden().Class("colorize--black translucent"))
    w.Lit t1
    w.Text currentUserName
    w.Lit t2

let render (w: Out) (ctx: ViewContext) (currentUserName: string) : unit =
    Templates.Layouts.Application.render w ctx pageTitle bodyClass ignore ignore (fun w -> content w ctx currentUserName) ignore sidebar
