// Port of rust/crates/views/tests/messages_views.rs: DOM parity of the message views with the reference app
// (goldens in rust/crates/views/tests/golden/b, read here and never written).
module Campfire.Views.Tests.MessagesViewsTests

open Xunit
open Campfire.Views
open Campfire.Views.Differential.Inputs
open Campfire.Views.Tests.GoldenB

[<Fact>]
let ``show text message`` () =
    let g = golden "messages_show_text"
    let message = messageView g.Input
    g.AssertContent(Render.text (fun w -> Templates.Messages.Show.render w g.Context message))

/// The partial is cached once for every request, so nothing in it may come from the request's Host header (README,
/// Known differences).
[<Fact>]
let ``message partial is the same on every host`` () =
    let g = golden "messages_show_text"
    let message = messageView g.Input
    let render (g: Golden) = Render.text (fun w -> Templates.Messages._Message.render w g.Context message)
    let onTheReferenceHost = render g
    Assert.Equal(onTheReferenceHost, render { g with BaseUrl = Some "https://evil.example" })
    Assert.Contains($"data-copy-to-clipboard-url-value=\"/rooms/{message.RoomId}/@{message.Id}\"", onTheReferenceHost)

[<Fact>]
let ``show image message`` () =
    let g = golden "messages_show_image"
    let message = messageView g.Input
    g.AssertContent(Render.text (fun w -> Templates.Messages.Show.render w g.Context message))

[<Fact>]
let ``index`` () =
    let g = golden "messages_index"
    let messages = arr (g.InputAt "messages") |> List.map messageItem
    g.AssertContent(Render.text (fun w -> Templates.Messages.Index.render w g.Context messages))

[<Fact>]
let ``edit text message`` () =
    let g = golden "messages_edit_text"
    let edit = editView g.Input
    g.AssertContent(Render.text (fun w -> Templates.Messages.Edit.render w g.Context edit))

[<Fact>]
let ``edit attachment message`` () =
    let g = golden "messages_edit_attachment"
    let edit = editView g.Input
    g.AssertContent(Render.text (fun w -> Templates.Messages.Edit.render w g.Context edit))

[<Fact>]
let ``create stream`` () =
    let g = golden "messages_create"
    let message = messageItem (g.InputAt "message")
    let roomKind = roomKind (g.InputAt "room_kind")
    g.AssertDom(Render.text (fun w -> Templates.Messages.CreateTurboStream.render w g.Context message roomKind))

[<Fact>]
let ``destroy stream`` () =
    let g = golden "messages_destroy"
    let message = messageView g.Input
    g.AssertDom(Render.text (fun w -> Templates.Messages.DestroyTurboStream.render w message))

[<Fact>]
let ``room not found`` () =
    let g = golden "messages_room_not_found"
    g.AssertContent(Render.text Templates.Messages.RoomNotFound.render)

[<Fact>]
let ``boosts index`` () =
    let g = golden "messages_boosts_index"
    let message = messageView g.Input
    g.AssertContent(Render.text (fun w -> Templates.Messages.Boosts.Index.render w g.Context message))

[<Fact>]
let ``new boost`` () =
    let g = golden "messages_boosts_new"
    let message = messageView (g.InputAt "message")
    let user = userView (g.InputAt "user")
    g.AssertContent(Render.text (fun w -> Templates.Messages.Boosts.New.render w g.Context message user))

[<Fact>]
let ``by bots index json`` () =
    let g = golden "messages_by_bots_index"
    let input = arr g.Input |> List.map messageJson
    Assert.Equal(str (get g.Json "raw"), MessagesJson.byBotsIndex input)

[<Fact>]
let ``by bots show json`` () =
    let g = golden "messages_by_bots_show"
    Assert.Equal(str (get g.Json "raw"), MessagesJson.byBotsShow (messageJson g.Input))

[<Fact>]
let ``boosts by bots show json`` () =
    let g = golden "messages_boosts_by_bots_show"
    Assert.Equal(str (get g.Json "raw"), MessagesJson.boostsByBotsShow (boostJson g.Input))
