// Port of the tests in rust/crates/richtext/src/dom.rs
module Campfire.RichText.Tests.DomTests

open Xunit
open Campfire.RichText
open Campfire.RichText.Tests.Support

let private roundtrip (html: string) : string =
    let dom = Dom()
    let f = okP (dom.ParseFragment html)
    dom.ToHtml f

[<Fact>]
let ``serializes_like_nokogiri`` () =
    Assert.Equal("x", roundtrip "<td>x</td>")
    Assert.Equal("<p></p><table><tbody><tr><td>a</td></tr></tbody></table>", roundtrip "<p><table><tr><td>a</td></tr></table>")
    Assert.Equal(
        "<a title=\"a<b>c\" href=\"x&amp;y&nbsp;z\">t&lt;&nbsp;&gt;\"'</a>",
        roundtrip "<a title='a<b>c' href=\"x&y z\">t&lt; >\"'</a>"
    )
    let dom = Dom()
    let f = okP (dom.ParseFragment "<a title='a<b>c'>d>e</a>")
    Assert.Equal("<a title=\"a&lt;b&gt;c\">d&gt;e</a>", dom.ToHtmlWithEscapedAttributeBrackets f)
    Assert.Equal("<pre>\nx</pre>", roundtrip "<pre>\n\nx</pre>")
    Assert.Equal("<noscript><b>x</b></noscript>", roundtrip "<noscript><b>x</b></noscript>")
    Assert.Equal("<!--?php x ?-->", roundtrip "<?php x ?>")
    Assert.Equal("<svg viewBox=\"0 0 1 1\"><clipPath></clipPath></svg>", roundtrip "<SVG viewBox='0 0 1 1'><CLIPPATH/></SVG>")

[<Fact>]
let ``drops_only_a_leading_byte_order_mark`` () =
    Assert.Equal("﻿x", roundtrip "﻿﻿x")
    Assert.Equal("<script></script>﻿x", roundtrip "<script></script>﻿x")

let private parse (html: string) : Result<unit, ParseError> = Dom().ParseFragment html |> Result.map ignore

let private numberedAttributes (first: int) (last: int) : string =
    [ for i in first..last -> $"a{i}={i}" ] |> String.concat " "

// Gumbo's answers here come from Nokogiri 1.19.4 in the reference image.

[<Fact>]
let ``enforces_gumbo_tree_depth_limit`` () =
    let b n = String.replicate n "<b>"
    Assert.Equal(Ok(), parse (b 400))
    Assert.Equal(Error TreeDepthExceeded, parse (b 401))
    Assert.Equal(Error TreeDepthExceeded, parse (b 401 + "x"))
    Assert.Equal(Error TreeDepthExceeded, parse (b 400 + "<p>"))
    Assert.Equal(Error TreeDepthExceeded, parse (b 397 + "<table><td>"))

[<Fact>]
let ``counts_open_elements_as_gumbo_does_rather_than_the_final_trees_depth`` () =
    let b n = String.replicate n "<b>"
    let s n = String.replicate n "<span>"
    let div n = String.replicate n "<div>"
    // A void element never goes on the stack of open elements
    Assert.Equal(Ok(), parse (b 400 + "<br>"))
    Assert.Equal(Ok(), parse (b 400 + "</p>"))
    // Closing everything again doesn't undo having been too deep
    Assert.Equal(Error TreeDepthExceeded, parse (b 401 + String.replicate 401 "</b>"))
    // The adoption agency moves blocks back up, and what counts is how deep they were
    Assert.Equal(Ok(), parse (String.replicate 3 ("<b>" + s 300 + div 10 + "</b>")))
    Assert.Equal(Error TreeDepthExceeded, parse ("<b>" + s 390 + div 10 + "</b>" + div 300))
    // Text pending in a table reopens the <b>s past the limit when the input ends, and Gumbo
    // doesn't check after that
    let bs = [ for i in 1..399 -> $"<b id={i}>" ] |> String.concat ""
    let reopened = $"<p>{bs}</p><div><div><table>x"
    Assert.Equal(Ok(), parse reopened)
    Assert.Equal(Error TreeDepthExceeded, parse (reopened + "<!---->"))

[<Fact>]
let ``enforces_gumbo_attribute_limit`` () =
    Assert.Equal(Ok(), parse $"<p {numberedAttributes 1 400}>x</p>")
    Assert.Equal(Error TooManyAttributes, parse $"<p {numberedAttributes 1 401}>x</p>")
    Assert.Equal(Ok(), parse $"""<p {String.replicate 1000 "a=1 "}>x</p>""")
    Assert.Equal(Ok(), parse $"<textarea><p {numberedAttributes 1 401}>")

[<Fact>]
let ``counts_attributes_as_gumbos_tokenizer_does`` () =
    let attributes = numberedAttributes 1 400
    // Before dropping a duplicate, on end tags, and on a tag the input ends inside
    Assert.Equal(Error TooManyAttributes, parse $"<p {attributes} a1=again>x</p>")
    Assert.Equal(Error TooManyAttributes, parse $"<p>x</p {attributes} a401>")
    Assert.Equal(Error TooManyAttributes, parse $"<p {attributes} a401")

[<Fact>]
let ``keeps_what_the_tokenizer_already_deduplicated`` () =
    Assert.Equal("<p title=\"a\" id=\"c\">x</p>", roundtrip "<p title=a TITLE=b id=c title=d>x</p>")
    Assert.Equal(
        "<svg xlink:href=\"a\" href=\"b\" viewBox=\"c\"><a xlink:href=\"d\">x</a></svg>",
        roundtrip "<svg xlink:href=a href=b viewbox=c><a xlink:href=d>x</a></svg>"
    )
