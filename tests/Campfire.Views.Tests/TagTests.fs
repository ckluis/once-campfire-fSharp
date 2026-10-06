// Ports of the #[cfg(test)] module in rust/crates/views/src/helpers/tag.rs, and of the one in
// helpers/url.rs
module Campfire.Views.Tests.TagTests

open Xunit
open Campfire.Views
open Campfire.Views.Helpers
open Campfire.Views.Helpers.Tag

/// What `attrs` render as, in `tag_options`' form.
let private renderOf (attrs: Attrs) : string = Render.text (fun w -> renderAttrs w attrs)

[<Fact>]
let ``renders attributes in order with rails value rules`` () =
    let attrs =
        Tag.attrs()
            .Class("a&b")
            .Hidden()
            .Required(false)
            .Data("turbo_permanent", true)
            .Aria("hidden", "true")
            .Tabindex(-1)
            .Attr("data-action", Html.Raw "a->\"b\"")
    let expected =
        " class=\"a&amp;b\" hidden=\"hidden\" data-turbo-permanent=\"true\" aria-hidden=\"true\" tabindex=\"-1\" data-action=\"a->&quot;b&quot;\""
    Assert.Equal(expected, renderOf attrs)
    // A copy (Rust's `view`) renders the same.
    Assert.Equal(expected, renderOf (attrs.Copy()))

[<Fact>]
let ``assignment keeps position`` () =
    let attrs = Tag.attrs().Value("x").Class("c")
    attrs.Set("value", ValueSome(Text "y"))
    Assert.Equal(" value=\"y\" class=\"c\"", renderOf attrs)
    let copy = attrs.Copy().Attr("value", "z").Attr("id", "i")
    Assert.Equal(" value=\"z\" class=\"c\" id=\"i\"", renderOf copy)

[<Fact>]
let ``default data goes where the callers data starts`` () =
    let attrs = Tag.attrs().Id("x").Data("b", "caller").Class("c").Data("a", "override")
    let defaults = [| "data-a", Text "default"; "data-z", Text "z" |]
    Assert.Equal(" id=\"x\" data-a=\"override\" data-z=\"z\" data-b=\"caller\" class=\"c\"", renderOf (attrs.WithDefaultData defaults))

[<Fact>]
let ``tags`` () =
    Assert.Equal("<textarea id=\"t\">\nx</textarea>", Render.text (fun w -> contentTag w "textarea" (Tag.attrs().Id("t")) "x"))
    Assert.Equal("<span>&lt;b&gt;</span>", Render.text (fun w -> contentTagText w "span" (Tag.attrs ()) "<b>"))
    Assert.Equal("<p>x</p>", Render.text (fun w -> contentTagBlock w "p" (Tag.attrs ()) (fun w -> w.Raw "x")))
    Assert.Equal("<turbo-frame id=\"f\"></turbo-frame>", Render.text (fun w -> builderTag w "turbo_frame" (Tag.attrs().Id("f"))))
    Assert.Equal("<meta name=\"n\">", Render.text (fun w -> builderTag w "meta" (Tag.attrs().Name("n"))))
    Assert.Equal("<img alt=\"a\" />", Render.text (fun w -> legacyTag w "img" (Tag.attrs().Alt("a"))))

[<Fact>]
let ``nil options keep their place and are never rendered`` () =
    let attrs = Tag.attrs().Attr("a", "1").AttrOpt("b", (None: string option)).Attr("c", "3")
    Assert.Equal(" a=\"1\" c=\"3\"", renderOf attrs)
    attrs.Set("b", ValueSome(Text "2"))
    Assert.Equal(" a=\"1\" b=\"2\" c=\"3\"", renderOf attrs)
    Assert.Equal(ValueSome(Text "2"), attrs.Remove "b")
    Assert.Equal(" a=\"1\" c=\"3\"", renderOf attrs)
    Assert.Equal(ValueNone, attrs.Remove "missing")

[<Fact>]
let ``boolean attributes render as their name or not at all`` () =
    Assert.Equal(" disabled=\"disabled\"", renderOf (Tag.attrs().Disabled true))
    Assert.Equal("", renderOf (Tag.attrs().Disabled false))
    // Not a boolean attribute: printed with `to_s`.
    Assert.Equal(" data-x=\"false\" aria-checked=\"true\"", renderOf (Tag.attrs().Data("x", false).Aria("checked", true)))

// helpers/url.rs

[<Fact>]
let ``builds rails query strings`` () =
    Assert.Equal("/rooms/directs?user_ids%5B%5D=5&user_ids%5B%5D=6", Url.roomsDirectsWithUsers [ 5L; 6L ])
    Assert.Equal("/x?a=1&z=a+b", Url.withQuery "/x" [ "z", Url.One "a b"; "a", Url.One "1" ])
    Assert.Equal("/x", Url.withQuery "/x" [])
