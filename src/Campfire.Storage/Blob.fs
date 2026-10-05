// Port of rust/crates/storage/src/blob.rs
/// `active_storage_blobs`, `active_storage_attachments` and `active_storage_variant_records` rows.
///
/// Every function takes a `SqliteConnection`, so callers decide the transaction boundaries the
/// way the Rails models' callbacks would.
namespace Campfire.Storage

open System
open System.Globalization
open System.IO
open Microsoft.Data.Sqlite
open Campfire.RailsCompat

type Blob =
    { Id: int64
      Key: string
      Filename: Filename
      ContentType: string option
      /// The `metadata` column, an ordered JSON object (`store :metadata, coder: JSON`).
      Metadata: Value
      ServiceName: string
      ByteSize: int64
      Checksum: string option
      CreatedAt: string }

/// A blob built from uploaded bytes, not yet saved (`Blob.build_after_unfurling`).
type NewBlob =
    { Key: string
      Filename: Filename
      ContentType: string option
      Metadata: Value
      ServiceName: string
      ByteSize: int64
      Checksum: string }

module Blob =
    /// Active Record's SQLite datetime format: UTC, microseconds only when non-zero.
    let formatTimestamp (t: Timestamp) : string =
        let utc = t.UtcDateTime
        let micros = (utc.Ticks % TimeSpan.TicksPerSecond) / 10L
        let basis = utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        if micros = 0L then basis else $"{basis}.{micros:D6}"

    let private columns = "id, key, filename, content_type, metadata, service_name, byte_size, checksum, created_at"

    let private optionalText (reader: SqliteDataReader) (i: int) : string option =
        if reader.IsDBNull i then None else Some(reader.GetString i)

    let private fromRow (row: SqliteDataReader) : Blob =
        { Id = row.GetInt64 0
          Key = row.GetString 1
          Filename = Filename.create (row.GetString 2)
          ContentType = optionalText row 3
          Metadata = optionalText row 4 |> Option.bind JsonValue.parse |> Option.defaultValue JsonValue.object
          ServiceName = row.GetString 5
          ByteSize = row.GetInt64 6
          Checksum = optionalText row 7
          CreatedAt = row.GetString 8 }

    let private selectBlob = $"SELECT {columns} FROM active_storage_blobs WHERE id = ?1"

    let private selectByKey = $"SELECT {columns} FROM active_storage_blobs WHERE key = ?1"

    let private selectAttached =
        let prefixed = columns.Split(", ") |> Array.map (fun c -> "b." + c) |> String.concat ", "
        $"SELECT {prefixed} FROM active_storage_blobs b JOIN active_storage_attachments a ON a.blob_id = b.id WHERE a.record_type = ?1 AND a.record_id = ?2 AND a.name = ?3 ORDER BY a.id LIMIT 1"

    let find (conn: SqliteConnection) (id: int64) : StorageResult<Blob option> = Sql.queryOne conn selectBlob [| id |] fromRow

    let findByKey (conn: SqliteConnection) (key: string) : StorageResult<Blob option> =
        Sql.queryOne conn selectByKey [| key |] fromRow

    /// The blob attached to `record` under `name` (`has_one_attached`).
    let attached (conn: SqliteConnection) (recordType: string) (recordId: int64) (name: string) : StorageResult<Blob option> =
        Sql.queryOne conn selectAttached [| recordType; recordId; name |] fromRow

    let contentType (blob: Blob) : string = defaultArg blob.ContentType ""

    let isImage (blob: Blob) : bool = (contentType blob).StartsWith("image", StringComparison.Ordinal)

    let isVideo (blob: Blob) : bool = (contentType blob).StartsWith("video", StringComparison.Ordinal)

    let isAudio (blob: Blob) : bool = (contentType blob).StartsWith("audio", StringComparison.Ordinal)

    /// `variable?`
    let isVariable (blob: Blob) : bool = ContentTypes.isVariable (contentType blob)

    /// `previewable?`: only the video previewer can accept in Campfire's image (no poppler or
    /// mupdf binaries are installed, so the PDF previewers never accept).
    let isPreviewable (blob: Blob) : bool = isVideo blob && Process.ffmpegExists ()

    /// `representable?`
    let isRepresentable (blob: Blob) : bool = isVariable blob || isPreviewable blob

    let isAnalyzed (blob: Blob) : bool =
        match blob.Metadata.TryGet "analyzed" with
        | Some Value.Null
        | Some(Value.Bool false)
        | None -> false
        | Some _ -> true

    /// `metadata[:width]`/`[:height]` as the views read them.
    let dimension (name: string) (blob: Blob) : float option =
        match blob.Metadata.TryGet name with
        | Some(Value.Int i) -> Some(float i)
        | Some(Value.Float f) -> Some f
        | _ -> None

    /// `update!(metadata:)`: the blob as saved.
    let updateMetadata (conn: SqliteConnection) (metadata: Value) (blob: Blob) : StorageResult<Blob> =
        Sql.execute conn "UPDATE active_storage_blobs SET metadata = ?1 WHERE id = ?2" [| JsonValue.encode metadata; blob.Id |]
        |> Result.map (fun _ -> { blob with Metadata = metadata })

    /// `Representable#format`: the filename's extension when Marcel agrees it names the
    /// content type, otherwise the content type's first registered extension.
    let private format (blob: Blob) : string option =
        let extension = Filename.extension blob.Filename
        if extension <> "" && Marcel.forExtension extension = contentType blob then
            Some extension
        else
            Marcel.extensionsOf (contentType blob) |> Array.tryHead

    /// `default_variant_format`: web images keep their format, everything else becomes PNG.
    let defaultVariantFormat (blob: Blob) : string =
        if ContentTypes.isWebImage (contentType blob) then format blob |> Option.defaultValue "png" else "png"

    /// `delete`: the file, plus any legacy untracked variants under `variants/<key>/`.
    let deleteFiles (service: DiskService) (blob: Blob) : StorageResult<unit> =
        result {
            do! DiskService.delete service blob.Key
            if isImage blob then
                do! DiskService.deletePrefixed service $"variants/{blob.Key}/"
        }

    /// Inserts an `active_storage_attachments` row. Rails then touches the record (and whatever it
    /// touches in turn, e.g. `Message belongs_to :room, touch: true`); that's up to the caller.
    let insertAttachment
        (conn: SqliteConnection)
        (name: string)
        (recordType: string)
        (recordId: int64)
        (blobId: int64)
        (createdAt: Timestamp)
        : StorageResult<int64> =
        Sql.execute
            conn
            "INSERT INTO active_storage_attachments (name, record_type, record_id, blob_id, created_at) VALUES (?1, ?2, ?3, ?4, ?5)"
            [| name; recordType; recordId; blobId; formatTimestamp createdAt |]
        |> Result.map (fun _ -> Sql.lastInsertRowid conn)

    /// The `(record_type, record_id)` of every attachment of a blob: the records `Blob#touch_attachments`
    /// touches after the blob is updated (e.g. by analysis).
    let attachmentRecords (conn: SqliteConnection) (blobId: int64) : StorageResult<(string * int64) list> =
        Sql.queryAll
            conn
            "SELECT record_type, record_id FROM active_storage_attachments WHERE blob_id = ?1 ORDER BY id"
            [| blobId |]
            (fun row -> row.GetString 0, row.GetInt64 1)

    /// `blob.variant_records.find_by(variation_digest:)`.
    let findVariantRecord (conn: SqliteConnection) (blobId: int64) (variationDigest: string) : StorageResult<int64 option> =
        Sql.queryOne
            conn
            "SELECT id FROM active_storage_variant_records WHERE blob_id = ?1 AND variation_digest = ?2"
            [| blobId; variationDigest |]
            (fun row -> row.GetInt64 0)

    /// `INSERT` half of `create_or_find_by!`: `None` when another writer created it first.
    let insertVariantRecord (conn: SqliteConnection) (blobId: int64) (variationDigest: string) : StorageResult<int64 option> =
        match
            Sql.execute
                conn
                "INSERT INTO active_storage_variant_records (blob_id, variation_digest) VALUES (?1, ?2)"
                [| blobId; variationDigest |]
        with
        | Ok _ -> Ok(Some(Sql.lastInsertRowid conn))
        | Error e when Sql.isUniqueViolation e -> Ok None
        | Error e -> Error e

