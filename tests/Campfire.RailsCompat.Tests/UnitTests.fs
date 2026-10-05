// Ports of the #[cfg(test)] modules in rust/crates/rails_compat/src/
// {clock,message_encryptor,encoding,metadata,json,marshal,signed_id,global_id,content_disposition}.rs
module Campfire.RailsCompat.Tests.UnitTests

open System
open System.Text.Json
open Xunit
open Campfire.RailsCompat
open Campfire.RailsCompat.Clock
open Campfire.Tests

let private ts (s: string) : Timestamp = (Timestamps.tryParse s).Value

// clock.rs

[<Fact>]
let ``a frozen clock moves only when told`` () =
    let t = ts "2024-06-01T12:00:00Z"
    let clock = TestClock.FrozenAt t
    Assert.Equal(t, clock.Now())
    clock.Travel(TimeSpan.FromSeconds 60.0)
    Assert.Equal(ts "2024-06-01T12:01:00Z", clock.Now())
    Assert.Equal("2024-06-01T12:01:00.000Z", Metadata.iso8601Millis (clock.Now()))
    clock.TravelTo t
    Assert.Equal(t, clock.Now())
    // Through the interface other crates take it by.
    let shared: SharedClock = clock
    Assert.Equal(t, shared.Now())

[<Fact>]
let ``travel shifts real time until travel back`` () =
    let clock = TestClock()
    clock.Travel(TimeSpan.FromHours 1.0)
    let shifted = clock.Now() - DateTimeOffset.UtcNow
    Assert.True(shifted > TimeSpan.FromMinutes 59.0 && shifted <= TimeSpan.FromHours 1.0, $"{shifted}")

    clock.TravelBack()
    Assert.True(clock.Now() - DateTimeOffset.UtcNow <= TimeSpan.Zero)

// message_encryptor.rs

[<Fact>]
let ``encryptor round trips and rejects tampering`` () =
    let encryptor = MessageEncryptor.create (Array.create 32 7uy) Serializer.Null
    let now = Timestamps.unixEpoch
    let message = MessageEncryptor.encryptAndSign encryptor (Value.String "hi") (Some "p") None
    Assert.True((MessageEncryptor.decryptAndVerify encryptor message (Some "p") now = Ok(Value.String "hi")))
    Assert.True((MessageEncryptor.decryptAndVerify encryptor message (Some "q") now = Error PurposeMismatch))
    let tampered = "A" + message.Substring 1
    Assert.True((MessageEncryptor.decryptAndVerify encryptor tampered (Some "p") now |> Result.isError) || tampered = message)
    Assert.True((MessageEncryptor.decryptAndVerify encryptor "short" None now = Error InvalidSignature))

// encoding.rs

[<Fact>]
let ``urlsafe decode is lenient like ruby`` () =
    Assert.Equal(Some [| byte 'A' |], RailsEncoding.urlsafeDecode "QQ")
    Assert.Equal(Some [| byte 'A' |], RailsEncoding.urlsafeDecode "QQ==")
    Assert.Equal(None, RailsEncoding.urlsafeDecode "QQ=")
    Assert.Equal(None, RailsEncoding.urlsafeDecode "QR")
    Assert.Equal(RailsEncoding.urlsafeDecode "a-b_", RailsEncoding.urlsafeDecode "a+b/")

// metadata.rs

[<Fact>]
let ``iso8601 truncates to milliseconds`` () =
    Assert.Equal("2026-01-01T12:00:00.123Z", Metadata.iso8601Millis (ts "2026-01-01T12:00:00.123999Z"))
    Assert.Equal("2046-01-01T12:00:00.000Z", Metadata.iso8601Millis (ts "2046-01-01T12:00:00Z"))

// json.rs

[<Fact>]
let ``escapes html entities but not separators or slashes`` () =
    Assert.Equal(
        "{\"key\":\"\\u003ca href=\\\"/x\\\"\\u003e\\u0026\\u003c/a\\u003e\u2028\"}",
        Json.encode (Value.Object [ "key", Value.String "<a href=\"/x\">&</a>\u2028" ])
    )
    Assert.Equal("\"<&>\"", Json.generate (Value.String "<&>"))
    Assert.Equal(
        "\"\\u003ca href=\\\"x\\\"\\u003e\\u0026'\u2028é\\n\\t\\u0001\u007f/\\u003c/a\\u003e\"",
        Json.encode (Value.String "<a href=\"x\">&'\u2028é\n\t\u0001\u007f/</a>")
    )

[<Fact>]
let ``escapes control characters like the json gem`` () =
    Assert.Equal("\"\\u001f\\n\\t\\b\\f\u007fé\"", Json.encode (Value.String "\u001f\n\t\u0008\u000c\u007fé"))

