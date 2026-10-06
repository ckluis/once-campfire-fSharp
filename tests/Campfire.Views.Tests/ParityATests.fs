// The cases of rust/crates/views/tests/parity_a.rs for the templates ported so far: DOM parity with golden
// renders from the reference app (`reference-tools/views/a/render.rb`, in rust/crates/views/tests/golden/a,
// read here and never written). Each case rebuilds, from facts.json, the view-model a controller would pass,
// renders it with the F# template, and compares normalized token streams.
module Campfire.Views.Tests.ParityATests

open System.Text.Json
open Xunit
open Campfire.Views
open Campfire.Views.Tests.Dom
open Campfire.Views.Tests.Facts

let private assertParity (name: string) (ext: string) (rendered: string) =
    let expected = normalizeHtml (golden name ext)
    let actual = normalizeHtml rendered
    match diff expected actual with
    | Some report -> failwith $"{name}: DOM differs from the reference\n{report}"
    | None -> ()

[<Fact>]
let ``pwa partials`` () =
    for ua in facts.Value.GetProperty("user_agents").EnumerateObject() |> Seq.map (fun p -> p.Name) do
        for kind in [ "browser_settings"; "system_settings"; "install_instructions" ] do
            let name = $"{kind}_{ua}"
            let ctx = context name Request.none
            let html =
                Render.text (fun w ->
                    match kind with
                    | "browser_settings" -> Templates.Pwa._BrowserSettings.render w ctx
                    | "system_settings" -> Templates.Pwa._SystemSettings.render w ctx
                    | _ -> Templates.Pwa._InstallInstructions.render w ctx)
            assertParity name "html" html

[<Fact>]
let ``users partials`` () =
    let name = "autocompletables_template"
    assertParity name "html" (Render.text (fun w -> Templates.Users.Autocompletables._Template.render w (context name Request.none)))

    let name = "mention"
    assertParity name "html" (Render.text (fun w -> Templates.Users._Mention.render w (context name Request.none) (mentionUser name "JZ")))

[<Fact>]
let ``welcome show`` () =
    // The first page to use the layout: its parity covers the layout and the lightbox.
    let name = "welcome"
    let ctx = context name Request.none
    let html = Render.text (fun w -> Templates.Welcome.Show.render w ctx ctx.CurrentUser.Value.Name)
    assertParity name "html" html

[<Fact>]
let ``application layout wrapper matches extended pages`` () =
    // A page rendered through the wrapper with its parts equals the page calling the layout directly.
    let name = "welcome"
    let ctx = context name Request.none
    let userName = ctx.CurrentUser.Value.Name
    let direct = Render.text (fun w -> Templates.Welcome.Show.render w ctx userName)
    let wrapped =
        Render.text (fun w ->
            Layouts.Application.render
                w
                ctx
                { Layouts.Application.create (Render.html (fun w -> Templates.Welcome.Show.content w ctx userName)) with
                    PageTitle = Templates.Welcome.Show.pageTitle
                    BodyClass = Templates.Welcome.Show.bodyClass
                    Sidebar = Render.html Templates.Welcome.Show.sidebar })
    Assert.Equal(direct, wrapped)
    assertParity name "html" wrapped

[<Fact>]
let ``a flash notice and a flash alert render in the layout`` () =
    let name = "welcome"
    let notice = Render.text (fun w -> Templates.Welcome.Show.render w (context name { Request.none with FlashNotice = Some "Saved <ok>" }) "X")
    Assert.Contains("<span class=\"for-screen-reader\" role=\"alert\" aria-atomic=\"true\">Saved &lt;ok&gt;</span>", notice)
    Assert.DoesNotContain("--flash-background", notice)
    let alert = Render.text (fun w -> Templates.Welcome.Show.render w (context name { Request.none with FlashAlert = Some "No" }) "X")
    Assert.Contains("style=\"--flash-background: var(--color-negative)\"", alert)
    Assert.Contains("alert-", alert)
