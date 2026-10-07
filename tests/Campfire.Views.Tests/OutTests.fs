// Tests of the rendering core (Core/Out.fs): there is no Rust counterpart, askama's writer being a
// `fmt::Write` on a `String`. These pin what the templates rely on: static text is copied as bytes,
// values are ERB-escaped, safe text isn't escaped twice, and a page records its big fragments.
module Campfire.Views.Tests.OutTests

open System
open System.Text
open Xunit
open Campfire.Views

let private lit = Utf8.lit "<p class=\"é\">"

[<Fact>]
let ``static text is copied as its utf-8 bytes`` () =
    let rendered = Render.plain (fun w -> w.Lit lit)
    Assert.Equal<byte[]>(Encoding.UTF8.GetBytes "<p class=\"é\">", rendered)

[<Fact>]
let ``text is erb escaped and raw text is not`` () =
    Assert.Equal("&lt;b&gt; &amp; &quot;q&quot; &#39;s&#39; é 😀", Render.text (fun w -> w.Text "<b> & \"q\" 's' é 😀"))
    Assert.Equal("<b> & \"q\" é 😀", Render.text (fun w -> w.Raw "<b> & \"q\" é 😀"))
    Assert.Equal("", Render.text (fun w -> w.Text(null: string | null)))

[<Fact>]
let ``safe html is written as it is, never escaped a second time`` () =
    let safe = Html.Text "a < b"
    Assert.Equal("a &lt; b", safe.ToString())
    Assert.Equal("[a &lt; b]", Render.text (fun w ->
        w.Byte(byte '[')
        w.Raw safe
        w.Byte(byte ']')))
    Assert.Equal("&amp;lt;", Render.text (fun w -> w.Text "&lt;"))

[<Fact>]
let ``integers are written in decimal`` () =
    Assert.Equal("0 -7 9223372036854775807 -9223372036854775808", Render.text (fun w ->
        w.Int 0L
        w.Byte(byte ' ')
        w.Int -7L
        w.Byte(byte ' ')
        w.Int Int64.MaxValue
        w.Byte(byte ' ')
        w.Int Int64.MinValue))

[<Fact>]
let ``a writer grows past its first buffer and keeps what it holds`` () =
    let big = String('é', 20000)
    let text = Render.text (fun w ->
        w.Raw "start"
        w.Text big
        w.Raw(String('x', 100000))
        w.Raw "end")
    Assert.Equal("start" + big + String('x', 100000) + "end", text)

[<Fact>]
let ``a writer comes back from the pool empty`` () =
    for _ in 1..3 do
        let out = Out.Rent 0
        Assert.Equal(0, out.Length)
        out.Raw "dirty"
        Out.Return out
    // A render nested in another takes a writer of its own.
    let nested =
        Render.text (fun w ->
            w.Raw "outer["
            w.Raw(Render.text (fun inner -> inner.Raw "inner"))
            w.Raw "]")
    Assert.Equal("outer[inner]", nested)

[<Fact>]
let ``a writer is returned when the template throws`` () =
    Assert.Throws<InvalidOperationException>(fun () -> Render.plain (fun w -> w.Raw "x"; raise (InvalidOperationException())) |> ignore) |> ignore
    Assert.Equal("fine", Render.text (fun w -> w.Raw "fine"))

[<Fact>]
let ``fragments of a kilobyte or more are recorded and shorter ones are copied`` () =
    let big = Fragment(String('b', 1024))
    let small = Fragment(String('s', 1023))
    let page =
        Render.page 0 (fun w ->
            w.Raw "<ul>"
            w.Fragment big
            w.Raw "|"
            w.Fragment small
            w.Raw "</ul>")
    Assert.Equal("<ul>|" + String('s', 1023) + "</ul>", Encoding.UTF8.GetString page.Text)
    Assert.Equal(1, page.Fragments.Length)
    let struct (offset, fragment) = page.Fragments[0]
    Assert.Equal(4, offset)
    Assert.Same(big, fragment)
    Assert.Equal("<ul>" + String('b', 1024) + "|" + String('s', 1023) + "</ul>", page.ToString())

[<Fact>]
let ``outside a page nothing is recorded`` () =
    let big = Fragment(String('b', 5000))
    Assert.Equal(String('b', 5000), Render.text (fun w -> w.Fragment big))
    Assert.Equal(String('b', 5000), (Render.page 0 (fun w -> w.Fragment big)).ToString())

[<Fact>]
let ``a page written into another keeps its fragments`` () =
    let big = Fragment(String('b', 2000))
    let inner = Render.page 0 (fun w -> w.Raw "<a>"; w.Fragment big; w.Raw "</a>")
    let outer = Render.page 0 (fun w -> w.Raw "<frame>"; w.Page inner; w.Raw "</frame>")
    Assert.Equal("<frame><a>" + String('b', 2000) + "</a></frame>", outer.ToString())
    Assert.Equal(1, outer.Fragments.Length)
    let struct (offset, fragment) = outer.Fragments[0]
    Assert.Equal("<frame><a>".Length, offset)
    Assert.Same(big, fragment)

[<Fact>]
let ``escaping a lone surrogate writes the replacement character`` () =
    // .NET strings can hold what Rust's can't; the writer must not throw on it.
    Assert.Equal("a�b", Render.text (fun w -> w.Text "a\uD800b"))

[<Fact>]
let ``a page renders into a pooled buffer and allocates little more than itself`` () =
    // The layout page of the reference's facts: ~21 KB of HTML written by ~60 template and helper calls.
    let name = "welcome"
    let ctx = Facts.context name Facts.Request.none
    let render () =
        Render.page 0 (fun w -> Templates.Welcome.Show.render w ctx "David")
    let length = (render ()).Length
    for _ in 1..200 do
        render () |> ignore
    let before = GC.GetAllocatedBytesForCurrentThread()
    let rounds = 500
    for _ in 1..rounds do
        render () |> ignore
    let perRender = (GC.GetAllocatedBytesForCurrentThread() - before) / int64 rounds
    printfn "welcome page: %d bytes rendered, %d bytes allocated per render" length perRender
    // The exact-size copy of the page is the one big allocation; the rest is the helpers' attribute
    // lists and a few small strings.
    Assert.True(perRender < int64 length + 12000L, $"{perRender} bytes allocated for a {length} byte page")
