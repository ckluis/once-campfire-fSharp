// Port of rust/crates/richtext/tests/hardening.rs
//
// Regression tests for places where the port deliberately diverges from the Rails pipeline to
// close a hole or bound the work a message body can cause (see "Known differences" in README.md).
module Campfire.RichText.Tests.HardeningTests

open System
open System.Diagnostics
open Xunit
open Campfire.RichText
open Campfire.RichText.Tests.Support

let private ctx = render (NoRecords()) (Some "once.campfire.test")

let private presentation (body: string) : string = okR (ActionText.messagePresentation body ctx)

let private assertQuick (started: Stopwatch) (what: string) =
    // Generous enough for a debug build; release takes a few milliseconds.
    let limit = bound (TimeSpan.FromSeconds 5.0) (TimeSpan.FromSeconds 1.0)
    Assert.True(started.Elapsed < limit, $"{what} took {started.Elapsed}")

let private contains (haystack: string) (needle: string) : bool = haystack.Contains(needle, StringComparison.Ordinal)

let private countOf (haystack: string) (needle: string) : int =
    let mutable count = 0
    let mutable i = haystack.IndexOf(needle, StringComparison.Ordinal)
    while i >= 0 do
        count <- count + 1
        i <- haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)
    count

// --- Autolinking inside attribute values ---------------------------------------------------------

[<Fact>]
let ``a_url_after_a_greater_than_sign_in_an_attribute_cannot_break_out_of_it`` () =
    // Rails serializes the title unescaped (`title="x> http://..."`), so auto_link's "inside a
    // tag" check misses, the inserted <a href="..."> closes the attribute, and the <img> after it
    // becomes live markup.
    let body = "<p title=\"x> http://evil.test/ <img src=x onerror=alert(1)>\">hi</p>"
    let html = presentation body
    let markup = parsedMarkup html
    Assert.False(markup |> List.exists (fun m -> m = "img" || m.Contains "onerror"), html)
    Assert.Equal<string list>([ "p"; "p[title]" ], markup)

[<Fact>]
let ``an_email_address_after_a_greater_than_sign_in_an_attribute_cannot_break_out_of_it`` () =
    let body = "<p><abbr title=\"x> me@evil.test <img src=x onerror=alert(1)>\">hi</abbr></p>"
    let html = presentation body
    Assert.Equal<string list>([ "p"; "abbr"; "abbr[title]" ], parsedMarkup html)

[<Fact>]
let ``name_attributes_cant_clobber_the_pages_globals`` () =
    let html = presentation "<p><a name=\"body\" href=\"/x\">x</a><span name=\"cookie\">y</span></p>"
    Assert.Equal<string list>([ "p"; "a"; "a[href]"; "span" ], parsedMarkup html)

[<Fact>]
let ``urls_in_text_are_still_linked`` () =
    let html = presentation "<p>see http://example.com/a?b=1&amp;c=2 and me@example.com</p>"
    Assert.True(
        contains
            html
            "<p>see <a target=\"_blank\" href=\"http://example.com/a?b=1&amp;c=2\">http://example.com/a?b=1&amp;c=2</a> and <a target=\"_blank\" href=\"mailto:me@example.com\">me@example.com</a></p>",
        html
    )

// --- Bounded work ----------------------------------------------------------------------------------

[<Fact>]
let ``many_bare_domains_autolink_in_linear_time`` () =
    let body = String.replicate (64 * 1024 / 16) "<p>www.a.com</p>"
    let started = Stopwatch.StartNew()
    let html = presentation body
    assertQuick started "autolinking 64 KB of bare domains"
    Assert.Equal(64 * 1024 / 16, countOf html "<a target=\"_blank\" href=\"http://www.a.com\">")

/// Content attachments nested `levels` deep, each saying which level it is.
let private nestedContentAttachments (levels: int) (padding: string) : string =
    let mutable body = ""
    for level in levels .. -1 .. 1 do
        let content = $"<p>level {level}{padding}</p>{body}".Replace("&", "&amp;").Replace("\"", "&quot;")
        body <- $"<action-text-attachment content-type=\"text/html\" content=\"{content}\"></action-text-attachment>"
    body

[<Fact>]
let ``content_attachments_render_eight_levels_deep`` () =
    let html = presentation (nestedContentAttachments 12 "")
    for level in 1..12 do
        Assert.Equal(level <= 8, contains html $"level {level}<")

[<Fact>]
let ``deeply_nested_content_attachments_render_quickly`` () =
    let body = nestedContentAttachments 200 (String('x', 1000))
    Assert.True(body.Length > 200_000)
    let started = Stopwatch.StartNew()
    presentation body |> ignore
    assertQuick started "rendering 200 nested content attachments"

/// Both the page and the search index (which is written inside the database transaction) have to
/// refuse `body`.
let private assertRefusedQuickly (body: string) (what: string) =
    let started = Stopwatch.StartNew()
    Assert.True(isError (ActionText.messagePresentation body ctx), $"{what} rendered")
    Assert.True(isError (ActionText.toPlainText body ctx), $"{what} was indexed")
    assertQuick started what

