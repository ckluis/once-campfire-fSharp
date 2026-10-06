// Port of rust/crates/views/tests/rooms_views.rs (`display_names` is in MessagesTests): DOM parity of the room views
// with the reference app (goldens in rust/crates/views/tests/golden/b, read here and never written).
module Campfire.Views.Tests.RoomsViewsTests

open Xunit
open Campfire.Views
open Campfire.Views.Templates
open Campfire.Views.Differential.Inputs
open Campfire.Views.Tests.GoldenB

let private show (name: string) =
    let g = golden name
    let show = showView g.Input
    g.AssertDom(Render.text (fun w -> Rooms.ShowPage.render w g.Context show))

/// A room page as it's served, recorded, is what a plain render gives, and each of its messages is a fragment the kit
/// gets as it is: rendered into the fragment cache first, then read from it.
[<Fact>]
let ``show recorded keeps every message as a fragment`` () =
    let g = golden "rooms_show_member"
    let show = showView g.Input
    Assert.NotEmpty show.Messages
    let cache = FragmentCache FragmentCacheLimits.DefaultMaxBytes
    let size = RenderSize()
    for round in [ "cold"; "warm" ] do
        let ctx = g.Context
        FragmentCache.withCache cache (fun () ->
            let page (w: Out) = Rooms.ShowPage.render w ctx show
            let head (w: Out) = Rooms.ShowPage.head w show
            let content (w: Out) = Rooms.ShowPage.content w ctx show
            let recorded = size.Render page
            let plain = Render.text page
            Assert.Equal(plain, recorded.ToString())
            Assert.Equal(show.Messages.Length, recorded.Fragments.Length)

            let framed = Layouts.frame head content
            let plainFrame =
                Render.text (fun w ->
                    Layouts.TurboRails.Frame.render w (fun w -> w.Raw(Render.text head)) (fun w -> w.Raw(Render.text content)))
            Assert.Equal(plainFrame, framed.ToString())
            Assert.Equal(show.Messages.Length, framed.Fragments.Length)
            ignore round)

[<Fact>]
let ``show closed room`` () = show "rooms_show_closed"

[<Fact>]
let ``show original room with invitation`` () = show "rooms_show_original"

[<Fact>]
let ``show direct room`` () = show "rooms_show_direct"

[<Fact>]
let ``show room as member`` () = show "rooms_show_member"

[<Fact>]
let ``show room around message`` () = show "rooms_show_at_message"

[<Fact>]
let ``new open room`` () =
    let g = golden "rooms_opens_new"
    let form = openFormView g.Input
    g.AssertDom(Render.text (fun w -> Rooms.Opens.New.render w g.Context form))

[<Fact>]
let ``edit open room`` () =
    for name in [ "rooms_opens_edit"; "rooms_opens_edit_member" ] do
        let g = golden name
        let form = openFormView g.Input
        g.AssertDom(Render.text (fun w -> Rooms.Opens.Edit.render w g.Context form))

[<Fact>]
let ``new closed room`` () =
    let g = golden "rooms_closeds_new"
    let form = closedFormView g.Input
    g.AssertDom(Render.text (fun w -> Rooms.Closeds.New.render w g.Context form))

[<Fact>]
let ``edit closed room`` () =
    for name in [ "rooms_closeds_edit"; "rooms_closeds_edit_member" ] do
        let g = golden name
        let form = closedFormView g.Input
        g.AssertDom(Render.text (fun w -> Rooms.Closeds.Edit.render w g.Context form))

[<Fact>]
let ``new direct room`` () =
    let g = golden "rooms_directs_new"
    g.AssertDom(Render.text (fun w -> Rooms.Directs.New.render w g.Context))

[<Fact>]
let ``edit direct room`` () =
    let g = golden "rooms_directs_edit"
    let edit = directEditView g.Input
    g.AssertDom(Render.text (fun w -> Rooms.Directs.Edit.render w g.Context edit))

[<Fact>]
let ``involvement`` () =
    for name in [ "rooms_involvements_show"; "rooms_involvements_show_direct" ] do
        let g = golden name
        let involvement = involvementView g.Input
        g.AssertContent(Render.text (fun w -> Rooms.Involvements.Show.render w g.Context involvement))

[<Fact>]
let ``refresh stream`` () =
    let g = golden "rooms_refreshes_show"
    let refresh = refreshView g.Input
    g.AssertDom(Render.text (fun w -> Rooms.Refreshes.ShowTurboStream.render w g.Context refresh))
