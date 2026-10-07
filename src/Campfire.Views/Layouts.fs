// Port of rust/crates/views/src/layouts.rs
/// Views for `reference/app/views/layouts`.
///
/// A page template (a module under `Templates/`) calls `Templates.Layouts.Application.render` with
/// what the ERB sets as `@page_title` and `@body_class` (askama's `layouts::Page` trait: here they are
/// arguments) and the regions it fills (`content_for`'s `head`, `nav`, `footer`, `sidebar`, and the
/// page itself as `content`). A page module exposes its `head` and `content` blocks as functions of
/// the writer, which `frame` takes (with the page's `RenderSize`) for Turbo-Frame requests.
module Campfire.Views.Layouts

open Campfire.Views
open Campfire.Views.Templates.Layouts

/// The application layout around page parts rendered elsewhere. Each part is what the ERB's
/// `content_for` / `yield` would have produced.
type Application =
    { /// `@page_title`.
      PageTitle: string option
      /// `@body_class`.
      BodyClass: string option
      Head: Html
      Nav: Html
      Content: Html
      Footer: Html
      Sidebar: Html }

module Application =
    /// Just a page body, with no title, body class or `content_for` regions.
    let create (content: Html) : Application =
        { PageTitle = None
          BodyClass = None
          Head = Html.Empty
          Nav = Html.Empty
          Content = content
          Footer = Html.Empty
          Sidebar = Html.Empty }

    /// `layouts::Application`'s render.
    let render (w: Out) (ctx: ViewContext) (page: Application) : unit =
        ApplicationWrapper.render w ctx page.PageTitle page.BodyClass page.Head page.Nav page.Content page.Footer page.Sidebar

/// What the Turbo-Frame layout's own text comes to, over the head and content it frames.
[<Literal>]
let private FrameLayoutSizeHint = 512

/// Renders a page's `head` and `content` blocks in the Turbo-Frame layout, recorded (the content's
/// cached fragments too): `frame size (Page.head model) (Page.content model)`. `size` is the page's
/// own `RenderSize` (what Rust's `C::SIZE_HINT` is for `recorded::render(&content, ..)`): the content
/// renders into a buffer sized from the last render of that page, not from 4 KB doubled until it fits.
let frame (size: RenderSize) (head: Out -> unit) (content: Out -> unit) : RecordedPage =
    let content = size.Render content
    let head = Render.html head
    Render.page (content.Text.Length + head.Length + FrameLayoutSizeHint) (fun w ->
        TurboRails.Frame.render w (fun w -> w.Raw head) (fun w -> w.Page content))
