// Port of rust/crates/views/templates/layouts/application_wrapper.html
/// The application layout around already-rendered page parts, for templates that don't call the
/// layout themselves (Rails picks the layout per request). Each part is what the ERB's `content_for`
/// / `yield` would have produced. (In askama this template `{% extends %}` the layout and fills each
/// block with its part; the text outside the blocks is dropped.)
module Campfire.Views.Templates.Layouts.ApplicationWrapper

open Campfire.Views

let render
    (w: Out)
    (ctx: ViewContext)
    (pageTitle: string option)
    (bodyClass: string option)
    (head: Html)
    (nav: Html)
    (content: Html)
    (footer: Html)
    (sidebar: Html)
    : unit =
    Application.render
        w
        ctx
        pageTitle
        bodyClass
        (fun w -> w.Raw head)
        (fun w -> w.Raw nav)
        (fun w -> w.Raw content)
        (fun w -> w.Raw footer)
        (fun w -> w.Raw sidebar)
