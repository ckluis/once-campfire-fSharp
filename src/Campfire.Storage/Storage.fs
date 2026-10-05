// Port of rust/crates/storage/src/storage.rs
/// The Active Storage flows Campfire drives, over one disk service and the app verifier:
/// uploads (`create_and_upload!`), analysis, tracked variants (`VariantWithRecord`) and video
/// previews (`ActiveStorage::Preview`).
///
/// Each flow is split in two, so the slow part never holds the database's writer: the file work
/// (copying, checksumming, libvips, ffmpeg, analysis) takes no connection and is blocking, so
/// call it from a blocking task; it leaves a `Staged` blob whose file is already uploaded. The
/// record step then saves rows inside a transaction and is quick. The `SqliteConnection`
/// functions that do both at once are for tests and tools.
namespace Campfire.Storage

open System
open System.IO
open Microsoft.Data.Sqlite
open Campfire.RailsCompat
open Campfire.Storage.Analyze

type Storage =
    { Service: DiskService
      /// `ActiveStorage.verifier`: `RailsCompat.appVerifier secrets "ActiveStorage"`. It signs
      /// blob ids (purpose "blob_id": `ActiveStorage::Blob` overrides both the signed-id verifier
      /// and `combine_signed_id_purposes`, so this is *not* the Active Record signed-id scheme),
      /// variation keys ("variation"), disk URLs ("blob_key") and direct-upload tokens
      /// ("blob_token"). The data goes in and comes out as encoded JSON, because key order is part
      /// of the signed bytes (e.g. `{key:, disposition:, content_type:, service_name:}`).
      Verifier: MessageVerifier }

/// A new blob whose file is already in the service but whose row isn't saved yet. Disposing it
/// deletes the file, so a write that rolls back (or never saves the row) leaves no orphan behind;
/// `Keep` it once the row is committed.
[<Sealed>]
type Staged internal (blob: NewBlob, service: DiskService) =
    let mutable blob = blob
    let mutable kept = false

    member _.Blob : NewBlob = blob

    member internal _.WithMetadata(metadata: Value) : unit = blob <- { blob with Metadata = metadata }

    /// Inserts the blob's row.
    member _.Insert(conn: SqliteConnection, now: Timestamp) : StorageResult<Blob> = NewBlob.insert conn now blob

    /// The row is saved for good: keep the file.
    member _.Keep() : unit = kept <- true

    interface IDisposable with
        member _.Dispose() =
            if not kept then
                DiskService.delete service blob.Key |> ignore

