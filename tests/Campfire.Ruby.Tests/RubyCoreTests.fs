// Port of rust/crates/ruby/tests/ruby_core.rs
//
// Every function against what Ruby, Rack, Active Record and Addressable answer in the reference
// (`vectors/ruby_core.json`, written by `reference-tools/ruby_core.rb`). Each test lists every
// input it gets wrong, not just the first, and asserts how many cases it ran, so a case that is
// silently skipped fails.
module Campfire.Ruby.Tests.RubyCoreTests

open System
open System.Globalization
open System.Text.Json
open Xunit
open Campfire.Ruby
open Campfire.Tests

// Case counts in vectors/ruby_core.json; regenerate the vectors and these together.
[<Literal>]
let StringCases = 427

[<Literal>]
let FloatCases = 555

[<Literal>]
let ByteRangeCases = 200

let private vectors = lazy (Repo.vector "ruby_core")

/// The string a JSON element holds.
let private str (e: JsonElement) : string = nonNull (e.GetString())

let private cases (section: string) : JsonElement[] =
    vectors.Value.RootElement.GetProperty(section).EnumerateArray() |> Seq.toArray

/// Each string, with what Ruby's version of `func` made of it.
let private strings (func: string) : (string * JsonElement) list =
    let all = cases "strings"
    Assert.Equal(StringCases, all.Length)
    all |> Array.map (fun c -> str (c.GetProperty "input"), c.GetProperty func) |> List.ofArray

/// Compares every result, and fails listing each mismatch; `expectedCount` guards against skipped cases.
let private assertMatchesRuby (func: string) (expectedCount: int) (results: (string * 'a * 'a) list) =
    Assert.Equal(expectedCount, results.Length)
    let mismatches =
        results
        |> List.filter (fun (_, ours, ruby) -> ours <> ruby)
        |> List.map (fun (input, ours, ruby) -> $"{func}({input}) is {ours}, Ruby's {ruby}")
    Assert.True(mismatches.IsEmpty, $"{mismatches.Length} mismatches:\n" + String.Join("\n", mismatches))

let private sameStrings (func: string) (ours: string -> string) =
    strings func
    |> List.map (fun (input, ruby) -> ($"{input:O}", ours input, str ruby))
    |> assertMatchesRuby func StringCases

let private tryParseInt64 (s: string) =
    match Int64.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
    | true, v -> Some v
    | _ -> None

let private show (s: string) = "\"" + s.Replace("\000", "\\0") + "\""

[<Fact>]
let ``to_i`` () =
    strings "to_i"
    |> List.map (fun (input, ruby) ->
        let ruby = str ruby
        let checkedValue = tryParseInt64 ruby
        let saturated =
            match checkedValue with
            | Some v -> v
            | None -> if ruby.StartsWith '-' then Int64.MinValue else Int64.MaxValue
        (show input, (Ruby.toI input, Ruby.toIChecked input), (saturated, checkedValue)))
    |> assertMatchesRuby "to_i" StringCases

[<Fact>]
let ``integer_cast`` () =
    // `SQLite3Integer#serialize`: nil, or a RangeError past 8 bytes.
    strings "integer_cast"
    |> List.map (fun (input, ruby) ->
        let ruby = if ruby.ValueKind = JsonValueKind.String then tryParseInt64 (str ruby) else None
        (show input, Ruby.integerCast input, ruby))
    |> assertMatchesRuby "integer_cast" StringCases

[<Fact>]
let ``to_f`` () =
    strings "to_f"
    |> List.map (fun (input, ruby) ->
        let ruby = Double.Parse(str ruby, NumberStyles.Float, CultureInfo.InvariantCulture)
        // Compared bit for bit, so -0.0 isn't 0.0.
        (show input, BitConverter.DoubleToInt64Bits(Ruby.toF input), BitConverter.DoubleToInt64Bits ruby))
    |> assertMatchesRuby "to_f" StringCases

[<Fact>]
let ``strip`` () = sameStrings "strip" Ruby.strip

[<Fact>]
let ``html_escape`` () = sameStrings "html_escape" Erb.htmlEscape

[<Fact>]
let ``cgi_escape`` () = sameStrings "cgi_escape" Ruby.cgiEscape

[<Fact>]
let ``url_encode`` () =
    sameStrings "url_encode" Ruby.urlEncode
    // And Addressable's `encode_component(s, UNRESERVED)`, which pagination links use.
    sameStrings "addressable_unreserved" Ruby.urlEncode

[<Fact>]
let ``float_to_s`` () =
    let all = cases "floats"
    all
    |> Array.toList
    |> List.map (fun case ->
        let float = BitConverter.UInt64BitsToDouble(UInt64.Parse(str (case.GetProperty "bits"), NumberStyles.HexNumber))
        (float.ToString("E", CultureInfo.InvariantCulture), Ruby.floatToS float, str (case.GetProperty "to_s")))
    |> assertMatchesRuby "float_to_s" FloatCases
    Assert.Equal(FloatCases, all.Length)

[<Fact>]
let ``byte_ranges`` () =
    let all = cases "byte_ranges"
    all
    |> Array.toList
    |> List.map (fun case ->
        let header =
            match case.GetProperty "header" with
            | h when h.ValueKind = JsonValueKind.String -> Some(str h)
            | _ -> None
        let size = case.GetProperty("size").GetUInt64()
        let ruby =
            match case.GetProperty "ranges" with
            | r when r.ValueKind = JsonValueKind.Array ->
                Some [ for range in r.EnumerateArray() -> (range[0].GetUInt64(), range[1].GetUInt64()) ]
            | _ -> None
        ($"{header}, {size}", Rack.byteRanges header size, ruby))
    |> assertMatchesRuby "byte_ranges" ByteRangeCases
    Assert.Equal(ByteRangeCases, all.Length)
