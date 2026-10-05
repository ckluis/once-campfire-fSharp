// Port of rust/crates/kit/tests/params_vectors.rs
//
// Differential check of the params parser against Rails' `ParamBuilder.from_query_string`, using
// vectors generated in the reference container by `params_vectors.rb` (they live beside the Rust
// kit's tests, not in vectors/).
module Campfire.Kit.Tests.ParamsVectorTests

open System.IO
open System.Text
open System.Text.Json
open Xunit
open Campfire.RailsCompat
open Campfire.Kit
open Campfire.Tests

/// How many cases `params_vectors.json` holds, so a case that is silently skipped fails.
[<Literal>]
let private ExpectedCases = 2755

[<Fact>]
let ``query strings parse like rails`` () =
    // Some outputs nest 100 deep, past System.Text.Json's default depth.
    let options = JsonDocumentOptions(MaxDepth = 200)
    use document = JsonDocument.Parse(File.ReadAllText(Repo.path "rust/crates/kit/tests/params_vectors.json"), options)
    let vectors = document.RootElement.EnumerateArray() |> Seq.toArray
    Assert.Equal(ExpectedCases, vectors.Length)
    let failures = ResizeArray<string>()
    for vector in vectors do
        let input = vector.GetProperty("input").GetString() |> nonNull
        let expected = (Json.parse (Encoding.UTF8.GetBytes(vector.GetProperty("output").GetRawText()))).Value
        let actual = Params.parseNested input
        if actual <> expected then
            failures.Add $"{input}\n  rails: {expected}\n  ours:  {actual}"
    let report = System.String.Join("\n", failures |> Seq.truncate 20)
    Assert.True(failures.Count = 0, $"{failures.Count} of {vectors.Length} differ:\n{report}")