module NewBlob =
    let private contentType (head: byte[]) (filename: Filename) (declaredType: string option) (identify: bool) : string option =
        if declaredType.IsNone || identify then
            Some(Marcel.identify head (Some(Filename.sanitized filename)) declaredType)
        else
            declaredType

    let private build (filename: Filename) (contentType: string option) (serviceName: string) (byteSize: int64) (checksum: string) : NewBlob =
        { Key = Key.generateKey ()
          Filename = filename
          ContentType = contentType
          Metadata = Value.Object [ "identified", Value.Bool true ]
          ServiceName = serviceName
          ByteSize = byteSize
          Checksum = checksum }

    /// `build_after_unfurling(io:, filename:, content_type:, identify:)`: generates the key,
    /// computes the checksum, identifies the content type with Marcel (unless a declared type
    /// is given with `identify: false`) and marks the blob `identified`.
    let unfurl (data: byte[]) (filename: Filename) (declaredType: string option) (serviceName: string) (identify: bool) : NewBlob =
        let contentType = contentType data filename declaredType identify
        build filename contentType serviceName (int64 data.Length) (Key.checksum data)

    /// [`unfurl`] for a file, reading only as much of it as identification needs, and streaming
    /// it through the checksum.
    let unfurlFile
        (path: string)
        (filename: Filename)
        (declaredType: string option)
        (serviceName: string)
        (identify: bool)
        : StorageResult<NewBlob> =
        Io.attempt (fun () ->
            let head =
                use file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)
                let buffer = Array.zeroCreate<byte> (Marcel.magicPrefixLen ())
                let mutable filled = 0
                let mutable finished = false
                while not finished && filled < buffer.Length do
                    let read = file.Read(buffer, filled, buffer.Length - filled)
                    if read = 0 then finished <- true else filled <- filled + read
                Array.sub buffer 0 filled
            let contentType = contentType head filename declaredType identify
            let byteSize = FileInfo(path).Length
            build filename contentType serviceName byteSize (Key.checksumFile path))

    let insert (conn: SqliteConnection) (createdAt: Timestamp) (blob: NewBlob) : StorageResult<Blob> =
        let createdAt = Blob.formatTimestamp createdAt
        Sql.execute
            conn
            "INSERT INTO active_storage_blobs (key, filename, content_type, metadata, service_name, byte_size, checksum, created_at) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)"
            [| box blob.Key
               box (Filename.raw blob.Filename)
               (match blob.ContentType with
                | Some t -> box t
                | None -> null)
               box (JsonValue.encode blob.Metadata)
               box blob.ServiceName
               box blob.ByteSize
               box blob.Checksum
               box createdAt |]
        |> Result.map (fun _ ->
            { Id = Sql.lastInsertRowid conn
              Key = blob.Key
              Filename = blob.Filename
              ContentType = blob.ContentType
              Metadata = blob.Metadata
              ServiceName = blob.ServiceName
              ByteSize = blob.ByteSize
              Checksum = Some blob.Checksum
              CreatedAt = createdAt })
