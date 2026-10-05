// Port of rust/crates/assets/tests/reference.rs
//
// Golden tests against what the reference app produced (tests/Campfire.Assets.Tests/reference/*,
// written by src/Campfire.Assets/script/revendor from `assets:precompile` and the real Rails
// helpers). Files in `overrides/` deliberately differ from the reference, so only their digests
// and bytes are allowed to.
module Campfire.Assets.Tests.ReferenceTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Xunit
open Campfire.Assets
open Campfire.Assets.Serve
open Campfire.Tests

// Case counts in tests/Campfire.Assets.Tests/reference; regenerate the fixtures and these together.
[<Literal>]
let ManifestEntries = 314

[<Literal>]
let StaticCases = 332

let private fixture (name: string) : string = File.ReadAllText(Repo.path $"tests/Campfire.Assets.Tests/reference/{name}")

let private jsonFixture (name: string) : JsonElement = JsonDocument.Parse(fixture name).RootElement

let private sha256 (bytes: ReadOnlySpan<byte>) : string = Convert.ToHexStringLower(SHA256.HashData bytes)

let private str (e: JsonElement) : string = nonNull (e.GetString())

/// The logical paths in `overrides/`.
let private overrideFiles () : string list =
    let dir = Repo.path "src/Campfire.Assets/overrides"
    Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
    |> Seq.map (fun file -> Path.GetRelativePath(dir, file).Replace('\\', '/'))
    |> Seq.sort
    |> List.ofSeq

/// Each overridden logical path, with the reference's digested path and ours, sorted by logical path.
let private overridden () : Map<string, string * string> =
    let reference = jsonFixture "manifest.json"
    let ours = Assets.manifest () |> Map.ofArray
    overrideFiles ()
    |> List.choose (fun logical ->
        match reference.TryGetProperty logical with
        | true, entry ->
            let theirs = str (entry.GetProperty "digested_path")
            let ours = ours[logical]
            Assert.True((theirs <> ours), $"{logical} is overridden but digests the same")
            Some(logical, (theirs, ours))
        | _ -> None)
    |> Map.ofList

/// Logical paths the overrides add, which the reference doesn't have at all.
let private added () : string list =
    let reference = jsonFixture "manifest.json"
    overrideFiles () |> List.filter (fun logical -> not (fst (reference.TryGetProperty logical)))

/// `text` with our digested paths for overridden files replaced by the reference's.
let private asReference (text: string) : string =
    overridden () |> Map.values |> Seq.fold (fun (text: string) (theirs, ours) -> text.Replace(ours, theirs)) text

let private get (path: string) : StaticResponse =
    match Assets.serve (StaticRequest.create "GET" path) with
    | Some response -> response
    | None -> failwith $"{path} isn't served"

/// A manifest's entries as comparable text (serde_json's `Value` equality ignores key order),
/// minus `integrity` of each logical path in `logicals`.
let private manifestWithoutIntegrity (entries: JsonProperty seq) (logicals: string list) : string =
    entries
    |> Seq.sortBy (fun p -> p.Name)
    |> Seq.map (fun p ->
        let entry =
            p.Value.EnumerateObject()
            |> Seq.filter (fun f -> not (List.contains p.Name logicals && f.Name = "integrity"))
            |> Seq.map (fun f -> $"{f.Name}={f.Value.GetRawText()}")
            |> String.concat ","
        $"{p.Name}:{entry}")
    |> String.concat ";"

[<Fact>]
let ``manifest matches the reference precompile`` () =
    let referenceJson = jsonFixture "manifest.json"
    let reference =
        referenceJson.EnumerateObject()
        |> Seq.map (fun p -> p.Name, str (p.Value.GetProperty "digested_path"))
        |> Map.ofSeq
    Assert.Equal(ManifestEntries, reference.Count)
    let added = added ()
    let ours =
        Assets.manifest ()
        |> Array.filter (fun (l, _) -> not (List.contains l added))
        |> Array.map (fun (l, d) -> l, asReference d)
        |> Map.ofArray

    let missing = reference |> Map.toList |> List.filter (fun (l, d) -> Map.tryFind l ours <> Some d)
    let extra = ours |> Map.toList |> List.map fst |> List.filter (fun l -> not (reference.ContainsKey l))
    Assert.True(missing.IsEmpty && extra.IsEmpty, $"differs from reference: %A{missing}, extra: %A{extra}")

    let served = JsonDocument.Parse(asReference (Assets.manifestJson ())).RootElement
    let overriddenLogicals = overridden () |> Map.keys |> List.ofSeq
    // The added files aren't in the reference's manifest, and an overridden file's integrity hash
    // covers its own bytes.
    let servedWithoutAdded =
        served.EnumerateObject() |> Seq.filter (fun p -> not (List.contains p.Name added)) |> Seq.toList
    Assert.Equal(reference.Count, servedWithoutAdded.Length)
    Assert.Equal(
        manifestWithoutIntegrity (referenceJson.EnumerateObject()) overriddenLogicals,
        manifestWithoutIntegrity servedWithoutAdded overriddenLogicals
    )

