// Port of the #[cfg(test)] module in rust/crates/assets/src/helpers.rs
module Campfire.Assets.Tests.HelpersTests

open Xunit
open Campfire.Assets

[<Fact>]
let ``digests logical paths`` () =
    Assert.Equal("/assets/campfire-icon-3d9986c5.png", Assets.assetPath "campfire-icon.png")
    Assert.Equal("/assets/bot-8a69692e.svg", Assets.imagePath "bot.svg")
    Assert.Equal("/assets/56k-67359aa6.mp3", Assets.audioPath "56k.mp3")
    let digested = (Assets.digestedPath "screenshots/android-chat.png").Value
    Assert.Equal($"/assets/{digested}", Assets.assetPath "screenshots/android-chat.png")

[<Fact>]
let ``keeps tails and passes through urls and absolute paths`` () =
    Assert.Equal("/assets/bot-8a69692e.svg?v=1#x", Assets.assetPath "bot.svg?v=1#x")
    Assert.Equal("https://example.com/a.png", Assets.assetPath "https://example.com/a.png")
    Assert.Equal("//cdn.example.com/a.png", Assets.assetPath "//cdn.example.com/a.png")
    Assert.Equal("data:image/png;base64,xx", Assets.assetPath "data:image/png;base64,xx")
    Assert.Equal("/rooms/1", Assets.assetPath "/rooms/1")
    Assert.Equal("", Assets.assetPath "")

[<Fact>]
let ``appends type extensions`` () =
    Assert.Equal(Assets.stylesheetPath "base.css", Assets.stylesheetPath "base")
    Assert.StartsWith("/assets/application-", Assets.javascriptPath "application")

[<Fact>]
let ``urls join the base url`` () =
    let image = Assets.imagePath "add.svg"
    Assert.Equal($"https://chat.example.com{image}", Assets.imageUrl "https://chat.example.com" "add.svg")

[<Fact>]
let ``missing assets are errors`` () =
    let error =
        match Assets.tryAssetPath "nope.png" with
        | Error e -> Helpers.MissingAssetError.message e
        | Ok path -> failwith $"found {path}"
    Assert.Equal("The asset 'nope.png' was not found in the load path.", error)
    // assetPath raises where tryAssetPath returns the error, as Rails raises while rendering.
    let raised = Assert.Throws<System.InvalidOperationException>(fun () -> Assets.assetPath "nope.png" |> ignore)
    Assert.Equal(error, raised.Message)