module Storage =
    let create (service: DiskService) (verifier: MessageVerifier) : Storage = { Service = service; Verifier = verifier }

    let private discard (staged: Staged) : unit = (staged :> IDisposable).Dispose()

    let private dispose (resource: IDisposable) : unit = resource.Dispose()

    // --- File work: no connection, blocking -------------------------------------------------------

    /// Copies the blob's bytes into the service. Unlike `DiskService#upload`, the copy isn't read
    /// back to verify its checksum: the checksum was just computed from these same local bytes, so
    /// reading the copy back would only compare them with themselves, and `openBlob` still
    /// verifies the file before it's analyzed or made into a variant or poster. The `Staged` comes
    /// first, so a failed copy deletes what it wrote.
    let internal stage (storage: Storage) (blob: NewBlob) (reader: Stream) : StorageResult<Staged> =
        let staged = new Staged(blob, storage.Service)
        match DiskService.upload storage.Service blob.Key reader None with
        | Ok() -> Ok staged
        | Error e ->
            discard staged
            Error e

    /// The file half of `Blob.create_and_upload!(io:, filename:, content_type:)` as attaching an
    /// uploaded file does (`identify: true`): unfurls the file at `source` and uploads it.
    let stageFile (storage: Storage) (source: string) (filename: Filename) (declaredType: string option) : StorageResult<Staged> =
        result {
            let! blob = NewBlob.unfurlFile source filename declaredType storage.Service.Name true
            let! file = Io.attempt (fun () -> new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete))
            use file = file
            return! stage storage blob file
        }

    /// `stageFile` for bytes in memory.
    let stageBytes (storage: Storage) (data: byte[]) (filename: Filename) (declaredType: string option) : StorageResult<Staged> =
        let blob = NewBlob.unfurl data filename declaredType storage.Service.Name true
        use reader = new MemoryStream(data, false)
        stage storage blob reader

    /// `blob.open`: a tempfile named `ActiveStorage-<id>-...<.ext>`, checksum-verified.
    let openBlob (storage: Storage) (blob: Blob) : StorageResult<TempFile> =
        result {
            let! file = Io.attempt (fun () -> TempFile.Create($"ActiveStorage-{blob.Id}-", Filename.extensionWithDelimiter blob.Filename))
            let checkedFile =
                match Io.attemptFound (fun () -> File.Copy(DiskService.pathFor storage.Service blob.Key, file.Path, true)) with
                | Error e -> Error e
                | Ok() ->
                    match blob.Checksum with
                    | None -> Ok()
                    | Some checksum ->
                        match Io.attempt (fun () -> Key.checksumFile file.Path) with
                        | Error e -> Error e
                        | Ok actual when actual <> checksum -> Error StorageError.Integrity
                        | Ok _ -> Ok()
            match checkedFile with
            | Ok() -> return file
            | Error e ->
                dispose file
                return! Error e
        }

    /// `metadata.merge(extracted.merge(analyzed: true))`
    let private analyzed (metadata: Value) (extracted: Value) : Value =
        JsonValue.merge (JsonValue.set "analyzed" (Value.Bool true) extracted) metadata

    /// What `blob.analyze` saves: `metadata.merge(analyzer.metadata.merge(analyzed: true))`.
    let analyzedMetadata (storage: Storage) (blob: Blob) : StorageResult<Value> =
        result {
            let analyzer = Analyzer.forContentType (Blob.contentType blob)
            let! extracted =
                match analyzer with
                | Null -> Ok JsonValue.object
                | _ ->
                    result {
                        let! file = openBlob storage blob
                        use file = file
                        return! Analyzer.metadata analyzer file.Path
                    }
            return analyzed blob.Metadata extracted
        }

    /// Stages a generated image with the metadata its analysis would save.
    let private stageAnalyzed (storage: Storage) (path: string) (filename: Filename) (contentType: string) : StorageResult<Staged> =
        result {
            let! staged = stageFile storage path filename (Some contentType)
            let analyzer = Analyzer.forContentType (defaultArg staged.Blob.ContentType "")
            match Analyzer.metadata analyzer path with
            | Error e ->
                discard staged
                return! Error e
            | Ok extracted ->
                staged.WithMetadata(analyzed staged.Blob.Metadata extracted)
                return staged
        }

    /// The file half of `VariantWithRecord#processed` for an already-defaulted variation (see
    /// `variationFor`): transforms the blob and stages the variant image, already analyzed (Rails
    /// analyzes it in an `AnalyzeJob` after commit).
    let transformVariant (storage: Storage) (blob: Blob) (variation: Variation) : StorageResult<Staged> =
        result {
            let! output =
                result {
                    let! input = openBlob storage blob
                    use input = input
                    return! Process.transform input.Path variation
                }
            use output = output
            let! format = Variation.format variation
            let filename = Filename.create $"{Filename.baseName blob.Filename}.{format.ToLowerInvariant()}"
            let! contentType = Variation.contentType variation
            return! stageAnalyzed storage output.Path filename contentType
        }

    /// The file half of `Preview#process`: draws the video's frame with ffmpeg and stages it as
    /// `<base>.jpg` (`image/jpeg`), already analyzed.
    let drawPreviewImage (storage: Storage) (blob: Blob) : StorageResult<Staged> =
        result {
            if not (Blob.isPreviewable blob) then
                return! Error(StorageError.Unpreviewable(Blob.contentType blob))
            let! frame =
                result {
                    let! input = openBlob storage blob
                    use input = input
                    return! Process.videoPreview input.Path
                }
            let! output = Io.attempt (fun () -> TempFile.Create("ActiveStorage-", ".jpg"))
            use output = output
            do! Io.attempt (fun () -> File.WriteAllBytes(output.Path, frame))
            return! stageAnalyzed storage output.Path (Filename.create $"{Filename.baseName blob.Filename}.jpg") "image/jpeg"
        }

    // --- Record work: quick, inside the caller's transaction -------------------------------------

    /// `blob.variant(transformations)`: the variation defaulted to the blob's variant format.
    let variationFor (blob: Blob) (transformations: Variation) : StorageResult<Variation> =
        if not (Blob.isVariable blob) then
            Error(StorageError.Invariable(Blob.contentType blob))
        else
            Ok(Variation.defaultTo [ "format", Marshal.Value.Str(Blob.defaultVariantFormat blob) ] transformations)

    /// The processed variant's image blob, if `variant_records` already has it.
    let existingVariant (conn: SqliteConnection) (blob: Blob) (variation: Variation) : StorageResult<Blob option> =
        match Blob.findVariantRecord conn blob.Id (Variation.digest variation) with
        | Ok(Some recordId) -> Blob.attached conn "ActiveStorage::VariantRecord" recordId "image"
        | Ok None -> Ok None
        | Error e -> Error e

    /// The record half of `VariantWithRecord#processed`: the variant record, its image blob and
    /// attachment. `None` when another request recorded the variant first; that one is then
    /// `existingVariant`, and `image` should be dropped.
    let recordVariant
        (conn: SqliteConnection)
        (blob: Blob)
        (variation: Variation)
        (image: Staged)
        (now: Timestamp)
        : StorageResult<Blob option> =
        result {
            match! Blob.insertVariantRecord conn blob.Id (Variation.digest variation) with
            | None -> return None
            | Some recordId ->
                let! image = image.Insert(conn, now)
                let! _ = Blob.insertAttachment conn "image" "ActiveStorage::VariantRecord" recordId image.Id now
                return Some image
        }

    /// `blob.preview_image`, if it has been generated.
    let existingPreviewImage (conn: SqliteConnection) (blob: Blob) : StorageResult<Blob option> =
        Blob.attached conn "ActiveStorage::Blob" blob.Id "preview_image"

    /// The record half of `Preview#process`: attaches the frame as the blob's `preview_image`.
    /// `None` when another request attached one first; `image` should then be dropped.
    let recordPreviewImage (conn: SqliteConnection) (blob: Blob) (image: Staged) (now: Timestamp) : StorageResult<Blob option> =
        result {
            match! existingPreviewImage conn blob with
            | Some _ -> return None
            | None ->
                let! image = image.Insert(conn, now)
                let! _ = Blob.insertAttachment conn "preview_image" "ActiveStorage::Blob" blob.Id image.Id now
                return Some image
        }

    // --- Both at once, for tests and tools ---------------------------------------------------------

    /// `Blob.create_and_upload!(io:, filename:, content_type:)`.
    let createAndUpload
        (storage: Storage)
        (conn: SqliteConnection)
        (data: byte[])
        (filename: Filename)
        (declaredType: string option)
        (now: Timestamp)
        : StorageResult<Blob> =
        result {
            let! staged = stageBytes storage data filename declaredType
            use staged = staged
            let! blob = staged.Insert(conn, now)
            staged.Keep()
            return blob
        }

    /// `blob.analyze`: `update!(metadata: metadata.merge(analyzer.metadata.merge(analyzed: true)))`,
    /// returning the blob as saved. Rails then touches the blob's attachment records (see
    /// `Blob.attachmentRecords`).
    let analyze (storage: Storage) (conn: SqliteConnection) (blob: Blob) : StorageResult<Blob> =
        result {
            let! metadata = analyzedMetadata storage blob
            return! Blob.updateMetadata conn metadata blob
        }

    /// `VariantWithRecord#processed`: reuses the variant record when present, otherwise
    /// transforms the blob and records the variant.
    let processVariant (storage: Storage) (conn: SqliteConnection) (blob: Blob) (variation: Variation) (now: Timestamp) : StorageResult<Blob> =
        result {
            match! existingVariant conn blob variation with
            | Some image -> return image
            | None ->
                let! image = transformVariant storage blob variation
                use image = image
                match! recordVariant conn blob variation image now with
                | Some recorded ->
                    image.Keep()
                    return recorded
                | None ->
                    match! existingVariant conn blob variation with
                    | Some existing -> return existing
                    | None -> return! Error StorageError.FileNotFound
        }

    /// `blob.preview_image`, generating it with ffmpeg when missing (`Preview#process`).
    let previewImage (storage: Storage) (conn: SqliteConnection) (blob: Blob) (now: Timestamp) : StorageResult<Blob> =
        result {
            match! existingPreviewImage conn blob with
            | Some image -> return image
            | None ->
                let! image = drawPreviewImage storage blob
                use image = image
                match! recordPreviewImage conn blob image now with
                | Some recorded ->
                    image.Keep()
                    return recorded
                | None ->
                    match! existingPreviewImage conn blob with
                    | Some existing -> return existing
                    | None -> return! Error StorageError.FileNotFound
        }

    /// `blob.preview(transformations).processed`, returning the blob to serve: the preview image
    /// itself for empty transformations, otherwise its processed variant.
    let processPreview
        (storage: Storage)
        (conn: SqliteConnection)
        (blob: Blob)
        (transformations: Variation)
        (now: Timestamp)
        : StorageResult<Blob> =
        result {
            let! image = previewImage storage conn blob now
            if Variation.isEmpty transformations then
                return image
            else
                let! variation = variationFor image transformations
                return! processVariant storage conn image variation now
        }

    /// `blob.representation(transformations).processed`: a preview for previewable blobs, a
    /// variant for variable ones.
    let processRepresentation
        (storage: Storage)
        (conn: SqliteConnection)
        (blob: Blob)
        (transformations: Variation)
        (now: Timestamp)
        : StorageResult<Blob> =
        if Blob.isPreviewable blob then
            processPreview storage conn blob transformations now
        elif Blob.isVariable blob then
            result {
                let! variation = variationFor blob transformations
                return! processVariant storage conn blob variation now
            }
        else
            Error(StorageError.Unrepresentable(Blob.contentType blob))

    let pathFor (storage: Storage) (blob: Blob) : string = DiskService.pathFor storage.Service blob.Key

    /// Deletes the blob's files (`Blob#delete`); rows are the caller's.
    let deleteFiles (storage: Storage) (blob: Blob) : StorageResult<unit> = Blob.deleteFiles storage.Service blob
