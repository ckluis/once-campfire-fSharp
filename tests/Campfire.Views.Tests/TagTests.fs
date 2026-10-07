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
let ``a numbered value renders as its text and number, and data and aria names are made once`` () =
    let numbered = Tag.attrs().Id(Turbo.domIdValue "rooms_open" 42L (Some "list")).Style(Numbered("view-transition-name: avatar-", 7L))
    let strings = Tag.attrs().Id(Turbo.domId "rooms_open" 42L (Some "list")).Style("view-transition-name: avatar-" + string 7L)
    Assert.Equal(" id=\"list_rooms_open_42\" style=\"view-transition-name: avatar-7\"", renderOf numbered)
    Assert.Equal(renderOf strings, renderOf numbered)
    Assert.Equal(" id=\"user_5\"", renderOf (Tag.attrs().Id(Turbo.domIdValue "user" 5L None)))
    Assert.Equal("_user_5", Turbo.domId "user" 5L (Some ""))
    Assert.Equal("user_5", Turbo.domId "user" 5L None)
    Assert.Equal("role_user_-3", Render.text (fun w -> Turbo.writeDomId w "user" -3L (Some "role")))
    Assert.Equal("list_rooms_open_42", numbered.GetStr("id").Value)
    // No string per call: the same name object comes back.
    let first = Tag.attrs().Data("turbo_frame", "_top").NameAt 0
    let second = Tag.attrs().Data("turbo_frame", "_top").NameAt 0
    Assert.Equal("data-turbo-frame", first)
    Assert.Same(first, second)
    Assert.Same(Tag.attrs().Aria("labelled_by", "x").NameAt 0, Tag.attrs().Aria("labelled_by", true).NameAt 0)

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

[<Fact>]
let ``a dom_id prefix that isn't a plain identifier is escaped, not written raw`` () =
    match Turbo.domIdValue "rooms_open" 42L (Some "list") with
    | Numbered(text, 42L) -> Assert.Equal("list_rooms_open_", text)
    | other -> failwithf "plain prefix took the escaped path: %A" other
    match Turbo.domIdValue "x\"><script>" 1L None with
    | Text text -> Assert.Equal("x\"><script>_1", text)
    | other -> failwithf "hostile prefix took the raw path: %A" other
