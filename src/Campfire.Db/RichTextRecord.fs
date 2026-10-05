// Port of rust/crates/db/src/models/rich_text_record.rs
// `ActionText::RichText` rows (`action_text_rich_texts`). Campfire has one: `Message#body`.
namespace Campfire.Db

type RichTextRecord =
    { Id: int64
      Name: string
      Body: string option
      RecordType: string
      RecordId: int64
      CreatedAt: Timestamp
      UpdatedAt: Timestamp }

module RichTextRecord =
    let private fromRow (r: Row) : RichTextRecord =
        { Id = r.Int64 0
          Name = r.Text 1
          Body = r.OptText 2
          RecordType = r.Text 3
          RecordId = r.Int64 4
          CreatedAt = r.Timestamp 5
          UpdatedAt = r.Timestamp 6 }

    let findFor (conn: Conn) (recordType: string) (recordId: int64) (name: string) : RichTextRecord option =
        conn.QueryOne(
            $"SELECT {Columns.RichTextRecord} FROM \"action_text_rich_texts\" WHERE \"action_text_rich_texts\".\"record_id\" = ? AND \"action_text_rich_texts\".\"record_type\" = ? AND \"action_text_rich_texts\".\"name\" = ? LIMIT 1",
            [| I recordId; S recordType; S name |],
            fromRow
        )

    let create (tx: Tx) (recordType: string) (recordId: int64) (name: string) (body: string) : RichTextRecord =
        let now = tx.Now()
        let id =
            tx.Conn.QueryRow(
                """INSERT INTO "action_text_rich_texts" ("body", "created_at", "name", "record_id", "record_type", "updated_at") VALUES (?, ?, ?, ?, ?, ?) RETURNING "id" """,
                [| S body; T now; S name; I recordId; S recordType; T now |],
                fun r -> r.Int64 0
            )
        { Id = id
          Name = name
          Body = Some body
          RecordType = recordType
          RecordId = recordId
          CreatedAt = now
          UpdatedAt = now }

    let updateBody (tx: Tx) (record: RichTextRecord) (body: string) : RichTextRecord =
        let now = tx.Now()
        tx.Conn.Execute(
            """UPDATE "action_text_rich_texts" SET "body" = ?, "updated_at" = ? WHERE "action_text_rich_texts"."id" = ?""",
            [| S body; T now; I record.Id |]
        )
        |> ignore
        { record with Body = Some body; UpdatedAt = now }

    let delete (tx: Tx) (record: RichTextRecord) : unit =
        tx.Conn.Execute("""DELETE FROM "action_text_rich_texts" WHERE "action_text_rich_texts"."id" = ?""", [| I record.Id |])
        |> ignore
