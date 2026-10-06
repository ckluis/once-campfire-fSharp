// Port of rust/crates/campfire/src/controllers/presenters/page.rs
//
// Rendering helpers for the controllers: pages in the application layout (or turbo-rails' frame layout
// for Turbo-Frame requests), and partials rendered outside a request for broadcasts
// (`ApplicationController.render`).
namespace Campfire.App.Presenters

open System.Threading.Tasks
open Campfire.App
open Campfire.App.Channels
open Campfire.Assets
open Campfire.Db
open Campfire.Kit
open Campfire.Views

module Page =
    /// A template that extends `layouts/application` itself (with `blocks = ["head", "content"]`): the
    /// full page, or for a Turbo-Frame request its `head` and `content` in turbo-rails' frame layout
    /// (`layout -> { "turbo_rails/frame" if turbo_frame_request? }`).
    ///
    /// `size` is the page's own `RenderSize` (Rust's `render_sized!` makes one per call site); `render` is
    /// the page module's `render w ctx ...` with its arguments applied, and `head` and `content` its
    /// regions:
    ///
    ///     Page.framedPage c Status.Ok size (fun w ctx -> New.render w ctx email help) New.head (fun w ctx -> New.content w ctx email help)
    let framedPage
        (c: Ctx)
        (status: int)
        (size: RenderSize)
        (render: Out -> ViewContext -> unit)
        (head: Out -> unit)
        (content: Out -> ViewContext -> unit)
        : Task<Result<Response, Error>> =
        Layout.pageOrFrame
            c
            status
            (fun ctx -> size.Render(fun w -> render w ctx))
            (fun ctx -> Layouts.frame size head (fun w -> content w ctx))

    /// A content-only template: Rails wraps it in the application layout, or in turbo-rails'
    /// `layouts/turbo_rails/frame` when the request carries a `Turbo-Frame` header.
    let content (c: Ctx) (status: int) (render: Out -> ViewContext -> unit) : Task<Result<Response, Error>> =
        task {
            match ViewContextBuilder.findTemplate c Format.Html with
            | Error e -> return Error e
            | Ok() ->
                match! Layout.load c with
                | Error e -> return Error e
                | Ok layout ->
                    let frame = c.IsTurboFrameRequest
                    let html =
                        Layout.render layout c (fun ctx ->
                            if frame then
                                Render.page 0 (fun w ->
                                    Templates.Layouts.TurboRails.Frame.render w (fun _ -> ()) (fun w -> render w ctx))
                            else
                                Render.page 0 (fun w -> Layouts.Application.render w ctx (Layouts.Application.create (Render.html (fun w -> render w ctx)))))
                    return Ok(if frame then Layout.frame c status html else Layout.page c status html)
        }

    /// A content-only template in the application layout even for Turbo-Frame requests: a controller
    /// that declares its own `layout` (MessagesController's `layout false, only: :index`) replaces
    /// turbo-rails' frame layout choice.
    let contentInApplicationLayout (c: Ctx) (status: int) (render: Out -> ViewContext -> unit) : Task<Result<Response, Error>> =
        task {
            match ViewContextBuilder.findTemplate c Format.Html with
            | Error e -> return Error e
            | Ok() ->
                match! Layout.load c with
                | Error e -> return Error e
                | Ok layout ->
                    let html =
                        Layout.render layout c (fun ctx ->
                            Render.page 0 (fun w -> Layouts.Application.render w ctx (Layouts.Application.create (Render.html (fun w -> render w ctx)))))
                    return Ok(Layout.page c status html)
        }

    /// A template rendered with `layout false` (or a turbo stream), no layout, labelled with the
    /// template's format.
    let bare (c: Ctx) (status: int) (template: Format) (render: ViewContext -> RecordedPage) : Task<Result<Response, Error>> =
        task {
            match ViewContextBuilder.findTemplate c template with
            | Error e -> return Error e
            | Ok() ->
                match! Layout.load c with
                | Error e -> return Error e
                | Ok layout ->
                    let html = Layout.render layout c render
                    return Ok(ViewContextBuilder.renderRecorded c status template html)
        }

    /// The base of the URLs in a `renderDetachedAt` during a request. `SetCurrentRequest`'s
    /// `default_url_options` only carries `request.host` and `request.protocol`, so the port comes from
    /// the renderer's own env (`example.org:80`) and never shows: a request to `http://localhost:3999`
    /// broadcasts `http://localhost/...` links (`reference/app/controllers/concerns/set_current_request.rb`).
    let rendererBaseUrl (c: Ctx) : string = c.Request.Protocol + c.Request.Host

    /// `renderDetached` during a request: URLs get the request's host through `default_url_options`
    /// (`SetCurrentRequest`), see `rendererBaseUrl`.
    let renderDetachedAt (app: AppState) (account: Account option) (baseUrl: string) (render: ViewContext -> 'T) : 'T =
        let stylesheets = ViewContextBuilder.stylesheetTags ()
        let ctx: ViewContext =
            { CurrentUser = None
              Account = ViewContextBuilder.accountSummary account false
              FlashNotice = None
              FlashAlert = None
              Platform = Platform.none
              VapidPublicKey = app.VapidPublicKey
              AssetPath = Assets.assetPath
              ImportmapTags = Assets.javascriptImportmapTags ()
              StylesheetTags = stylesheets.Html
              CustomStyles = None
              CableUrl = "/cable"
              BaseUrl = baseUrl
              RequestUrl = baseUrl + "/"
              Referrer = None
              LastRoomVisitedId = None
              AppVersion = app.Config.AppVersion }
        // Renders outside a request (broadcasts from jobs) share the fragment cache too.
        FragmentCache.withCache app.FragmentCache (fun () -> render ctx)

    /// Renders with the `ViewContext` `ApplicationController.render` has: no request, no
    /// `Current.user`, no CSRF tokens, and the renderer's default host (`http://example.org`).
    let renderDetached (app: AppState) (account: Account option) (render: ViewContext -> 'T) : 'T =
        renderDetachedAt app account "http://example.org" render

/// The broadcast partials, rendered up front by the controller (which has the view models) and handed
/// to `Broadcasts`.
type Rendered =
    { Message: Campfire.Views.Fragment option
      MessagePresentation: string option
      Boost: Campfire.Views.Fragment option
      SharedRoom: string option
      /// `users/sidebars/rooms/_direct`, per membership id.
      DirectRooms: (int64 * Campfire.Views.Fragment) list }

module Rendered =
    let empty: Rendered =
        { Message = None
          MessagePresentation = None
          Boost = None
          SharedRoom = None
          DirectRooms = [] }

    let partials (rendered: Rendered) : IPartials =
        { new IPartials with
            member _.Message _ = rendered.Message |> Option.map (fun fragment -> fragment.Memory) |> Option.defaultValue System.ReadOnlyMemory<byte>.Empty
            member _.MessagePresentation _ = defaultArg rendered.MessagePresentation ""
            member _.Boost _ = rendered.Boost |> Option.map (fun fragment -> fragment.Memory) |> Option.defaultValue System.ReadOnlyMemory<byte>.Empty
            member _.SharedRoom _ = defaultArg rendered.SharedRoom ""

            member _.DirectRoom(membership: Membership) =
                rendered.DirectRooms
                |> List.tryFind (fun (id, _) -> id = membership.Id)
                |> Option.map (fun (_, html) -> string html)
                |> Option.defaultValue "" }
