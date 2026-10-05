// Tests for what stands in for a Rust dependency here: serde_json's parser and `Value`, the
// base64 crate's STANDARD engine, and jiff's `Timestamp` parser. Rust leans on the crates' own
// tests for these; the cases below are the behaviors rails_compat relies on.
module Campfire.RailsCompat.Tests.StandInTests

open System
open System.Text
open Xunit
open Campfire.RailsCompat

let private parse (s: string) : Value option = Json.parse (Encoding.UTF8.GetBytes s)

[<Fact>]
let ``json parse keeps key order and the last of a repeated key`` () =
    let value = (parse """{"b":1,"a":2,"b":3}""").Value
    Assert.Equal("""{"b":3,"a":2}""", Json.generate value)
    // Objects compare without regard to order, as serde_json's map does; arrays don't.
    Assert.Equal((parse """{"a":1,"b":2}""").Value, (parse """{"b":2,"a":1}""").Value)
    Assert.NotEqual((parse "[1,2]").Value, (parse "[2,1]").Value)

[<Fact>]
let ``json parse reads numbers as serde_json does`` () =
    Assert.Equal(Some(Value.Int 5L), parse "5")
    Assert.Equal(Some(Value.Int Int64.MinValue), parse "-9223372036854775808")
    Assert.Equal(Some(Value.UInt UInt64.MaxValue), parse "18446744073709551615")
    Assert.Equal(Some(Value.Float 1.8446744073709552e19), parse "18446744073709551616")
    Assert.Equal(Some(Value.Float 1.0), parse "1.0")
    Assert.Equal(Some(Value.Float 100.0), parse "1e2")
    Assert.True(match parse "-0" with Some(Value.Float f) -> Double.IsNegative f && f = 0.0 | _ -> false)
    // A float is not an integer.
    Assert.NotEqual(Value.Int 1L, (parse "1.0").Value)
    Assert.Equal(None, parse "1e400")

[<Fact>]
let ``json parse refuses what serde_json refuses`` () =
    for bad in [ ""; " "; "{"; "[1,]"; "{\"a\":1,}"; "01"; "+1"; "NaN"; "'a'"; "1 2"; "{} x"; "\"\\ud800\""; "[1]//c"; "\"a\nb\"" ] do
        Assert.True((parse bad).IsNone, bad)
    Assert.True((Json.parse [| 0xEFuy; 0xBBuy; 0xBFuy; byte '1' |]).IsNone)
    Assert.True((Json.parse [| byte '"'; 0xFFuy; byte '"' |]).IsNone)
    Assert.True((parse (String.replicate 129 "[" + String.replicate 129 "]")).IsNone)
    Assert.True((parse (String.replicate 128 "[" + String.replicate 128 "]")).IsSome)
    Assert.Equal(Some(Value.String "\u00e9\ud83d\ude00"), parse "\"\\u00e9\\ud83d\\ude00\"")
    Assert.Equal(Some(Value.Bool true), parse " true\n")

[<Fact>]
let ``strict base64 decode is canonical`` () =
    let decode = RailsEncoding.strictDecode
    Assert.Equal(Some [||], decode "")
    Assert.Equal(Some(Encoding.ASCII.GetBytes "hello"), decode "aGVsbG8=")
    Assert.Equal(Some(Encoding.ASCII.GetBytes "hell"), decode "aGVsbA==")
    for bad in [ "aGVsbG8"; "aGVsbG8=="; "aGVsbA="; "aGVsbB=="; "aGVsbG9="; "a GVsbG8="; "aGV\nsbG8="; "aGVs bA=="; "====" ; "aG=sbG8="; "aGVs-G8=" ] do
        Assert.Equal(None, decode bad)
    for length in 0..40 do
        let data = Array.init length (fun i -> byte (i * 37 + 11))
        Assert.Equal(Some data, decode (RailsEncoding.strictEncode data))
        Assert.Equal(Some data, RailsEncoding.urlsafeDecode (RailsEncoding.urlsafeEncodeUnpadded data))
        Assert.Equal(Some data, RailsEncoding.urlsafeDecode (RailsEncoding.urlsafeEncodePadded data))

[<Fact>]
let ``timestamps parse rfc 3339 with an offset`` () =
    let utc (s: string) = (Timestamps.tryParse s).Value.ToUniversalTime()
    Assert.Equal(DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), utc "2026-01-01T12:00:00Z")
    Assert.Equal(DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), utc "2026-01-01T13:00:00+01:00")
    Assert.Equal(DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), utc "2026-01-01T07:00:00-0500")
    Assert.Equal(DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero).AddTicks 1234560L, utc "2026-01-01T12:00:00.123456Z")
    // Finer than 100ns is cut off.
    Assert.Equal(DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero).AddTicks 1239999L, utc "2026-01-01T12:00:00.123999999Z")
    for bad in [ ""; "2026-01-01"; "2026-01-01T12:00:00"; "2026-13-01T12:00:00Z"; "2026-02-30T12:00:00Z"; "x2026-01-01T12:00:00Z"; "2026-01-01T12:00:00Z " ] do
        Assert.Equal(None, Timestamps.tryParse bad)
