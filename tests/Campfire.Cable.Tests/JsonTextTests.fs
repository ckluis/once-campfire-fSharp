// Not in the Rust crate (which hands serde_json a &str): `JsonStringWriter` must write what
// `Json.encode (Value.String s)` writes, however the text is split up.
module Campfire.Cable.Tests.JsonTextTests

open System
open System.Text
open Xunit
open Campfire.RailsCompat
open Campfire.Cable

let private samples =
    [ ""
      "plain"
      "<b>&</b>"
      "quote \" and backslash \\ and slash /"
      "controls \u0000 \u0001 \b \f \n \r \t \u001f \u007f"
      "snowman ☃, é,    , a pair \U0001F600 end"
      String.replicate 500 "<div class=\"m\">Hi &amp; bye ☃</div>\n" ]

[<Fact>]
let ``strings are escaped as Active Support encodes them`` () =
    for sample in samples do
        Assert.Equal(Json.encode (Value.String sample), Encoding.UTF8.GetString(JsonText.encodeString sample))

[<Fact>]
let ``pieces and UTF-8 input write the same text`` () =
    for sample in samples do
        let expected = Json.encode (Value.String sample)
        use pieces = new JsonStringWriter(8)
        pieces.Begin()
        let mutable i = 0
        while i < sample.Length do
            // A split inside a surrogate pair would not be text, so pairs stay whole.
            let mutable n = min 7 (sample.Length - i)
            if n < sample.Length - i && Char.IsHighSurrogate sample[i + n - 1] then n <- n + 1
            pieces.Append(sample.AsSpan(i, n))
            i <- i + n
        pieces.End()
        Assert.Equal(expected, Encoding.UTF8.GetString pieces.Span)
        use utf8 = new JsonStringWriter()
        utf8.Begin()
        utf8.AppendUtf8(ReadOnlySpan(Encoding.UTF8.GetBytes sample))
        utf8.End()
        Assert.Equal(expected, Encoding.UTF8.GetString utf8.Span)

[<Fact>]
let ``a long text grows the buffer`` () =
    let text = String.replicate 100000 "<&>"
    Assert.Equal(Json.encode (Value.String text), Encoding.UTF8.GetString(JsonText.encodeString text))
