// Ports of the #[cfg(test)] modules in rust/crates/ruby/src/{erb,string,uri,integer,rack,float}.rs
module Campfire.Ruby.Tests.UnitTests

open System
open System.Text
open Xunit
open Campfire.Ruby
open Campfire.Ruby.Integer

// erb.rs

[<Fact>]
let ``erb escapes like erb util`` () =
    Assert.Equal("&lt;&amp;&gt;&quot;&#39;x é", Erb.htmlEscape "<&>\"'x é")
    let out = StringBuilder "a"
    Erb.pushHtmlEscaped out "<b>"
    Assert.Equal("a&lt;b&gt;", out.ToString())
    // writeHtmlEscaped writes the same bytes to any TextWriter.
    use writer = new IO.StringWriter()
    Erb.writeHtmlEscaped writer "x<y>&\"z'"
    Assert.Equal("x&lt;y&gt;&amp;&quot;z&#39;", writer.ToString())

[<Fact>]
let ``erb escapes straight into a utf-8 buffer`` () =
    let escaped (s: string) =
        let buffer = Buffers.ArrayBufferWriter<byte>()
        Erb.writeHtmlEscapedUtf8 buffer (s.AsSpan())
        Encoding.UTF8.GetString(buffer.WrittenSpan)
    for s in [ ""; "plain"; "<&>\"'x é"; "😀<😀>"; "&&&"; "'" ; String('x', 5000) + "<" + String('é', 3000) ] do
        Assert.Equal(Erb.htmlEscape s, escaped s)
    // Appended after what the buffer already holds, as bytes.
    let buffer = Buffers.ArrayBufferWriter<byte>()
    Buffers.BuffersExtensions.Write(buffer, ReadOnlySpan<byte> "a"B)
    Erb.writeHtmlEscapedUtf8 buffer ("<é>".AsSpan())
    Assert.Equal<byte[]>(Array.append "a&lt;"B (Array.append (Encoding.UTF8.GetBytes "é") "&gt;"B), buffer.WrittenSpan.ToArray())

// string.rs

[<Fact>]
let ``strips nul and ascii whitespace`` () =
    Assert.Equal("x", Ruby.strip "\000 \t\u000b x \000\n")
    Assert.Equal("\u00a0x\u00a0", Ruby.strip " \u00a0x\u00a0 ")

// uri.rs

[<Fact>]
let ``uri escapes like ruby`` () =
    Assert.Equal("a%2A~+b-._%2B%C3%A9", Ruby.cgiEscape "a*~ b-._+é")
    Assert.Equal("a%2A~%20b-._%2B%C3%A9", Ruby.urlEncode "a*~ b-._+é")

// integer.rs

[<Fact>]
let ``to_i like ruby`` () =
    // `String#to_i` in the reference.
    Assert.Equal(1717243200000L, toI "1717243200000")
    Assert.Equal(12L, toI " +12abc")
    Assert.Equal(7L, toI "\t\n\u000b\u000c\r 7")
    Assert.Equal(0L, toI "\u00a05")
    Assert.Equal(0L, toI "abc")
    Assert.Equal(-5L, toI "-5")
    Assert.Equal(0L, toI "--5")
    Assert.Equal(56L, toI "5_6")
    Assert.Equal(5L, toI "5__6")
    Assert.Equal(0L, toI "_5")
    Assert.Equal(0L, toI "0__5")
    Assert.Equal(-5L, toI "-0d5")
    Assert.Equal(0L, toI "0d_5")
    Assert.Equal(0L, toI "0x5")
    Assert.Equal(Int64.MaxValue, toI "99999999999999999999")
    Assert.Equal(Int64.MinValue, toI "-99999999999999999999")
    Assert.Equal(None, toIChecked "99999999999999999999")
    Assert.Equal(Int128.Parse "99999999999999999999", toI128 "99999999999999999999")

[<Fact>]
let ``casts like active record`` () =
    // `Room.type_for_attribute(:id).serialize(s)` in the reference; a RangeError is None.
    let cases: (string * int64 option) list =
        [ "12", Some 12L
          "12abc", Some 12L
          " -3", Some -3L
          " +12abc", Some 12L
          "\t\n\u000b\u000c\r 7", Some 7L
          "\u000b-0d7", Some -7L
          "0d12", Some 12L
          "0D12", Some 12L
          "0d", Some 0L
          "0d_5", Some 0L
          "0d0_5", Some 5L
          "5_6", Some 56L
          "5__6", Some 5L
          "1_000", Some 1000L
          "0x5", Some 0L
          "-0", Some 0L
          "5\000", Some 5L
          "3000000000", Some 3000000000L
          "9223372036854775807", Some Int64.MaxValue
          "-9223372036854775808", Some Int64.MinValue
          "9223372036854775808", None
          "-9223372036854775809", None
          "99999999999999999999", None
          "\u00a05", None
          "\000 5", None
          "_5", None
          "--5", None
          "+-5", None
          "+", None
          "abc", None
          "", None ]
    for (value, id) in cases do
        Assert.True((integerCast value = id), $"{value}")

