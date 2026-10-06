// Port of rust/crates/campfire/src/controllers/presenters/attachments.rs, all but `attached_blob`
// (which the layout needs, so it is in `Attachments.fs`, earlier in the project; this part needs
// `ActiveStorage`, which comes after the concerns).
//
// `has_one_attached` as `User::Avatar` (`:avatar`) and `Account` (`:logo`) use it, over
// `Campfire.Storage` (reference/app/models/user/avatar.rb, account.rb, and Active Storage's
// `Attached::Changes::CreateOne` / `Attachment`).
//
// Assigning an uploaded file and saving the record, in the record's transaction:
// the old attachment is destroyed first (`has_one ... dependent: :destroy` replacing its target;
// its blob is purged after commit, `dependent: :purge_later`), then the new blob and attachment
// rows are inserted, and each attachment change touches the record
// (`belongs_to :record, touch: true`). Since a fresh blob isn't analyzed, `ActiveStorage::AnalyzeJob`
// runs after commit (analysis touches the record again).
//
// Unlike Rails, which uploads after commit, the file is uploaded before the transaction
// (`Assignment.stage`), so the writer never waits on copying and checksumming it; a transaction that
// rolls back deletes it again.
namespace Campfire.App.Presenters

open System
open System.Threading.Tasks
open Campfire.Db
open Campfire.Kit
open Campfire.Storage
open Campfire.App

/// An uploaded file (`ActionDispatch::Http::UploadedFile`), still in its multipart tempfile.
type Upload =
    { File: UploadedFile
      Filename: string
      ContentType: string option }

module Upload =
    /// The upload in `param`, if it is one. `""` and nil mean "no change" to the controllers
    /// here (`params.permit(...).compact` / `avatar=` with nil or "" deletes, see `Assignment`).
    let fromParam (param: Param voption) : Upload option =
        match param with
        | ValueSome(Param.File file) ->
            Some
                { File = file
                  Filename = file.OriginalFilename
                  ContentType = Option.ofObj file.ContentType }
        | _ -> None

    /// Uploads the file to storage for a blob whose row is saved next, off the async threads.
    let stage (app: AppState) (upload: Upload) : Task<Result<Staged, Error>> =
        ActiveStorage.stageFile app upload.File.Path (Filename upload.Filename) upload.ContentType

/// What `record.avatar = value` does with a permitted param value: `Create` holds the `Upload`,
/// then, once staged, the `Staged` blob.
type Assignment<'U> =
    /// The key wasn't given.
    | Unchanged
    /// `nil` or `""`: `Attached::Changes::DeleteOne` (the attachment is destroyed on save).
    | Delete
    /// An uploaded file: `Attached::Changes::CreateOne`.
    | Create of 'U
    /// Anything else (e.g. a plain string that isn't a signed blob id): Rails raises.
    | Invalid

module Assignment =
    let fromParams (parameters: ParamMap) (key: string) : Assignment<Upload> =
        if not (parameters.ContainsKey key) then
            Unchanged
        else
            match parameters.Get key with
            | ValueNone -> Delete
            | ValueSome Param.Null -> Delete
            | ValueSome(Param.Str "") -> Delete
            | ValueSome param ->
                match Upload.fromParam (ValueSome param) with
                | Some upload -> Create upload
                | None -> Invalid

    /// Uploads a new file, so the save only has rows to write.
    let stage (app: AppState) (assignment: Assignment<Upload>) : Task<Result<Assignment<Staged>, Error>> =
        task {
            match assignment with
            | Unchanged -> return Ok Unchanged
            | Delete -> return Ok Delete
            | Invalid -> return Ok Invalid
            | Create upload ->
                match! Upload.stage app upload with
                | Ok staged -> return Ok(Create staged)
                | Error e -> return Error e
        }

/// A blob inserted for an attachment, to analyze after commit.
type Pending = { Blob: Campfire.Storage.Blob }

/// The record an attachment belongs to: its polymorphic type and its table.
type Record =
    { RecordType: string
      Table: string
      Id: int64 }

module Record =
    let user (id: int64) : Record = { RecordType = "User"; Table = "users"; Id = id }

    let account (id: int64) : Record = { RecordType = "Account"; Table = "accounts"; Id = id }

