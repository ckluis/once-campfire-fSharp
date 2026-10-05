// Port of the tests in rust/crates/richtext/src/autolink.rs
module Campfire.RichText.Tests.AutolinkTests

open System.Text.RegularExpressions
open Xunit
open Campfire.RichText

/// rails_autolink's `auto_linked?`, as regular expressions over the whole of `left`.
let private autoLinkedByRegex (left: string) (right: string) : bool =
    let openTagAtLineEnd = Regex(@"<[^>]+$", RegexOptions.Multiline)
    let closesTag = Regex(@"^[^>]*>", RegexOptions.Multiline)
    let openAnchor = Regex(@"\G<a\b.*?>", RegexOptions.IgnoreCase)
    let closeAnchor = Regex(@"</a>", RegexOptions.IgnoreCase)
    if openTagAtLineEnd.IsMatch left && closesTag.IsMatch right then
        true
    else
        let last =
            [ left.Length - 1 .. -1 .. 0 ]
            |> List.tryPick (fun i ->
                let m = openAnchor.Match(left, i)
                if m.Success && m.Index = i then Some m else None)
        match last with
        | Some m -> not (closeAnchor.IsMatch(left.Substring(m.Index + m.Length)))
        | None -> false

[<Fact>]
let ``tag_index_answers_as_the_regular_expressions_do`` () =
    for text in
        [ "<p>www.a.com</p><p>b</p>"
          "<p title=\"a\nb\">x</p> y <p>z</p>"
          "<p title=\"a\n\">x</p>"
          "<a href=\"x\">in <b>link</b></a> out <A\nhref=\"y\">z</A> <a>q</a>"
          "<ab>x</ab><a\tclass=\"c\">y"
          "x<\ny>z<é>"
          "<"
          "<a>"
          "" ] do
        let index = Autolink.TagIndex text
        for start in 0 .. text.Length do
            for finish in start .. text.Length do
                Assert.True(
                    (index.AutoLinked(start, finish) = autoLinkedByRegex (text.Substring(0, start)) (text.Substring finish)),
                    $"{text} at {start}..{finish}"
                )
