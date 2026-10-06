// Port of rust/crates/campfire/src/controllers/presenters/view_context.rs
//
// Builds the `Campfire.Views.ViewContext` every page renders with: what the application layout and the
// helpers read from `Current`, `request`, `flash`, the session and the config
// (`reference/app/views/layouts/application.html.erb` and `app/helpers`).
//
// Gather the per-request data with `Layout.load` (it reads the database), then render inside
// `Layout.render`, which lends the templates a `ViewContext` for this request:
//
//     let! layout = Layout.load c
//     let html = Layout.render layout c (fun ctx -> Render.page 0 (fun w -> Templates.Sessions.New.render w ctx ...))
//     return layout.Page(c, Status.Ok, html)
namespace Campfire.App.Presenters

open System
open System.Threading.Tasks
open Campfire.App
open Campfire.Assets
open Campfire.Db
open Campfire.Kit
open Campfire.Views

/// Everything the layout needs, loaded before rendering.
type Layout =
    { CurrentUser: Campfire.Views.CurrentUser option
      Account: AccountSummary
      CustomStyles: string option
      Platform: Platform
      LastRoomVisitedId: int64 option
      VapidPublicKey: string option
      AppVersion: string }

module ViewContextBuilder =
    /// The layout's `stylesheet_link_tag :all, "data-turbo-track": "reload"`: the assets are fixed at
    /// build time, so it renders once per process.
    let private tags = lazy (Assets.stylesheetLinkTagAll [ "data-turbo-track", "reload" ])

    let stylesheetTags () : Campfire.Assets.Tags.StylesheetTags = tags.Value

    /// `Current.user` as the layout's meta tags and helpers see it.
    let currentUser (secrets: Campfire.RailsCompat.Secrets) (user: User) : Campfire.Views.CurrentUser =
        { Id = user.Id
          Name = user.Name
          Administrator = User.canAdminister user None false
          Bot = User.isBot user
          AvatarUrl = Common.avatarPath secrets user }

    /// `Current.account` for the layout: its name, `fresh_account_logo_path` and whether a logo is
    /// attached.
    let accountSummary (account: Account option) (hasLogo: bool) : AccountSummary =
        { Name = account |> Option.map (fun account -> account.Name) |> Option.defaultValue ""
          LogoUrl = Accounts.freshAccountLogoPath account None
          HasLogo = hasLogo }

    /// The implicit render's template lookup (`default_render`): an action whose only template is
    /// `<action>.html.erb` can't answer a request that doesn't accept HTML, which is
    /// `ActionController::UnknownFormat` (406), and the response carries the template's format
    /// whatever the `Accept` header preferred.
    let findTemplate (c: Ctx) (template: Format) : Result<unit, Error> = c.RespondTo [ template ] |> Result.map ignore

    /// `Ctx::render` for a recorded page: its text with its cached fragments spliced in, which the kit
    /// keeps as parts rather than joining them.
    let renderRecorded (c: Ctx) (status: int) (template: Format) (page: RecordedPage) : Response =
        FragmentGlue.renderRecorded c status template page

