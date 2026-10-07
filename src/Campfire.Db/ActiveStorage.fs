// Port of rust/crates/db/src/models/active_storage.rs
// Rows of `active_storage_blobs` and `active_storage_attachments`, for the models that
// have attachments. Uploading, analysis and variants belong to `Campfire.Storage`.
namespace Campfire.Db

type Blob =
    { Id: int64
      Key: string
      Filename: string
      ContentType: string option
      /// JSON text, e.g. `{"identified":true,"width":800,"height":600,"analyzed":true}`.
      Metadata: string option
      ServiceName: string
      ByteSize: int64
      Checksum: string option
      CreatedAt: Timestamp }

module Blob =
    let private fromRow (r: Row) : Blob =
        { Id = r.Int64 0
          Key = r.Text 1
          Filename = r.Text 2
          ContentType = r.OptText 3
          Metadata = r.OptText 4
          ServiceName = r.Text 5
          ByteSize = r.Int64 6
          Checksum = r.OptText 7
          CreatedAt = r.Timestamp 8 }

    let find (conn: Conn) (id: int64) : Blob =
        conn.QueryOne(
            $"SELECT {Columns.Blob} FROM \"active_storage_blobs\" WHERE \"active_storage_blobs\".\"id\" = ? LIMIT 1",
            [| I id |],
            fromRow
        )
        |> Err.orNotFound "ActiveStorage::Blob"

    /// Inserts a blob row; `blob.Id` and `CreatedAt` are ignored and assigned.
    let create (tx: Tx) (blob: Blob) : Blob =
        let now = tx.Now()
        let id =
            tx.Conn.QueryRow(
                """INSERT INTO "active_storage_blobs" ("byte_size", "checksum", "content_type", "created_at", "filename", "key", "metadata", "service_name") VALUES (?, ?, ?, ?, ?, ?, ?, ?) RETURNING "id" """,
                [| I blob.ByteSize
                   Sql.optS blob.Checksum
                   Sql.optS blob.ContentType
                   T now
                   S blob.Filename
                   S blob.Key
                   Sql.optS blob.Metadata
                   S blob.ServiceName |],
                fun r -> r.Int64 0
            )
        { blob with Id = id; CreatedAt = now }

type Attachment =
    { Id: int64
      Name: string
      RecordType: string
      RecordId: int64
      BlobId: int64
      CreatedAt: Timestamp }

module Attachment =
    let private fromRow (r: Row) : Attachment =
        { Id = r.Int64 0
          Name = r.Text 1
          RecordType = r.Text 2
          RecordId = r.Int64 3
          BlobId = r.Int64 4
          CreatedAt = r.Timestamp 5 }

    /// `has_one_attached`'s lookup.
    let findFor (conn: Conn) (recordType: string) (recordId: int64) (name: string) : Attachment option =
        conn.QueryOne(
            $"SELECT {Columns.Attachment} FROM \"active_storage_attachments\" WHERE \"active_storage_attachments\".\"record_id\" = ? AND \"active_storage_attachments\".\"record_type\" = ? AND \"active_storage_attachments\".\"name\" = ? LIMIT 1",
            [| I recordId; S recordType; S name |],
            fromRow
        )

    let create (tx: Tx) (recordType: string) (recordId: int64) (name: string) (blobId: int64) : Attachment =
        let now = tx.Now()
        let id =
            tx.Conn.QueryRow(
                """INSERT INTO "active_storage_attachments" ("blob_id", "created_at", "name", "record_id", "record_type") VALUES (?, ?, ?, ?, ?) RETURNING "id" """,
                [| I blobId; T now; S name; I recordId; S recordType |],
                fun r -> r.Int64 0
            )
        { Id = id
          Name = name
          RecordType = recordType
          RecordId = recordId
          BlobId = blobId
          CreatedAt = now }

    let delete (tx: Tx) (attachment: Attachment) : unit =
        tx.Conn.Execute("""DELETE FROM "active_storage_attachments" WHERE "active_storage_attachments"."id" = ?""", [| I attachment.Id |])
        |> ignore

    let blob (conn: Conn) (attachment: Attachment) : Blob = Blob.find conn attachment.BlobId
