// Ports of the #[cfg(test)] modules in rust/crates/views/src/{recorded,sized}.rs. Askama's templates
// are written with `Out` here, and a fragment is noted by `Out.Fragment` instead of by the pointer
// check on the bytes a `Display` writes, so what is tested is the same: which writes are recorded, and
// that the page is the bytes a plain render gives.
module Campfire.Views.Tests.RecordedTests

open System
open System.Text
open Xunit
open Campfire.Views

/// `MIN_RECORDED`.
let private minRecorded = 1024

let private fragment (n: int) : Fragment = Fragment($"<li id=\"{n}\">{String('x', minRecorded + n)}</li>")

/// `List` in recorded.rs: `<ul>{% for item in items %}\n  {{ item|safe }}{% endfor %}\n</ul>{{ unhanded|safe }}`.
let private list (items: Fragment list) (unhanded: string) (w: Out) : unit =
    w.Raw "<ul>"
    for item in items do
        w.Raw "\n  "
        w.Fragment item
    w.Raw "\n</ul>"
    w.Raw unhanded

let private frame (content: RecordedPage) (w: Out) : unit =
    w.Raw "<frame>"
    w.Page content
    w.Raw "</frame>"

let private items () : Fragment list =
    [ fragment 1; Fragment "<li>small</li>"; fragment 2 ]

[<Fact>]
let ``records handed fragments and renders the same bytes`` () =
    let items = items ()
    let unhanded = (fragment 3).ToString()
    let page = Render.page 0 (list items unhanded)
    Assert.Equal(Encoding.UTF8.GetString(Render.plain (list items unhanded)), page.ToString())
    // The small one is text, and so is the one written without being handed over.
    Assert.Equal(2, page.Fragments.Length)
    let struct (first, firstFragment) = page.Fragments[0]
    let struct (second, secondFragment) = page.Fragments[1]
    Assert.Same(items[0], firstFragment)
    Assert.Same(items[2], secondFragment)
    let text = Encoding.UTF8.GetString page.Text
    Assert.Equal("<ul>\n  ", text.Substring(0, first))
    Assert.Equal("\n  <li>small</li>\n  ", text.Substring(first, second - first))

[<Fact>]
let ``a page written into another keeps its fragments`` () =
    let items = items ()
    let content = Render.page 0 (list items "")
    let framed = Render.page 0 (frame content)
    Assert.Equal($"<frame>{content}</frame>", framed.ToString())
    Assert.Equal(2, framed.Fragments.Length)

[<Fact>]
let ``equal bytes elsewhere are not the fragment`` () =
    // Handing a fragment to a writer that isn't recording (or rendering equal bytes as text) notes nothing.
    let item = fragment 1
    let copy = item.ToString()
    let page = Render.page 0 (list [] copy)
    Assert.Empty page.Fragments
    let page = Render.page 0 (list [ item ] copy)
    Assert.Equal(1, page.Fragments.Length)
    Assert.Equal($"<ul>\n  {item}\n</ul>{copy}", page.ToString())

[<Fact>]
let ``outside a recording nothing is held`` () =
    let item = fragment 1
    Assert.Equal(item.ToString(), Render.text (fun w -> w.Fragment item))
    Assert.Empty (Render.page 0 (fun w -> w.Raw "no fragments")).Fragments

// sized.rs

[<Fact>]
let ``reserves the last length with headroom up to a cap`` () =
    let size = RenderSize()
    let page (length: int) (w: Out) = w.Raw($"<p>{String('x', length)}</p>")
    size.Render(page 8000) |> ignore
    Assert.Equal(8007 + 8007 / 8, size.Capacity 0)
    // A shorter page sizes the next render: one long page doesn't stay reserved.
    size.Render(page 7000) |> ignore
    Assert.Equal(7007 + 7007 / 8, size.Capacity 0)
    size.Render(page (2 * RenderSize.Max)) |> ignore
    Assert.Equal(RenderSize.Max, size.Capacity 0)
    // The size hint is a floor.
    Assert.Equal(5000, RenderSize().Capacity 5000)

[<Fact>]
let ``each call site has its own size`` () =
    let short = RenderSize()
    let long = RenderSize()
    long.Render(fun w -> w.Raw(String('x', 4000))) |> ignore
    short.Render(fun w -> w.Raw "short") |> ignore
    Assert.True(short.Capacity 0 < 100)
    Assert.True(long.Capacity 0 > 4000)

// layouts.rs: `frame`

[<Fact>]
let ``the turbo frame layout around a page keeps the pages fragments`` () =
    let items = items ()
    let head (w: Out) = w.Raw "<title>t</title>"
    let framed = Layouts.frame (RenderSize()) head (list items "")
    let plain = Render.text (fun w -> Templates.Layouts.TurboRails.Frame.render w head (list items ""))
    Assert.Equal(plain, framed.ToString())
    Assert.Equal(2, framed.Fragments.Length)
    Assert.StartsWith("<html>\n  <head>\n    <title>t</title>\n  </head>\n  <body>\n    <ul>", framed.ToString())

[<Fact>]
let ``the turbo frame layout sizes the content from the pages last render`` () =
    let head (w: Out) = w.Raw "<title>t</title>"
    let big (w: Out) = w.Raw(String('x', 50_000))
    let size = RenderSize()
    Assert.Equal(0, size.Capacity 0)
    let first = Layouts.frame size head big
    // The next frame of this page starts from what the last one took (an eighth more), not from nothing.
    Assert.Equal(50_000 + 50_000 / 8, size.Capacity 0)
    Assert.Equal(first.ToString(), (Layouts.frame size head big).ToString())
