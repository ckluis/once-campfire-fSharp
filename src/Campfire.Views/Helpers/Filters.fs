// Port of rust/crates/views/src/helpers/filters.rs
/// Block helpers. Askama has them as filter blocks (`{% filter link_to(url, ..) %}..{% endfilter %}`)
/// where Ruby had `link_to url, class: "btn" do ... end`; here each is a function whose last
/// argument is the block, a function of the writer, written `Filters.linkTo w url options (fun w -> ..)`.
/// They are `inline`, so a block costs no closure. Those that live with their helper's module
/// (`Links.linkTo`, `Forms.buttonToBlock`, `Application.linkToZoomQrCode`, ...) are re-exported here
/// under the Rust names, so a template opens `Filters` and finds all of them.
module Campfire.Views.Helpers.Filters

open Campfire.Views
open Campfire.Views.Helpers.Tag
open Campfire.Views.Helpers.Turbo

/// `form_with(...) do |form| ... end`.
let formWith (w: Out) (form: Forms.FormWith) (content: Out -> unit) : unit = form.Wrap(w, content)

/// `link_to(url, options) do ... end`.
let inline linkTo (w: Out) (url: string) (options: Attrs) ([<InlineIfLambda>] content: Out -> unit) : unit =
    Links.linkTo w url options content

/// `button_to(url, options) do ... end`; `options` may include `method`.
let inline buttonTo (w: Out) (url: string) (options: Attrs) ([<InlineIfLambda>] content: Out -> unit) : unit =
    Forms.buttonToBlock w url options content

/// `form.button(options) do ... end`.
let inline button (w: Out) (options: Attrs) ([<InlineIfLambda>] content: Out -> unit) : unit =
    contentTagBlock w "button" (Forms.buttonOptions options) content

/// `tag.name(options) do ... end` / `content_tag(name, options) do ... end`.
let inline contentTag (w: Out) (name: string) (options: Attrs) ([<InlineIfLambda>] content: Out -> unit) : unit =
    contentTagBlock w name options content

/// `turbo_frame_tag(id, src:, target:, **attributes) do ... end`; `src` and `target` may be in
/// `options` and are moved after the id as turbo-rails does.
let inline turboFrameTag (w: Out) (id: string) (options: Attrs) ([<InlineIfLambda>] content: Out -> unit) : unit =
    let src = options.Remove "src"
    let target = options.Remove "target"
    let asOption (value: AttrValue voption) =
        match value with
        | ValueSome v -> Some v.AsString
        | ValueNone -> None
    contentTagBlock w "turbo-frame" (turboFrameOptions id (asOption src) (asOption target) options) content

/// `sidebar_turbo_frame_tag do ... end` (the block form never passes `src:`).
let inline sidebarTurboFrameTag (w: Out) ([<InlineIfLambda>] content: Out -> unit) : unit =
    UsersHelper.sidebarTurboFrameTag w None content

/// `link_to_room(room, **attributes) do ... end`.
let inline linkToRoom (w: Out) (roomId: int64) (options: Attrs) ([<InlineIfLambda>] content: Out -> unit) : unit =
    RoomsHelper.linkToRoom w roomId options content

/// `link_to_zoom_qr_code(url) do ... end`.
let inline linkToZoomQrCode (w: Out) (url: string) ([<InlineIfLambda>] content: Out -> unit) : unit =
    Application.linkToZoomQrCode w url content

/// `button_to_copy_to_clipboard(url) do ... end`.
let inline buttonToCopyToClipboard (w: Out) (url: string) ([<InlineIfLambda>] content: Out -> unit) : unit =
    Application.buttonToCopyToClipboard w url content

/// `web_share_session_button(url, title, text) do ... end`.
let inline webShareSessionButton (w: Out) (url: string) (title: string) (text: string) ([<InlineIfLambda>] content: Out -> unit) : unit =
    Application.webShareSessionButton w url title text content

/// `user_filter_menu_tag do ... end`.
let inline userFilterMenuTag (w: Out) ([<InlineIfLambda>] content: Out -> unit) : unit = UsersHelper.userFilterMenuTag w content
