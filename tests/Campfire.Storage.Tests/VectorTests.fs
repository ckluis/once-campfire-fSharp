// Port of rust/crates/storage/tests/vectors.rs
//
// Golden vectors from `reference-tools/storage/generate.rb` (`vectors/storage.json`).
//
// Set `CAMPFIRE_STORAGE_VECTORS=/path/to/storage.json` to check against another run (e.g. one
// generated on a host whose libvips/ffmpeg match the local ones). Processed media is compared
// byte for byte only when the local libvips/ffmpeg versions match the ones that produced the
// vectors; otherwise the mismatch is reported and the byte checks are skipped, unless
// `CAMPFIRE_REQUIRE_MEDIA_VECTORS` is set, as it is in Dockerfile.toolchain, where the comparisons
// run: there a mismatch fails.
//
// Every test asserts how many vector cases it ran, so a case that is silently skipped fails.
//
// When `CAMPFIRE_STORAGE_REPORT` names a file, the pipeline test writes what it compared there.
module Campfire.Storage.Tests.VectorTests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open Microsoft.Data.Sqlite
open Xunit
open Campfire.RailsCompat
open Campfire.Storage
open Campfire.Tests

let private vectorsPath () : string =
    match Environment.GetEnvironmentVariable "CAMPFIRE_STORAGE_VECTORS" with
    | null
    | "" -> Repo.path "vectors/storage.json"
    | path -> path

let private vectors = lazy (JsonDocument.Parse(File.ReadAllText(vectorsPath ())).RootElement)

let private fixture (name: string) : string = Repo.path $"reference/test/fixtures/files/{name}"

/// `ActiveStorage.verifier`, built as the app builds it (crates/campfire/src/app.rs).
let private verifier () : MessageVerifier =
    let env = File.ReadAllLines(Repo.path "parity/.env.reference")
    let secretKeyBase = env |> Array.pick (fun l -> if l.StartsWith "SECRET_KEY_BASE=" then Some(l.Substring "SECRET_KEY_BASE=".Length) else None)
    RailsCompat.appVerifier (Secrets.create secretKeyBase) "ActiveStorage"

let private prop (name: string) (e: JsonElement) : JsonElement = e.GetProperty name

let private str (e: JsonElement) : string = nonNull (e.GetString())

let private optStr (e: JsonElement) : string option =
    if e.ValueKind = JsonValueKind.String then Some(str e) else None

let private has (name: string) (e: JsonElement) : bool =
    match e.TryGetProperty name with
    | true, _ -> true
    | _ -> false

/// `e[name].as_str()`: None for a missing key, a null and anything that isn't a string.
let private optProp (name: string) (e: JsonElement) : string option =
    match e.TryGetProperty name with
    | true, value -> optStr value
    | _ -> None

let private items (e: JsonElement) : JsonElement[] = e.EnumerateArray() |> Seq.toArray

/// The array `name` of the vectors, which must have `expected` cases.
let private cases (name: string) (expected: int) : JsonElement[] =
    let all = items (prop name vectors.Value)
    Assert.True((all.Length = expected), $"{name} has {all.Length} cases, expected {expected}")
    all

let rec private typed (value: JsonElement) : Marshal.Value =
    match value.ValueKind with
    | JsonValueKind.Null -> Marshal.Value.Nil
    | JsonValueKind.True -> Marshal.Value.Bool true
    | JsonValueKind.False -> Marshal.Value.Bool false
    | JsonValueKind.Number -> Marshal.Value.Int(value.GetInt64())
    | JsonValueKind.Array -> Marshal.Value.Array [ for item in items value -> typed item ]
    | JsonValueKind.Object when has "sym" value -> Marshal.Value.Symbol(str (prop "sym" value))
    | JsonValueKind.Object when has "str" value -> Marshal.Value.Str(str (prop "str" value))
    | JsonValueKind.Object ->
        Marshal.Value.Hash [ for pair in items (prop "hash" value) -> (str pair[0], typed pair[1]) ]
    | _ -> failwith "untyped string"

let private variation (value: JsonElement) : Variation =
    match typed value with
    | Marshal.Value.Hash entries -> Variation.create entries
    | other -> failwith $"{other}"

let private hex (bytes: byte[]) : string = Convert.ToHexStringLower bytes

let private unhex (s: string) : byte[] = Convert.FromHexString s

let private now () : Timestamp = (Timestamps.tryParse "2026-09-26T12:00:00Z").Value