module Layout =
    /// `Current.account`, `Current.user`, `last_room_visited` and the platform. With no account
    /// yet (first run) the account summary is blank: the pages that reference the account raise
    /// in Rails then, the others don't read it.
    let load (c: Ctx) : Task<Result<Layout, Error>> =
        task {
            let app = c.App
            let secrets = app.Secrets
            let user = Current.currentUser c
            let userId = user |> Option.map (fun user -> user.Id)
            let lastRoom = Current.lastRoomCookie c
            // One trip to a reader for all three: each trip is a hand-off to a reader thread and back.
            match!
                app.Read(fun conn ->
                    let account = Account.first conn
                    let hasLogo =
                        match account with
                        | Some account -> (Attachments.attachedBlob conn "Account" account.Id "logo").IsSome
                        | None -> false
                    let lastRoomVisitedId =
                        match userId with
                        | Some userId -> Current.lastRoomVisitedIn conn userId lastRoom |> Option.map (fun room -> room.Id)
                        | None -> None
                    account, hasLogo, lastRoomVisitedId)
            with
            | Error e -> return Error e
            | Ok(account, hasLogo, lastRoomVisitedId) ->
                return
                    Ok
                        { CurrentUser = user |> Option.map (ViewContextBuilder.currentUser secrets)
                          Account = ViewContextBuilder.accountSummary account hasLogo
                          CustomStyles = account |> Option.bind (fun account -> account.CustomStyles)
                          Platform = Accounts.platform c
                          LastRoomVisitedId = lastRoomVisitedId
                          VapidPublicKey = app.VapidPublicKey
                          AppVersion = app.Config.AppVersion }
        }

    let private option (value: string | null) : string option =
        match value with
        | null -> None
        | value -> Some value

    /// Renders with a `ViewContext` for this request. The flash is read (and so swept at the end of the
    /// request) the way the layout's `flash[:notice]` / `flash[:alert]` read it.
    let render (layout: Layout) (c: Ctx) (render: ViewContext -> 'T) : 'T =
        let flashNotice = option (c.Flash().Notice)
        let flashAlert = option (c.Flash().Alert)
        let baseUrl = c.UrlFor ""
        let requestUrl = c.Request.Url
        let referrer = option c.Request.Referer
        let stylesheets = ViewContextBuilder.stylesheetTags ()
        let ctx: ViewContext =
            { CurrentUser = layout.CurrentUser
              Account = layout.Account
              FlashNotice = flashNotice
              FlashAlert = flashAlert
              Platform = layout.Platform
              VapidPublicKey = layout.VapidPublicKey
              AssetPath = Assets.assetPath
              ImportmapTags = Assets.javascriptImportmapTags ()
              StylesheetTags = stylesheets.Html
              CustomStyles = layout.CustomStyles
              CableUrl = "/cable"
              BaseUrl = baseUrl
              RequestUrl = requestUrl
              Referrer = referrer
              LastRoomVisitedId = layout.LastRoomVisitedId
              AppVersion = layout.AppVersion }
        render ctx

    /// A page rendered in the application layout: `text/html`, plus the `Link` preload header
    /// `stylesheet_link_tag` adds (`config.action_view.preload_links_header`).
    let page (c: Ctx) (status: int) (html: RecordedPage) : Response =
        let links = (ViewContextBuilder.stylesheetTags ()).PreloadLinks
        let existing = match c.Headers.Get "link" with null -> "" | value -> value
        c.SetHeader("link", Assets.appendPreloadLinks existing links)
        ViewContextBuilder.renderRecorded c status Format.Html html

    /// A page rendered in turbo-rails' frame layout (no stylesheets, so no `Link` header).
    let frame (c: Ctx) (status: int) (html: RecordedPage) : Response = ViewContextBuilder.renderRecorded c status Format.Html html

    /// Renders a page in the application layout without the implicit render's template lookup: an
    /// explicit `render template:` answers HTML whatever the request's format.
    let pageInAnyFormat (c: Ctx) (status: int) (full: ViewContext -> RecordedPage) : Task<Result<Response, Error>> =
        task {
            match! load c with
            | Error e -> return Error e
            | Ok layout ->
                let html = render layout c full
                return Ok(page c status html)
        }

    /// Renders a page in the application layout, or, for templates that expose their `head`/`content`
    /// blocks, turbo-rails' frame layout for a Turbo-Frame request
    /// (`layout -> { "turbo_rails/frame" if turbo_frame_request? }`), without the template lookup.
    let pageOrFrameInAnyFormat
        (c: Ctx)
        (status: int)
        (full: ViewContext -> RecordedPage)
        (frameLayout: ViewContext -> RecordedPage)
        : Task<Result<Response, Error>> =
        task {
            match! load c with
            | Error e -> return Error e
            | Ok layout ->
                if c.IsTurboFrameRequest then
                    let html = render layout c frameLayout
                    return Ok(frame c status html)
                else
                    let html = render layout c full
                    return Ok(page c status html)
        }

    /// `pageOrFrameInAnyFormat` after the implicit render's template lookup.
    let pageOrFrame
        (c: Ctx)
        (status: int)
        (full: ViewContext -> RecordedPage)
        (frameLayout: ViewContext -> RecordedPage)
        : Task<Result<Response, Error>> =
        task {
            match ViewContextBuilder.findTemplate c Format.Html with
            | Error e -> return Error e
            | Ok() -> return! pageOrFrameInAnyFormat c status full frameLayout
        }