// rack.rs

/// `Rack::Utils.get_byte_ranges(header, size)` in the reference (rack 3.2.6); None is the whole file.
let private rackRanges: (string * uint64 * (uint64 * uint64) list option) list =
    [ "bytes=0-4", 10UL, Some [ (0UL, 4UL) ]
      "bytes=5-", 10UL, Some [ (5UL, 9UL) ]
      "bytes=-3", 10UL, Some [ (7UL, 9UL) ]
      "bytes=-30", 10UL, Some [ (0UL, 9UL) ]
      "bytes=0-99999999999999999999", 10UL, Some [ (0UL, 9UL) ]
      "bytes=99999999999999999999-", 10UL, Some []
      "bytes=-99999999999999999999", 10UL, Some [ (0UL, 9UL) ]
      "bytes=0-1,", 10UL, Some [ (0UL, 1UL) ]
      "bytes= -5", 10UL, Some [ (0UL, 5UL) ]
      "bytes=0-1, 3-4", 10UL, Some [ (0UL, 1UL); (3UL, 4UL) ]
      "bytes=0-1,\t 3-4", 10UL, Some [ (0UL, 1UL); (3UL, 4UL) ]
      "bytes=3-5,1-2", 10UL, Some [ (3UL, 5UL); (1UL, 2UL) ]
      "bytes=+1-2", 10UL, Some [ (1UL, 2UL) ]
      "bytes=1-+2", 10UL, Some [ (1UL, 2UL) ]
      "bytes=1_0-2_0", 100UL, Some [ (10UL, 20UL) ]
      "bytes=0d5-0d9", 100UL, Some [ (5UL, 9UL) ]
      "bytes=\u00a01-2", 10UL, Some [ (0UL, 2UL) ]
      "bytes=a-b", 10UL, Some [ (0UL, 0UL) ]
      "bytes=0-0x5", 10UL, Some [ (0UL, 0UL) ]
      "bytes=0-1 ", 10UL, Some [ (0UL, 1UL) ]
      "bytes=0 -1", 10UL, Some [ (0UL, 1UL) ]
      "bytes=1-2-3", 10UL, Some [ (1UL, 2UL) ]
      "bytes=9-9", 10UL, Some [ (9UL, 9UL) ]
      "bytes=10-", 10UL, Some []
      "bytes=10-12", 10UL, Some []
      "bytes=-0", 10UL, Some []
      "bytes=--5", 10UL, Some []
      "bytes=0-4,5-9,0-0", 10UL, Some []
      "bytes=;bytes=2-3", 10UL, Some [ (2UL, 3UL) ]
      "bytes=0-1;bytes=2-3", 10UL, Some [ (0UL, 1UL) ]
      "xbytes=0-1", 10UL, Some [ (0UL, 1UL) ]
      "bytes=;0-1", 10UL, None
      "bytes=", 10UL, None
      "bytes=-", 10UL, None
      "bytes=5", 10UL, None
      "bytes=1-0", 10UL, None
      "bytes=0-1,,2-3", 10UL, None
      "items=0-1", 10UL, None
      "bytes=0-4", 0UL, None ]

[<Fact>]
let ``byte ranges match rack`` () =
    for (header, size, expected) in rackRanges do
        Assert.True((Rack.byteRanges (Some header) size = expected), $"{header} of {size}")
    Assert.Equal(None, Rack.byteRanges None 10UL)
    // 99 commas are read, and 100 aren't (`max_ranges`).
    Assert.Equal(Some [], Rack.byteRanges (Some("bytes=0-1," + String.replicate 98 "0-0,")) 10UL)
    Assert.Equal(None, Rack.byteRanges (Some("bytes=0-1," + String.replicate 99 "0-0,")) 10UL)

// float.rs

let private bits (f: float) = BitConverter.DoubleToInt64Bits f

[<Fact>]
let ``to_f like ruby`` () =
    // `String#to_f` in the reference.
    let cases: (string * float) list =
        [ "0.5", 0.5
          "0.5.1", 0.5
          "1.5.5e2", 1.5
          "1e2", 100.0
          "1E2", 100.0
          "1.2e-1", 0.12
          "1.5e+2", 150.0
          "1.e5", 100000.0
          "1e2.5", 100.0
          "1e0_1", 10.0
          "1e", 1.0
          "1e+", 1.0
          "0.5e-", 0.5
          "1e_2", 1.0
          "1_0.5", 10.5
          "1_2_3.4_5", 123.45
          "0.5_5", 0.55
          "0_0.5", 0.5
          "1__0", 1.0
          "1_e2", 1.0
          "1._5", 1.0
          "_1", 0.0
          ".5", 0.5
          "+.5", 0.5
          "-.5", -0.5
          "5.", 5.0
          "00.5", 0.5
          "  -1.5x", -1.5
          "\u000b0.5", 0.5
          "1,5", 1.0
          "-0", -0.0
          "0x1A", 0.0
          "-0x1A", -26.0
          "-0x1.8p1", -3.0
          ".e5", 0.0
          "e5", 0.0
          ".", 0.0
          "-", 0.0
          "+", 0.0
          "", 0.0
          "Infinity", 0.0
          "1e-400", 0.0
          "1e400", Double.PositiveInfinity
          "1\0002", 1.0 ]
    for (s, f) in cases do
        Assert.True((bits (Ruby.toF s) = bits f), $"{s}: got {Ruby.toF s}")