[<Fact>]
let ``variation digests and keys`` () =
    let verifier = verifier ()
    for v in cases "variations" 12 do
        let inspect = str (prop "inspect" v)
        let variation' = variation (prop "typed" v)
        Assert.True((hex (Variation.marshal variation') = str (prop "marshal_hex" v)), inspect)
        Assert.True((Variation.digest variation' = str (prop "digest" v)), inspect)
        Assert.True((Variation.key verifier variation' = str (prop "key" v)), inspect)

        let decoded = (Variation.decode verifier (str (prop "key" v)) (now ())).Value
        let decodedInspect = str (prop "decoded_inspect" v)
        Assert.True((decoded = variation (prop "decoded_typed" v)), decodedInspect)
        Assert.True((hex (Variation.marshal decoded) = str (prop "decoded_marshal_hex" v)), decodedInspect)
        Assert.True((Variation.digest decoded = str (prop "decoded_digest" v)), decodedInspect)

[<Fact>]
let ``verifier messages and disk urls`` () =
    let verifier = verifier ()
    let v = prop "verifier" vectors.Value
    let expiresAt = (Timestamps.tryParse "2030-01-02T03:04:05.678Z").Value
    let expiring = str (prop "expiring" v)
    Assert.Equal(expiring, MessageVerifier.generateRaw verifier "\"x\"" (Some "p") (Some expiresAt))
    Assert.Equal(Some "\"x\"", MessageVerifier.verifyRaw verifier expiring (Some "p") (now ()) |> Result.toOption)
    Assert.True((MessageVerifier.verifyRaw verifier expiring (Some "p") expiresAt |> Result.isError))
    Assert.True((MessageVerifier.verifyRaw verifier expiring (Some "q") (now ()) |> Result.isError))

    let service = DiskService.create "/tmp/unused" "local"
    let weird = Filename.create "weird & <name> \u00fcn\u00ef.png"
    let key = "abcdefghijklmnopqrstuvwxyz12"
    Assert.Equal(str (prop "disk_url_path" v), DiskService.urlPath service verifier key None weird (Some "image/png") "inline")
    Assert.Equal(str (prop "disk_url_path_nil_type" v), DiskService.urlPath service verifier key None weird None "attachment")

    let encodedKey = (str (prop "disk_url_path" v)).Split('/')[4]
    let decoded = (Disk.decodeVerifiedKey verifier encodedKey (now ())).Value
    Assert.Equal(key, decoded.Key)
    Assert.Equal(Some "image/png", decoded.ContentType)
    Assert.Equal("local", decoded.ServiceName)

    let token = (str (prop "direct_upload_path" v)).Split('/') |> Array.last
    let issued = (Timestamps.tryParse "2020-01-01T00:00:00Z").Value
    let decoded = (Disk.decodeVerifiedToken verifier token issued).Value
    Assert.Equal((42L, "abc=="), (decoded.ContentLength, decoded.Checksum))

[<Fact>]
let ``marcel identification`` () =
    for sample in cases "marcel" 51 do
        let data =
            match sample.TryGetProperty "fixture" with
            | true, name when name.ValueKind = JsonValueKind.String -> File.ReadAllBytes(fixture (str name))
            | _ -> unhex (str (prop "data_hex" sample))
        let name = optProp "name" sample
        let declared = optProp "declared_type" sample
        Assert.True(
            (Marcel.identify data name declared = str (prop "content_type" sample)),
            $"{name} declared {declared}"
        )

/// `ContentDisposition` checks the `inline` and `attachment` headers for these names.
[<Fact>]
let ``filenames and dispositions`` () =
    for f in cases "filenames" 16 do
        let input = unhex (str (prop "input_hex" f))
        let filename = Filename.fromBytes input
        let sanitized = Filename.sanitized filename
        Assert.Equal(str (prop "sanitized" f), sanitized)
        // Invalid bytes are replaced on input, so compare base/extension only for valid names.
        let valid =
            try
                UTF8Encoding(false, true).GetBytes(UTF8Encoding(false, true).GetString input) |> ignore
                true
            with :? DecoderFallbackException ->
                false
        if valid then
            Assert.True((hex (Encoding.UTF8.GetBytes(Filename.baseName filename)) = str (prop "base_hex" f)), sanitized)
            Assert.True((hex (Encoding.UTF8.GetBytes(Filename.extension filename)) = str (prop "extension_hex" f)), sanitized)
        Assert.Equal(str (prop "escaped_path" f), Disposition.escapePath sanitized)

let private blobFrom (row: JsonElement) : Blob =
    { Id = (prop "id" row).GetInt64()
      Key = str (prop "key" row)
      Filename = Filename.create (str (prop "filename" row))
      ContentType = optProp "content_type" row
      Metadata = (JsonValue.parse (str (prop "metadata" row))).Value
      ServiceName = str (prop "service_name" row)
      ByteSize = (prop "byte_size" row).GetInt64()
      Checksum = optStr (prop "checksum" row)
      CreatedAt = "" }

[<Fact>]
let ``route paths`` () =
    let verifier = verifier ()
    let service = DiskService.create "/tmp/unused" "local"
    for m in cases "messages" 5 do
        let blob = blobFrom (prop "blob" m)
        let path (name: string) = str (prop name m)
        Assert.Equal(path "rails_blob_path", Paths.blobRedirectPath verifier blob None)
        Assert.Equal(path "rails_blob_download_path", Paths.blobRedirectPath verifier blob (Some "attachment"))
        Assert.Equal(path "rails_blob_proxy_path", Paths.blobProxyPath verifier blob None)
        Assert.Equal(Some blob.Id, Paths.verifySignedBlobId verifier ((path "rails_blob_path").Split('/')[5]) (now ()))

        // `blob.url` -> the disk service URL, with the forced disposition for non-inline types.
        let contentType = Blob.contentType blob
        let disposition = ContentTypes.forcedDisposition contentType |> Option.defaultValue "inline"
        let servicePath (requested: string) =
            let d = ContentTypes.forcedDisposition contentType |> Option.defaultValue requested
            "http://campfire.test"
            + DiskService.urlPath service verifier blob.Key None blob.Filename (Some(ContentTypes.forServing contentType)) d
        Assert.Equal(path "service_url", servicePath disposition)
        Assert.Equal(path "service_url_attachment", servicePath "attachment")

        match optProp "thumb_path" m with
        | Some thumbPath ->
            let first = (items (prop "variants" m))[0]
            let thumb = variation (prop "transformations_typed" first)
            Assert.Equal(thumbPath, Paths.representationRedirectPath verifier blob thumb)
            Assert.Equal(path "thumb_proxy_path", Paths.representationProxyPath verifier blob thumb)
        | None -> ()
        match optProp "poster_path" m with
        | Some posterPath ->
            let poster =
                Variation.create
                    [ "format", Marshal.Value.Symbol "webp"
                      "resize_to_limit", Marshal.Value.Array [ Marshal.Value.Int 1200L; Marshal.Value.Int 800L ] ]
            Assert.Equal(posterPath, Paths.representationRedirectPath verifier blob poster)
            Assert.Equal(path "poster_proxy_path", Paths.representationProxyPath verifier blob poster)
        | None -> ()

// --- The upload -> analyze -> variant/preview pipeline -------------------------------------------

let private requireMediaVectors () : bool =
    not (String.IsNullOrEmpty(Environment.GetEnvironmentVariable "CAMPFIRE_REQUIRE_MEDIA_VECTORS"))

/// The first line of `<program> -version`, or None when the program isn't there.
let private firstLineOf (program: string) : string option =
    let info = ProcessStartInfo(program)
    info.ArgumentList.Add "-version"
    match Process.outputWithin info (TimeSpan.FromSeconds 30.0) with
    | Ok output when output.Success ->
        let text = Encoding.UTF8.GetString output.Stdout
        let first = text.Split('\n')[0]
        Some(first.Trim())
    | _ -> None

/// Straight to stderr, which the test runner shows even for a passing test.
let private note (message: string) : unit = Console.Error.WriteLine message

type private Comparison(versions: JsonElement) =
    let localVips = Vips.version () |> Result.toOption
    let localFfmpeg = firstLineOf "ffmpeg"
    let localFfprobe = firstLineOf "ffprobe"
    let compareImages = Some(str (prop "libvips" versions)) = localVips
    let ffmpegMatches = Some(str (prop "ffmpeg" versions)) = localFfmpeg && Some(str (prop "ffprobe" versions)) = localFfprobe
    let compareVideo = compareImages && ffmpegMatches
    let identical = List<string>()
    let processedIdentical = List<string>()
    let mismatches = List<string>()

    do
        if not compareImages || not compareVideo then
            let vectorVips = str (prop "libvips" versions)
            let vectorFfmpeg = str (prop "ffmpeg" versions)
            let message = $"vectors have libvips {vectorVips} / {vectorFfmpeg}, local libvips {localVips} / {localFfmpeg}"
            Assert.False(requireMediaVectors (), $"byte comparisons would be skipped: {message}")
            note $"note: skipping byte comparisons that depend on versions: {message}"

    member _.CompareImages = compareImages
    member _.CompareVideo = compareVideo
    /// Without ffmpeg and ffprobe the video fixture can be neither identified as previewable nor analyzed.
    member _.CanProcessVideo = localFfmpeg.IsSome && localFfprobe.IsSome
    member _.Identical = identical
    member _.ProcessedIdentical = processedIdentical
    member _.Mismatches = mismatches

    /// Row fields that don't depend on processing output always match; checksum, size and
    /// dimensions of processed media only when the versions match.
    member _.Blob(label: string, actual: Blob, expected: JsonElement, processed: bool, video: bool) : unit =
        Assert.True((Filename.raw actual.Filename = str (prop "filename" expected)), $"{label} filename")
        Assert.True((actual.ContentType = optStr (prop "content_type" expected)), $"{label} content_type")
        Assert.True((actual.ServiceName = str (prop "service_name" expected)), $"{label} service_name")
        let compare = not processed || (if video then compareVideo else compareImages)
        if compare then
            Assert.True((JsonValue.encode actual.Metadata = str (prop "metadata" expected)), $"{label} metadata")
            if actual.Checksum = optStr (prop "checksum" expected) && actual.ByteSize = (prop "byte_size" expected).GetInt64() then
                identical.Add label
                if processed then processedIdentical.Add label
            else
                let expectedSize = (prop "byte_size" expected).GetInt64()
                let expectedChecksum = str (prop "checksum" expected)
                mismatches.Add $"{label}: {actual.ByteSize} bytes {actual.Checksum}, expected {expectedSize} bytes {expectedChecksum}"

[<Fact>]
let ``pipeline matches the reference`` () =
    let vectors' = vectors.Value
    let files = Path.Combine(Path.GetDirectoryName(vectorsPath ()) |> nonNull, "storage")
    use root = TempDir.Create()
    let storage = Storage.create (DiskService.create root.Path "local") (verifier ())
    use conn = openDatabase ()
    let comparison = Comparison(prop "versions" vectors')
    let messages = cases "messages" 5
    let mutable expectedImages = 0
    let mutable expectedVideo = 0

    let checkVariant (source: Blob) (v: JsonElement) (image: Blob) (video: bool) : unit =
        let label = str (prop "label" v)
        let digest = str (prop "variation_digest" v)
        Assert.True((Variation.digest (variation (prop "transformations_typed" v)) = digest), $"{label} digest")
        comparison.Blob(label, image, prop "blob" v, true, video)
        if video then expectedVideo <- expectedVideo + 1 else expectedImages <- expectedImages + 1
        let key = image.Key
        Assert.Equal(Path.Combine(root.Path, key.Substring(0, 2), key.Substring(2, 2), key), Storage.pathFor storage image)
        // The saved reference file is the variant blob's content, and ours is what we recorded.
        let expected = File.ReadAllBytes(Path.Combine(files, str (prop "file" v)))
        Assert.True((Key.checksum expected = str (prop "checksum" (prop "blob" v))), $"{label} vector file")
        let actual = File.ReadAllBytes(Storage.pathFor storage image)
        Assert.True((Some(Key.checksum actual) = image.Checksum), $"{label} stored file")
        let recordId = (Blob.findVariantRecord conn source.Id digest).Value
        let attached = (Blob.attached conn "ActiveStorage::VariantRecord" recordId.Value "image").Value
        Assert.True((attached |> Option.map (fun b -> b.Id) = Some image.Id), $"{label} variant record attachment")

    let mutable skippedVideo = 0
    for m in messages do
        let name = str (prop "fixture" m)
        let isVideoFixture = str (prop "declared_type" m) |> fun t -> t.StartsWith "video"
        if isVideoFixture && not comparison.CanProcessVideo then
            Assert.False(requireMediaVectors (), $"{name} needs ffmpeg and ffprobe")
            note $"note: skipping {name}: ffmpeg and ffprobe are not installed"
            skippedVideo <- skippedVideo + 1
        else
            let data = File.ReadAllBytes(fixture name)
            let created =
                (Storage.createAndUpload storage conn data (Filename.create name) (optProp "declared_type" m) (now ())).Value
            Blob.insertAttachment conn "attachment" "Message" 1L created.Id (now ()) |> ignore
            let blob = (Storage.analyze storage conn created).Value
            comparison.Blob(name, blob, prop "blob" m, false, false)
            Assert.True((Blob.isVariable blob = (prop "variable" m).GetBoolean()), $"{name} variable?")
            Assert.True((Blob.isPreviewable blob = (prop "previewable" m).GetBoolean()), $"{name} previewable?")

            if Blob.isVideo blob then
                let previewImage = (Storage.previewImage storage conn blob (now ())).Value
                comparison.Blob($"{name} preview_image", previewImage, prop "blob" (prop "preview_image" m), true, true)
                expectedVideo <- expectedVideo + 1
                for v in items (prop "variants" m) do
                    let image = (Storage.processPreview storage conn blob (variation (prop "transformations_typed" v)) (now ())).Value
                    checkVariant previewImage v image true
            elif Blob.isVariable blob then
                let thumb = Variation.resizeToLimit 1200L 800L None
                let variation' = (Storage.variationFor blob thumb).Value
                let v = (items (prop "variants" m))[0]
                Assert.True((variation' = variation (prop "transformations_typed" v)))
                let image = (Storage.processVariant storage conn blob variation' (now ())).Value
                // Processing again reuses the variant record.
                Assert.Equal(image.Id, (Storage.processVariant storage conn blob variation' (now ())).Value.Id)
                checkVariant blob v image false

    let named =
        [ ("avatars", 2, Variation.resizeToLimit 512L 512L (Some "webp"))
          ("logos", 1, Variation.resizeToLimit 512L 512L (Some "png")) ]
    for kind, count, first in named do
        for entry in cases kind count do
            let row = prop "blob" entry
            let data = File.ReadAllBytes(fixture (str (prop "fixture" entry)))
            let created =
                (Storage.createAndUpload storage conn data (Filename.create (str (prop "filename" row))) (optProp "content_type" row) (now ())).Value
            let blob = (Storage.analyze storage conn created).Value
            comparison.Blob(str (prop "filename" row), blob, row, false, false)
            for i, v in items (prop "variants" entry) |> Array.indexed do
                let transformations = if i = 0 then first else Variation.resizeToLimit 192L 192L (Some "png")
                let variation' = (Storage.variationFor blob transformations).Value
                Assert.True((variation' = variation (prop "transformations_typed" v)), str (prop "label" v))
                let image = (Storage.processVariant storage conn blob variation' (now ())).Value
                checkVariant blob v image false

    // How many byte comparisons of processed media ran: all of them where the versions match.
    let ranImages = if comparison.CompareImages then expectedImages else 0
    let ranVideo = if comparison.CompareVideo then expectedVideo else 0
    note ("byte-identical: " + String.Join(", ", comparison.Identical))
    note $"processed media byte comparisons: {comparison.ProcessedIdentical.Count} identical (images {ranImages}, video {ranVideo}); all blobs compared: {comparison.Identical.Count}"
    match Environment.GetEnvironmentVariable "CAMPFIRE_STORAGE_REPORT" with
    | null
    | "" -> ()
    | report ->
        let lines =
            [ $"vectors={vectorsPath ()}"
              $"processed_comparisons_ran={ranImages + ranVideo}"
              $"processed_identical={comparison.ProcessedIdentical.Count}"
              $"images_ran={ranImages}"
              $"video_ran={ranVideo}"
              $"all_identical={comparison.Identical.Count}"
              $"mismatches={comparison.Mismatches.Count}"
              $"skipped_video_fixtures={skippedVideo}" ]
        File.WriteAllLines(report, lines @ [ "identical:" ] @ List.ofSeq comparison.ProcessedIdentical @ [ "mismatched:" ] @ List.ofSeq comparison.Mismatches)

    Assert.True(
        comparison.Mismatches.Count = 0,
        "not byte-identical:\n"
        + String.Join("\n", comparison.Mismatches)
        + "\nThe video frame is made by ffmpeg's colour conversion, which differs between CPU architectures: vectors made on another architecture than this one can't match it (the reference image's own ffmpeg gives what this port does). Check against vectors the reference image makes here: bin/storage-media-vectors."
    )
    Assert.True(
        (comparison.ProcessedIdentical.Count = ranImages + ranVideo),
        $"{comparison.ProcessedIdentical.Count} processed comparisons, expected {ranImages + ranVideo}"
    )
    if comparison.CompareImages && comparison.CompareVideo then
        Assert.Equal(0, skippedVideo)
        Assert.Equal(11, comparison.ProcessedIdentical.Count)

/// The video poster in two halves. ffmpeg's frame depends on the architecture it runs on, but libvips'
/// processing of that frame does not, so the reference's own frame (saved in the vectors) must give the
/// reference's posters here, whatever the machine.
[<Fact>]
let ``posters are made from the reference frame as the reference made them`` () =
    let vectors' = vectors.Value
    let versions = prop "versions" vectors'
    let localVips = Vips.version () |> Result.toOption
    if Some(str (prop "libvips" versions)) <> localVips then
        Assert.False(requireMediaVectors (), "byte comparisons would be skipped: libvips differs from the vectors'")
        note $"note: skipping poster comparisons: local libvips {localVips}"
    else
        let files = Path.Combine(Path.GetDirectoryName(vectorsPath ()) |> nonNull, "storage")
        use root = TempDir.Create()
        let storage = Storage.create (DiskService.create root.Path "local") (verifier ())
        use conn = openDatabase ()
        let video = cases "messages" 5 |> Array.find (fun m -> str (prop "fixture" m) = "alpha-centuri.mov")
        let frame = prop "blob" (prop "preview_image" video)
        let upload =
            (Storage.createAndUpload storage conn (File.ReadAllBytes(Path.Combine(files, str (prop "file" (prop "preview_image" video))))) (Filename.create (str (prop "filename" frame))) (optProp "content_type" frame) (now ())).Value
        let image = (Storage.analyze storage conn upload).Value
        let variants = items (prop "variants" video)
        Assert.Equal(3, variants.Length)
        for v in variants do
            let label = str (prop "label" v)
            let variation' = (Storage.variationFor image (variation (prop "transformations_typed" v))).Value
            let processed = (Storage.processVariant storage conn image variation' (now ())).Value
            let expected = File.ReadAllBytes(Path.Combine(files, str (prop "file" v)))
            Assert.True((File.ReadAllBytes(Storage.pathFor storage processed) = expected), $"{label} is not byte-identical to the reference's")
            Assert.True((processed.Checksum = optStr (prop "checksum" (prop "blob" v))), $"{label} checksum")

[<Fact>]
let ``staging a file unfurls it as its bytes would`` () =
    use root = TempDir.Create()
    let storage = Storage.create (DiskService.create root.Path "local") (verifier ())
    for m in cases "messages" 5 do
        let name = str (prop "fixture" m)
        let declared = optProp "declared_type" m
        use fromFile = (Storage.stageFile storage (fixture name) (Filename.create name) declared).Value
        use fromBytes = (Storage.stageBytes storage (File.ReadAllBytes(fixture name)) (Filename.create name) declared).Value
        let a, b = fromFile.Blob, fromBytes.Blob
        Assert.True(((a.ContentType, a.Checksum, a.ByteSize) = (b.ContentType, b.Checksum, b.ByteSize)), name)
        Assert.Equal<byte[]>(File.ReadAllBytes(fixture name), File.ReadAllBytes(DiskService.pathFor storage.Service a.Key))
        // Staging doesn't read the copies back, so check here what `DiskService#upload` would.
        for staged in [ a; b ] do
            Assert.True((Key.checksumFile (DiskService.pathFor storage.Service staged.Key) = staged.Checksum), name)

[<Fact>]
let ``a staged file is deleted unless kept`` () =
    use root = TempDir.Create()
    let storage = Storage.create (DiskService.create root.Path "local") (verifier ())
    let dropped = (Storage.stageBytes storage "dropped"B (Filename.create "a.txt") None).Value
    let droppedPath = DiskService.pathFor storage.Service dropped.Blob.Key
    Assert.True(File.Exists droppedPath)
    (dropped :> IDisposable).Dispose()
    Assert.False(File.Exists droppedPath)

    let kept = (Storage.stageBytes storage "kept"B (Filename.create "b.txt") None).Value
    let keptPath = DiskService.pathFor storage.Service kept.Blob.Key
    kept.Keep()
    (kept :> IDisposable).Dispose()
    Assert.True(File.Exists keptPath)
