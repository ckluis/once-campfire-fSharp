// Port of rust/crates/storage/src/paths.rs
/// Active Storage route helpers (`activestorage/config/routes.rb`) and blob signed ids.
///
/// `rails_blob_path` and `url_for(representation)` resolve through
/// `resolve_model_to_route = :rails_storage_redirect`; `rails_storage_proxy_path` gives the
/// proxy variants. `urls_expire_in` is unset in Campfire, so these signed ids never expire.
module Campfire.Storage.Paths

open Campfire.RailsCompat
open Campfire.Ruby
open Campfire.Storage.Disposition

[<Literal>]
let Prefix = "/rails/active_storage"

/// `blob.signed_id`: the Active Storage verifier with purpose "blob_id".
let signedBlobId (verifier: MessageVerifier) (blobId: int64) (expiresAt: Timestamp option) : string =
    MessageVerifier.generateRaw verifier (string blobId) (Some "blob_id") expiresAt

/// `ActiveStorage::Blob.find_signed(id)`'s verification half.
let verifySignedBlobId (verifier: MessageVerifier) (signedId: string) (now: Timestamp) : int64 option =
    match MessageVerifier.verifyRaw verifier signedId (Some "blob_id") now with
    | Ok data -> JsonValue.parse data |> Option.bind (fun json -> json.AsInt64)
    | Error _ -> None

let private blobPath (kind: string) (verifier: MessageVerifier) (blobId: int64) (filename: Filename) (disposition: string option) : string =
    let path =
        $"{Prefix}/blobs/{kind}/{escapeSegment (signedBlobId verifier blobId None)}/{escapePath (Filename.sanitized filename)}"
    match disposition with
    // `Hash#to_query`, which escapes with `CGI.escape`.
    | Some disposition -> path + "?disposition=" + Ruby.cgiEscape disposition
    | None -> path

let private representationPath (kind: string) (verifier: MessageVerifier) (blob: Blob) (variation: Variation) : string =
    $"{Prefix}/representations/{kind}/{escapeSegment (signedBlobId verifier blob.Id None)}/{escapeSegment (Variation.key verifier variation)}/{escapePath (Filename.sanitized blob.Filename)}"

/// `rails_blob_path(blob, disposition:)` -> `/rails/active_storage/blobs/redirect/:signed_id/*filename`.
let blobRedirectPath (verifier: MessageVerifier) (blob: Blob) (disposition: string option) : string =
    blobPath "redirect" verifier blob.Id blob.Filename disposition

/// `rails_storage_proxy_path(blob)` -> `/rails/active_storage/blobs/proxy/:signed_id/*filename`.
let blobProxyPath (verifier: MessageVerifier) (blob: Blob) (disposition: string option) : string =
    blobPath "proxy" verifier blob.Id blob.Filename disposition

/// `url_for(blob.representation(...))`/`url_for(variant)`/`url_for(preview)` -> the path of
/// `/rails/active_storage/representations/redirect/:signed_blob_id/:variation_key/*filename`.
/// `blob` is the *original* blob (the video for previews), `variation` is exactly the one the
/// representation holds (defaulted for variants, as given for previews).
let representationRedirectPath (verifier: MessageVerifier) (blob: Blob) (variation: Variation) : string =
    representationPath "redirect" verifier blob variation

let representationProxyPath (verifier: MessageVerifier) (blob: Blob) (variation: Variation) : string =
    representationPath "proxy" verifier blob variation
