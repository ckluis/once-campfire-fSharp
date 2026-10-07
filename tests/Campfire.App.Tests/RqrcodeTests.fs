// Port of the tests of rust/crates/campfire/src/controllers/qr_code/rqrcode.rs
//
// `testdata/rqrcode.json` is `rust/crates/campfire/src/controllers/qr_code/testdata/rqrcode.json`, what
// `reference-tools/campfire/rqrcode.rb` prints when run in the reference image (RQRCode itself).
module Campfire.App.Tests.RqrcodeTests

open System
open System.IO
open System.Text.Json
open Xunit
open Campfire.App.Controllers
open Campfire.App.Tests.Support
open Campfire.RailsCompat

[<Fact>]
let ``matches rqrcode`` () =
    use vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", "rqrcode.json")))
    let vectors = vectors.RootElement.EnumerateArray() |> Seq.toList
    Assert.True(vectors.Length >= 30)
    for vector in vectors do
        let inputBase64 = str (vector.GetProperty "input_base64")
        let input = (RailsEncoding.strictDecode inputBase64).Value
        let segment = Rqrcode.Segment.create input
        Assert.True(
            (Rqrcode.minimumVersion segment = Some(vector.GetProperty("version").GetInt32())),
            $"version for {inputBase64}"
        )
        let modules =
            (Rqrcode.modules input).Value
            |> Array.map (fun row -> row |> Array.map (fun m -> if m then '1' else '0') |> String)
        Assert.True((String.Join("\n", modules) = str (vector.GetProperty "modules")), $"modules for {inputBase64}")
        match vector.TryGetProperty "svg" with
        | true, svg when svg.ValueKind = JsonValueKind.String ->
            Assert.True((Rqrcode.svgBytes input = Some(str svg)), $"svg for {inputBase64}")
        | _ -> ()

[<Fact>]
let ``data too long for version 40 is none`` () =
    Assert.True((Rqrcode.svgBytes (Array.create 3_000 (byte 'a'))).IsNone)
    Assert.True((Rqrcode.svgBytes (Text.Encoding.UTF8.GetBytes "http://campfire.test")).IsSome)
