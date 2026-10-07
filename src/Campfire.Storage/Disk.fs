// Port of rust/crates/storage/src/disk.rs
namespace Campfire.Storage

open System
open System.IO
open System.Text
open Campfire.RailsCompat
open Campfire.Storage.Disposition

/// `ActiveStorage::Service::DiskService`: files at `<root>/<key[0..2]>/<key[2..4]>/<key>`, and the
/// signed disk URLs (`/rails/active_storage/disk/:encoded_key/*filename`) and upload tokens
/// (`PUT /rails/active_storage/disk/:encoded_token`).
type DiskService = { Root: string; Name: string }

/// The payload of a disk URL's `encoded_key` (purpose "blob_key").
type DiskKey =
    { Key: string
      Disposition: string
      ContentType: string option
      ServiceName: string }

/// The payload of a direct-upload `encoded_token` (purpose "blob_token").
type DiskToken =
    { Key: string
      ContentType: string option
      ContentLength: int64
      Checksum: string
      ServiceName: string }

module DiskService =
    /// Campfire's `local` service: `root: Rails.root.join("storage", "files")` (config/storage.yml).
    let create (root: string) (name: string) : DiskService = { Root = root; Name = name }

    let name (service: DiskService) : string = service.Name

    let root (service: DiskService) : string = service.Root

    /// `str::get(start..stop)`: the bytes `start..stop` of the UTF-8 text, or None when they are
    /// out of range or cut a character in two.
    let private strGet (text: string) (start: int) (stop: int) : string option =
        let bytes = Encoding.UTF8.GetBytes text
        let isBoundary (i: int) = i = 0 || i = bytes.Length || (i < bytes.Length && (bytes[i] &&& 0xC0uy) <> 0x80uy)
        if start <= stop && stop <= bytes.Length && isBoundary start && isBoundary stop then
            Some(Encoding.UTF8.GetString(bytes, start, stop - start))
        else
            None

    /// `[key[0..1], key[2..3]].join("/")`.
    let private folderFor (key: string) : string =
        // Keys are base36; anything else takes the byte-exact path.
        if key.Length >= 4 && key.AsSpan(0, 4).IndexOfAnyExceptInRange('\000', '\127') < 0 then
            $"{key.Substring(0, 2)}/{key.Substring(2, 2)}"
        else
            let a = strGet key 0 2 |> Option.defaultValue key
            let b =
                strGet key 2 4
                |> Option.orElse (strGet key 2 (Encoding.UTF8.GetByteCount key))
                |> Option.defaultValue ""
            $"{a}/{b}"

    /// Rust's `PathBuf::join`: a rooted component replaces what came before.
    let private join (a: string) (b: string) : string = Path.Combine(a, b)

    let pathFor (service: DiskService) (key: string) : string = join (join service.Root (folderFor key)) key

    let private makePathFor (service: DiskService) (key: string) : StorageResult<string> =
        let path = pathFor service key
        Io.attempt (fun () ->
            match Path.GetDirectoryName path with
            | null
            | "" -> ()
            | parent -> Directory.CreateDirectory parent |> ignore
            path)

    let delete (service: DiskService) (key: string) : StorageResult<unit> =
        Io.attempt (fun () ->
            // `fs::remove_file` fails for a missing file, and `File.Delete` doesn't; either way it is Ok here.
            File.Delete(pathFor service key))

    let private ensureIntegrityOf (service: DiskService) (key: string) (checksum: string) : StorageResult<unit> =
        match Io.attempt (fun () -> Key.checksumFile (pathFor service key)) with
        | Error e -> Error e
        | Ok actual when actual <> checksum ->
            match delete service key with
            | Error e -> Error e
            | Ok() -> Error StorageError.Integrity
        | Ok _ -> Ok()

    /// `upload(key, io, checksum:)`: write, then verify the MD5 and delete on mismatch.
    let upload (service: DiskService) (key: string) (reader: Stream) (checksum: string option) : StorageResult<unit> =
        result {
            let! path = makePathFor service key
            do!
                Io.attempt (fun () ->
                    use file = new FileStream(path, FileMode.Create, FileAccess.Write)
                    reader.CopyTo file
                    file.Flush())
            match checksum with
            | Some checksum -> do! ensureIntegrityOf service key checksum
            | None -> ()
        }

    let download (service: DiskService) (key: string) : StorageResult<byte[]> =
        try
            Ok(File.ReadAllBytes(pathFor service key))
        with
        | :? FileNotFoundException
        | :? DirectoryNotFoundException -> Error StorageError.FileNotFound
        | :? IOException as e -> Error(StorageError.Io e)
        | :? UnauthorizedAccessException as e -> Error(StorageError.Io e)

    /// `delete_prefixed(prefix)`: `rm_rf` everything matching `path_for("#{prefix}*")`.
    let deletePrefixed (service: DiskService) (prefix: string) : StorageResult<unit> =
        let pattern = pathFor service prefix
        let dir, stem =
            match pattern.LastIndexOf '/' with
            | -1 -> ".", pattern
            | i -> pattern.Substring(0, i), pattern.Substring(i + 1)
        if not (Directory.Exists dir) then
            Ok()
        else
            Io.attempt (fun () ->
                for entry in Directory.EnumerateFileSystemEntries dir |> Seq.toArray do
                    if (nonNull (Path.GetFileName entry)).StartsWith(stem, StringComparison.Ordinal) then
                        if Directory.Exists entry then Directory.Delete(entry, true) else File.Delete entry)

    let exist (service: DiskService) (key: string) : bool =
        let path = pathFor service key
        File.Exists path || Directory.Exists path

    /// The path of `service.url(key, expires_in:, filename:, content_type:, disposition:)`
    /// (the caller prefixes `ActiveStorage::Current.url_options`' protocol and host).
    let urlPath
        (service: DiskService)
        (verifier: MessageVerifier)
        (key: string)
        (expiresAt: Timestamp option)
        (filename: Filename)
        (contentType: string option)
        (disposition: string)
        : string =
        let sanitized = Filename.sanitized filename
        let payload =
            Value.Object
                [ "key", Value.String key
                  "disposition", Value.String(contentDispositionWith disposition sanitized)
                  "content_type", (match contentType with Some t -> Value.String t | None -> Value.Null)
                  "service_name", Value.String service.Name ]
        let encodedKey = MessageVerifier.generateRaw verifier (JsonValue.encode payload) (Some "blob_key") expiresAt
        $"/rails/active_storage/disk/{escapeSegment encodedKey}/{escapePath sanitized}"

    /// The path of `url_for_direct_upload`.
    let urlPathForDirectUpload
        (service: DiskService)
        (verifier: MessageVerifier)
        (key: string)
        (expiresAt: Timestamp)
        (contentType: string option)
        (contentLength: int64)
        (checksum: string)
        : string =
        let payload =
            Value.Object
                [ "key", Value.String key
                  "content_type", (match contentType with Some t -> Value.String t | None -> Value.Null)
                  "content_length", Value.Int contentLength
                  "checksum", Value.String checksum
                  "service_name", Value.String service.Name ]
        let token = MessageVerifier.generateRaw verifier (JsonValue.encode payload) (Some "blob_token") (Some expiresAt)
        $"/rails/active_storage/disk/{escapeSegment token}"