module AttachmentWrites =
    /// `record.<name>.destroy` (the attachment): delete it, touch the record, and purge its blob
    /// after commit. Nothing happens without an attachment (`delegate_missing_to :attachment, allow_nil: true`).
    let destroy (tx: Tx) (record: Record) (name: string) : bool =
        let attachment =
            tx.Conn.QueryOne(
                "SELECT id, blob_id FROM active_storage_attachments WHERE record_type = ? AND record_id = ? AND name = ? LIMIT 1",
                [| S record.RecordType; I record.Id; S name |],
                fun r -> r.Int64 0, r.Int64 1
            )
        match attachment with
        | None -> false
        | Some(attachmentId, blobId) ->
            tx.Conn.Execute("DELETE FROM active_storage_attachments WHERE id = ?", [| I attachmentId |]) |> ignore
            Common.touch tx.Conn record.Table record.Id (tx.Now())
            tx.EmitAfterCommit(Event.PurgeBlob blobId)
            true

    /// `record.<name> = uploaded_file; record.save`: replaces any current attachment.
    let attach (tx: Tx) (record: Record) (name: string) (staged: Staged) : Pending =
        destroy tx record name |> ignore
        let now = tx.Now()
        let blob = Common.raiseStorage (staged.Insert(tx.Conn.Raw, now.ToDateTimeOffset()))
        ActiveStorage.keepAfterCommit tx staged
        Common.raiseStorage (Campfire.Storage.Blob.insertAttachment tx.Conn.Raw name record.RecordType record.Id blob.Id (now.ToDateTimeOffset()))
        |> ignore
        Common.touch tx.Conn record.Table record.Id now
        { Blob = blob }

    /// Applies an assignment inside the record's save. Returns the blob to analyze after commit.
    let assign (tx: Tx) (record: Record) (name: string) (assignment: Assignment<Staged>) : Pending option =
        match assignment with
        | Unchanged -> None
        | Delete ->
            destroy tx record name |> ignore
            None
        | Create staged -> Some(attach tx record name staged)
        | Invalid -> Err.fail (DbError.other "Could not find or build blob: expected attachable")

    let private tableFor (recordType: string) : string option =
        match recordType with
        | "User" -> Some "users"
        | "Account" -> Some "accounts"
        | "Message" -> Some "messages"
        | _ -> None

    /// `ActiveStorage::AnalyzeJob`: `blob.analyze`, then `touch_attachment_records`. The file is
    /// analyzed off the writer; only the metadata update and touches run on it.
    let analyze (app: AppState) (blobId: int64) : Task<Result<unit, string>> =
        task {
            match! app.Db.Read(fun conn -> Common.raiseStorage (Campfire.Storage.Blob.find conn.Raw blobId)) with
            | Error error -> return Error(DbError.display error)
            | Ok None -> return Ok()
            | Ok(Some blob) ->
                match! ActiveStorage.analyzedMetadata app blob with
                | Error e -> return Error(sprintf "%A" e)
                | Ok metadata ->
                    let! written =
                        app.Db.Write(fun tx ->
                            Common.raiseStorage (Campfire.Storage.Blob.updateMetadata tx.Conn.Raw metadata blob) |> ignore
                            for (recordType, recordId) in Common.raiseStorage (Campfire.Storage.Blob.attachmentRecords tx.Conn.Raw blobId) do
                                match tableFor recordType with
                                | Some table -> Common.touch tx.Conn table recordId (tx.Now())
                                | None -> ())
                    return written |> Result.mapError DbError.display
        }

    /// After commit: `analyze_blob_later` (the file was uploaded before the save).
    let analyzeLater (app: AppState) (pending: Pending option) : unit =
        match pending with
        | None -> ()
        | Some { Blob = blob } -> app.Jobs.PerformLater("ActiveStorage::AnalyzeJob", fun () -> analyze app blob.Id)

    /// `record.<name>.variant(name).processed if record.<name>.variable?`: the processed variant's
    /// blob, or `None` when there's no attachment or it can't be transformed.
    let processedVariant (app: AppState) (record: Record) (name: string) (transformations: Variation) : Task<Result<Campfire.Storage.Blob option, Error>> =
        task {
            match! app.Read(fun conn -> Attachments.attachedBlob conn record.RecordType record.Id name) with
            | Error e -> return Error e
            | Ok(Some blob) when Campfire.Storage.Blob.isVariable blob ->
                match! ActiveStorage.processedRepresentation app blob transformations with
                | Ok variant -> return Ok(Some variant)
                | Error e -> return Error e
            | Ok _ -> return Ok None
        }
