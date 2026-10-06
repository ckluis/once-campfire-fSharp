// Replays what the Rust views crate rendered (differential.jsonl, written by `bin/views-differential
// golden`) for a sample of each kind of case: every template ported so far, the view helpers, the fragment
// cache and the view-model helpers. Anything the F# views render differently, byte for byte, fails, and
// `bin/views-differential` runs the same comparison live against the Rust crate on thousands more.
module Campfire.Views.Tests.DifferentialTests

open System
open System.IO
open System.Text.Json
open Xunit
open Campfire.Tests
open Campfire.Views.Differential

[<Fact>]
let ``the views render as the rust views did`` () =
    let lines = File.ReadAllLines(Repo.path "tests/Campfire.Views.Tests/differential.jsonl")
    let mutable shared = JsonElement()
    let counts = Collections.Generic.Dictionary<string, int>()
    let failures = ResizeArray<string>()
    let mutable replayed = 0
    for line in lines do
        let document = JsonDocument.Parse line
        let root = document.RootElement
        match root.TryGetProperty "shared" with
        | true, value -> shared <- value
        | _ ->
            let case = root.GetProperty "case"
            let rust = root.GetProperty "rust"
            let op = Inputs.str (Inputs.get case "op")
            counts[op] <- (match counts.TryGetValue op with | true, n -> n | _ -> 0) + 1
            replayed <- replayed + 1
            let ours = Operations.run case shared
            let expected = Inputs.str (Inputs.get rust "out")
            if ours.Out <> expected then
                let at = Seq.zip ours.Out expected |> Seq.tryFindIndex (fun (a, b) -> a <> b) |> Option.defaultValue (min ours.Out.Length expected.Length)
                let around (s: string) = s.Substring(max 0 (at - 30), min 90 (s.Length - max 0 (at - 30)))
                failures.Add $"{op}: differs at {at}\n  rust: {around expected}\n  ours: {around ours.Out}"
            match ours.Fragments, Inputs.get rust "fragments" with
            | Some fragments, parts when parts.ValueKind = JsonValueKind.Array ->
                let theirs = [ for p in parts.EnumerateArray() -> Inputs.intOf (Inputs.at p 0), Inputs.intOf (Inputs.at p 1) ]
                if fragments <> theirs then failures.Add $"{op}: fragments {fragments} against rust's {theirs}"
            | _ -> ()
    // The sample covers every kind of case, so that a case dropped from the file fails.
    for op in
        [ "layouts/application_wrapper"; "layouts/_lightbox"; "layouts/turbo_rails/frame"; "recorded/page"; "accounts/_help_contact"
          "accounts/_invite"; "pwa/_install_instructions"; "pwa/_browser_settings"; "pwa/_system_settings"; "users/_mention"
          "users/autocompletables/_template"; "welcome/show"; "helpers/tag"; "helpers/form"; "helpers/button"; "helpers/link"
          "helpers/image_tag"; "helpers/application"; "helpers/users"; "helpers/rooms"; "helpers/translations"; "helpers/url"
          "helpers/turbo"; "fragment_cache/keys"; "fragment_cache/script"; "messages/presentation"; "messages/epoch_ms"
          "messages/ruby_number"; "messages/json_by_bots_index" ] do
        Assert.True(counts.ContainsKey op, $"no {op} case in differential.jsonl")
    let report = String.Join("\n", failures |> Seq.truncate 10)
    Assert.True(failures.Count = 0, $"{failures.Count} of {replayed} differ:\n{report}")