/// Verifies a signed payload and reads it as JSON (`Json::parse(&verifier.verify_raw(..).ok()?).ok()?`).
module private Signed =
    let verified (verifier: MessageVerifier) (message: string) (purpose: string) (now: Timestamp) : Value option =
        match MessageVerifier.verifyRaw verifier message (Some purpose) now with
        | Ok data -> JsonValue.parse data
        | Error _ -> None

module Disk =
    /// `DiskController#decode_verified_key`.
    let decodeVerifiedKey (verifier: MessageVerifier) (encodedKey: string) (now: Timestamp) : DiskKey option =
        match Signed.verified verifier encodedKey "blob_key" now with
        | None -> None
        | Some data ->
            let str (name: string) = data.TryGet name |> Option.bind (fun v -> v.AsString)
            match str "key", str "disposition", str "service_name" with
            | Some key, Some disposition, Some serviceName ->
                Some
                    { Key = key
                      Disposition = disposition
                      ContentType = str "content_type"
                      ServiceName = serviceName }
            | _ -> None

    /// `DiskController#decode_verified_token`.
    let decodeVerifiedToken (verifier: MessageVerifier) (encodedToken: string) (now: Timestamp) : DiskToken option =
        match Signed.verified verifier encodedToken "blob_token" now with
        | None -> None
        | Some data ->
            let str (name: string) = data.TryGet name |> Option.bind (fun v -> v.AsString)
            let contentLength = data.TryGet "content_length" |> Option.bind (fun v -> v.AsInt64)
            match str "key", contentLength, str "checksum", str "service_name" with
            | Some key, Some contentLength, Some checksum, Some serviceName ->
                Some
                    { Key = key
                      ContentType = str "content_type"
                      ContentLength = contentLength
                      Checksum = checksum
                      ServiceName = serviceName }
            | _ -> None