[<Fact>]
let ``compiled files are byte identical to the reference precompile`` () =
    let reference = jsonFixture "compiled_sha256.json"
    let overridden = overridden () |> Map.values |> Seq.map fst |> List.ofSeq
    let mutable checkedFiles = 0
    let mismatched =
        [ for p in reference.EnumerateObject() do
              if not (List.contains p.Name overridden) then
                  checkedFiles <- checkedFiles + 1
                  let response = get $"/assets/{p.Name}"
                  if sha256 response.Body.Span <> str p.Value then yield p.Name ]
    Assert.Empty mismatched
    Assert.Equal(reference.EnumerateObject() |> Seq.length |> (+) (added () |> List.length), (Assets.manifest ()).Length)
    Assert.Equal(reference.EnumerateObject() |> Seq.length |> (+) -overridden.Length, checkedFiles)

[<Fact>]
let ``stylesheet link tag all matches the reference`` () =
    let tags = Assets.stylesheetLinkTagAll [ "data-turbo-track", "reload" ]
    Assert.Equal(fixture "stylesheet_link_tag_all.html", tags.Html)
    Assert.Equal(fixture "link_header.txt", Assets.appendPreloadLinks "" tags.PreloadLinks)

[<Fact>]
let ``javascript importmap tags match the reference`` () =
    Assert.Equal(fixture "javascript_importmap_tags.html", asReference (Assets.javascriptImportmapTags ()))

[<Fact>]
let ``public files are served like action dispatch static`` () =
    let overridden = overridden ()
    let mutable cases = 0
    for case in (jsonFixture "static_responses.json").EnumerateArray() do
        cases <- cases + 1
        let env = case.GetProperty "env"
        let path = str (case.GetProperty "path")
        let overrideOf = overridden |> Map.values |> Seq.tryFind (fun (theirs, _) -> path = $"/assets/{theirs}")
        let ourPath = overrideOf |> Option.map (fun (_, ours) -> $"/assets/{ours}")
        let envString (name: string) =
            match env.TryGetProperty name with
            | true, v -> Some(str v)
            | _ -> None
        let request =
            { Method = str (case.GetProperty "method")
              Path = defaultArg ourPath path
              Range = envString "HTTP_RANGE"
              AcceptEncoding = envString "HTTP_ACCEPT_ENCODING"
              IfModifiedSince = None }
        let label = $"{request.Method} {request.Path} {env.GetRawText()}"
        let expectedStatus = case.GetProperty("status").GetInt32()
        let expectedHeaders = case.GetProperty "headers"

        // The probe's fallthrough app answers 404 with x-cascade: pass.
        if expectedStatus = 404 && fst (expectedHeaders.TryGetProperty "x-cascade") then
            Assert.True((Assets.serve request).IsNone, $"{label} should fall through")
        else
            let response =
                match Assets.serve request with
                | Some response -> response
                | None -> failwith $"{label} not served"
            Assert.True((response.Status = expectedStatus), $"{label}: status {response.Status}, expected {expectedStatus}")

            let ours =
                response.Headers |> List.filter (fun (name, _) -> name <> "last-modified") |> Map.ofList
            let theirs = expectedHeaders.EnumerateObject() |> Seq.map (fun p -> p.Name, str p.Value) |> Map.ofSeq

            // Our manifest lists the same entries in load-path order rather than the build
            // machine's readdir order, so its length and bytes can't match; an overridden file's
            // length, ETag and bytes are its own.
            if request.Path = "/assets/.manifest.json" || overrideOf.IsSome then
                Assert.Equal(Map.tryFind "content-type" theirs, Map.tryFind "content-type" ours)
            else
                Assert.True((ours = theirs), $"{label}: headers %A{ours}, expected %A{theirs}")
                if request.Method = "GET" then
                    Assert.True((sha256 response.Body.Span = str (case.GetProperty "body_sha256")), $"{label}: body differs")
    Assert.Equal(StaticCases, cases)

[<Fact>]
let ``last modified round trips to a 304`` () =
    let response = get "/robots.txt"
    let lastModified = (StaticResponse.header "last-modified" response).Value
    let notModified =
        Assets.serve { StaticRequest.create "GET" "/robots.txt" with IfModifiedSince = Some lastModified } |> Option.get
    Assert.Equal(304, notModified.Status)
    Assert.True(notModified.Headers.IsEmpty && notModified.Body.IsEmpty)

[<Fact>]
let ``head requests have no body`` () =
    let response = Assets.serve (StaticRequest.create "HEAD" "/robots.txt") |> Option.get
    Assert.Equal(200, response.Status)
    Assert.True response.Body.IsEmpty
    Assert.Equal(Some "99", StaticResponse.header "content-length" response)

[<Fact>]
let ``multiple ranges are multipart`` () =
    let sound = Assets.audioPath "56k.mp3"
    let response =
        Assets.serve { StaticRequest.create "GET" sound with Range = Some "bytes=0-1, 4-5" } |> Option.get
    Assert.Equal(206, response.Status)
    // Rack sets multipart/byteranges, then Static overwrites it with the file's type.
    Assert.Equal(Some "audio/mpeg", StaticResponse.header "content-type" response)
    let body = Encoding.UTF8.GetString response.Body.Span
    Assert.StartsWith("\r\n--AaB03x\r\ncontent-type: audio/mpeg\r\ncontent-range: bytes 0-1/", body)
    Assert.EndsWith("\r\n--AaB03x--\r\n", body)
