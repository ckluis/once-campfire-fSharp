// Replays what the Rust kit answered (differential.jsonl, written by `bin/kit-differential golden`) to
// the inputs of each kind: Accept headers and formats, cookie headers, host and client address from proxy
// headers, JSON and form bodies, strong parameters, multipart bodies and their content types. Anything
// the F# kit answers differently fails, and `bin/kit-differential` runs the same comparison live against
// the Rust crate on thousands more.
module Campfire.Kit.Tests.DifferentialTests

open System
open System.Globalization
open System.IO
open System.Text.Json
open Xunit
open Campfire.RailsCompat
open Campfire.Tests
open Campfire.Kit.Differential

/// How many cases differential.jsonl holds of each kind, so that a case dropped from the file fails.
let private expected =
    [ "accept", 150
      "formats", 150
      "cookie", 150
      "request", 150
      "json_body", 150
      "form", 150
      "permit", 150
      "multipart", 150
      "boundary", 150 ]

/// A JSON answer as a `Value`, whatever its depth (the answers to the deepest inputs nest past what
/// `Json.parse` reads), with numbers kept as the integer or float they are written as.
let rec private toValue (e: JsonElement) : Value =
    match e.ValueKind with
    | JsonValueKind.Null -> Value.Null
    | JsonValueKind.True -> Value.Bool true
    | JsonValueKind.False -> Value.Bool false
    | JsonValueKind.String -> Value.String(nonNull (e.GetString()))
    | JsonValueKind.Number ->
        let raw = e.GetRawText()
        let style = NumberStyles.Float
        let culture = CultureInfo.InvariantCulture
        if raw.IndexOfAny [| '.'; 'e'; 'E' |] >= 0 || raw = "-0" then
            Value.Float(Double.Parse(raw, style, culture))
        else
            match Int64.TryParse(raw, NumberStyles.AllowLeadingSign, culture) with
            | true, i -> Value.Int i
            | _ ->
                match UInt64.TryParse(raw, NumberStyles.None, culture) with
                | true, u -> Value.UInt u
                | _ -> Value.Float(Double.Parse(raw, style, culture))
    | JsonValueKind.Array -> Value.Array [ for item in e.EnumerateArray() -> toValue item ]
    | _ -> Value.Object [ for p in e.EnumerateObject() -> p.Name, toValue p.Value ]

[<Fact>]
let ``the kit answers as the rust kit did`` () =
    let lines = File.ReadAllLines(Repo.path "tests/Campfire.Kit.Tests/differential.jsonl")
    let counts = Collections.Generic.Dictionary<string, int>()
    let failures = ResizeArray<string>()
    for line in lines do
        use document = JsonDocument.Parse(line, JsonDocumentOptions(MaxDepth = 400))
        let input = document.RootElement.GetProperty "input"
        let op = input.GetProperty("op").GetString() |> nonNull
        counts[op] <- (match counts.TryGetValue op with | true, n -> n | _ -> 0) + 1
        let rust = toValue (document.RootElement.GetProperty "rust")
        use answer = JsonDocument.Parse(Operations.run (input.GetRawText()), JsonDocumentOptions(MaxDepth = 400))
        let ours = toValue answer.RootElement
        if rust <> ours then
            failures.Add $"{input.GetRawText().Substring(0, min 300 (input.GetRawText().Length))}\n  rust: {rust}\n  ours: {ours}"
    for (op, count) in expected do
        let found = match counts.TryGetValue op with | true, n -> n | _ -> 0
        Assert.True((found = count), $"{op}: {found} cases, expected {count}")
    Assert.Equal(lines.Length, expected |> List.sumBy snd)
    let report = String.Join("\n", failures |> Seq.truncate 10)
    Assert.True(failures.Count = 0, $"{failures.Count} of {lines.Length} differ:\n{report}")