[<Fact>]
let ``floats match the json gem`` () =
    // JSON.generate(f) and ActiveSupport::JSON.encode(f) in the reference (json 2.21.2).
    let cases: (float * string) list =
        [ 320.0, "320.0"
          65.84, "65.84"
          -2.5, "-2.5"
          0.0, "0.0"
          -0.0, "-0.0"
          0.1, "0.1"
          0.0001, "0.0001"
          0.00001, "0.00001"
          1.25e-5, "0.0000125"
          1.5e-7, "0.00000015"
          -1.5e-7, "-0.00000015"
          1e-7, "0.0000001"
          1.2e-9, "0.0000000012"
          1.23456789012e-8, "0.0000000123456789012"
          1e-10, "1e-10"
          5e-324, "5e-324"
          1e14, "100000000000000.0"
          -1e14, "-100000000000000.0"
          123456789012345.6, "123456789012345.6"
          1e15, "1e+15"
          -1e15, "-1e+15"
          1.5e15, "1.5e+15"
          1234567890123456.0, "1.234567890123456e+15"
          9007199254740992.0, "9.007199254740992e+15"
          1e16, "1e+16"
          12345678901234567.0, "1.2345678901234568e+16"
          1e21, "1e+21"
          1e100, "1e+100"
          Double.MaxValue, "1.7976931348623157e+308"
          // Exact decimal ties: the json gem (and Rust's `{:e}`) take the upper digit, where .NET's
          // "R" takes the even one. Checked against json 2.21.2 in campfire-reference:app.
          667020902720176.25, "667020902720176.3"
          1125899906842624.25, "1125899906842624.3"
          24603114260468.0625, "24603114260468.063"
          210745403561986.125, "210745403561986.13"
          Math.Pow(2.0, -25.0), "0.000000029802322387695313"
          -667020902720176.25, "-667020902720176.3" ]
    for (f, json) in cases do
        Assert.True((Json.generate (Value.Float f) = json), $"{f:E}: {Json.generate (Value.Float f)} vs {json}")
        Assert.True((Json.encode (Value.Array [ Value.Float f ]) = $"[{json}]"), $"{f:E}")
    Assert.Equal("null", Json.encode (Value.Float Double.NaN))
    Assert.Equal("null", Json.encode (Value.Float Double.PositiveInfinity))
    // ActiveSupport::JSON.encode({ "a" => 1e16, "b" => [1e-5] })
    Assert.Equal("""{"a":1e+16,"b":[0.00001]}""", Json.encode (Value.Object [ "a", Value.Float 1e16; "b", Value.Array [ Value.Float 1e-5 ] ]))

// marshal.rs

[<Fact>]
let ``loads marshal strings`` () =
    let latin1 (s: string) = Text.Encoding.Latin1.GetBytes s
    // Marshal.dump("gid://campfire/User/1")
    let dumped = latin1 "\u0004\u0008I\"\u001agid://campfire/User/1\u0006:\u0006ET"
    Assert.Equal<byte[]>(Text.Encoding.ASCII.GetBytes "gid://campfire/User/1", (Marshal.loadString dumped).Value)
    // A 300-byte string uses a two-byte length: "\x02\x2c\x01".
    let long = Array.append (latin1 "\u0004\u0008I\"\u0002,\u0001") (Array.create 300 (byte 'x'))
    Assert.Equal(300, (Marshal.loadString long).Value.Length)
    Assert.Equal(None, Marshal.loadString (latin1 "\u0004\u0008i\u0006"))

// signed_id.rs

[<Fact>]
let ``combines purposes`` () =
    Assert.Equal("user/avatar", SignedId.combinePurposes "User" (Some "avatar"))
    Assert.Equal("user", SignedId.combinePurposes "User" None)
    Assert.Equal("rooms/open", SignedId.combinePurposes "Rooms::Open" (Some ""))
    Assert.Equal("http_request/x", SignedId.combinePurposes "HTTPRequest" (Some "x"))

// global_id.rs

[<Fact>]
let ``global id parses and formats`` () =
    let gid = (GlobalId.parse "gid://campfire/Rooms::Open/1?expires_in").Value
    Assert.Equal(GlobalId.create "Rooms::Open" "1", gid)
    Assert.Equal("Z2lkOi8vY2FtcGZpcmUvUm9vbXM6Ok9wZW4vMQ", GlobalId.toParam gid)
    Assert.Equal(Some gid, GlobalId.fromParam (GlobalId.toParam gid))
    Assert.Equal(None, GlobalId.parse "gid://campfire/User")

// content_disposition.rs

/// Case count of `filenames` in vectors/storage.json.
[<Literal>]
let FilenameCases = 16

/// The filenames in `vectors/storage.json` (`reference-tools/storage/generate.rb`), as the
/// reference formats them after `ActiveStorage::Filename#sanitized`.
[<Fact>]
let ``content disposition formats like rails`` () =
    use vectors = Repo.vector "storage"
    let str (e: JsonElement) : string = nonNull (e.GetString())
    let filenames = vectors.RootElement.GetProperty("filenames").EnumerateArray() |> Seq.toList
    Assert.Equal(FilenameCases, filenames.Length)
    for f in filenames do
        let sanitized = str (f.GetProperty "sanitized")
        Assert.Equal(str (f.GetProperty "inline"), ContentDisposition.format "inline" sanitized)
        Assert.Equal(str (f.GetProperty "attachment"), ContentDisposition.format "attachment" sanitized)

[<Fact>]
let ``content disposition transliterates beyond latin 1`` () =
    Assert.Equal(
        "inline; filename=\"Lodz x.pdf\"; filename*=UTF-8''%C5%81%C3%B3d%C5%BA%20%C3%97.pdf",
        ContentDisposition.format "inline" "Łódź ×.pdf"
    )
