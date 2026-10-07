// Port of the tests in rust/crates/richtext/src/sanitizer.rs
module Campfire.RichText.Tests.SanitizerTests

open Xunit
open Campfire.RichText
open Campfire.RichText.Tests.Support

let private sanitize (html: string) (list: SafeList) : string = okP (Sanitizer.sanitize html list)

[<Fact>]
let ``scrubs_like_rails`` () =
    let list = SafeList.contentFilter
    Assert.Equal("<div><a>x</a></div>", sanitize "<div><a href=\"javascript:alert(1)\">x</a></div>" list)
    Assert.Equal("<a href=\"/x\">x</a>", sanitize "<a href=\"/x\" onmouseover=\"alert(1)\">x</a>" list)
    Assert.Equal("<a>x</a>", sanitize "<a href=\"data:text/html,pwned\">x</a>" list)
    Assert.Equal("<a href=\"a%20b\">x</a>", sanitize "<a href=\"a b\">x</a><!-- c -->" list)
    Assert.Equal("yz", sanitize "<svg><a>x</a></svg>y<script>z</script>" list)

/// The outputs are rails-html-sanitizer's, from the reference image.
[<Fact>]
let ``re_escapes_url_attributes_as_each_attribute_is_scrubbed`` () =
    let list = SafeList([| "img"; "a" |], [| "src"; "href"; "name"; "title"; "alt" |])
    // The blank src is removed, and the escaping it still triggers lets the href through
    Assert.Equal("<img href=\"%20javascript:alert(1)\">", sanitize "<img src=\" \" href=\" javascript:alert(1)\">" list)
    Assert.Equal("<img>", sanitize "<img href=\" javascript:alert(1)\">" list)
    Assert.Equal(
        "<a href=\"a%20b\" name=\"c%20d\" title=\"x y\">t</a>",
        sanitize "<a href=\"a b\" name=\"c d\" title=\"x y\">t</a>" list
    )
    Assert.Equal("<img src=\"a%20b\" alt=\"q\">", sanitize "<img src=\"a b\" alt=\"q\">" list)

[<Fact>]
let ``keeps_only_lexxys_highlight_colors_in_style`` () =
    let list = SafeList.actionText
    let highlight = "<mark style=\"color: var(--highlight-1);background-color: var(--highlight-bg-2);\">x</mark>"
    Assert.Equal(highlight, sanitize highlight list)
    Assert.Equal(
        "<span style=\"color: #f00;\">x</span>",
        sanitize "<span style=\"color: #f00; position: fixed; top: 0\">x</span>" list
    )
    let rgb = "<span style=\"COLOR: rgb(1 2 3 / 50%)\">x</span>"
    Assert.Equal(rgb, sanitize rgb list)
    for hostile in
        [ "background-color: url(https://evil.test/beacon)"
          "color: expression(alert(1))"
          "background-color: red; background-image: url(x)"
          "color: \\72 ed"
          "color: red /* */"
          "width: 100000px"
          "" ] do
        let html = sanitize $"<span style=\"{hostile}\">x</span>" list
        Assert.True(
            not (html.Contains "url") && not (html.Contains "expression") && not (html.Contains '\\') && not (html.Contains "width"),
            $"{hostile}: {html}"
        )
    Assert.Equal("<span>x</span>", sanitize "<span style=\"position: fixed\">x</span>" list)

[<Fact>]
let ``checks_uris_like_loofah`` () =
    Assert.False(Sanitizer.allowedUri "javascript:alert(1)")
    Assert.False(Sanitizer.allowedUri "java\nscript:alert(1)")
    Assert.False(Sanitizer.allowedUri "javascript&#58;alert(1)")
    Assert.False(Sanitizer.allowedUri "&#106;avascript:alert(1)")
    Assert.False(Sanitizer.allowedUri "javascript&colon;alert(1)")
    Assert.True(Sanitizer.allowedUri "/rooms/1")
    Assert.True(Sanitizer.allowedUri "https://example.com")
    Assert.True(Sanitizer.allowedUri "data:image/png;base64,xx")
    Assert.False(Sanitizer.allowedUri "data:text/html,xx")
    // Rails' SafeListSanitizer drops this href: CGI.unescapeHTML turns the zero-padded `&` into
    // `&#106;`, which Loofah then decodes.
    let list = SafeList.contentFilter
    Assert.Equal("<a>x</a>", sanitize "<a href=\"&amp;#0000000000038;#106;avascript:alert(1)\">x</a>" list)

[<Fact>]
let ``unescapes_html_like_cgi`` () =
    // `CGI.unescapeHTML(s)` in the reference.
    for (escaped, unescaped) in
        [ "&#65;", "A"
          "&#X41;", "A"
          "&#00000000065;", "A"
          "&#0000000000000000000000065;", "A"
          "&#x000000041;", "A"
          "&#x0000000000000000000041;", "A"
          "&#00000000000000000000000000000000106;avascript", "javascript"
          "&#0;", "\000"
          "&#65535;", "￿"
          "&#1114110;", "\U0010FFFE"
          "&#1114111;", "&#1114111;"
          "&#x10FFFF;", "&#x10FFFF;"
          "&#x110000;", "&#x110000;"
          "&#99999999999999999999;", "&#99999999999999999999;"
          "&#18446744073709551615;", "&#18446744073709551615;"
          "&#x10000000000000041;", "&#x10000000000000041;"
          "&#;", "&#;"
          "&#x;", "&#x;"
          "&#65", "&#65"
          "&#0x41;", "&#0x41;"
          "&amp;#65;", "&#65;" ] do
        Assert.True((Sanitizer.cgiUnescapeHtml escaped = unescaped), escaped)
