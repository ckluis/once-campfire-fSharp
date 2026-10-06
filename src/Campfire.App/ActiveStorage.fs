// Port of rust/crates/campfire/src/active_storage.rs
//
// The Active Storage endpoints (`activestorage/config/routes.rb`) over `Campfire.Storage`, as the
// engine's controllers serve them, plus `ActiveStorage::Blob#purge` for the purge job.
//
// Downloads stay public behind signed URLs; the disk `PUT` and direct uploads require a Campfire
// session (`reference/config/initializers/active_storage_authentication.rb`). The disk service's
// `show` gets `Cache-Control: max-age=3600, public`
// (`reference/config/initializers/active_storage.rb`). These controllers inherit from
// `ActiveStorage::BaseController` (`protect_from_forgery with: :exception`), not
// `ApplicationController`, so none of Campfire's concerns run.
namespace Campfire.App

open System
open System.Globalization
open System.IO
open System.Threading
open System.Threading.Tasks
open Campfire.RailsCompat
open Campfire.Ruby
open Campfire.Kit
open Campfire.App.Presenters
open Campfire.Db
open Campfire.Storage
open Campfire.Storage.FileServer

/// A read-only stream over parts of files and the bytes between them, read a chunk at a time, so that
/// several byte ranges are never read into memory up front.
[<Sealed>]
type PartsStream(parts: BodyPart list) =
    inherit Stream()
    let mutable remaining = parts
    let mutable file: FileStream | null = null
    let mutable fileLeft = 0L
    let mutable pending = ReadOnlyMemory<byte>.Empty

    let closeFile () =
        match file with
        | null -> ()
        | open' ->
            open'.Dispose()
            file <- null

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = raise (NotSupportedException())
        and set _ = raise (NotSupportedException())

    override _.Flush() = ()
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength _ = raise (NotSupportedException())
    override _.Write(_: byte[], _: int, _: int) = raise (NotSupportedException())

    override this.Read(buffer: byte[], offset: int, count: int) : int =
        this.ReadAsync(Memory<byte>(buffer, offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult()

    override _.ReadAsync(buffer: Memory<byte>, cancellationToken: CancellationToken) : ValueTask<int> =
        let read =
            task {
                let mutable produced = -1
                while produced < 0 do
                    if buffer.Length = 0 then
                        produced <- 0
                    elif pending.Length > 0 then
                        let n = min pending.Length buffer.Length
                        pending.Slice(0, n).CopyTo buffer
                        pending <- pending.Slice n
                        produced <- n
                    else
                        match file with
                        | null ->
                            match remaining with
                            | [] -> produced <- 0
                            | Bytes bytes :: rest ->
                                pending <- ReadOnlyMemory<byte> bytes
                                remaining <- rest
                            | File(path, start, stop) :: rest ->
                                let opened =
                                    new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete, 64 * 1024, FileOptions.Asynchronous ||| FileOptions.SequentialScan)
                                opened.Seek(int64 start, SeekOrigin.Begin) |> ignore
                                file <- opened
                                fileLeft <- int64 (stop - start + 1UL)
                                remaining <- rest
                        | open' ->
                            if fileLeft <= 0L then
                                closeFile ()
                            else
                                let! n = open'.ReadAsync(buffer.Slice(0, int (min (int64 buffer.Length) fileLeft)), cancellationToken)
                                if n = 0 then
                                    closeFile ()
                                else
                                    fileLeft <- fileLeft - int64 n
                                    produced <- n
                return produced
            }
        ValueTask<int> read

    override _.Dispose(disposing: bool) =
        if disposing then closeFile ()
        base.Dispose disposing

module ActiveStorage =
    /// `ActiveStorage.service_urls_expire_in`
    [<Literal>]
    let private ServiceUrlsExpireIn = 300L

    /// `http_cache_forever`: `expires_in 100.years`.
    [<Literal>]
    let private HundredYears = 3_155_695_200UL

    /// The most image and video jobs (variants, previews, analysis) that run at once.
    [<Literal>]
    let private MaxMediaJobs = 4

    /// A storage error inside a database closure (see `Common.storageError`).
    let storageError = Common.storageError

    let private storageToError (error: StorageError) : Error = Internal(Exception(StorageError.message error))

    let private statusError (code: int) : Error = Campfire.Kit.Error.Status code

    // --- Blobs -----------------------------------------------------------------------------------------

    /// `ActiveStorage::SetBlob#set_blob`: `Blob.find_signed!(params[:signed_blob_id] || params[:signed_id])`.
    /// A bad signature is `head :not_found`; a valid one for a missing blob is `RecordNotFound`.
    let private setBlob (c: Ctx) : Task<Result<Campfire.Storage.Blob, Error>> =
        task {
            let signedId =
                match c.ParamStr "signed_blob_id" with
                | null ->
                    match c.ParamStr "signed_id" with
                    | null -> ""
                    | id -> id
                | id -> id
            let storage = c.App.Storage
            match Paths.verifySignedBlobId storage.Verifier signedId (c.Now()) with
            | None -> return halt (Concerns.head Status.NotFound)
            | Some blobId ->
                match! c.App.Read(fun conn -> Common.raiseStorage (Campfire.Storage.Blob.find conn.Raw blobId)) with
                | Ok(Some blob) -> return Ok blob
                | Ok None -> return Error NotFound
                | Error e -> return Error e
        }

    /// `blob.url(disposition:)` on the disk service: a signed `/rails/active_storage/disk/...` URL on
    /// this request's host that expires in `service_urls_expire_in`.
    let private blobUrl (c: Ctx) (blob: Campfire.Storage.Blob) (disposition: string option) : string =
        let storage = c.App.Storage
        let contentType = ContentTypes.forServing (Campfire.Storage.Blob.contentType blob)
        let disposition =
            ContentTypes.forcedDisposition (Campfire.Storage.Blob.contentType blob)
            |> Option.orElse disposition
            |> Option.defaultValue "inline"
        let expiresAt = c.Now().AddSeconds(float ServiceUrlsExpireIn)
        let path = DiskService.urlPath storage.Service storage.Verifier blob.Key (Some expiresAt) blob.Filename (Some contentType) disposition
        c.UrlFor path

    let private disposition (c: Ctx) : string option =
        match c.ParamStr "disposition" with
        | null -> None
        | value -> Some value

    /// `http_cache_forever(public: true)`: cache for 100 years, ETag on the full path, and a fixed
    /// Last-Modified. `Some 304` when the client's copy is fresh.
    let private httpCacheForever (c: Ctx) : Response voption =
        c.ExpiresIn(HundredYears, { ExpiresIn.Default with Public = true; Immutable = true })
        let lastModified = (Timestamps.tryParse "2011-01-01T00:00:00Z").Value
        c.FreshWhen { Freshness.Default with Etag = c.Request.Fullpath; LastModified = ValueSome lastModified; Public = true }

    /// `send_data`/`send_stream`'s `Content-Disposition` for the blob's sanitized filename.
    let private withDisposition (response: Response) (disposition: string) (blob: Campfire.Storage.Blob) : Response =
        response.Header(Hdr.ContentDisposition, ContentDisposition.format disposition (Filename.sanitized blob.Filename))

    /// `send_blob_stream(blob, disposition:)`: the whole file, inline unless the type is forced to
    /// download.
    let private sendBlobStream (c: Ctx) (blob: Campfire.Storage.Blob) (disposition: string option) : Result<Response, Error> =
        let storage = c.App.Storage
        let path = Storage.pathFor storage blob
        if not (System.IO.File.Exists path) then
            // `rescue ActiveStorage::FileNotFoundError`: expires_now, head :not_found.
            c.ExpiresNow()
            Ok(c.Head Status.NotFound)
        else
            let disposition =
                ContentTypes.forcedDisposition (Campfire.Storage.Blob.contentType blob)
                |> Option.orElse disposition
                |> Option.defaultValue "inline"
            match
                c.SendFile(
                    path,
                    { SendOptions.Default with
                        ContentType = ContentTypes.forServing (Campfire.Storage.Blob.contentType blob)
                        Disposition = null }
                )
            with
            | Ok response -> Ok(withDisposition response disposition blob)
            | Error e -> Error e

    /// The body for byte ranges of files and the bytes between them: a single range is sent as a
    /// file body and several are streamed, so neither is read into memory up front.
    let partsBody (parts: BodyPart list) : Body =
        match parts with
        | [ File(path, start, stop) ] -> Body.File { Path = path; Offset = int64 start; Len = int64 (stop - start + 1UL) }
        | [ Bytes bytes ] -> Body.Bytes(ReadOnlyMemory<byte> bytes)
        | [] -> Body.Empty
        | parts -> Body.Stream(new PartsStream(parts))

    let partsLen (parts: BodyPart list) : uint64 =
        parts
        |> List.sumBy (fun part ->
            match part with
            | Bytes bytes -> uint64 bytes.Length
            | File(_, start, stop) -> stop - start + 1UL)

    /// `send_blob_byte_range_data(blob, range_header)`
    let private sendBlobByteRangeData (c: Ctx) (blob: Campfire.Storage.Blob) (range: string) : Result<Response, Error> =
        let storage = c.App.Storage
        let size = uint64 (max blob.ByteSize 0L)
        match Rack.byteRanges (Some range) size with
        | Some ranges when not ranges.IsEmpty ->
            let path = Storage.pathFor storage blob
            if not (System.IO.File.Exists path) then
                Error(Internal(Exception(StorageError.message StorageError.FileNotFound)))
            else
                let contentTypeForServing = ContentTypes.forServing (Campfire.Storage.Blob.contentType blob)
                let contentType, parts, contentRange =
                    match ranges with
                    | [ (start, stop) ] -> contentTypeForServing, [ File(path, start, stop) ], Some $"bytes {start}-{stop}/{size}"
                    | _ ->
                        // `SecureRandom.hex`: 16 random bytes.
                        let boundary = Convert.ToHexStringLower(Security.Cryptography.RandomNumberGenerator.GetBytes 16)
                        let parts =
                            [ for (start, stop) in ranges do
                                  let heading =
                                      $"\r\n--{boundary}\r\nContent-Type: {contentTypeForServing}\r\nContent-Range: bytes {start}-{stop}/{size}\r\n\r\n"
                                  Bytes(Text.Encoding.UTF8.GetBytes heading)
                                  File(path, start, stop)
                              Bytes(Text.Encoding.UTF8.GetBytes $"\r\n--{boundary}--\r\n") ]
                        $"multipart/byteranges; boundary={boundary}", parts, None
                let disposition = ContentTypes.forcedDisposition (Campfire.Storage.Blob.contentType blob) |> Option.defaultValue "inline"
                let response =
                    c.SendData(
                        ReadOnlyMemory<byte>.Empty,
                        { SendOptions.Default with ContentType = contentType; Disposition = null; Status = 206 }
                    )
                let response = withDisposition response disposition blob
                let length = partsLen parts
                response.Body <- partsBody parts
                match response.Body with
                | Body.Stream _ -> response.Header(Hdr.ContentLength, string length) |> ignore
                | _ -> ()
                match contentRange with
                | Some contentRange -> response.Header("content-range", contentRange) |> ignore
                | None -> ()
                Ok(response.Header("accept-ranges", "bytes"))
        | _ -> Ok(c.Head 416)

    /// `ActiveStorage::Blobs::RedirectController#show`
    let blobsRedirect (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! c.VerifyAuthenticityToken()
            let! blob = setBlob c
            c.ExpiresIn(uint64 ServiceUrlsExpireIn, ExpiresIn.Default)
            let url = blobUrl c blob (disposition c)
            return! c.RedirectToWith(url, { Redirect.Default with AllowOtherHost = true })
        }

    /// `ActiveStorage::Blobs::ProxyController#show`
    let blobsProxy (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! c.VerifyAuthenticityToken()
            let! blob = setBlob c
            let disposition = disposition c
            match c.Request.Header "range" with
            | range when not (isNull range) && not (String.IsNullOrWhiteSpace range) -> return! sendBlobByteRangeData c blob (nonNull range)
            | _ ->
                match httpCacheForever c with
                | ValueSome notModified -> return notModified
                | ValueNone ->
                    let! response = sendBlobStream c blob disposition
                    return response.Header("accept-ranges", "bytes")
        }

    // --- Media work ------------------------------------------------------------------------------------

    /// Runs libvips, ffmpeg or ffprobe work on the blocking pool, a few jobs at a time: each can take a
    /// lot of memory and CPU (libvips threads its own work), and uploads shouldn't queue behind more of
    /// them than the machine can run at once.
    let private permits = lazy (new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount, 1, MaxMediaJobs)))

    let processMedia (work: unit -> StorageResult<'T>) : Task<Result<'T, Error>> =
        task {
            // The permit goes with the work: a request that gives up (a timeout, a closed connection)
            // doesn't stop the blocking task, so it mustn't free the slot either.
            do! permits.Value.WaitAsync()
            return!
                Task.Run(fun () ->
                    try
                        match work () with
                        | Ok value -> Ok value
                        | Error error -> Error(storageToError error)
                    finally
                        permits.Value.Release() |> ignore)
        }

    /// Keeps a staged file once the write saving its row commits; a rollback drops it instead, which
    /// deletes the file.
    let keepAfterCommit (tx: Tx) (staged: Staged) : unit =
        tx.AfterCommit(fun _ -> staged.Keep())

    /// `VariantWithRecord#processed` for an already-defaulted variation: the existing variant, or one
    /// transformed (by `transform`) off the writer and then recorded.
    let processedVariantWith
        (app: AppState)
        (blob: Campfire.Storage.Blob)
        (variation: Variation)
        (transform: Storage -> Campfire.Storage.Blob -> Variation -> StorageResult<Staged>)
        : Task<Result<Campfire.Storage.Blob, Error>> =
        task {
            let storage = app.Storage
            match! app.Read(fun conn -> Common.raiseStorage (Storage.existingVariant conn.Raw blob variation)) with
            | Error e -> return Error e
            | Ok(Some image) -> return Ok image
            | Ok None ->
                match! processMedia (fun () -> transform storage blob variation) with
                | Error e -> return Error e
                | Ok image ->
                    return!
                        app.Write(fun tx ->
                            let conn = tx.Conn.Raw
                            match Common.raiseStorage (Storage.recordVariant conn blob variation image (tx.Now().ToDateTimeOffset())) with
                            | Some recorded ->
                                keepAfterCommit tx image
                                recorded
                            // Another request recorded it first; ours is dropped (and its file deleted).
                            | None ->
                                (image :> IDisposable).Dispose()
                                match Common.raiseStorage (Storage.existingVariant conn blob variation) with
                                | Some existing -> existing
                                | None -> Err.fail (RecordNotFound "ActiveStorage::VariantRecord"))
        }

    let private processedVariant (app: AppState) (blob: Campfire.Storage.Blob) (variation: Variation) =
        processedVariantWith app blob variation (fun storage blob variation -> Storage.transformVariant storage blob variation)

    /// `blob.preview_image`, drawing it with ffmpeg off the writer when it's missing.
    let private previewImage (app: AppState) (blob: Campfire.Storage.Blob) : Task<Result<Campfire.Storage.Blob, Error>> =
        task {
            let storage = app.Storage
            match! app.Read(fun conn -> Common.raiseStorage (Storage.existingPreviewImage conn.Raw blob)) with
            | Error e -> return Error e
            | Ok(Some image) -> return Ok image
            | Ok None ->
                match! processMedia (fun () -> Storage.drawPreviewImage storage blob) with
                | Error e -> return Error e
                | Ok image ->
                    return!
                        app.Write(fun tx ->
                            let conn = tx.Conn.Raw
                            match Common.raiseStorage (Storage.recordPreviewImage conn blob image (tx.Now().ToDateTimeOffset())) with
                            | Some recorded ->
                                keepAfterCommit tx image
                                recorded
                            | None ->
                                (image :> IDisposable).Dispose()
                                match Common.raiseStorage (Storage.existingPreviewImage conn blob) with
                                | Some existing -> existing
                                | None -> Err.fail (RecordNotFound "ActiveStorage::Blob"))
        }

    /// `blob.preview(transformations).processed`: the preview image itself for empty transformations,
    /// otherwise its processed variant.
    let processedPreview (app: AppState) (blob: Campfire.Storage.Blob) (transformations: Variation) : Task<Result<Campfire.Storage.Blob, Error>> =
        task {
            match! previewImage app blob with
            | Error e -> return Error e
            | Ok image ->
                if Variation.isEmpty transformations then
                    return Ok image
                else
                    match Storage.variationFor image transformations with
                    | Error e -> return Error(storageToError e)
                    | Ok variation -> return! processedVariant app image variation
        }

    /// `blob.representation(variation).processed`, reusing an existing variant or preview.
    let processedRepresentation (app: AppState) (blob: Campfire.Storage.Blob) (variation: Variation) : Task<Result<Campfire.Storage.Blob, Error>> =
        task {
            if Campfire.Storage.Blob.isPreviewable blob then
                return! processedPreview app blob variation
            elif Campfire.Storage.Blob.isVariable blob then
                match Storage.variationFor blob variation with
                | Error e -> return Error(storageToError e)
                | Ok variation -> return! processedVariant app blob variation
            else
                return Error(storageToError (StorageError.Unrepresentable(Campfire.Storage.Blob.contentType blob)))
        }

    /// What `blob.analyze` would save, worked out off the writer.
    let analyzedMetadata (app: AppState) (blob: Campfire.Storage.Blob) : Task<Result<Value, Error>> =
        processMedia (fun () -> Storage.analyzedMetadata app.Storage blob)

    /// Uploads a file to storage for a blob whose row the caller saves next (see `keepAfterCommit`).
    let stageFile (app: AppState) (path: string) (filename: Filename) (contentType: string option) : Task<Result<Staged, Error>> =
        task {
            try
                let! staged = Task.Run(fun () -> Storage.stageFile app.Storage path filename contentType)
                return staged |> Result.mapError storageToError
            with e ->
                return Error(Internal e)
        }

    // --- Representations -------------------------------------------------------------------------------

    /// `set_representation`: `@blob.representation(params[:variation_key]).processed`. A bad variation key
    /// is `head :not_found`. Returns the blob that represents it (the variant's or preview's image).
    let private setRepresentation (c: Ctx) (blob: Campfire.Storage.Blob) : Task<Result<Campfire.Storage.Blob, Error>> =
        task {
            let storage = c.App.Storage
            let key = match c.ParamStr "variation_key" with null -> "" | key -> key
            match Variation.decode storage.Verifier key (c.Now()) with
            | Ok variation -> return! processedRepresentation c.App blob variation
            | Error StorageError.InvalidSignature -> return halt (Concerns.head Status.NotFound)
            | Error error -> return Error(storageToError error)
        }

    /// `ActiveStorage::Representations::RedirectController#show`
    let representationsRedirect (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! c.VerifyAuthenticityToken()
            let! blob = setBlob c
            let! image = setRepresentation c blob
            c.ExpiresIn(uint64 ServiceUrlsExpireIn, ExpiresIn.Default)
            let url = blobUrl c image (disposition c)
            return! c.RedirectToWith(url, { Redirect.Default with AllowOtherHost = true })
        }

    /// `ActiveStorage::Representations::ProxyController#show`
    let representationsProxy (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! c.VerifyAuthenticityToken()
            let! blob = setBlob c
            let! image = setRepresentation c blob
            match httpCacheForever c with
            | ValueSome notModified -> return notModified
            | ValueNone -> return! sendBlobStream c image (disposition c)
        }

    // --- Disk service ----------------------------------------------------------------------------------

    let private diskServe (c: Ctx) : Result<Response, Error> =
        let storage = c.App.Storage
        let encodedKey = match c.ParamStr "encoded_key" with null -> "" | key -> key
        match Disk.decodeVerifiedKey storage.Verifier encodedKey (c.Now()) with
        | None -> Ok(c.Head Status.NotFound)
        | Some key ->
            let request: FileServer.Request =
                { Method = c.Request.Method
                  Range = (match c.Request.Header "range" with null -> None | value -> Some value)
                  IfModifiedSince = (match c.Request.Header "if-modified-since" with null -> None | value -> Some value) }
            match FileServer.serveFile request (DiskService.pathFor storage.Service key.Key) key.ContentType (Some key.Disposition) with
            | Error(StorageError.Io(:? FileNotFoundException))
            | Error(StorageError.Io(:? DirectoryNotFoundException)) -> Ok(c.Head Status.NotFound)
            | Error error -> Error(storageToError error)
            | Ok served ->
                let response = Response(served.Status)
                for (name, value) in served.Headers do
                    response.Header(name, value) |> ignore
                // `served.Headers` carries the Content-Length of every part together.
                response.Body <- partsBody served.Body
                Ok response

    /// `ActiveStorage::DiskController#show`, plus the initializer's `after_action` cache header.
    let diskShow (c: Ctx) : Task<Result<Response, Error>> =
        act {
            let! response = diskServe c
            return response.Header(Hdr.CacheControl, "max-age=3600, public")
        }

    /// `ActiveStorageAuthentication#require_active_storage_authentication`: 401 without a session.
    let private requireActiveStorageAuthentication (c: Ctx) : Task<Result<unit, Error>> =
        task {
            match! Concerns.findSessionByCookie c with
            | Error e -> return Error e
            | Ok None -> return halt (Concerns.head 401)
            | Ok(Some _) -> return Ok()
        }

    /// `token[:content_type] == request.content_mime_type && token[:content_length] == request.content_length`
    let private acceptableContent (c: Ctx) (token: DiskToken) : bool =
        let mediaType = match c.Request.MediaType with null -> None | media -> Some(media.ToLowerInvariant())
        let contentLength =
            match c.Request.Header "content-length" with
            | null -> None
            | length ->
                match Int64.TryParse(length.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
                | true, n -> Some n
                | _ -> None
        (token.ContentType |> Option.map (fun t -> t.ToLowerInvariant())) = mediaType && Some token.ContentLength = contentLength

    /// `ActiveStorage::DiskController#update` (the direct-upload PUT), behind
    /// `require_active_storage_authentication`.
    let diskUpdate (c: Ctx) : Task<Result<Response, Error>> =
        task {
            match! requireActiveStorageAuthentication c with
            | Error e -> return Error e
            | Ok() ->
                let storage = c.App.Storage
                let encodedToken = match c.ParamStr "encoded_token" with null -> "" | token -> token
                match Disk.decodeVerifiedToken storage.Verifier encodedToken (c.Now()) with
                | None -> return Ok(c.Head Status.NotFound)
                | Some token ->
                    if not (acceptableContent c token) then
                        return Ok(c.Head Status.UnprocessableEntity)
                    else
                        let body = c.Request.RawPost.ToArray()
                        let uploaded =
                            Task.Run(fun () ->
                                use stream = new MemoryStream(body, false)
                                DiskService.upload storage.Service token.Key stream (Some token.Checksum))
                        match! uploaded with
                        | Ok() -> return Ok(c.Head Status.NoContent)
                        | Error StorageError.Integrity -> return Ok(c.Head Status.UnprocessableEntity)
                        | Error error -> return Error(storageToError error)
        }

    // --- Direct uploads ----------------------------------------------------------------------------------

    /// A stored `created_at` (`YYYY-MM-DD HH:MM:SS[.ffffff]`, UTC) as `ActiveSupport::JSON` encodes
    /// times: ISO 8601 with milliseconds.
    let jsonTime (dbTime: string) : string =
        let formats = [| "yyyy-MM-dd HH:mm:ss.FFFFFFF"; "yyyy-MM-dd HH:mm:ss" |]
        match DateTime.TryParseExact(dbTime, formats, CultureInfo.InvariantCulture, DateTimeStyles.None) with
        | true, time -> time.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
        | _ -> dbTime

    /// `blob.as_json(root: false, methods: :signed_id).merge(direct_upload: { url:, headers: })`
    let private directUploadJson (blob: Campfire.Storage.Blob) (signedId: string) (url: string) (contentType: string option) : string =
        let text (value: string option) = match value with Some value -> Value.String value | None -> Value.Null
        JsonValue.encode (
            Value.Object
                [ "id", Value.Int blob.Id
                  "key", Value.String blob.Key
                  "filename", Value.String(Filename.raw blob.Filename)
                  "content_type", text blob.ContentType
                  "metadata", blob.Metadata
                  "service_name", Value.String blob.ServiceName
                  "byte_size", Value.Int blob.ByteSize
                  "checksum", text blob.Checksum
                  "created_at", Value.String(jsonTime blob.CreatedAt)
                  "signed_id", Value.String signedId
                  "direct_upload",
                  Value.Object [ "url", Value.String url; "headers", Value.Object [ "Content-Type", text contentType ] ] ]
        )

    /// `ActiveStorage::DirectUploadsController#create`, behind CSRF and
    /// `require_active_storage_authentication`.
    let directUploadsCreate (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! c.VerifyAuthenticityToken()
            do! requireActiveStorageAuthentication c
            // `params.expect(blob: [:filename, :byte_size, :checksum, :content_type, metadata: {}])`
            let! blobParam = c.Params.Require "blob"
            let blobParams =
                match blobParam.AsHash with
                | null -> None
                | hash -> Some hash
            match blobParams with
            | None -> return! Error(ParameterMissing "blob")
            | Some blobParams ->
                // Strings, and numbers as their text: Active Storage's JavaScript sends `byte_size` as a number.
                let text (key: string) : string option =
                    match blobParams.Get key with
                    | ValueSome(Param.Str s) -> Some s
                    | ValueSome(Param.Number n) -> Some(JsonNumber.toString n)
                    | _ -> None
                match text "filename" |> Option.filter (fun f -> f <> ""), text "checksum" |> Option.filter (fun c -> c <> "") with
                | Some filename, Some checksum ->
                    // Stricter than Rails, whose attribute cast makes a byte size that isn't a number 0 (an
                    // upload only an empty file could fill): it's refused.
                    match text "byte_size" |> Option.bind Ruby.integerCast with
                    | None -> return! Error(statusError Status.UnprocessableEntity)
                    // The upload's PUT body is read into memory, so it's capped like other bodies: don't hand out
                    // a URL for more than it will accept. (Campfire's editor only attaches mentions and embeds;
                    // files go up with the message form.)
                    | Some byteSize when byteSize < 0L || byteSize > int64 RequestBody.MaxBufferedBody ->
                        return! Error(statusError Status.PayloadTooLarge)
                    | Some byteSize ->
                        let contentType = text "content_type"
                        let metadata =
                            match blobParams.Get "metadata" with
                            | ValueSome(Param.Hash metadata) -> metadata.ToJson()
                            | _ -> JsonValue.object
                        let storage = c.App.Storage
                        let now = c.Now()
                        let newBlob: NewBlob =
                            { Key = Key.generateKey ()
                              Filename = Filename.create filename
                              ContentType = contentType
                              Metadata = metadata
                              ServiceName = storage.Service.Name
                              ByteSize = byteSize
                              Checksum = checksum }
                        let! (blob: Campfire.Storage.Blob) = c.App.Write(fun tx -> Common.raiseStorage (NewBlob.insert tx.Conn.Raw now newBlob))
                        let expiresAt = now.AddSeconds(float ServiceUrlsExpireIn)
                        let url =
                            c.UrlFor(DiskService.urlPathForDirectUpload storage.Service storage.Verifier blob.Key expiresAt contentType byteSize checksum)
                        let signedId = Paths.signedBlobId storage.Verifier blob.Id None
                        let json = directUploadJson blob signedId url contentType
                        return c.RenderAs(Status.Ok, Response.JsonUtf8, json)
                | _ -> return! Error(statusError Status.UnprocessableEntity)
        }

    // --- Purging ---------------------------------------------------------------------------------------

    /// Deletes the attachment row, returning its blob id.
    let private destroyAttachment (conn: Conn) (recordType: string) (recordId: int64) (name: string) : int64 option =
        let attachment =
            conn.QueryOne(
                "SELECT id, blob_id FROM active_storage_attachments WHERE record_type = ? AND record_id = ? AND name = ? LIMIT 1",
                [| S recordType; I recordId; S name |],
                fun r -> r.Int64 0, r.Int64 1
            )
        match attachment with
        | None -> None
        | Some(id, blobId) ->
            conn.Execute("DELETE FROM active_storage_attachments WHERE id = ?", [| I id |]) |> ignore
            Some blobId

    let private deleteFiles (storage: Storage) (blob: Campfire.Storage.Blob) : Task<Result<unit, string>> =
        task {
            try
                match! Task.Run(fun () -> Storage.deleteFiles storage blob) with
                | Ok() -> return Ok()
                | Error error -> return Error(StorageError.message error)
            with e ->
                return Error e.Message
        }

    /// `ActiveStorage::Blob#purge`: `destroy` (refused while any attachment still points at the blob;
    /// destroys its variant records and preview image attachment, whose blobs are purged later), then
    /// delete the files.
    let purge (app: AppState) (blobId: int64) : Task<Result<unit, string>> =
        task {
            let! destroyed =
                app.Db.Write(fun tx ->
                    let conn = tx.Conn
                    match Common.raiseStorage (Campfire.Storage.Blob.find conn.Raw blobId) with
                    | None -> None
                    | Some blob ->
                        // before_destroy(prepend: true) { raise ActiveRecord::InvalidForeignKey if attachments.exists? }
                        if not (Common.raiseStorage (Campfire.Storage.Blob.attachmentRecords conn.Raw blobId)).IsEmpty then
                            None
                        else
                            let dependents = ResizeArray<int64>()
                            // before_destroy { variant_records.destroy_all }: each record's image attachment goes too.
                            let variantRecords =
                                conn.QueryAll("SELECT id FROM active_storage_variant_records WHERE blob_id = ?", [| I blobId |], fun r -> r.Int64 0)
                            for recordId in variantRecords do
                                destroyAttachment conn "ActiveStorage::VariantRecord" recordId "image" |> Option.iter dependents.Add
                                conn.Execute("DELETE FROM active_storage_variant_records WHERE id = ?", [| I recordId |]) |> ignore
                            // has_one_attached :preview_image (dependent: :destroy on the attachment)
                            destroyAttachment conn "ActiveStorage::Blob" blobId "preview_image" |> Option.iter dependents.Add
                            conn.Execute("DELETE FROM active_storage_blobs WHERE id = ?", [| I blobId |]) |> ignore
                            // after_destroy_commit :purge_dependent_blob_later
                            for dependent in dependents do
                                tx.EmitAfterCommit(Event.PurgeBlob dependent)
                            Some blob)
            match destroyed with
            | Error error -> return Error(DbError.display error)
            | Ok(Some blob) -> return! deleteFiles app.Storage blob
            | Ok None -> return Ok()
        }
