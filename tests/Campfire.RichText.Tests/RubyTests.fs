// Port of the tests in rust/crates/richtext/src/ruby.rs
module Campfire.RichText.Tests.RubyTests

open System
open Xunit
open Campfire.RailsCompat
open Campfire.RichText

[<Fact>]
let ``matches_ruby`` () =
    Assert.Equal("a", RubyExt.chompNewlines "a\r\n\r\n")
    Assert.Equal("a\n\r", RubyExt.chompNewlines "a\n\r")
    let inspected = Value.Object [ "a", Value.Array [ Value.Float 1.5e16; Value.Float 0.1 ] ] |> RubyExt.jsonValueInspect
    Assert.Equal("""{"a" => [1.5e+16, 0.1]}""", inspected)
    Assert.Equal("\"\\#{a} \\#$b \\#@c # #\"", RubyExt.stringInspect "#{a} #$b #@c # #")
    Assert.Equal(Some(Value.Int 1L), (RubyExt.jsonParse "{\"a\":1/*c*/}") |> Option.bind (fun v -> v.TryGet "a"))
    Assert.True((RubyExt.jsonParse "{\"a\":1,}").IsNone)
    Assert.Equal(280, (RubyExt.truncate (String('a', 300)) 280 "…").Length)
