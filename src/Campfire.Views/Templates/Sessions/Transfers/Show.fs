// Port of rust/crates/views/templates/sessions/transfers/show.html (reference/app/views/sessions/transfers/show.html.erb)
/// `sessions/transfers/show.html.erb`: an auto-submitting form that PUTs back to the page's own URL
/// (`url_for({})`, i.e. `session_transfer_path(id)`).
module Campfire.Views.Templates.Sessions.Transfers.Show

open Campfire.Views
open Campfire.Views.Helpers

let private t0 = Utf8.lit "\n"
let private t1 = Utf8.lit "\n"

/// `content_for :head` (empty).
let head: Out -> unit = ignore

/// The page itself. `action` is the request path, `session_transfer_path(params[:id])`. A block-less
/// `form_with` renders only the opening tag and hidden fields, never `</form>`.
let content (w: Out) (action: string) : unit =
    w.Lit t0
    (Forms.formWith action).Method("put").AutoSubmit().Open w
    w.Lit t1

let render (w: Out) (ctx: ViewContext) (action: string) : unit =
    Templates.Layouts.Application.render w ctx None None head ignore (fun w -> content w action) ignore ignore