[<Fact>]
let ``deeply_nested_elements_are_refused_quickly`` () =
    // Gumbo's depth limit is enforced as the tree is built. Checked on the finished tree, 400 KB
    // of nested <div>s took 16 seconds in Rust, since html5ever's scope checks walk every open element.
    assertRefusedQuickly (String.replicate 80_000 "<div>") "400 KB of nested <div>s"
    assertRefusedQuickly (String.replicate 80_000 "<a><b>") "480 KB of <a><b>"
    assertRefusedQuickly (String.replicate 30_000 "<a><div><div>") "390 KB of <a><div><div>"

[<Fact>]
let ``the_rest_of_a_body_is_not_read_once_it_is_too_deep`` () =
    // Gumbo stops there. Tokenizing the rest of a 16 MB body (kit's request body limit) only to
    // throw it away took 200 ms a parse, and a message is parsed several times. Stopping once the
    // next token has been read isn't enough, as it can be all the rest: a comment took 130 ms.
    let tooDeep = String.replicate 401 "<div>"
    let rest = String('x', 16 * 1024 * 1024)
    for (what, body) in
        [ "tags", tooDeep + String.replicate (rest.Length / 6) "<a><b>"
          "a comment", tooDeep + "<!--" + rest
          "a tag name", tooDeep + "<" + rest ] do
        let started = Stopwatch.StartNew()
        Assert.True(isError (ActionText.messagePresentation body ctx) && isError (ActionText.toPlainText body ctx))
        // Copying the body to parse it is all that's left
        let limit = bound (TimeSpan.FromSeconds 1.0) (TimeSpan.FromMilliseconds 100.0)
        Assert.True(started.Elapsed < limit, $"refusing 16 MB of {what} took {started.Elapsed}")

[<Fact>]
let ``a_tag_with_too_many_attributes_is_refused_quickly`` () =
    // Each attribute is checked against the tag's others for a duplicate, up to Gumbo's limit
    let attributes = [ for i in 1..64_000 -> $"a{i}=1" ]
    assertRefusedQuickly $"<b {String.Join(' ', attributes)}>x</b>" "a tag with 64,000 attributes"

[<Fact>]
let ``html_tags_in_the_body_parse_in_linear_time`` () =
    // Each one's attributes go to the fragment's root <html> element, unless it has them already.
    // Checking every one against all the root had collected made 800 KB of them take 1.5 seconds.
    let body =
        [ for tag in 0..199 ->
              let names = [ for i in 1 .. DomLimits.MaxAttributes -> $"a{tag * DomLimits.MaxAttributes + i}" ]
              $"<html {String.Join(' ', names)}>" ]
        |> String.concat ""
    Assert.True(body.Length > 500_000)
    let started = Stopwatch.StartNew()
    Assert.Equal("", okR (ActionText.toPlainText body ctx))
    Assert.Equal(presentation "", presentation body)
    assertQuick started "550 KB of <html> tags, each with 400 new attributes"

[<Fact>]
let ``elements_misplaced_in_a_table_parse_in_linear_time`` () =
    // Foster parenting inserts each of them before the table. Finding the table from the front of
    // its parent's children made that quadratic: 480 KB of them took 1.5 seconds.
    let body = "<table>" + String.replicate 200_000 "<br>"
    let started = Stopwatch.StartNew()
    Assert.True(not (isError (ActionText.toPlainText body ctx)))
    assertQuick started "800 KB of <br>s in a table"

/// Every SGID names a user who has since been deleted.
type private DeletedUsers() =
    interface IAttachableResolver with
        member _.LocateSigned _ = SignedLookup.MissingRecord "User"
        member _.FindGid _ = GidLookup.NotFound

let private deletedMention =
    "<p>Hi <action-text-attachment sgid=\"eyJfcmFpbHMiOnsiZGF0YSI6ImdpZDovL2NhbXBmaXJlL1VzZXIvNj9leHBpcmVzX2luIiwicHVyIjoiYXR0YWNoYWJsZSJ9fQ==--fc4f83a239475557295b8e2f5ff55482bebc9bfe\" content-type=\"application/vnd.campfire.mention\"></action-text-attachment>, welcome</p>"

[<Fact>]
let ``a_mention_of_a_deleted_user_leaves_the_rest_of_the_message`` () =
    let ctx = render (DeletedUsers()) None
    let html = okR (ActionText.messagePresentation deletedMention ctx)
    Assert.True(contains html "Hi" && contains html "☒" && contains html "welcome", html)

[<Fact>]
let ``a_mention_of_a_deleted_user_leaves_the_editor`` () =
    let ctx = render (DeletedUsers()) None
    let value = (okR (ActionText.editableValue deletedMention ctx)).Value
    Assert.True(not (contains value "action-text-attachment") && contains value "welcome", value)