[<Fact>]
let ``to_f drops what ruby has no room for`` () =
    // Significand characters past 60, when there's more after the number.
    Assert.Equal(1e59, Ruby.toF ("1" + String('0', 70) + "x"))
    Assert.Equal(1e70, Ruby.toF ("1" + String('0', 70)))
    // Fraction digits past 60 significant ones: halfway between two doubles, but not seen as
    // halfway, so this rounds down rather than to the even one.
    let halfway = "0.00000000093132257461547882581772970738537807677825952623607008717954158782958984375"
    Assert.Equal(BitConverter.UInt64BitsToDouble 0x3E10_0000_0000_0001UL, Ruby.toF halfway)

[<Fact>]
let ``float_to_s like ruby 3.4`` () =
    // `Float#to_s` in the reference (Ruby 3.4.10): from 1e15, the exponent form unless there
    // are digits after the decimal point.
    let cases: (float * string) list =
        [ 100.0, "100.0"
          0.1, "0.1"
          0.0001, "0.0001"
          0.00012345, "0.00012345"
          1e-5, "1.0e-05"
          1.5e-7, "1.5e-07"
          5e-324, "5.0e-324"
          1e14, "100000000000000.0"
          123456789012345.6, "123456789012345.6"
          999999999999999.0, "999999999999999.0"
          999999999999999.9, "999999999999999.9"
          -999999999999999.0, "-999999999999999.0"
          1e15, "1.0e+15"
          -1e15, "-1.0e+15"
          1.5e15, "1.5e+15"
          1234567890123456.0, "1.234567890123456e+15"
          9007199254740992.0, "9.007199254740992e+15"
          1000000000000001.0, "1.000000000000001e+15"
          1963684456584958.8, "1963684456584958.8"
          1000000000000000.1, "1000000000000000.1"
          2251799813685248.5, "2251799813685248.5"
          -2551800308696183.5, "-2551800308696183.5"
          1e16, "1.0e+16"
          1e20, "1.0e+20"
          Double.MaxValue, "1.7976931348623157e+308" ]
    for (f, s) in cases do
        Assert.True((Ruby.floatToS f = s), $"{f:E}: {Ruby.floatToS f} vs {s}")

[<Fact>]
let ``float_to_s breaks ties to even like ruby`` () =
    // `Float#to_s` in the reference: of two shortest forms equally close, the even one, as long
    // as it reads back. Doubles are twice as dense just below 2^-24, so its even form
    // (5.960464477539062e-08) reads back as a different double.
    let cases: (float * string) list =
        [ 667020902720176.0 + 0.25, "667020902720176.2"
          667020902720176.0 + 0.75, "667020902720176.8"
          1000000000000000.2, "1000000000000000.2"
          1125899906842624.0 + 0.25, "1125899906842624.2"
          -(2074704973491874.0 + 0.25), "-2074704973491874.2"
          24603114260468.0 + 0.0625, "24603114260468.062"
          210745403561986.0 + 0.125, "210745403561986.12"
          Math.Pow(2.0, -24.0), "5.960464477539063e-08"
          Math.Pow(2.0, -25.0), "2.9802322387695312e-08"
          0.1 + 0.2, "0.30000000000000004"
          1.0 / 3.0, "0.3333333333333333" ]
    for (f, s) in cases do
        Assert.True((Ruby.floatToS f = s), $"{f:E}: {Ruby.floatToS f} vs {s}")


[<Fact>]
let ``to_i without 128-bit arithmetic gives the answers of the general one`` () =
    let random = Random 20261006
    let alphabet = " +-0123456789_dD\tx"
    let clamp (n: Int128) =
        if n < Int128.CreateChecked Int64.MinValue then Int64.MinValue
        elif n > Int128.CreateChecked Int64.MaxValue then Int64.MaxValue
        else Int64.CreateChecked n
    for _ in 1..200000 do
        let length = random.Next(0, 24)
        let text = String(Array.init length (fun _ -> alphabet[random.Next alphabet.Length]))
        Assert.True((toI text = clamp (toI128Slowly text)), $"to_i of {text}")
        Assert.True((toI128 text = toI128Slowly text), $"to_i of {text}")
    for text in [ "0"; "-0"; "007"; "0d12"; "-0d5"; "1_000"; "999999999999999999"; "1000000000000000000"; " \n12abc"; "+7"; "" ] do
        Assert.Equal(clamp (toI128Slowly text), toI text)
